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
    }

    internal float RunoffMl { get; private set; }
    internal float RemovedMl { get; private set; }
    internal Vector2 DrainUv { get; private set; }
    internal float HeldMl => _ml.Sum();
    internal float At(int x, int y) => _ml[y * Size + x];
    private static Vector2 Uv(int x, int y) => new Vector2(x + 0.5f, y + 0.5f) / Size;

    internal void Add(Vector2 uv, float ml) {
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
        Array.Clear(_change);
        for (var i = 0; i < _ml.Length; i++) {
            _height[i] = (basis * _skin[i]).Y;
        }
        for (var y = 0; y < Size; y++) {
            for (var x = 0; x < Size; x++) {
                var i = y * Size + x;
                if (!_holds[i]) {
                    _change[i] -= _ml[i];
                    RunoffMl += _ml[i];
                    if (_ml[i] > 0f) { DrainUv = Uv(x, y); }
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
                        RunoffMl += amount;
                        if (amount > 0f) { DrainUv = Uv(x, y); }
                    }
                }
            }
        }
        for (var i = 0; i < _ml.Length; i++) {
            _ml[i] = Mathf.Max(_ml[i] + _change[i], 0f);
        }
    }

    internal void ReconcileWipes(WoundMap map) {
        for (var y = 0; y < Size; y++) {
            for (var x = 0; x < Size; x++) {
                var i = y * Size + x;
                if (_painted[i] <= 0f || _ml[i] <= 0f) { continue; }
                var now = map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, Uv(x, y));
                if (now + 0.5f / 255f < _painted[i]) {
                    var remains = _ml[i] * now / _painted[i];
                    RemovedMl += _ml[i] - remains;
                    _ml[i] = remains;
                    _painted[i] = now;
                }
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    internal void Paint(WoundMap map) {
        for (var i = 0; i < _ml.Length; i++) {
            _coverage[i] = 1f - Mathf.Exp(-_ml[i] * _depthPerMl / 0.0004f);
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
