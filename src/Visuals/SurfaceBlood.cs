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
    internal bool NeedsTransport(Basis basis) => HasFilm && (_canFlow || !_hasBasis || basis != _basis);
    private readonly float[] _normalDepthPerMl = new float[Size * Size];
    private readonly float _projectedDepthPerMl;
    private readonly Vector2 _cell;
    private readonly float _scale;
    private readonly Func<Vector2, float> _heightAt;

    internal SurfaceBlood(Vector2 siteSize, Func<Vector2, float> height, Func<Vector2, bool>? holds = null, float scale = 1f) {
        _heightAt = height;
        _scale = scale;
        _cell = siteSize * scale / Size;
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
    internal float QueuedRunoffMl => _runoff.Sum();
    internal float HeldMl => _ml.Sum();
    internal float At(int x, int y) => _ml[y * Size + x];
    private static Vector2 Uv(int x, int y) => new Vector2(x + 0.5f, y + 0.5f) / Size;

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
        if (!_hasBasis || basis != _basis || _basisGeometryRevision != _geometryRevision) {
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
                var total = 0f;
                for (var n = 0; n < Neighbors.Length; n++) {
                    var (dx, dy) = Neighbors[n];
                    var nx = x + dx;
                    var ny = y + dy;
                    var inside = nx >= 0 && nx < Size && ny >= 0 && ny < Size;
                    var neighbor = inside ? ny * Size + nx
                        : Math.Clamp(y - dy, 0, Size - 1) * Size + Math.Clamp(x - dx, 0, Size - 1);
                    // Continue the local slope off the boundary instead of inventing a vertical drop there.
                    var ground = inside ? _height[neighbor] : 2f * _height[i] - _height[neighbor];
                    var other = inside && _holds[neighbor] ? _head[neighbor] : ground;
                    _weights[n] = Mathf.Max(head - other - Pinning, 0f) / _distances[n];
                    total += _weights[n];
                }
                if (total <= 0f) { continue; }
                // Viscous film mobility scales with thickness squared. A thin advancing rim must slow down
                // instead of travelling as quickly as its deep supply and leaving a uniformly broad sheet.
                var thickness = _thickness[i];
                var mobility = Mathf.Min(1f, thickness * thickness / (MobilityDepth * MobilityDepth));
                var outflow = movable * Mathf.Min(0.24f, total * delta * 1.5f * mobility);
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
                        // The map holds coverage times the contact mask; the painted depth has no mask.
                        var unmasked = now / map.BloodSupport(Uv(x, y));
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
