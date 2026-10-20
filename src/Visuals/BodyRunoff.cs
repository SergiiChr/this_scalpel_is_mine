using System.Threading.Tasks;

namespace Scalpel.Visuals;

/// <summary>Conserved milliliters travel down attached, tapering model streaks before reaching the bed or a drop.
/// Deposited coating stays on the model; a changed source or pose gets its own downhill route.</summary>
internal sealed class BodyRunoff(BodyLiquidSurface model, Node3D site, BedLiquidSurface bed, MeshInstance3D mesh) {
    private sealed class Outlet(int bucket, Vector3 source, Basis basis, int seed) {
        internal readonly int Bucket = bucket;
        internal readonly Vector3 Source = source;
        internal readonly Basis Basis = basis;
        internal readonly LiquidPulse Pulse = new(seed);
        internal BodyLiquidSurface.Path? Path;
        internal float Ml;
        internal float TargetMl = float.NaN;
        internal float AffordableDistance;
        internal float Distance;
        internal float Drawn;
        internal float Ready;
    }
    private readonly List<Outlet> _outlets = [];
    private (Outlet Outlet, BodyLiquidSurface.Path? Path, float Distance)[] _snapshot = [];
    private readonly LiquidSurface _surface = new(withUv: true);
    private Task<BodyLiquidSurface.Path>? _tracing;
    private Outlet? _traceOutlet;
    private int _traceRevision;
    private Task? _drawing;
    private int _drawRevision;
    private Basis _basis;
    private bool _hasBasis;
    private int _revision;
    private bool _redraw;
    private float _clock;
    internal float HeldMl => _outlets.Sum(o => o.Ml + o.Pulse.Pending + o.Ready);
    internal float VisibleMl => _outlets.Sum(o => o.Path?.VolumeAt(o.Drawn) ?? 0f);
    internal bool HasReachedEnd => _outlets.Any(o => o.Basis.IsEqualApprox(_basis) && o.Path is { } p && o.Drawn > 0f && o.Drawn >= p.Length);
    internal BodyLiquidSurface.Path? GrowingPath => _outlets.FirstOrDefault(o => o.Basis.IsEqualApprox(_basis)
        && o.Drawn * _basis.Scale.X > 0.015f && o.Path is { } p && o.Drawn < p.Length)?.Path;
    internal bool HasGrowingStreak => GrowingPath is not null;
    internal bool TracePending => _tracing is not null;
    internal bool DrawPending => _drawing is not null;
    internal IEnumerable<BodyLiquidSurface.Path> ActivePaths => _outlets
        .Where(outlet => outlet.Basis.IsEqualApprox(_basis) && outlet.Path is not null).Select(outlet => outlet.Path!);
    internal IEnumerable<BodyLiquidSurface.Path> Paths => _outlets.Where(o => o.Path is not null && o.Drawn > 0f).Select(o => o.Path!);

    internal void Add(Vector2 uv, Vector3 at, float ml) {
        if (ml <= 0f) { return; }
        UpdatePose();
        Supply(SurfaceBlood.RunoffBucket(uv), at, ml);
    }

    private void Supply(int bucket, Vector3 source, float ml) {
        // The bucket only narrows the search. It is not a release position: separated sources must not
        // teleport liquid to an older streak. A few millimetres tolerate reconstruction noise at one inlet.
        var reach = 0.004f / site.GlobalBasis.Scale.X;
        Outlet? outlet = null;
        foreach (var candidate in _outlets) {
            if (candidate.Bucket != bucket || !candidate.Basis.IsEqualApprox(_basis)
                || candidate.Source.DistanceSquaredTo(source) > reach * reach) { continue; }
            outlet = candidate;
            break;
        }
        if (outlet is null) {
            outlet = new Outlet(bucket, source, _basis, bucket + 301);
            _outlets.Add(outlet);
        }
        outlet.Ml += ml;
    }

    private void UpdatePose() {
        var basis = site.GlobalBasis;
        if (_hasBasis && _basis.IsEqualApprox(basis)) { return; }
        _basis = basis;
        if (!_hasBasis) { _hasBasis = true; return; }
        _revision++;
        _redraw = true;
        // Freeze only the coating that has actually been uploaded. Uncommitted liquid, including pending
        // drops, goes back to its own inlet and is traced under the new gravity. Returning to an earlier pose
        // reuses its existing coating, rather than adding duplicate meshes or inventing its capacity again.
        var count = _outlets.Count;
        for (var i = 0; i < count; i++) {
            var outlet = _outlets[i];
            if (outlet.Basis.IsEqualApprox(basis)) { continue; }
            var retained = outlet.Path?.VolumeAt(outlet.Drawn) ?? 0f;
            var moving = Mathf.Max(0f, outlet.Ml - retained) + outlet.Pulse.Drain() + outlet.Ready;
            outlet.Ml = retained;
            outlet.Distance = outlet.Drawn;
            outlet.TargetMl = float.NaN;
            outlet.Ready = 0f;
            if (moving > 0f) { Supply(outlet.Bucket, outlet.Source, moving); }
        }
    }

    internal void Tick(float delta) {
        UpdatePose();
        if (WorkerTask.TryTake(ref _drawing, out var drawing)) {
            // Clear the handle before observing a fault. A failed worker must not throw again every frame.
            _redraw = true;
            drawing.GetAwaiter().GetResult();
            if (_drawRevision == _revision) {
                mesh.Mesh = _surface.Upload(mesh.Mesh as ArrayMesh);
                foreach (var (outlet, _, distance) in _snapshot) { outlet.Drawn = distance; }
                _redraw = false;
            }
        }
        if (WorkerTask.TryTake(ref _tracing, out var tracing)) {
            var path = tracing.GetAwaiter().GetResult();
            if (_traceRevision == _revision) { _traceOutlet!.Path = path; }
        }
        for (var i = 0; i < _outlets.Count; i++) {
            var outlet = _outlets[i];
            if (!outlet.Basis.IsEqualApprox(_basis) || (outlet.Ml <= 0f && outlet.Pulse.Pending <= 0f)) { continue; }
            if (outlet.Path is null) {
                if (_tracing is null) {
                    _traceOutlet = outlet;
                    _traceRevision = _revision;
                    var at = outlet.Source; var transform = site.GlobalTransform; var seed = outlet.Bucket;
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
                outlet.Distance = Mathf.Min(outlet.AffordableDistance, outlet.Distance + delta * (0.03f + 0.025f * (0.5f + 0.5f * Mathf.Sin(outlet.Bucket * 2.17f))) / site.GlobalBasis.Scale.X);
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
        if (!_redraw && !_outlets.Any(o => o.Distance != o.Drawn)) { return; }
        if (_snapshot.Length != _outlets.Count) { _snapshot = new (Outlet, BodyLiquidSurface.Path?, float)[_outlets.Count]; }
        for (var i = 0; i < _outlets.Count; i++) { _snapshot[i] = (_outlets[i], _outlets[i].Path, _outlets[i].Distance); }
        _drawRevision = _revision;
        _drawing = Task.Run(() => {
            _surface.Clear();
            foreach (var (_, path, distance) in _snapshot) { path?.Draw(_surface, distance); }
            _surface.PrepareUpload();
        });
    }

    internal bool TryRelease(out Vector3 at, out float ml, out bool onBed) {
        UpdatePose();
        foreach (var outlet in _outlets) {
            if (!outlet.Basis.IsEqualApprox(_basis) || outlet.Ready <= 0f) { continue; }
            ml = outlet.Ready; outlet.Ready = 0f;
            at = site.ToGlobal(outlet.Path is { Samples.Length: > 0 } path ? path.End : outlet.Source);
            // Contact is a current world-space fact, including breathing/translation since the trace.
            onBed = bed.Landing(at + Vector3.Up * 0.001f, at - Vector3.Up * 0.002f) is not null;
            return true;
        }
        at = Vector3.Zero; ml = 0f; onBed = false; return false;
    }
}
