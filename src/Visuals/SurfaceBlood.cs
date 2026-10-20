namespace Scalpel.Visuals;

/// <summary>A shallow viscous film: cells hold milliliters, and exchange them according to skin height plus liquid
/// depth. Shared pressure spreads puddles and joins streams; no independently drawn paths or artificial wandering.</summary>
internal sealed class SurfaceBlood {
    internal const int Size = 64;
    private const float Pinning = 0.00008f;
    private const float ResidueDepth = 0.00004f;
    private static readonly (int X, int Y)[] Neighbors = [
        (-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, -1), (-1, 1), (1, 1),
    ];
    private readonly float[] _ml = new float[Size * Size];
    private readonly float[] _change = new float[Size * Size];
    private readonly float[] _height = new float[Size * Size];
    private readonly float[] _coverage = new float[Size * Size];
    private readonly float[] _painted = new float[Size * Size];
    private readonly Vector3[] _skin = new Vector3[Size * Size];
    private readonly bool[] _holds = new bool[Size * Size];
    private readonly float[] _weights = new float[8];
    private readonly float[] _distances = new float[8];
    private readonly float[] _runoff = new float[32];
    private int _drainCursor;
    private readonly int[] _runoffCursor = new int[4];
    private readonly Vector2[] _runoffUv = new Vector2[32];
    internal bool HasFilm { get; private set; }
    internal bool NeedsPaint { get; private set; }
    private bool _canFlow;
    private bool _hasBasis;
    private Basis _basis;
    private int _wipeVersion = -1;
    private readonly List<(int Index, int[] Neighbors)> _edgeNeighbors = [];
    internal bool NeedsTransport(Basis basis) => HasFilm && (_canFlow || !_hasBasis || basis != _basis);
    private readonly float _depthPerMl;
    private readonly Vector2 _cell;

    internal SurfaceBlood(Vector2 siteSize, Func<Vector2, float> height, Func<Vector2, bool>? holds = null) {
        _cell = siteSize / Size;
        _depthPerMl = 0.000001f / (_cell.X * _cell.Y);
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
        if (!_hasBasis || basis != _basis) {
            for (var i = 0; i < _ml.Length; i++) { _height[i] = (basis * _skin[i]).Y; }
            _basis = basis; _hasBasis = true;
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
                var movable = _ml[i] - ResidueDepth / _depthPerMl;
                if (movable <= 0f) { continue; }
                var head = _height[i] + _ml[i] * _depthPerMl;
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
                    var other = ground + (inside && _holds[neighbor] ? _ml[neighbor] * _depthPerMl : 0f);
                    _weights[n] = Mathf.Max(head - other - Pinning, 0f) / _distances[n];
                    total += _weights[n];
                }
                if (total <= 0f) { continue; }
                var outflow = movable * Mathf.Min(0.24f, total * delta * 1.5f);
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
        var x = Mathf.Min(uv.X, 1f - uv.X);
        var y = Mathf.Min(uv.Y, 1f - uv.Y);
        var side = x < y ? (uv.X < 0.5f ? 0 : 1) : (uv.Y < 0.5f ? 2 : 3);
        var along = side < 2 ? uv.Y : uv.X;
        var bucket = side * 8 + Math.Clamp((int)(along * 8), 0, 7);
        RunoffMl += ml;
        _runoff[bucket] += ml;
        _runoffUv[bucket] += uv * ml;
    }

    /// <summary>Each side retains its own queued volume and release position, including while drop capacity is full.</summary>
    internal bool TryRunoff(float ml, out Vector2 uv) {
        for (var n = 0; n < 4; n++) {
            var side = (_drainCursor + n) % 4;
            for (var k = 0; k < 8; k++) {
                var along = (_runoffCursor[side] + k) % 8;
                var bucket = side * 8 + along;
                if (_runoff[bucket] < ml) { continue; }
                uv = _runoffUv[bucket] / _runoff[bucket];
                _runoff[bucket] -= ml;
                _runoffUv[bucket] = uv * _runoff[bucket];
                _runoffCursor[side] = (along + 1) % 8;
                _drainCursor = (side + 1) % 4;
                return true;
            }
        }
        uv = Vector2.Zero;
        return false;
    }

    internal void ReconcileWipes(WoundMap map) {
        if (!HasFilm || _wipeVersion == map.BloodRemovalVersion) { return; }
        _wipeVersion = map.BloodRemovalVersion;
        HasFilm = false;
        for (var y = 0; y < Size; y++) {
            for (var x = 0; x < Size; x++) {
                var i = y * Size + x;
                if (_painted[i] > 0f && _ml[i] > 0f) {
                    var now = map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, Uv(x, y));
                    if (now + 0.5f / 255f < _painted[i]) {
                        var remains = _ml[i] * now / _painted[i];
                        RemovedMl += _ml[i] - remains;
                        _ml[i] = remains;
                        _painted[i] = now;
                        NeedsPaint = _canFlow = true;
                    }
                }
                HasFilm |= _ml[i] > 0f;
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal void Paint(WoundMap map) {
        NeedsPaint = false;
        for (var i = 0; i < _ml.Length; i++) {
            _coverage[i] = 1f - Mathf.Exp(-_ml[i] * _depthPerMl / 0.0004f);
        }
        // Extend only the displayed density into one boundary cell. The cached continuous contact mask clips
        // it back to the body, so the simulation grid cannot imprint teeth into a saturated pool's edge.
        foreach (var (i, neighbors) in _edgeNeighbors) {
            var sum = 0f;
            foreach (var neighbor in neighbors) { sum += _coverage[neighbor]; }
            _coverage[i] = sum / neighbors.Length;
        }
        map.PaintBloodFilm(_coverage, Size);
        for (var y = 0; y < Size; y++) {
            for (var x = 0; x < Size; x++) {
                var i = y * Size + x;
                _painted[i] = _ml[i] > 0f ? map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, Uv(x, y)) : 0f;
            }
        }
    }
}
