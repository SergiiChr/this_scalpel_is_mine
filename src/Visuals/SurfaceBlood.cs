namespace Scalpel.Visuals;

/// <summary>A shallow viscous film: cells hold milliliters, and exchange them according to skin height plus liquid
/// depth. Shared pressure spreads puddles and joins streams; no independently drawn paths or artificial wandering.</summary>
internal sealed class SurfaceBlood {
    internal const int Size = 64;
    private const float Pinning = 0.00008f;
    private const float ResidueDepth = 0.00004f;
    // Bounded wetting pressure on inclined skin; horizontal pools retain their hydrostatic spreading.
    private const float ContactHead = 0.04f;
    private const float ContactDepth = 0.004f;
    internal const float CoverDepth = 0.00015f;
    // Dense films approach full mobility; thinner films move more slowly under viscous drag.
    private const float MobilityDepth = 0.0006f;
    /// <summary>How fast a thin film runs for its slope, and a deep pool, as on the body's own film (SurfaceFilm). A
    /// small seep stays where it starts; a deep pool runs off instead of standing millimeters high.</summary>
    private const float ThinFlowRate = 1.5f, DeepFlowRate = 6f;
    private static readonly (int X, int Y)[] Neighbors = [
        (-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, -1), (-1, 1), (1, 1),
    ];
    private readonly float[] _ml = new float[Size * Size];
    private readonly float[] _change = new float[Size * Size];
    private readonly float[] _height = new float[Size * Size];
    private readonly float[] _head = new float[Size * Size];
    private readonly float[] _thickness = new float[Size * Size];
    private readonly float[] _wetRow = new float[Size * Size];
    private readonly float[] _wettingHead = new float[Size * Size];
    private readonly float[] _curved = new float[Size * Size];
    private readonly List<int> _wetCells = new(Size * Size);
    private readonly List<int> _blurCells = new(Size * Size);
    private readonly bool[] _blurNeeded = new bool[Size * Size];
    private readonly float[] _opticalDepth = new float[Size * Size];
    private readonly float[] _painted = new float[Size * Size];
    private readonly float[] _paintedDepth = new float[Size * Size];
    private readonly bool[] _reducedCells = new bool[Size * Size];
    private readonly Vector3[] _skin = new Vector3[Size * Size];
    private readonly Vector3[] _normal = new Vector3[Size * Size];
    private readonly bool[] _holds = new bool[Size * Size];
    /// <summary>Skin turned further from the site's up than this (the up component of its normal) is the body's film's
    /// (SurfaceFilm): projected onto the site's grid, a steep flank stretches each cell into a sliver, and a film's
    /// edge drawn over slivers comes out as teeth.</summary>
    private const float SteepNormal = 0.45f;
    /// <summary>Which cells hand their blood to the body's film: a steep flank, and every cell past one, which the
    /// site's blood could only reach by crossing it, however level the grid is there (the far side of a limb). Taken
    /// from the skin at rest: the body's film is laid out once from the same cells (<see cref="Carries"/>), so a cell
    /// that steepens later as the skin moves still keeps its blood.</summary>
    private readonly bool[] _steep = new bool[Size * Size];
    /// <summary>The skin's own normal under each cell at rest, from the body model's faces where it has them
    /// (<see cref="MarkHandover"/>), else the grid's: what the shared blood layer lays a cell's blood on.</summary>
    private readonly Vector3[] _skinNormal = new Vector3[Size * Size];
    /// <summary>The skin's height at rest per cell: <see cref="Carries"/> answers from it after the skin moves.</summary>
    private readonly float[] _restHeight = new float[Size * Size];
    /// <summary>Blood that reached steep skin, per cell, on its way to the body's film there.</summary>
    private readonly float[] _steepRunoff = new float[Size * Size];
    private readonly List<int> _steepPending = [];
    private readonly bool[] _refitMarked = new bool[Size * Size];
    private readonly float[] _refitHeights = new float[Size * Size];
    private readonly List<int> _refitQueue = new(Size * Size);
    private int _refitCursor;
    private bool _refitPending;
    private bool _refitChanged;
    private int _geometryRevision;
    private int _basisGeometryRevision = -1;
    private readonly float[] _weights = new float[8];
    private readonly float[] _distances = new float[8];
    private readonly float[] _runoff = new float[32];
    private readonly Vector2[] _runoffUv = new Vector2[32];
    internal bool HasFilm { get; private set; }
    internal bool NeedsPaint { get; private set; }
    private bool _canFlow;
    private bool _hasBasis;
    private Basis _basis;
    private int _wipeVersion = -1;
    private readonly List<(int Index, int[] Neighbors)> _edgeNeighbors = [];
    internal bool NeedsTransport(Basis basis) => HasFilm && (_canFlow || !_hasBasis || !SameAs(basis));

    /// <summary>Whether the site's basis still lies as it did when the heights were last taken: a limb breathing or
    /// flinching turns it by less than this every frame, which would keep a settled film stepping for good. A slow
    /// turn adds up to it and takes the film along then.</summary>
    private bool SameAs(Basis basis) => (basis.X - _basis.X).LengthSquared() + (basis.Y - _basis.Y).LengthSquared()
        + (basis.Z - _basis.Z).LengthSquared() < BasisTolerance * BasisTolerance;

    private const float BasisTolerance = 0.002f;
    private readonly float[] _normalDepthPerMl = new float[Size * Size];
    private readonly float _projectedDepthPerMl;
    private readonly Vector2 _cell;
    private readonly float _scale;
    /// <summary>How far (site units) the skin may lie under the grid's surface and still be it: the most the surface
    /// falls across a cell this side of a steep flank.</summary>
    private readonly float _flankDrop;
    private readonly Func<Vector2, float> _heightAt;

    /// <summary>A film over the site's skin, <paramref name="height"/> over the site plane at each uv, on the cells
    /// <paramref name="holds"/> says are on the body.</summary>
    /// <param name="skinNormal">The skin's own normal under uv, from the body model's faces, null where it has none.
    /// Seen from the site's up, a limb's flank falls into the grid's last cells, whose slope stays gentle while the skin
    /// in them turns vertical: the model's faces tell, and the body's film is laid over those same faces. Without it
    /// the grid's own slope decides.</param>
    internal SurfaceBlood(Vector2 siteSize, Func<Vector2, float> height, Func<Vector2, bool>? holds = null, float scale = 1f,
        Func<Vector2, Vector3?>? skinNormal = null) {
        _heightAt = height;
        _scale = scale;
        _cell = siteSize * scale / Size;
        _flankDrop = Mathf.Max(siteSize.X, siteSize.Y) / Size * Mathf.Sqrt(1f / (SteepNormal * SteepNormal) - 1f);
        _projectedDepthPerMl = 0.000001f / (_cell.X * _cell.Y);
        for (var n = 0; n < Neighbors.Length; n++) {
            var (x, y) = Neighbors[n];
            _distances[n] = new Vector2(x * _cell.X, y * _cell.Y).Length();
        }
        for (var y = 0; y < Size; y++) {
            for (var x = 0; x < Size; x++) {
                var uv = Uv(x, y);
                var i = y * Size + x;
                _skin[i] = new Vector3((uv.X - 0.5f) * siteSize.X, height(uv), (uv.Y - 0.5f) * siteSize.Y);
                _holds[i] = holds?.Invoke(uv) ?? true;
            }
        }
        RebuildGeometryCache();
        MarkHandover(skinNormal);
        for (var i = 0; i < _restHeight.Length; i++) { _restHeight[i] = _skin[i].Y; }
        for (var y = 0; y < Size; y++) {
            for (var x = 0; x < Size; x++) {
                var i = y * Size + x;
                if (_holds[i]) { continue; }
                var neighbors = Neighbors.Select(n => (X: x + n.X, Y: y + n.Y))
                    .Where(p => p.X >= 0 && p.X < Size && p.Y >= 0 && p.Y < Size && _holds[p.Y * Size + p.X])
                    .Select(p => p.Y * Size + p.X).ToArray();
                if (neighbors.Length > 0) { _edgeNeighbors.Add((i, neighbors)); }
            }
        }
    }

    internal float RunoffMl { get; private set; }
    internal float RemovedMl { get; private set; }
    internal float QueuedRunoffMl => _runoff.Sum() + _steepRunoff.Sum();
    internal float HeldMl => _ml.Sum();
    internal float At(int x, int y) => _ml[y * Size + x];
    internal Vector3 SkinNormalAt(Vector2 uv) =>
        _skinNormal[Math.Clamp((int)(uv.Y * Size), 0, Size - 1) * Size + Math.Clamp((int)(uv.X * Size), 0, Size - 1)];
    private static Vector2 Uv(int x, int y) => new Vector2(x + 0.5f, y + 0.5f) / Size;

    /// <summary>The cells this film carries are the level skin round the site's middle, as far as the first steep
    /// flank (see <see cref="_steep"/>): what blood can spread to over its grid.</summary>
    private void MarkHandover(Func<Vector2, Vector3?>? skinNormal) {
        var level = new bool[Size * Size];
        for (var i = 0; i < level.Length; i++) {
            _skinNormal[i] = skinNormal?.Invoke(Uv(i % Size, i / Size)) ?? _normal[i];
            level[i] = _skinNormal[i].Y >= SteepNormal;
        }
        Array.Fill(_steep, true);
        // From the level cell nearest the site's middle.
        var start = -1;
        var nearest = int.MaxValue;
        for (var i = 0; i < level.Length; i++) {
            var (dx, dy) = (i % Size - Size / 2, i / Size - Size / 2);
            if (_holds[i] && level[i] && dx * dx + dy * dy < nearest) { (start, nearest) = (i, dx * dx + dy * dy); }
        }
        if (start < 0) { return; }
        var open = new Stack<int>();
        open.Push(start);
        _steep[start] = false;
        while (open.Count > 0) {
            var i = open.Pop();
            var x = i % Size; var y = i / Size;
            foreach (var (dx, dy) in Neighbors) {
                var nx = x + dx; var ny = y + dy;
                if (nx < 0 || nx >= Size || ny < 0 || ny >= Size) { continue; }
                var n = ny * Size + nx;
                if (!_steep[n] || !_holds[n] || !level[n]) { continue; }
                _steep[n] = false;
                open.Push(n);
            }
        }
    }

    /// <summary>Whether this film keeps blood on the skin at uv, <paramref name="height"/> over the site plane: on the
    /// body, this side of the first steep flank, and no deeper under the grid's own surface at rest than that surface
    /// falls across a cell. Skin lower than that in the same column is the flank seen edge-on from the site's up,
    /// which the grid can't tell from the top of it. Anywhere else blood runs on to the body's film, which holds
    /// exactly the skin this says it doesn't, so no liquid is held twice or by neither. Fixed once the film is made,
    /// so it's safe to ask from the thread that builds the body's film.</summary>
    internal bool Carries(Vector2 uv, float height) {
        var x = Math.Clamp((int)(uv.X * Size), 0, Size - 1);
        var y = Math.Clamp((int)(uv.Y * Size), 0, Size - 1);
        var i = y * Size + x;
        return _holds[i] && !_steep[i] && height >= _restHeight[i] - _flankDrop;
    }

    /// <summary>Whether this film keeps the blood that reaches the cell at uv (<see cref="Carries"/> for its top).
    /// </summary>
    internal bool Carries(Vector2 uv) => Carries(uv, float.PositiveInfinity);

    /// <summary>Moves the film already under a closure stroke to both sides of it, the <paramref name="share"/> of it
    /// the closure squeezes out. This is a one-time physical displacement; subsequent bleeding remains free to wet the
    /// seam again.</summary>
    internal float DisplaceStroke(IReadOnlyList<Vector2> path, float radius, float share = 1f) {
        if (path.Count < 2 || radius <= 0f || !HasFilm) { return 0f; }
        Array.Clear(_change);
        var moved = 0f;
        var clearance = radius + 2f / Size;

        void Deposit(Vector2 uv, float ml) {
            if (ml <= 0f) { return; }
            if (uv.X < 0f || uv.X > 1f || uv.Y < 0f || uv.Y > 1f) {
                CollectRunoff(uv.Clamp(Vector2.Zero, Vector2.One), ml);
                return;
            }
            // Match Add()'s cell-centred bilinear deposition, but stage the deltas so the source scan only sees
            // pre-existing liquid and never displaces liquid deposited by an earlier source cell.
            var p = (uv * Size - Vector2.One * 0.5f).Clamp(Vector2.Zero, Vector2.One * (Size - 1));
            var x = Math.Min((int)p.X, Size - 2);
            var y = Math.Min((int)p.Y, Size - 2);
            var f = p - new Vector2(x, y);
            _change[y * Size + x] += ml * (1f - f.X) * (1f - f.Y);
            _change[y * Size + x + 1] += ml * f.X * (1f - f.Y);
            _change[(y + 1) * Size + x] += ml * (1f - f.X) * f.Y;
            _change[(y + 1) * Size + x + 1] += ml * f.X * f.Y;
        }

        for (var y = 0; y < Size; y++) {
            for (var x = 0; x < Size; x++) {
                var i = y * Size + x;
                var amount = _ml[i];
                if (amount <= 0f || !_holds[i]) { continue; }
                var uv = Uv(x, y);
                var nearest = Vector2.Zero;
                var tangent = Vector2.Zero;
                var distanceSquared = float.PositiveInfinity;
                for (var segment = 0; segment + 1 < path.Count; segment++) {
                    var start = path[segment];
                    var span = path[segment + 1] - start;
                    var lengthSquared = span.LengthSquared();
                    if (lengthSquared <= 1e-12f) { continue; }
                    var t = Mathf.Clamp((uv - start).Dot(span) / lengthSquared, 0f, 1f);
                    var point = start + span * t;
                    var delta = uv - point;
                    var d2 = delta.LengthSquared();
                    if (d2 >= distanceSquared) { continue; }
                    distanceSquared = d2;
                    nearest = point;
                    tangent = span.Normalized();
                }
                if (distanceSquared > radius * radius || tangent == Vector2.Zero) { continue; }
                var perpendicular = new Vector2(-tangent.Y, tangent.X);
                amount *= share;
                _change[i] -= amount;
                Deposit(nearest + perpendicular * clearance, amount * 0.5f);
                Deposit(nearest - perpendicular * clearance, amount * 0.5f);
                moved += amount;
            }
        }

        if (moved <= 0f) { return 0f; }
        HasFilm = _canFlow = true;
        NeedsPaint = true;
        for (var i = 0; i < _ml.Length; i++) {
            _ml[i] = Mathf.Max(_ml[i] + _change[i], 0f);
        }
        return moved;
    }

    /// <summary>Begin a coalesced refit of only the wet region and its two-cell slope/curvature margin.</summary>
    internal bool BeginGeometryRefit() {
        if (_refitPending || !HasFilm) { return false; }
        Array.Clear(_refitMarked);
        _refitQueue.Clear();
        _refitCursor = 0;
        _refitChanged = false;
        for (var i = 0; i < _ml.Length; i++) {
            if (_ml[i] <= 0f) { continue; }
            var x = i % Size; var y = i / Size;
            for (var dy = -2; dy <= 2; dy++) {
                var ny = y + dy;
                if (ny < 0 || ny >= Size) { continue; }
                for (var dx = -2; dx <= 2; dx++) {
                    var nx = x + dx;
                    if (nx < 0 || nx >= Size) { continue; }
                    var index = ny * Size + nx;
                    if (_refitMarked[index]) { continue; }
                    _refitMarked[index] = true;
                    _refitHeights[index] = _skin[index].Y;
                    _refitQueue.Add(index);
                }
            }
        }
        _refitPending = _refitQueue.Count > 0;
        return _refitPending;
    }

    /// <summary>Sample at most <paramref name="budget"/> current skin heights on this frame. Derived geometry is
    /// rebuilt once, after the batch, so transport never sees a partially updated support surface.</summary>
    internal void ProcessGeometryRefit(int budget) {
        if (!_refitPending) { return; }
        if (!HasFilm) {
            foreach (var i in _refitQueue) { _refitMarked[i] = false; }
            _refitQueue.Clear();
            _refitCursor = 0;
            _refitPending = false;
            _refitChanged = false;
            return;
        }
        var end = Math.Min(_refitCursor + Math.Max(0, budget), _refitQueue.Count);
        for (; _refitCursor < end; _refitCursor++) {
            var i = _refitQueue[_refitCursor];
            var next = _heightAt(Uv(i % Size, i / Size));
            var previous = _skin[i].Y;
            if (float.IsNaN(next) || Mathf.Abs(next - previous) <= 0.00005f) { continue; }
            _refitHeights[i] = next;
            _refitChanged = true;
        }
        if (_refitCursor < _refitQueue.Count) { return; }
        foreach (var i in _refitQueue) {
            _refitMarked[i] = false;
            if (_refitChanged) { _skin[i].Y = _refitHeights[i]; }
        }
        _refitQueue.Clear();
        _refitPending = false;
        if (!_refitChanged) { return; }
        RebuildGeometryCache();
        _geometryRevision++;
        _canFlow |= HasFilm;
        NeedsPaint |= HasFilm;
    }

    private void RebuildGeometryCache() {
        // A projected cell covers more skin on a steep flank. Use its actual surface area for both
        // normal film thickness and pigment, otherwise curved skin visibly multiplies the supplied blood.
        for (var y = 0; y < Size; y++) {
            for (var x = 0; x < Size; x++) {
                var i = y * Size + x;
                float Slope(int dx, int dy, float spacing) {
                    var before = x - dx >= 0 && y - dy >= 0 && _holds[(y - dy) * Size + x - dx] ? i - dy * Size - dx : i;
                    var after = x + dx < Size && y + dy < Size && _holds[(y + dy) * Size + x + dx] ? i + dy * Size + dx : i;
                    var steps = (before == i ? 0 : 1) + (after == i ? 0 : 1);
                    return steps == 0 ? 0f : (_skin[after].Y - _skin[before].Y) * _scale / (spacing * steps);
                }
                var sx = Slope(1, 0, _cell.X); var sy = Slope(0, 1, _cell.Y);
                _normalDepthPerMl[i] = _projectedDepthPerMl / Mathf.Sqrt(1f + sx * sx + sy * sy);
                _normal[i] = new Vector3(-sx, 1f, -sy).Normalized();
            }
        }
        // Gathering belongs on a convex flank, where the film wraps around the model. A broad surface
        // tilted by the patient's pose must still spread rather than being forced into parallel channels.
        for (var y = 0; y < Size; y++) {
            for (var x = 0; x < Size; x++) {
                var i = y * Size + x;
                float Curvature(int dx, int dy) {
                    var before = x - dx >= 0 && y - dy >= 0 && _holds[(y - dy) * Size + x - dx] ? i - dy * Size - dx : i;
                    var after = x + dx < Size && y + dy < Size && _holds[(y + dy) * Size + x + dx] ? i + dy * Size + dx : i;
                    var span = _skin[after] - _skin[before];
                    return span.LengthSquared() > 0f ? (_normal[after] - _normal[before]).Dot(span) / (span.LengthSquared() * _scale) : 0f;
                }
                _curved[i] = Mathf.SmoothStep(8f, 22f, Curvature(1, 0) + Curvature(0, 1));
            }
        }
    }

    internal void Add(Vector2 uv, float ml) {
        if (ml <= 0f) { return; }
        HasFilm = NeedsPaint = _canFlow = true;
        var p = (uv * Size - Vector2.One * 0.5f).Clamp(Vector2.Zero, Vector2.One * (Size - 1));
        var x = Math.Min((int)p.X, Size - 2);
        var y = Math.Min((int)p.Y, Size - 2);
        var f = p - new Vector2(x, y);
        _ml[y * Size + x] += ml * (1f - f.X) * (1f - f.Y);
        _ml[y * Size + x + 1] += ml * f.X * (1f - f.Y);
        _ml[(y + 1) * Size + x] += ml * (1f - f.X) * f.Y;
        _ml[(y + 1) * Size + x + 1] += ml * f.X * f.Y;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal void Step(Basis basis, float delta) {
        if (!NeedsTransport(basis)) { return; }
        if (!_hasBasis || !SameAs(basis) || _basisGeometryRevision != _geometryRevision) {
            _wetCells.Clear(); _blurCells.Clear();
            Array.Clear(_blurNeeded);
            for (var i = 0; i < _ml.Length; i++) {
                _height[i] = (basis * _skin[i]).Y;
                _wettingHead[i] = 0f;
                if (!_holds[i] || _curved[i] <= 0f) { continue; }
                var up = (basis * _normal[i]).Normalized().Y;
                _wettingHead[i] = ContactHead * _curved[i] * Mathf.SmoothStep(0.5f, 0.9f, 1f - up * up);
                if (_wettingHead[i] <= 0f) { continue; }
                _wetCells.Add(i);
                for (var dy = -2; dy <= 2; dy++) { _blurNeeded[Math.Clamp(i / Size + dy, 0, Size - 1) * Size + i % Size] = true; }
            }
            for (var i = 0; i < _blurNeeded.Length; i++) { if (_blurNeeded[i]) { _blurCells.Add(i); } }
            _basis = basis; _hasBasis = true;
            _basisGeometryRevision = _geometryRevision;
        }
        for (var i = 0; i < _ml.Length; i++) {
            _thickness[i] = _ml[i] * _normalDepthPerMl[i];
            _head[i] = _height[i] + _ml[i] * _projectedDepthPerMl;
        }
        // The five-by-five footprint is separable. Cache its required cells with the pose, so flat
        // areas never pay for cohesion and the hot path allocates nothing.
        foreach (var i in _blurCells) {
            var x = i % Size; var row = i - x;
            _wetRow[i] = (_thickness[row + Math.Max(x - 2, 0)] + 4f * _thickness[row + Math.Max(x - 1, 0)]
                + 6f * _thickness[i] + 4f * _thickness[row + Math.Min(x + 1, Size - 1)]
                + _thickness[row + Math.Min(x + 2, Size - 1)]) / 16f;
        }
        foreach (var i in _wetCells) {
            var x = i % Size; var y = i / Size;
            var before2 = Math.Max(y - 2, 0) * Size; var before = Math.Max(y - 1, 0) * Size;
            var after = Math.Min(y + 1, Size - 1) * Size; var after2 = Math.Min(y + 2, Size - 1) * Size;
            var wet = (_wetRow[before2 + x] + 4f * _wetRow[before + x] + 6f * _wetRow[i]
                + 4f * _wetRow[after + x] + _wetRow[after2 + x]) / 16f;
            // Resolve cohesion over neighbouring skin, not individual cells: the latter creates
            // grid-sized holes rather than continuous narrowing rivulets.
            _head[i] -= _wettingHead[i] * wet / (wet + ContactDepth);
        }
        Array.Clear(_change);
        for (var y = 0; y < Size; y++) {
            for (var x = 0; x < Size; x++) {
                var i = y * Size + x;
                if (!_holds[i]) {
                    _change[i] -= _ml[i];
                    CollectRunoff(Uv(x, y), _ml[i]);
                    continue;
                }
                var movable = _ml[i] - ResidueDepth / _normalDepthPerMl[i];
                if (movable <= 0f) { continue; }
                var head = _head[i];
                var cohesion = _height[i] + _ml[i] * _projectedDepthPerMl - head;
                var total = 0f;
                for (var n = 0; n < Neighbors.Length; n++) {
                    var (dx, dy) = Neighbors[n];
                    var nx = x + dx;
                    var ny = y + dy;
                    var inside = nx >= 0 && nx < Size && ny >= 0 && ny < Size;
                    var neighbor = inside ? ny * Size + nx
                        : Math.Clamp(y - dy, 0, Size - 1) * Size + Math.Clamp(x - dx, 0, Size - 1);
                    // Continue the local slope off the boundary instead of inventing a vertical drop there. The skin
                    // beyond the site holds blood as this cell does, so cohesion holds nothing back at the edge: what
                    // stands deeper than the pinning depth runs on onto the body's film.
                    var ground = inside ? _height[neighbor] : 2f * _height[i] - _height[neighbor] - cohesion;
                    var other = inside && _holds[neighbor] ? _head[neighbor] : ground;
                    _weights[n] = Mathf.Max(head - other - Pinning, 0f) / _distances[n];
                    total += _weights[n];
                }
                if (total <= 0f) { continue; }
                // Viscous film mobility scales with thickness squared. A thin advancing rim must slow down
                // instead of travelling as quickly as its deep supply and leaving a uniformly broad sheet.
                var thickness = _thickness[i];
                var mobility = Mathf.Min(1f, thickness * thickness / (MobilityDepth * MobilityDepth));
                var rate = Mathf.Lerp(ThinFlowRate, DeepFlowRate, mobility);
                var outflow = movable * Mathf.Min(0.24f, total * delta * rate * mobility);
                _change[i] -= outflow;
                for (var n = 0; n < Neighbors.Length; n++) {
                    var (dx, dy) = Neighbors[n];
                    var nx = x + dx;
                    var ny = y + dy;
                    var amount = outflow * _weights[n] / total;
                    if (nx >= 0 && nx < Size && ny >= 0 && ny < Size && _holds[ny * Size + nx]) {
                        _change[ny * Size + nx] += amount;
                    } else {
                        CollectRunoff(Uv(x, y), amount);
                    }
                }
            }
        }
        HasFilm = _canFlow = false;
        for (var i = 0; i < _ml.Length; i++) {
            var before = _ml[i];
            _ml[i] = Mathf.Max(before + _change[i], 0f);
            if (_steep[i] && _ml[i] > 0f) {
                if (_steepRunoff[i] <= 0f) { _steepPending.Add(i); }
                _steepRunoff[i] += _ml[i];
                RunoffMl += _ml[i];
                _ml[i] = 0f;
            }
            HasFilm |= _ml[i] > 0f;
            _canFlow |= _ml[i] != before;
        }
        NeedsPaint |= _canFlow;
    }

    private void CollectRunoff(Vector2 uv, float ml) {
        if (ml <= 0f) { return; }
        var bucket = RunoffBucket(uv);
        RunoffMl += ml;
        _runoff[bucket] += ml;
        _runoffUv[bucket] += uv * ml;
    }

    internal static int RunoffBucket(Vector2 uv) {
        var x = Mathf.Min(uv.X, 1f - uv.X);
        var y = Mathf.Min(uv.Y, 1f - uv.Y);
        var side = x < y ? (uv.X < 0.5f ? 0 : 1) : (uv.Y < 0.5f ? 2 : 3);
        var along = side < 2 ? uv.Y : uv.X;
        return side * 8 + Math.Clamp((int)(along * 8), 0, 7);
    }

    /// <summary>Transfers each local outlet in one operation, without quantizing or iterating over drop counts.</summary>
    internal bool TakeRunoff(out Vector2 uv, out float ml) {
        for (var bucket = 0; bucket < _runoff.Length; bucket++) {
            if (_runoff[bucket] <= 0f) { continue; }
            ml = _runoff[bucket];
            uv = _runoffUv[bucket] / ml;
            _runoff[bucket] = 0f;
            _runoffUv[bucket] = Vector2.Zero;
            return true;
        }
        if (_steepPending.Count > 0) {
            var i = _steepPending[^1];
            _steepPending.RemoveAt(_steepPending.Count - 1);
            ml = _steepRunoff[i];
            uv = Uv(i % Size, i / Size);
            _steepRunoff[i] = 0f;
            return true;
        }
        uv = Vector2.Zero; ml = 0f; return false;
    }

    internal void ReconcileWipes(WoundMap map) {
        if (!HasFilm || _wipeVersion == map.BloodRemovalVersion) { return; }
        _wipeVersion = map.BloodRemovalVersion;
        Array.Clear(_reducedCells);
        var anyReduced = false;
        HasFilm = false;
        for (var y = 0; y < Size; y++) {
            for (var x = 0; x < Size; x++) {
                var i = y * Size + x;
                if (_painted[i] > 0f && _ml[i] > 0f) {
                    var now = map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, Uv(x, y));
                    if (now + 0.5f / 255f < _painted[i]) {
                        // The map holds coverage times the contact mask; the painted depth has no mask. Where the
                        // mask hides everything the wipe took all of it.
                        var support = map.BloodSupport(Uv(x, y));
                        var unmasked = support > 0f ? now / support : 0f;
                        var reduction = _paintedDepth[i] > 0f
                            ? Mathf.Clamp(WoundMap.FilmDepthFromCoverage(unmasked) / _paintedDepth[i], 0f, 1f) : 0f;
                        var remains = _ml[i] * reduction;
                        if (remains < _ml[i]) {
                            _reducedCells[i] = true;
                            anyReduced = true;
                        }
                        RemovedMl += _ml[i] - remains;
                        _ml[i] = remains;
                        _painted[i] = now;
                        NeedsPaint = _canFlow = true;
                    }
                }
                HasFilm |= _ml[i] > 0f;
            }
        }
        if (anyReduced) { map.AcknowledgeBloodFilmReduction(_reducedCells, Size); }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal void Paint(WoundMap map) {
        NeedsPaint = false;
        for (var i = 0; i < _ml.Length; i++) {
            _opticalDepth[i] = _ml[i] * _normalDepthPerMl[i] / CoverDepth;
        }
        // Extend only the displayed density into one boundary cell. The cached continuous contact mask clips
        // it back to the body, so the simulation grid cannot imprint teeth into a saturated pool's edge.
        foreach (var (i, neighbors) in _edgeNeighbors) {
            var sum = 0f;
            foreach (var neighbor in neighbors) { sum += _opticalDepth[neighbor]; }
            _opticalDepth[i] = sum / neighbors.Length;
        }
        map.PaintBloodFilmDepth(_opticalDepth, Size);
        for (var y = 0; y < Size; y++) {
            for (var x = 0; x < Size; x++) {
                var i = y * Size + x;
                if (_ml[i] > 0f) {
                    _painted[i] = map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, Uv(x, y));
                    _paintedDepth[i] = _ml[i] * _normalDepthPerMl[i] / CoverDepth;
                } else {
                    _painted[i] = _paintedDepth[i] = 0f;
                }
            }
        }
    }
}
