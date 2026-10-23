using System.Threading.Tasks;

namespace Scalpel.Visuals;

/// <summary>
/// Liquid spreading over one surface (the body, the bed, the floor). Points spread over its triangles hold milliliters
/// and pass them downhill to their neighbours by surface height plus liquid depth, as <see cref="SurfaceBlood"/> does
/// on the site's grid. Where the surface faces down, the liquid gathers into drips that leave it; whatever lies below
/// takes them as its own input. A film steps on a worker thread and either draws a surface mesh or publishes
/// samples for the single body-wide skin layer.
/// </summary>
internal sealed class SurfaceFilm {
    /// <summary>How a surface holds its film.</summary>
    /// <param name="Spacing">Meters between neighbouring points.</param>
    /// <param name="HoldDepth">Depth (m) the liquid's edge keeps before it flows on: how thick a pool stands.</param>
    /// <param name="Lift">How far (m) the drawn film floats over the surface.</param>
    /// <param name="DripMl">Milliliters a hanging drip gathers before it falls.</param>
    internal readonly record struct Settings(float Spacing, float HoldDepth, float Lift, float DripMl = 0.05f);

    /// <summary><paramref name="Ml"/> leaving the surface at point <paramref name="Node"/>, local position
    /// <paramref name="At"/>.</summary>
    internal readonly record struct Drip(int Node, Vector3 At, float Ml);
    internal readonly record struct FilmSample(int Node, Vector3 At, Vector3 Normal, float Coverage);

    /// <summary>Steps per second. Fixed, so a run plays out the same however fast the worker is.</summary>
    internal const float Rate = 30f;
    private const int Substeps = 2;
    private const float MobilityDepth = 0.0006f;
    /// <summary>How fast a film runs for its slope: liquid moved per second per unit of head over distance.</summary>
    private const float FlowRate = 6f;
    private const float ResidueDepth = 0.00004f;
    /// <summary>Points whose surface faces further down than this (cosine to up) can't hold liquid: it drips off.
    /// </summary>
    private const float Overhang = -0.2f;
    /// <summary>Liquid depth (m) that hides about two thirds of the surface, as on the site's skin.</summary>
    private const float CoverDepth = SurfaceBlood.CoverDepth;
    /// <summary>Milliliters a point passes on in a substep below which the film counts as settled: a pool's last
    /// trickle, which would otherwise keep every pool stepping and redrawn for good.</summary>
    private const float SettledMl = 0.00001f;
    /// <summary>Steps between drawings of a moving film.</summary>
    private const int StepsPerDraw = 3;
    /// <summary>How far (m) the drawn film rises with its depth at most.</summary>
    private const float MaxRise = 0.001f;
    /// <summary>Drawn coverage below which a triangle's corner shows nothing.</summary>
    private const float Visible = 0.01f;
    /// <summary>How many spacings the drawn film fades over toward an open edge it fades at.</summary>
    private const float EdgeFadeSpacings = 5f;

    private readonly Settings _settings;
    private readonly bool _drawMesh;
    /// <summary>The step's samples go into whichever of these the published <see cref="Samples"/> isn't in, so a
    /// reader of those never sees the next step's. They grow to the most samples a step had.</summary>
    private readonly FilmSample[][] _sampleBuffers = [[], []];
    private ArraySegment<FilmSample> _stepSamples;
    private readonly Vector3[] _at;
    private readonly Vector3[] _normal;
    private readonly Vector3[] _drawNormal;
    private readonly bool[] _carries;
    private readonly float[] _area;
    private readonly int[] _neighborStart;
    private readonly int[] _neighbors;
    private readonly float[] _distance;
    private readonly int[] _triangles;
    /// <summary>The triangles at each point: <see cref="_pointTriangles"/> from <see cref="_pointTriangleStart"/>[k].
    /// </summary>
    private readonly int[] _pointTriangleStart;
    private readonly int[] _pointTriangles;
    private readonly int[] _drawnStamp;
    private readonly List<int> _drawn = [];
    private readonly List<int> _reached = [];
    private readonly int[] _reachedStamp;
    private int _stamp;
    private readonly BodyLiquidSurface.SkinWeights[]? _skin;
    private readonly Dictionary<Vector3I, List<int>> _cells = [];
    private readonly float _reach;
    /// <summary>How much of a point's liquid is drawn, 0 at an open edge the film fades toward, else 1.</summary>
    private readonly float[] _shown;
    // Drawn coverage blurred once and twice over each point's neighbours, worked out once a step (stamped).
    private readonly float[] _blurredOnce;
    private readonly float[] _coverage;
    private readonly int[] _coverageAsked;
    private readonly float[] _blurredTwice;
    private readonly int[] _onceAsked;
    private readonly int[] _twiceAsked;

    // Owned by the worker while a step runs.
    private readonly float[] _ml;
    private readonly float[] _shed;
    private readonly float[] _change;
    private readonly float[] _height;
    /// <summary>Neighbours a point passes liquid to at most: the nearest within reach.</summary>
    private const int MaxNeighbors = 16;
    private readonly float[] _weights = new float[MaxNeighbors];
    private readonly List<int> _wet = [];
    private readonly bool[] _isWet;
    private readonly List<Drip> _drips = [];
    private Vector3 _gravity;
    private Vector3[] _vertices = [];
    private Vector3[] _normals = [];
    private Vector2[] _uvs = [];
    private int[] _indices = [];
    private readonly List<int> _drawnPoints = [];
    private readonly int[] _vertexStamp;
    private readonly int[] _vertexOf;
    private int[] _bones = [];
    private float[] _boneWeights = [];

    // Main thread.
    private readonly List<(int Node, float Ml)> _pending = [];
    private readonly List<Drip> _released = [];
    private float _pendingMl;
    private float _committed;
    private Task? _step;
    private Func<int, bool>? _drains;
    private int _stepCount;
    private readonly int[] _drainAsked;
    private readonly bool[] _drainsHere;
    private float _clock;
    // Whether liquid was added or moved since the film was last drawn, and whether the last step drew it anew.
    private bool _moved;
    private bool _rebuilt;
    // Whether the last step moved nothing worth drawing; the film then sleeps (see Tick).
    private bool _settled;
    // Moved since it was last drawn: drawn on its turn, or as soon as it stops moving, so it never settles undrawn.
    private bool _mustDraw;

    internal int Count => _at.Length;
    /// <summary>How far apart (m) the film's points are laid.</summary>
    internal float Spacing => _settings.Spacing;
    internal Vector3 At(int node) => _at[node];
    internal bool CarriesAt(int node) => _carries[node];
    internal Vector3 NormalAt(int node) => _normal[node];
    internal BodyLiquidSurface.SkinWeights SkinAt(int node) => _skin?[node] ?? default;

    /// <summary>The film drawn: what the last finished step left.</summary>
    internal ArrayMesh Mesh { get; }
    internal ArraySegment<FilmSample> Samples { get; private set; } = ArraySegment<FilmSample>.Empty;

    /// <summary>Liquid on the surface or gathering into drips, including what was added for the next step.</summary>
    internal float HeldMl => _committed + _pendingMl;

    /// <summary>Drips finished steps let go, until <see cref="ClearReleased"/>. Their liquid has left the film.
    /// </summary>
    internal IReadOnlyList<Drip> Released => _released;

    internal void ClearReleased() => _released.Clear();

    /// <summary>Whether a step is running on the worker.</summary>
    internal bool Stepping => _step is not null;

    /// <summary>Lays points over the surface and links each to its near neighbours.</summary>
    /// <param name="triangles">The surface's triangles, three corners each, in the film's own space.</param>
    /// <param name="outward">One outward normal per corner, to orient each triangle; null takes the winding's.</param>
    /// <param name="skin">One set of bone weights per corner, for a film drawn on a skinned mesh.</param>
    /// <param name="keep">Whether the part of the surface at a point with a normal belongs to the film.</param>
    /// <param name="mesh">Where to draw it; made on the main thread when the film is built on a worker.</param>
    /// <param name="fadesAt">Which points of the surface's open edges the drawn film fades out toward: where the
    /// surface was cut out of a larger one (the body round the site), so that edge is no edge of the real thing.
    /// </param>
    /// <param name="carries">Which points hold liquid: liquid added at any other point goes to the nearest one that
    /// does, and never flows to one that doesn't. The body's film leaves the site's own skin to the site's film this
    /// way.</param>
    internal SurfaceFilm(IReadOnlyList<Vector3> triangles, Settings settings, IReadOnlyList<Vector3>? outward = null,
        IReadOnlyList<BodyLiquidSurface.SkinWeights>? skin = null, Func<Vector3, Vector3, bool>? keep = null,
        ArrayMesh? mesh = null, Func<Vector3, bool>? fadesAt = null, Func<Vector3, Vector3, bool>? carries = null,
        bool drawMesh = true) {
        _settings = settings;
        _drawMesh = drawMesh;
        Mesh = mesh ?? new ArrayMesh();
        var index = new Dictionary<(long, long, long), int>();
        var points = new List<Vector3>();
        var normals = new List<Vector3>();
        var drawNormals = new List<Vector3>();
        var areas = new List<float>();
        var weights = skin is null ? null : new List<BodyLiquidSurface.SkinWeights>();
        var faces = new List<int>();

        // Points closer than about half the spacing are one: a long sliver of a triangle, stepped along its length,
        // would otherwise crowd thousands of points across its width. Faces turned against each other, such as the two
        // sides of a thin plate, keep their own points.
        var weld = 1f / (settings.Spacing * 0.6f);
        var facing = new List<Vector3>();
        var sameCell = new List<int>();
        int Node(Vector3 p, BodyLiquidSurface.SkinWeights weight, Vector3 side, Vector3 smooth) {
            var key = ((long)Mathf.Round(p.X * weld), (long)Mathf.Round(p.Y * weld), (long)Mathf.Round(p.Z * weld));
            var first = index.GetValueOrDefault(key, -1);
            for (var found = first; found >= 0; found = sameCell[found]) {
                if (facing[found].Dot(side) > -0.3f) { drawNormals[found] += smooth; return found; }
            }
            index[key] = points.Count;
            sameCell.Add(first);
            facing.Add(side);
            points.Add(p);
            normals.Add(Vector3.Zero);
            drawNormals.Add(smooth);
            areas.Add(0f);
            weights?.Add(weight);
            return points.Count - 1;
        }

        // Built on a worker beside the game: reused buffers keep it from piling up garbage the game would pause for.
        var grid = Array.Empty<int>();
        for (var t = 0; t + 2 < triangles.Count; t += 3) {
            var (a, b, c) = (triangles[t], triangles[t + 1], triangles[t + 2]);
            var face = (c - a).Cross(b - a);
            if (face.LengthSquared() < 1e-14f) { continue; }
            var (wa, wb, wc) = skin is null ? default : (skin[t], skin[t + 1], skin[t + 2]);
            var (na, nb, nc) = outward is null ? (face.Normalized(), face.Normalized(), face.Normalized())
                : (outward[t], outward[t + 1], outward[t + 2]);
            if (outward is not null && face.Dot(outward[t] + outward[t + 1] + outward[t + 2]) < 0f) {
                (b, c) = (c, b);
                (wb, wc) = (wc, wb);
                (nb, nc) = (nc, nb);
                face = -face;
            }
            var side = face.Normalized();
            // Where the triangle's middle and corners agree, the whole triangle is in or out: asking once for each of
            // its many small faces would cost far more than building them. Fine ownership boundaries use carries
            // instead: the drawn triangles remain intact while only their liquid transport is restricted.
            bool? whole = null;
            if (keep is not null) {
                var middle = (a + b + c) / 3f;
                var (nearA, nearB, nearC) = (middle.Lerp(a, 0.9f), middle.Lerp(b, 0.9f), middle.Lerp(c, 0.9f));
                var inside = keep(middle, side);
                if (keep(nearA, side) == inside && keep(nearB, side) == inside && keep(nearC, side) == inside) { whole = inside; }
            }
            if (whole == false) { continue; }
            var kab = EdgeSteps(a, b);
            var kbc = EdgeSteps(b, c);
            var kca = EdgeSteps(c, a);
            var n = Math.Max(kab, Math.Max(kbc, kca));
            var size = (n + 1) * (n + 2) / 2;
            if (grid.Length < size) { grid = new int[size]; }
            var row = 0;
            // u weighs b, v weighs c. Points on an edge snap to that edge's own steps, so the triangle beside it,
            // stepped differently, shares them and the surface stays one piece.
            for (var i = 0; i <= n; i++) {
                for (var j = 0; j <= n - i; j++) {
                    var (u, v) = ((float)i / n, (float)j / n);
                    Vector3 p;
                    if (j == 0) {
                        p = OnEdge(a, b, u, kab);
                    } else if (i == 0) {
                        p = OnEdge(a, c, v, kca);
                    } else if (i + j == n) {
                        p = OnEdge(b, c, v, kbc);
                    } else {
                        p = a * (1f - u - v) + b * u + c * v;
                    }
                    var weight = skin is null ? default
                        : BodyLiquidSurface.BlendSkin(wa, wb, wc, new Vector3(1f - u - v, u, v));
                    grid[row + j] = Node(p, weight, side, na * (1f - u - v) + nb * u + nc * v);
                }
                row += n + 1 - i;
            }
            int At(int i, int j) => grid[i * (n + 1) - i * (i - 1) / 2 + j];
            void Face(int p, int q, int r) {
                if (p == q || q == r || r == p) { return; }
                var normal = (points[r] - points[p]).Cross(points[q] - points[p]);
                // Snapping and welding a thin source triangle can fold a tiny face over its neighbour.
                // It is no part of the outer surface, and would show as a detached dark/red shard.
                if (normal.Dot(side) <= 0f) { return; }
                var centroid = (points[p] + points[q] + points[r]) / 3f;
                if (whole is null && keep is not null && !keep(centroid, normal.Normalized())) { return; }
                var third = normal.Length() / 6f;
                normals[p] += normal; normals[q] += normal; normals[r] += normal;
                areas[p] += third; areas[q] += third; areas[r] += third;
                faces.Add(p); faces.Add(q); faces.Add(r);
            }
            for (var i = 0; i < n; i++) {
                for (var j = 0; j < n - i; j++) {
                    Face(At(i, j), At(i + 1, j), At(i, j + 1));
                    if (j + 1 < n - i) { Face(At(i + 1, j), At(i + 1, j + 1), At(i, j + 1)); }
                }
            }
        }

        // Drop the points no kept triangle uses, and link each to its triangles' other corners.
        var used = new int[points.Count];
        Array.Fill(used, -1);
        var count = 0;
        foreach (var k in faces) {
            if (used[k] < 0) { used[k] = count++; }
        }
        _at = new Vector3[count];
        _normal = new Vector3[count];
        _drawNormal = new Vector3[count];
        _area = new float[count];
        _carries = new bool[count];
        _skin = weights is null ? null : new BodyLiquidSurface.SkinWeights[count];
        for (var k = 0; k < points.Count; k++) {
            if (used[k] < 0) { continue; }
            _at[used[k]] = points[k];
            _normal[used[k]] = normals[k].Normalized();
            _drawNormal[used[k]] = outward is null ? _normal[used[k]] : drawNormals[k].Normalized();
            _area[used[k]] = Mathf.Max(areas[k], 1e-9f);
            _carries[used[k]] = carries?.Invoke(_at[used[k]], _normal[used[k]]) ?? true;
            if (_skin is not null) { _skin[used[k]] = weights![k]; }
        }
        _triangles = faces.Select(k => used[k]).ToArray();
        // Every point within reach is a neighbour, not only its triangles' corners: those lean along the triangles'
        // diagonals and grow a round pool into a diamond. Points facing away from each other (two sides of a thin
        // part) aren't linked.
        var reach = _reach = _settings.Spacing * 1.6f;
        var cells = _cells;
        for (var k = 0; k < count; k++) {
            if (!cells.TryGetValue(Cell(_at[k]), out var list)) { cells.Add(Cell(_at[k]), list = []); }
            list.Add(k);
        }
        _neighborStart = new int[count + 1];
        var flat = new List<int>();
        var around = new List<(int Node, float Distance)>();
        for (var k = 0; k < count; k++) {
            _neighborStart[k] = flat.Count;
            around.Clear();
            var home = Cell(_at[k]);
            for (var x = -1; x <= 1; x++) {
                for (var y = -1; y <= 1; y++) {
                    for (var z = -1; z <= 1; z++) {
                        if (!cells.TryGetValue(home + new Vector3I(x, y, z), out var list)) { continue; }
                        foreach (var other in list) {
                            var d = _at[k].DistanceTo(_at[other]);
                            if (other != k && d <= reach && _normal[k].Dot(_normal[other]) > 0.3f) { around.Add((other, d)); }
                        }
                    }
                }
            }
            around.Sort(static (x, y) => x.Distance.CompareTo(y.Distance));
            for (var i = 0; i < Math.Min(around.Count, MaxNeighbors); i++) { flat.Add(around[i].Node); }
        }
        _neighborStart[count] = flat.Count;
        _neighbors = [.. flat];
        _distance = new float[_neighbors.Length];
        for (var k = 0; k < count; k++) {
            for (var e = _neighborStart[k]; e < _neighborStart[k + 1]; e++) {
                _distance[e] = Mathf.Max(_at[k].DistanceTo(_at[_neighbors[e]]), 1e-5f);
            }
        }
        _pointTriangleStart = new int[count + 1];
        foreach (var k in _triangles) { _pointTriangleStart[k + 1]++; }
        for (var k = 0; k < count; k++) { _pointTriangleStart[k + 1] += _pointTriangleStart[k]; }
        _pointTriangles = new int[_triangles.Length];
        var filled = (int[])_pointTriangleStart.Clone();
        for (var t = 0; t < _triangles.Length; t++) { _pointTriangles[filled[_triangles[t]]++] = t / 3; }
        _drawnStamp = new int[_triangles.Length / 3];
        _ml = new float[count];
        _shed = new float[count];
        _change = new float[count];
        _height = new float[count];
        _isWet = new bool[count];
        _drainAsked = new int[count];
        _drainsHere = new bool[count];
        _reachedStamp = new int[count];
        _vertexStamp = new int[count];
        _vertexOf = new int[count];
        _blurredOnce = new float[count];
        _coverage = new float[count];
        _coverageAsked = new int[count];
        _blurredTwice = new float[count];
        _onceAsked = new int[count];
        _twiceAsked = new int[count];
        _shown = fadesAt is null ? Enumerable.Repeat(1f, count).ToArray() : EdgeFade(fadesAt, settings.Spacing * EdgeFadeSpacings);
    }

    /// <summary>For each point, how far it lies from the surface's open edge (an edge of one triangle only) where
    /// <paramref name="fadesAt"/>, over <paramref name="reach"/>, eased: 0 on the edge, 1 from reach on.</summary>
    private float[] EdgeFade(Func<Vector3, bool> fadesAt, float reach) {
        var edges = new Dictionary<(int, int), int>();
        for (var i = 0; i < _triangles.Length; i += 3) {
            for (var e = 0; e < 3; e++) {
                var (a, b) = (_triangles[i + e], _triangles[i + (e + 1) % 3]);
                var key = a < b ? (a, b) : (b, a);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        }
        var distance = Enumerable.Repeat(float.PositiveInfinity, _at.Length).ToArray();
        var open = new PriorityQueue<int, float>();
        void OnEdge(int k) {
            if (!fadesAt(_at[k])) { return; }
            distance[k] = 0f;
            open.Enqueue(k, 0f);
        }
        foreach (var ((a, b), uses) in edges) {
            if (uses != 1) { continue; }
            OnEdge(a);
            OnEdge(b);
        }
        while (open.TryDequeue(out var k, out var d)) {
            if (d > distance[k] || d >= reach) { continue; }
            for (var e = _neighborStart[k]; e < _neighborStart[k + 1]; e++) {
                var other = _neighbors[e];
                var through = d + _distance[e];
                if (through < distance[other]) {
                    distance[other] = through;
                    open.Enqueue(other, through);
                }
            }
        }
        return distance.Select(d => Mathf.SmoothStep(0f, 1f, Mathf.Min(d / reach, 1f))).ToArray();
    }

    private int EdgeSteps(Vector3 a, Vector3 b) => Math.Max(1, Mathf.CeilToInt(a.DistanceTo(b) / _settings.Spacing));

    /// <summary>The point <paramref name="t"/> along a to b, snapped to the edge's <paramref name="steps"/> and
    /// worked out from the same end whichever triangle asks, so both sides get the very same point.</summary>
    private static Vector3 OnEdge(Vector3 a, Vector3 b, float t, int steps) {
        var forward = a.X < b.X || (a.X == b.X && (a.Y < b.Y || (a.Y == b.Y && a.Z <= b.Z)));
        var (from, to) = forward ? (a, b) : (b, a);
        var step = Mathf.RoundToInt((forward ? t : 1f - t) * steps);
        return step == 0 ? from : step == steps ? to : from.Lerp(to, (float)step / steps);
    }

    private Vector3I Cell(Vector3 p) => (Vector3I)(p / _reach).Floor();

    /// <summary>The point nearest <paramref name="local"/>, or -1 for an empty film. With <paramref name="facing"/> it
    /// takes the nearest that faces that way, if any does: liquid landing on a thin plate wets its top, not its
    /// underside.</summary>
    internal int Nearest(Vector3 local, Vector3? facing = null) {
        var best = -1;
        var distance = float.PositiveInfinity;
        var home = Cell(local);
        // Look a few cells around first: a landing or an inlet is on or right by the surface.
        for (var ring = 0; ring <= 3 && best < 0; ring++) {
            for (var x = -ring; x <= ring; x++) {
                for (var y = -ring; y <= ring; y++) {
                    for (var z = -ring; z <= ring; z++) {
                        if (Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))) != ring
                            || !_cells.TryGetValue(home + new Vector3I(x, y, z), out var list)) { continue; }
                        foreach (var k in list) {
                            if (!_carries[k] || facing is { } side && _normal[k].Dot(side) < 0.3f) { continue; }
                            var d = _at[k].DistanceSquaredTo(local);
                            if (d < distance) { distance = d; best = k; }
                        }
                    }
                }
            }
        }
        if (best >= 0) { return best; }
        for (var k = 0; k < _at.Length; k++) {
            if (!_carries[k] || facing is { } side && _normal[k].Dot(side) < 0.3f) { continue; }
            var d = _at[k].DistanceSquaredTo(local);
            if (d < distance) { distance = d; best = k; }
        }
        return best < 0 && facing is not null ? Nearest(local) : best;
    }

    /// <summary>Adds <paramref name="ml"/> at point <paramref name="node"/>, from the next step on.</summary>
    internal void Add(int node, float ml) {
        if (ml <= 0f || node < 0) { return; }
        if (!_carries[node]) { node = Nearest(_at[node], _normal[node]); }
        if (node < 0) { return; }
        _pending.Add((node, ml));
        _pendingMl += ml;
    }

    /// <summary>Advances the main thread's clock. At every step's time it finishes the running step, which fills
    /// <see cref="Released"/> and <see cref="Mesh"/>, and returns true: the caller then starts the next with
    /// <see cref="Start"/>. A step the worker hasn't finished isn't waited for: the frame goes on and the step's time
    /// stays due, so a film too big for its rate falls behind rather than stall the game.</summary>
    internal bool Tick(float delta) {
        _clock = Mathf.Min(_clock + delta, 1f / Rate);
        if (_clock < 1f / Rate || _step is { IsCompleted: false }) { return false; }
        _clock = 0f;
        Finish();
        return true;
    }

    /// <summary>Starts a step on the worker with gravity <paramref name="gravity"/> (in the film's space).
    /// <paramref name="drains"/> marks points where the surface meets another one, such as skin pressed on the bed:
    /// their liquid leaves as drips there, to pass on to that surface. It runs on the worker.</summary>
    internal void Start(Vector3 gravity, Func<int, bool>? drains = null) {
        var down = gravity.Normalized();
        // A settled film stays as it is until liquid comes or the surface turns: stepping it would change nothing.
        if (_pending.Count == 0 && (_wet.Count == 0 || _settled && down.IsEqualApprox(_gravity))) { return; }
        TakePending();
        _drains = drains;
        _step = Task.Run(() => Step(down, 1f / Rate));
    }

    /// <summary>Finishes any running step, then runs one on this thread; for tests.</summary>
    internal void StepNow(Vector3 gravity, float delta) {
        Finish();
        _drains = null;
        _released.Clear();
        TakePending();
        Step(gravity.Normalized(), delta);
        Publish();
    }

    /// <summary>Waits for a running step and publishes its drips and mesh.</summary>
    internal void Finish() {
        if (_step is not { } running) { return; }
        _step = null;
        try {
            running.Wait();
        } catch (AggregateException failure) {
            GD.PushWarning($"Surface film step failed: {failure.InnerException}");
        }
        Publish();
    }

    private void TakePending() {
        foreach (var (node, ml) in _pending) {
            _ml[node] += ml;
            if (!_isWet[node]) { _isWet[node] = true; _wet.Add(node); }
        }
        _committed += _pendingMl;
        _moved |= _pending.Count > 0;
        _pending.Clear();
        _pendingMl = 0f;
        _drips.Clear();
    }

    private void Publish() {
        foreach (var drip in _drips) {
            _released.Add(drip);
            _committed -= drip.Ml;
        }
        _drips.Clear();
        if (_rebuilt) {
            if (_drawMesh) {
                Upload();
            } else {
                Samples = _stepSamples;
            }
        }
        _rebuilt = false;
    }

    /// <summary>Whether point k passes its liquid on, asked once a step.</summary>
    private bool Drains(int k) {
        if (_drains is null) { return false; }
        if (_drainAsked[k] != _stepCount) {
            _drainAsked[k] = _stepCount;
            _drainsHere[k] = _drains(k);
        }
        return _drainsHere[k];
    }

    private void Step(Vector3 down, float delta) {
        _stepCount++;
        if (down != _gravity) {
            _gravity = down;
            for (var k = 0; k < _at.Length; k++) { _height[k] = -_at[k].Dot(down); }
        }
        var hold = _settings.HoldDepth;
        for (var sub = 0; sub < Substeps; sub++) {
            var dt = delta / Substeps;
            foreach (var k in _wet) { _change[k] = 0f; }
            var count = _wet.Count;
            for (var w = 0; w < count; w++) {
                var k = _wet[w];
                var ml = _ml[k];
                if (ml <= 0f) { continue; }
                var depth = ml * 0.000001f / _area[k];
                var movable = ml - ResidueDepth * _area[k] * 1000000f;
                if (movable <= 0f) { continue; }
                if (-_normal[k].Dot(down) < Overhang || Drains(k)) {
                    // Facing the floor nothing holds it up; where it meets another surface it passes onto that.
                    _change[k] -= movable;
                    _moved = true;
                    _shed[k] += movable;
                    continue;
                }
                var head = _height[k] + depth;
                var total = 0f;
                var first = _neighborStart[k];
                var last = _neighborStart[k + 1];
                for (var e = first; e < last; e++) {
                    var other = _neighbors[e];
                    var otherHead = _height[other] + _ml[other] * 0.000001f / _area[other];
                    // A dry edge holds the liquid back until it stands HoldDepth deep, however far the surface drops.
                    var weight = !_carries[other] || _ml[other] <= 0f && depth <= hold ? 0f : Mathf.Max(head - otherHead - hold, 0f) / _distance[e];
                    _weights[e - first] = weight;
                    total += weight;
                }
                if (total <= 0f) { continue; }
                // Viscous film mobility scales with depth squared: a thin advancing rim creeps, its deep supply runs.
                var mobility = Mathf.Min(1f, depth * depth / (MobilityDepth * MobilityDepth));
                var outflow = movable * Mathf.Min(0.24f, total * dt * FlowRate * mobility);
                _change[k] -= outflow;
                _moved |= outflow > SettledMl;
                for (var e = first; e < last; e++) {
                    var share = outflow * _weights[e - first] / total;
                    if (share <= 0f) { continue; }
                    var other = _neighbors[e];
                    if (!_isWet[other]) { _isWet[other] = true; _wet.Add(other); _change[other] = 0f; }
                    _change[other] += share;
                }
            }
            foreach (var k in _wet) { _ml[k] = Mathf.Max(_ml[k] + _change[k], 0f); }
        }
        for (var w = _wet.Count - 1; w >= 0; w--) {
            var k = _wet[w];
            // A point holds what it sheds until a whole drop gathers, or until it has nothing more to give.
            if (_shed[k] >= _settings.DripMl || _shed[k] > 0f && _ml[k] <= ResidueDepth * _area[k] * 1000000f) {
                _drips.Add(new Drip(k, _at[k] + _normal[k] * 0.002f, _shed[k]));
                _shed[k] = 0f;
            }
            if (_ml[k] <= 0f && _shed[k] <= 0f) {
                _isWet[k] = false;
                _wet[w] = _wet[^1];
                _wet.RemoveAt(_wet.Count - 1);
            }
        }
        // A settled film looks the same: drawing it again would only cost the main thread an upload. A moving one is
        // drawn every few steps: liquid creeping over skin reads the same, at a fraction of the work.
        var undrawn = _moved || _mustDraw;
        _rebuilt = undrawn && (_stepCount % StepsPerDraw == 0 || !_moved);
        if (_rebuilt) {
            if (_drawMesh) { BuildMesh(); } else { BuildSamples(); }
        }
        _mustDraw = undrawn && !_rebuilt;
        _settled = !_moved && !_mustDraw;
        _moved = false;
    }

    /// <summary>How much of the surface at point k the liquid hides, 0..1.</summary>
    private float Coverage(int k) => 1f - Mathf.Exp(-_ml[k] * 0.000001f / _area[k] / CoverDepth);

    private void Reach(int k) {
        if (_reachedStamp[k] == _stamp) { return; }
        _reachedStamp[k] = _stamp;
        _reached.Add(k);
    }

    private void ReachWet() {
        _stamp++;
        _reached.Clear();
        foreach (var k in _wet) { Reach(k); }
        for (var ring = 0; ring < 2; ring++) {
            var count = _reached.Count;
            for (var r = 0; r < count; r++) {
                var k = _reached[r];
                for (var e = _neighborStart[k]; e < _neighborStart[k + 1]; e++) { Reach(_neighbors[e]); }
            }
        }
    }

    private void BuildSamples() {
        ReachWet();
        // The shared spatial field filters these densities once, uniformly on both sides of the site.
        // Exporting the old mesh blur here would smooth only the body a second time.
        var slot = ReferenceEquals(Samples.Array, _sampleBuffers[0]) ? 1 : 0;
        var buffer = _sampleBuffers[slot];
        var count = 0;
        foreach (var k in _reached) {
            if (!_carries[k]) { continue; }
            if (count == buffer.Length) { Array.Resize(ref buffer, Math.Max(buffer.Length * 2, 256)); }
            buffer[count++] = new FilmSample(k, _at[k], _drawNormal[k], Coverage(k));
        }
        _sampleBuffers[slot] = buffer;
        _stepSamples = new ArraySegment<FilmSample>(buffer, 0, count);
    }

    private void BuildMesh() {
        // Draw the two-ring blur and its dry margin together, so the fade never ends on a triangle edge.
        ReachWet();
        _drawn.Clear();
        foreach (var k in _reached) {
            for (var i = _pointTriangleStart[k]; i < _pointTriangleStart[k + 1]; i++) {
                var t = _pointTriangles[i];
                if (_drawnStamp[t] == _stamp) { continue; }
                _drawnStamp[t] = _stamp;
                var c = t * 3;
                var shows = false;
                for (var corner = 0; corner < 3; corner++) { shows |= DrawnCoverage(_triangles[c + corner]) > Visible; }
                if (shows) { _drawn.Add(c); }
            }
        }
        _drawn.Sort();
        // One vertex per point, shared by its triangles: a third or less of the data to upload.
        _drawnPoints.Clear();
        foreach (var t in _drawn) {
            for (var corner = 0; corner < 3; corner++) {
                var k = _triangles[t + corner];
                if (_vertexStamp[k] == _stamp) { continue; }
                _vertexStamp[k] = _stamp;
                _vertexOf[k] = _drawnPoints.Count;
                _drawnPoints.Add(k);
            }
        }
        var vertices = _drawnPoints.Count;
        if (_vertices.Length != vertices) {
            _vertices = new Vector3[vertices];
            _normals = new Vector3[vertices];
            _uvs = new Vector2[vertices];
            _bones = _skin is null ? [] : new int[vertices * 4];
            _boneWeights = _skin is null ? [] : new float[vertices * 4];
        }
        if (_indices.Length != _drawn.Count * 3) { _indices = new int[_drawn.Count * 3]; }
        for (var v = 0; v < vertices; v++) {
            var k = _drawnPoints[v];
            // Raised by its depth, tapering with the drawn coverage: a deep point beside a dry one would stand out
            // of curved skin as a flap.
            var depth = Mathf.Min(_ml[k] * 0.000001f / _area[k], MaxRise) * _blurredTwice[k];
            _vertices[v] = _at[k] + _normal[k] * (_settings.Lift + depth);
            // Preserve the source skin's smooth lighting across the film boundary. Clipped microtriangles
            // still supply geometric normals for transport, never the reflected highlight.
            _normals[v] = _drawNormal[k];
            _uvs[v] = new Vector2(_blurredTwice[k], OpticalDepth(k) * _shown[k]);
            if (_skin is not null) {
                var skin = _skin[k];
                for (var b = 0; b < 4; b++) {
                    _bones[v * 4 + b] = skin.Bones[b];
                    _boneWeights[v * 4 + b] = skin.Weights[b];
                }
            }
        }
        var index = 0;
        foreach (var t in _drawn) {
            for (var corner = 0; corner < 3; corner++) { _indices[index++] = _vertexOf[_triangles[t + corner]]; }
        }
    }

    /// <summary>How much of the surface point k is drawn hiding: its coverage blurred twice over its neighbours, faded
    /// toward an open edge. A pool's coverage drops from full to none within a point; blurred, it falls off over a few,
    /// and the drawn edge where it crosses one half runs round between the points instead of along them.</summary>
    private float DrawnCoverage(int k) {
        if (_twiceAsked[k] == _stamp) { return _blurredTwice[k]; }
        _twiceAsked[k] = _stamp;
        var (first, last) = (_neighborStart[k], _neighborStart[k + 1]);
        var sum = BlurredOnce(k);
        for (var e = first; e < last; e++) { sum += BlurredOnce(_neighbors[e]); }
        return _blurredTwice[k] = sum / (last - first + 1);
    }

    private float ShownCoverage(int k) {
        if (!_isWet[k]) { return 0f; }
        if (_coverageAsked[k] == _stamp) { return _coverage[k]; }
        _coverageAsked[k] = _stamp;
        return _coverage[k] = Coverage(k) * _shown[k];
    }

    private float BlurredOnce(int k) {
        if (_onceAsked[k] == _stamp) { return _blurredOnce[k]; }
        _onceAsked[k] = _stamp;
        var (first, last) = (_neighborStart[k], _neighborStart[k + 1]);
        var sum = ShownCoverage(k);
        for (var e = first; e < last; e++) { sum += ShownCoverage(_neighbors[e]); }
        return _blurredOnce[k] = sum / (last - first + 1);
    }

    private float OpticalDepth(int k) => _ml[k] * 0.000001f / _area[k] / CoverDepth;

    private void Upload() {
        Mesh.ClearSurfaces();
        if (_vertices.Length == 0) { return; }
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        Shapes.SetPacked(arrays, Godot.Mesh.ArrayType.Vertex, _vertices);
        Shapes.SetPacked(arrays, Godot.Mesh.ArrayType.Normal, _normals);
        Shapes.SetPacked(arrays, Godot.Mesh.ArrayType.TexUV, _uvs);
        Shapes.SetPacked(arrays, Godot.Mesh.ArrayType.Index, _indices);
        if (_skin is not null) {
            Shapes.SetPacked(arrays, Godot.Mesh.ArrayType.Bones, _bones);
            Shapes.SetPacked(arrays, Godot.Mesh.ArrayType.Weights, _boneWeights);
        }
        Mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
    }

    /// <summary>Milliliters at point k, as of the last finished step: 0 while one runs (<see cref="Finish"/> first).
    /// </summary>
    internal float MlAt(int node) => _step is null ? _ml[node] : 0f;

    /// <summary>How much of the surface at point k the liquid hides (0..1), as of the last finished step: 0 while one
    /// runs (<see cref="Finish"/> first).</summary>
    internal float CoverageAt(int node) => _step is null ? Coverage(node) : 0f;
}
