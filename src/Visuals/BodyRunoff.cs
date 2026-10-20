using System.Threading.Tasks;

namespace Scalpel.Visuals;

/// <summary>Conserved milliliters travel down attached, tapering model streaks before reaching the bed or a drop.
/// The rendered front cannot outrun either the supplied volume or viscous travel, and release waits for its upload.</summary>
internal sealed class BodyRunoff(BodyLiquidSurface model, Node3D site, BedLiquidSurface bed, MeshInstance3D mesh) {
    private sealed class Outlet(int seed) {
        internal readonly LiquidPulse Pulse = new(seed);
        internal BodyLiquidSurface.Path? Path;
        internal Vector3 Source;
        internal float Ml;
        internal float TargetMl = float.NaN;
        internal float AffordableDistance;
        internal float Distance;
        internal float Drawn;
        internal float Ready;
    }
    private readonly Outlet[] _outlets = Enumerable.Range(0, 32).Select(i => new Outlet(i + 301)).ToArray();
    private readonly (BodyLiquidSurface.Path? Path, float Distance)[] _snapshot = new (BodyLiquidSurface.Path?, float)[32];
    private readonly LiquidSurface _surface = new();
    private Task<BodyLiquidSurface.Path>? _tracing;
    private int _traceIndex;
    private Task? _drawing;
    private float _clock;
    internal float HeldMl => _outlets.Sum(o => o.Ml + o.Pulse.Pending + o.Ready);
    internal float VisibleMl => _outlets.Sum(o => o.Path?.VolumeAt(o.Drawn) ?? 0f);
    internal bool HasReachedEnd => _outlets.Any(o => o.Path is { } p && o.Drawn > 0f && o.Drawn >= p.Length);
    internal BodyLiquidSurface.Path? GrowingPath => _outlets.FirstOrDefault(o => o.Drawn * site.GlobalBasis.Scale.X > 0.015f && o.Path is { } p && o.Drawn < p.Length)?.Path;
    internal bool HasGrowingStreak => GrowingPath is not null;
    internal IEnumerable<BodyLiquidSurface.Path> Paths => _outlets.Where(o => o.Path is not null && o.Drawn > 0f).Select(o => o.Path!);

    internal void Add(Vector2 uv, Vector3 at, float ml) {
        var outlet = _outlets[SurfaceBlood.RunoffBucket(uv)];
        if (outlet.Ml <= 0f && outlet.Path is null) { outlet.Source = at; }
        outlet.Ml += ml;
    }

    internal void Tick(float delta) {
        if (_drawing is { IsCompleted: true }) {
            _drawing.GetAwaiter().GetResult();
            mesh.Mesh = _surface.Upload(mesh.Mesh as ArrayMesh);
            for (var i = 0; i < _outlets.Length; i++) { _outlets[i].Drawn = _snapshot[i].Distance; }
            _drawing = null;
        }
        if (_tracing is { IsCompleted: true }) {
            var outlet = _outlets[_traceIndex];
            outlet.Path = _tracing.GetAwaiter().GetResult();
            _tracing = null;
        }
        for (var i = 0; i < _outlets.Length; i++) {
            var outlet = _outlets[i];
            if (outlet.Ml <= 0f && outlet.Pulse.Pending <= 0f) { continue; }
            if (outlet.Path is null) {
                if (_tracing is null) {
                    _traceIndex = i;
                    var at = outlet.Source; var transform = site.GlobalTransform; var seed = i;
                    var gravity = (transform.Basis.Inverse() * Vector3.Down).Normalized();
                    _tracing = Task.Run(() => model.Trace(at, gravity, seed, transform, bed));
                }
                continue;
            }
            var path = outlet.Path;
            if (outlet.TargetMl != outlet.Ml) {
                outlet.AffordableDistance = path.DistanceFor(outlet.Ml);
                outlet.TargetMl = outlet.Ml;
            }
            if (outlet.Distance < outlet.AffordableDistance) {
                outlet.Distance = Mathf.Min(outlet.AffordableDistance, outlet.Distance + delta * (0.03f + 0.025f * (0.5f + 0.5f * Mathf.Sin(i * 2.17f))) / site.GlobalBasis.Scale.X);
            }
            if (outlet.Distance < path.Length || outlet.Drawn < path.Length) { continue; }
            var excess = Mathf.Max(0f, outlet.Ml - path.CapacityMl);
            outlet.Pulse.Add(excess);
            outlet.Ml -= excess;
            var released = outlet.Pulse.Advance(delta, outlet.Ready <= 0f);
            outlet.Ready += released;
        }
        _clock += delta;
        if (_clock < 1f / 15f || _drawing is not null) { return; }
        _clock %= 1f / 15f;
        if (!_outlets.Any(o => o.Distance != o.Drawn)) { return; }
        for (var i = 0; i < _outlets.Length; i++) { _snapshot[i] = (_outlets[i].Path, _outlets[i].Distance); }
        _drawing = Task.Run(() => {
            _surface.Clear();
            foreach (var (path, distance) in _snapshot) { path?.Draw(_surface, distance); }
            _surface.PrepareUpload();
        });
    }

    internal bool TryRelease(out Vector3 at, out float ml, out bool onBed) {
        foreach (var outlet in _outlets) {
            if (outlet.Ready <= 0f) { continue; }
            ml = outlet.Ready; outlet.Ready = 0f;
            at = site.ToGlobal(outlet.Path is { Samples.Length: > 0 } path ? path.End : outlet.Source);
            onBed = outlet.Path?.OnBed ?? false;
            return true;
        }
        at = Vector3.Zero; ml = 0f; onBed = false; return false;
    }
}
