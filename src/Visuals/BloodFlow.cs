using System.Threading;
using System.Threading.Tasks;

namespace Scalpel.Visuals;

/// <summary>A bleeding wound: where (site uv), how fast (ml/s) and how deep under the skin (meters). Depth 0 bleeds onto
/// the skin; deeper bleeds into the cavity. <paramref name="Point"/>: it bleeds onto the skin from a single hole (a
/// puncture, a bullet hole), which shows a spot there. A cut along a line shows none on the skin: split by the cut, it
/// would lie across both lips.</summary>
public readonly record struct BleedSource(Vector2 Uv, float Rate, float Depth = 0f, bool Point = false) {
    public bool Inside => Depth > 0f;

    /// <summary>Flat for the network, five floats a source: uv, rate, depth, point.</summary>
    public static float[] Pack(List<BleedSource> sources) {
        var flat = new float[sources.Count * 5];
        for (var i = 0; i < sources.Count; i++) {
            var (uv, rate, depth, point) = sources[i];
            flat[i * 5] = uv.X;
            flat[i * 5 + 1] = uv.Y;
            flat[i * 5 + 2] = rate;
            flat[i * 5 + 3] = depth;
            flat[i * 5 + 4] = point ? 1f : 0f;
        }
        return flat;
    }

    /// <summary>Replaces <paramref name="into"/> with what <see cref="Pack"/> wrote.</summary>
    public static void Unpack(float[] flat, List<BleedSource> into) {
        into.Clear();
        for (var i = 0; i + 5 <= flat.Length; i += 5) {
            into.Add(new BleedSource(new Vector2(flat[i], flat[i + 1]), flat[i + 2], flat[i + 3], flat[i + 4] > 0f));
        }
    }
}

/// <summary>Blood held in a cut that doesn't reach the cavity: where it bleeds (site uv), how deep under the skin the first
/// layer it hasn't cut through lies (meters), which the blood rests on, and how full it is (0..1).</summary>
public readonly record struct CutPool(Vector2 Uv, float Floor, float Fill) {
    /// <summary>Flat for the network, four floats a pool: uv, floor, fill.</summary>
    public static float[] Pack(List<CutPool> pools) {
        var flat = new float[pools.Count * 4];
        for (var i = 0; i < pools.Count; i++) {
            var (uv, floor, fill) = pools[i];
            flat[i * 4] = uv.X;
            flat[i * 4 + 1] = uv.Y;
            flat[i * 4 + 2] = floor;
            flat[i * 4 + 3] = fill;
        }
        return flat;
    }

    /// <summary>Replaces <paramref name="into"/> with what <see cref="Pack"/> wrote.</summary>
    public static void Unpack(float[] flat, List<CutPool> into) {
        into.Clear();
        for (var i = 0; i + 4 <= flat.Length; i += 4) {
            into.Add(new CutPool(new Vector2(flat[i], flat[i + 1]), flat[i + 2], flat[i + 3]));
        }
    }
}

/// <summary>
/// Blood as a fluid, on every peer. Bleeding wounds (sources, synced by the host) well up into a puddle that grows
/// with the blood lost. A volume grid spreads the film across the skin and carries it downhill, leaving a thin stain
/// in the fluid map. Where the film runs off the body it drips: droplets fall to the table or the floor and collect
/// into pools that grow. Strong bleeds also spurt droplets into the air.
/// Every bleed shows its source as a spot a little darker than blood: on the skin where it bleeds onto it, on the cut's
/// wall at its depth where it bleeds inside the opening (never on the skin over it), its blood running down the wall
/// from it. Blood pooled over it in the opening moves it up to the pool's surface, so its top shows, until the pool
/// brims over the lips. A needle's puncture of the skin shows the same spot as a bleed onto it.
/// Spots are painted by uv, so they ride the tissue wherever it's pulled.
/// Bleeding under skin that isn't cut open spreads a bruise over it.
/// Purely visual and local: each peer runs its own, so the stains differ a little between players, which is fine.
/// </summary>
public partial class BloodFlow : Node3D {
    /// <summary>Blood thrown up right in front of the local surgeon's eyes hits their view (see Hud), amount 0..1.
    /// </summary>
    [Signal] public delegate void SplashedEventHandler(float amount);

    private const int MaxDrops = 96;
    private const int RinsePoolSlots = 64;
    private const float DropRadius = 0.0035f;
    private float _dropVolumeScale;
    private float _unitDropMl;
    internal float DropShapeMl { get; private set; }
    /// <summary>Bleeding faster than this (ml/s) spurts.</summary>
    private const float SpurtRate = 3f;
    /// <summary>Reference detached drop volume in milliliters.</summary>
    internal const float DropMl = 0.15f;
    internal const float FloorY = 0.004f;
    internal const float TableTop = Room.TableHeight + 0.001f;
    /// <summary>Blood on the skin around the site: a thin film that runs off the body's sides.</summary>
    private static readonly SurfaceFilm.Settings BodyFilmSettings = new(0.007f, 0.00015f, 0.0003f, 0.03f);
    /// <summary>One physical reconstruction radius for skin blood, wide enough to bridge the coarser body samples.
    /// Changing the radius with the source grid imprints its resolution boundary onto the shared field.</summary>
    private static readonly float LayerBlendRadius = BodyFilmSettings.Spacing * 1.6f;
    /// <summary>Blood on the mattress and frame: a thin soaked layer that runs over the edges.</summary>
    private static readonly SurfaceFilm.Settings BedFilmSettings = new(0.01f, 0.0004f, 0.0005f);
    /// <summary>Blood on the floor: puddles about a millimeter and a half deep.</summary>
    private static readonly SurfaceFilm.Settings FloorFilmSettings = new(0.015f, 0.0015f, 0.0008f);
    /// <summary>How far (m) round the table the floor's film reaches.</summary>
    private const float FloorFilmMargin = 0.8f;
    private const float PuddlePaintInterval = 0.2f;
    /// <summary>Blood thrown up closer than this (m) to the camera, while looking at it, can land on the view.</summary>
    private const float SplashReach = 0.75f;
    /// <summary>Radius (m) of a bleed's spot, and how much it grows per sqrt(ml/s): the slightest bleed still shows.
    /// </summary>
    private const float SpotMin = 0.0012f;
    private const float SpotGrowth = 0.0015f;
    /// <summary>Radius (m) of the spot a needle leaves where it went into the skin.</summary>
    private const float PunctureRadius = 0.003f;
    /// <summary>How far (m) a pool in a cut keeps off its floor, and how high it stands.</summary>
    private const float CutPoolLift = 0.0003f;
    /// <summary>How deep under the skin a full pool in a cut lies: this share of its floor's depth, at least
    /// CutPoolFull (m).</summary>
    private const float CutPoolBrim = 0.3f;
    private const float CutPoolFull = 0.0006f;
    /// <summary>Rebuild a wound liquid when its sampled opening contour or supporting skin moves at least this far.</summary>
    private const float WoundLiquidGeometryTolerance = 0.0001f;
    /// <summary>Bruise radius (uv) over a bleed under closed skin, and how much it grows per sqrt(ml).</summary>
    private const float BruiseStart = 0.02f;
    private const float BruiseGrowth = 0.01f;
    private const float BruiseMax = 0.1f;

    internal sealed class Drop(Vector3 position, Vector3 velocity, float ml, Vector3 shape, float twist) {
        public Vector3 Position = position;
        public Vector3 Velocity = velocity;
        public float Ml = ml;
        public Vector3 Shape = shape;
        public readonly float Twist = twist;
    }

    private static readonly PaintOp[] WipeSpot =
    [
        new(WoundMap.SkinSpot, 1f, WoundMap.Mode.Sub), new(WoundMap.Spot, 1f, WoundMap.Mode.Sub),
        new(WoundMap.SpotRadius, 1f, WoundMap.Mode.Sub), new(WoundMap.SpotDepth, 1f, WoundMap.Mode.Sub),
    ];

    private SurfaceBlood _surface = null!;
    // Built on workers while the scene loads: nothing lands on a film for a while, and the first that does waits for it.
    private Task<SurfaceFilm> _bodyFilmBuild = null!;
    private Task<SurfaceFilm> _bedFilmBuild = null!;
    private Task<SurfaceFilm> _floorFilmBuild = null!;
    private MeshInstance3D _skinMesh = null!;
    private Skeleton3D? _skeleton;
    private int[] _bindBones = [];
    private BedLiquidSurface _bed = null!;
    /// <summary>Blood on the skin round the site, in the skin mesh's space.</summary>
    internal SurfaceFilm BodyFilm => _bodyFilmBuild.Result;
    /// <summary>Blood on the mattress and frame, in world space.</summary>
    internal SurfaceFilm BedFilm => _bedFilmBuild.Result;
    /// <summary>Blood on the floor, in world space.</summary>
    internal SurfaceFilm FloorFilm => _floorFilmBuild.Result;
    /// <summary>Done once every film is built: blood that reaches a film before then waits for it.</summary>
    internal Task FilmsBuilt => Task.WhenAll(_bodyFilmBuild, _bedFilmBuild, _floorFilmBuild);
    /// <summary>Builds a film on a thread of its own. Seconds long, a build on the shared pool would hold up the short
    /// jobs the game waits for each frame (the tissue's step, the hands' grip fitting) and stall it.</summary>
    private static Task<SurfaceFilm> Build(Func<SurfaceFilm> build) {
        var task = Task.Factory.StartNew(build, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        // Said once: a film that failed to build is left out (Built) rather than asked for every frame.
        task.ContinueWith(failed => GD.PushError($"Blood film build failed: {failed.Exception?.InnerException}"),
            TaskContinuationOptions.OnlyOnFaulted);
        return task;
    }

    /// <summary>The film <paramref name="build"/> made, or null while it's still building or when it failed.</summary>
    private static SurfaceFilm? Built(Task<SurfaceFilm> build) => build.IsCompletedSuccessfully ? build.Result : null;

    private static float HeldOn(Task<SurfaceFilm> build) => Built(build)?.HeldMl ?? 0f;
    internal float SurfaceSuppliedMl { get; private set; }
    internal float SurfaceAccountedMl => _surface.HeldMl + _surface.QueuedRunoffMl + _surface.RemovedMl
        + HeldOn(_bodyFilmBuild) + GroundMl;
    /// <summary>Blood that has left the patient: falling, and on the bed and floor.</summary>
    internal float GroundMl => _drops.Sum(drop => drop.Ml) + HeldOn(_bedFilmBuild) + HeldOn(_floorFilmBuild);
    /// <summary>Whether the site's film keeps blood on the skin at uv, <paramref name="height"/> over the site plane
    /// (its top when omitted), rather than handing it to the body's film.</summary>
    internal bool SiteFilmCarries(Vector2 uv, float height = float.PositiveInfinity) => _surface.Carries(uv, height);

    /// <summary>Physically squeezes existing surface film away from a newly closed seam.</summary>
    internal float DisplaceSurfaceFilm(Vector2[] path, float radius, float share) =>
        _body is null ? 0f : _surface.DisplaceStroke(path, radius, share);
    private float _surfaceClock;
    private float _surfaceGeometryClock;
    private int _surfaceGeometrySteps;
    private int _surfaceGeometryTopology;
    private bool _surfaceNeedsPaint;
    private int _surfaceSteps;
    // Cut pools and irrigation pools take turns rebuilding when a pull changes both (see PoolInCuts).
    private bool _rinseFirst;
    private readonly List<Drop> _drops = [];
    internal IReadOnlyList<Drop> Drops => _drops;
    private readonly StandardMaterial3D _poolMaterial = Materials.BloodPool();
    private readonly RandomNumberGenerator _dripRng = new() { Seed = 83 };
    private float _cutClock;
    /// <summary>Blood (ml) collected under closed skin, keyed by source UV.</summary>
    private readonly Dictionary<Vector2I, float> _bruised = [];
    private readonly RandomNumberGenerator _rng = new() { Seed = 7 };
    private float _puddleTimer;
    /// <summary>This frame paints puddles and bruises.</summary>
    private bool _paintPuddles;
    private MultiMeshInstance3D _dropMesh = null!;
    private MeshInstance3D _cutPoolMesh = null!;
    private readonly List<CutPool> _drawnCuts = [];
    private readonly List<WoundGeometry> _drawnCutGeometry = [];
    private readonly List<bool> _cutGeometryNeedsCapture = [];
    private readonly LiquidSurface _cutBuffer = new();
    internal int CutPoolMeshRefreshes { get; private set; }
    private sealed class RinsePool(Vector2 uv, MeshInstance3D mesh) {
        public Vector2 Uv = uv;
        public readonly MeshInstance3D Mesh = mesh;
        public readonly LiquidSurface Buffer = new();
        public float Ml;
        public float Blood;
        public float DrawnFill = -1f;
        public float DrawnFloor = -1f;
        public WoundGeometry? DrawnGeometry;
        public int MeshRefreshes;
    }
    private readonly Dictionary<int, RinsePool> _rinses = [];
    private readonly Queue<RinsePool> _availableRinses = [];
    private readonly Dictionary<int, RinsePool> _drainedRinses = [];
    private readonly List<int> _drainedRinseIds = [];
    /// <summary>The spots painted last (uv, radius in uv, depth of their center), wiped before the sources are painted
    /// again.</summary>
    private readonly List<(Vector2 Uv, float Radius, float Center)> _spots = [];
    /// <summary>Where needles went into the skin (site uv), each showing a spot like a bleed onto the skin.</summary>
    private readonly List<Vector2> _punctures = [];
    /// <summary>The blood drawn on the skin, body and site alike, built from both films' samples.</summary>
    internal BodyBloodLayer Layer { get; } = new();
    /// <summary>How often (s) the skin is sampled for the layer while it holds still: the blood on it changes slowly.
    /// </summary>
    private const float LayerInterval = 0.1f;
    /// <summary>How often while the skin moves (a pull, a cut, the body turning), so its blood goes with it within a
    /// frame or two, and for a second after, while it settles. Samples that haven't changed cost no field.</summary>
    private const float MovingLayerInterval = 1f / 30f;
    /// <summary>The most site samples along a side: one per voxel of the layer up to this.</summary>
    private const int MaxSiteSamples = 160;
    private float _layerClock;
    /// <summary>Seconds left of the moving rate since the skin last moved.</summary>
    private float _layerMoving;
    /// <summary>Whether the site's blood or skin changed since the layer last sampled it.</summary>
    private bool _layerDue;
    private int _layerFluidsVersion;
    private Transform3D[] _layerBonePoses = [];
    private int _layerTopology;
    // The worker's input (PrepareLayerInput) and its own scratch (GatherLayerSamples).
    private readonly SkinSnapshot _layerSkin = new();
    private byte[] _layerFluids = [];
    private int _layerFluidsSize;
    private bool _layerSitePainted;
    private Vector2 _layerSiteSize;
    private Transform3D _layerWorldToField, _layerSiteToField, _layerMeshToWorld;
    private SurfaceFilm? _layerFilm;
    private readonly List<SurfaceFilm.FilmSample> _layerFilmSamples = [];
    private BodyLiquidSurface.SkinPose? _layerPose;
    private readonly List<BodyBloodLayer.Sample> _layerSamples = [];
    private readonly Func<IReadOnlyList<BodyBloodLayer.Sample>> _gatherLayer;
    private float[] _siteCoverage = [];
    private PatientBody? _body;

    /// <summary>The bleeding wounds. The patient swaps in a complete list every tick; nothing keeps a tick's own.
    /// </summary>
    public List<BleedSource> Sources { get; internal set; } = [];

    /// <summary>Blood held in cuts that don't reach the cavity, swapped in like <see cref="Sources"/>.</summary>
    public List<CutPool> Pools { get; internal set; } = [];

    /// <summary>Scratch for <see cref="Spots"/>: the uvs bleeding inside, and the sources' indices by rate.</summary>
    private readonly HashSet<Vector2> _insideUvs = [];
    private readonly List<int> _byRate = [];
    private readonly FasterFirst _fasterFirst;

    /// <summary>Source indices, the fastest bleed first, equal rates in their order: the spots are painted in it.
    /// </summary>
    private sealed class FasterFirst(BloodFlow flow) : IComparer<int> {
        public int Compare(int a, int b) {
            var byRate = flow.Sources[b].Rate.CompareTo(flow.Sources[a].Rate);
            return byRate != 0 ? byRate : a.CompareTo(b);
        }
    }

    public BloodFlow() {
        _fasterFirst = new FasterFirst(this);
        _gatherLayer = GatherLayerSamples;
    }

    public void Setup(PatientBody patientBody) {
        _body = patientBody;
        _surface = new SurfaceBlood(patientBody.SiteSize, patientBody.SkinHeight, uv => patientBody.LiquidSupport(uv) >= 0f,
            patientBody.Site.GlobalBasis.Scale.X, patientBody.ModelFaceNormal);
        _surfaceGeometrySteps = patientBody.Tissue.StepsDone;
        _surfaceGeometryTopology = patientBody.Tissue.TopologyVersion;
        patientBody.WoundMap.SetBloodFilmSupport(patientBody.LiquidSupport);
        for (var i = 0; i < RinsePoolSlots; i++) {
            var material = Materials.Stream();
            material.Roughness = 0.22f;
            material.MetallicSpecular = 0.2f;
            var mesh = new MeshInstance3D {
                Name = "IrrigationPoolSlot",
                Mesh = new ArrayMesh(),
                MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false,
            };
            patientBody.Site.AddChild(mesh);
            _availableRinses.Enqueue(new RinsePool(Vector2.Zero, mesh));
        }
        SetupFilms(patientBody);
        var dropShape = LiquidSurface.Drop(out var dropVolume);
        _unitDropMl = dropVolume * 1000000f;
        DropShapeMl = dropVolume * DropRadius * DropRadius * DropRadius * 1000000f;
        _dropVolumeScale = Mathf.Pow(DropMl / DropShapeMl, 1f / 3f);
        _dropMesh = new MultiMeshInstance3D {
            Name = "BloodDrops",
            Multimesh = new MultiMesh {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = dropShape,
                InstanceCount = MaxDrops,
                VisibleInstanceCount = 0,
            },
            MaterialOverride = Materials.BloodPool(),
            TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_dropMesh);
        _cutPoolMesh = new MeshInstance3D {
            Name = "CutPools",
            Mesh = new ArrayMesh(),
            MaterialOverride = _poolMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        patientBody.Site.AddChild(_cutPoolMesh);
    }

    public override void _Process(double delta) {
        if (_body is null || delta <= 0) {
            return;
        }
        Spawn(_body, (float)delta);
        Inside(_body, (float)delta);
        foreach (var rinse in _rinses.Values) {
            var draining = Mathf.Exp(-(float)delta * 0.9f);
            rinse.Ml *= draining;
            rinse.Blood *= draining;
        }
        _drainedRinseIds.Clear();
        _cutClock += (float)delta;
        if (_cutClock >= 0.1f) {
            _cutClock = 0f;
            PoolInCuts(_body);
        }
        if (_paintPuddles) {
            Spots(_body);
        }
        Flow(_body, (float)delta);
        Fall((float)delta);
        UpdateLayer((float)delta);
    }

    private void Spawn(PatientBody body, float delta) {
        _puddleTimer += delta;
        _paintPuddles = _puddleTimer >= PuddlePaintInterval;
        if (_paintPuddles) {
            _puddleTimer = 0f;
        }
        for (var i = 0; i < Sources.Count; i++) {
            var (uv, rate, _, _) = Sources[i];
            if (Sources[i].Inside) {
                continue;
            }
            var supplied = rate * delta;
            SurfaceSuppliedMl += supplied;
            var thrown = Spurt(body, body.UvToWorld(uv), rate, delta, supplied);
            _surface.Add(uv, supplied - thrown);
        }
    }

    /// <summary>A strong bleed throws droplets up from <paramref name="at"/> now and then.</summary>
    private float Spurt(PatientBody body, Vector3 at, float rate, float delta, float available = DropMl) {
        if (_drops.Count < MaxDrops && rate > SpurtRate && _rng.Randf() < delta * (rate - SpurtRate) * 1.5f) {
            var spray = body.Site.GlobalBasis.Y * _rng.RandfRange(0.6f, 1.3f)
                + new Vector3(_rng.RandfRange(-0.4f, 0.4f), 0f, _rng.RandfRange(-0.4f, 0.4f));
            var ml = Mathf.Min(DropMl, available);
            AddDrop(at, spray, ml);
            Splash(at, 0.04f);
            return ml;
        }
        return 0f;
    }

    /// <summary>
    /// Bleeds inside spurt when strong, from the cavity pool's surface once that covers them: the pool hides where they
    /// come from, nothing stands on it. A source under skin that isn't open spreads a bruise over itself instead.
    /// </summary>
    private void Inside(PatientBody body, float delta) {
        foreach (var (uv, rate, depth, _) in Sources) {
            if (depth <= 0f) {
                continue;
            }
            if (!body.Tissue.IsOpen(uv, TissueDepth.Skin)) {
                Bruise(body, uv, rate * delta);
                continue;
            }
            var local = body.Site.ToLocal(body.UvToWorld(uv, depth));
            local.Y = Mathf.Max(local.Y, body.CavityPoolHeight);
            // The cavity pool keeps its own volume; thrown drops are extra surface liquid.
            SurfaceSuppliedMl += Spurt(body, body.Site.ToGlobal(local), rate, delta);
        }
    }

    /// <summary>
    /// Blood held in a cut that doesn't reach the cavity: a flat pool resting on the first layer the cut hasn't gone
    /// through, rising toward the skin as it fills and spreading along the cut. The skin around hides all but what lies
    /// in the opening. It doesn't move with the tissue, like the cavity's pool.
    /// </summary>
    private bool CutPoolsChanged(PatientBody body) {
        _cutGeometryNeedsCapture.Clear();
        if (Pools.Count != _drawnCuts.Count || Pools.Count != _drawnCutGeometry.Count) { return true; }
        var changed = false;
        for (var i = 0; i < Pools.Count; i++) {
            var current = Pools[i];
            var previous = _drawnCuts[i];
            var uvDelta = body.UvToMeters(current.Uv.DistanceTo(previous.Uv));
            var geometryChanged = uvDelta > WoundLiquidGeometryTolerance
                || GeometryChanged(_drawnCutGeometry[i], body, current.Uv);
            _cutGeometryNeedsCapture.Add(geometryChanged);
            if (geometryChanged
                || Mathf.Abs(current.Floor - previous.Floor) > WoundLiquidGeometryTolerance
                || (current.Fill <= 0f) != (previous.Fill <= 0f) || (current.Fill >= 1f) != (previous.Fill >= 1f)) {
                changed = true;
                continue;
            }
            if (current.Fill == previous.Fill) { continue; }
            var gap = body.Tissue.GapAt(current.Uv, 0.04f);
            changed |= VisibleFillChange(current.Fill, previous.Fill, current.Floor, gap);
        }
        return changed;
    }

    private static bool VisibleFillChange(float fill, float drawn, float floor, float gap) => drawn < 0f
        || Mathf.Abs(fill - drawn) * floor * (1f - CutPoolBrim) > 0.0001f
        || Mathf.Abs(Mathf.Sqrt(fill) - Mathf.Sqrt(Mathf.Max(drawn, 0f))) * gap * 0.2f > 0.0001f;

    private sealed class WoundGeometry(int topology, float gap, Vector2[] uvs, float[] values) {
        internal int Topology { get; } = topology;
        internal float Gap { get; } = gap;
        internal Vector2[] Uvs { get; } = uvs;
        internal float[] Values { get; } = values;
        /// <summary>Where the next bounded recheck of <see cref="Uvs"/> starts.</summary>
        internal int CheckCursor;
    }

    /// <summary>Cache a wound's sampling positions and baseline only when its visible mesh changes.
    /// Ordinary solver ticks compare against that baseline without allocating or uploading geometry.</summary>
    private static WoundGeometry CaptureWoundGeometry(PatientBody body, Wound? wound, Vector2 uv) {
        var positions = new List<Vector2>();
        var gap = body.Tissue.GapAt(uv, 0.04f);
        if (wound is not { Points.Count: >= 2 }) {
            for (var y = -1; y <= 1; y++) {
                for (var x = -1; x <= 1; x++) {
                    positions.Add(uv + new Vector2(x * 0.008f / body.SiteSize.X, y * 0.008f / body.SiteSize.Y));
                }
            }
        } else {
            var reach = Mathf.Max(gap * 0.8f + 0.002f, 0.004f);
            var grid = LiquidSurface.PlanRibbon(wound.Points.Select(point => point * body.SiteSize).ToArray(), reach);
            for (var column = 0; column <= grid.Columns; column++) {
                for (var row = 0; row <= grid.Rows; row++) {
                    positions.Add(grid.At(column, grid.RowOffset(row)) / body.SiteSize);
                }
            }
        }
        var uvs = positions.ToArray();
        var values = new float[uvs.Length * 2];
        for (var i = 0; i < uvs.Length; i++) {
            values[i * 2] = body.Tissue.OpeningDistance(uvs[i]);
            values[i * 2 + 1] = body.SurfaceHeight(uvs[i]);
        }
        var result = new WoundGeometry(body.Tissue.TopologyVersion, gap, uvs, values);
        return result;
    }

    private static bool GeometryChanged(WoundGeometry? drawn, PatientBody body, Vector2 uv) {
        if (drawn is null || drawn.Topology != body.Tissue.TopologyVersion
            || Mathf.Abs(drawn.Gap - body.Tissue.GapAt(uv, 0.04f)) > WoundLiquidGeometryTolerance) { return true; }
        // A long cut ribbon can contain hundreds of samples. Rechecking every point of every cut and rinse
        // contour on every solver frame makes a settled pool expensive even though its mesh is unchanged.
        // Rotate through a bounded batch; a real displacement is still caught as soon as its sample is visited.
        const int SamplesPerFrame = 32;
        var count = Math.Min(SamplesPerFrame, drawn.Uvs.Length);
        for (var n = 0; n < count; n++) {
            var i = (drawn.CheckCursor + n) % drawn.Uvs.Length;
            if (Mathf.Abs(body.Tissue.OpeningDistance(drawn.Uvs[i]) - drawn.Values[i * 2]) > WoundLiquidGeometryTolerance
                || Mathf.Abs(body.SurfaceHeight(drawn.Uvs[i]) - drawn.Values[i * 2 + 1]) > WoundLiquidGeometryTolerance) {
                return true;
            }
        }
        drawn.CheckCursor = (drawn.CheckCursor + count) % drawn.Uvs.Length;
        return false;
    }

    private void PoolInCuts(PatientBody body) {
        // A tissue pull can invalidate blood and irrigation simultaneously. Spread their contour builds
        // across consecutive frames, while alternating priority so continued filling cannot starve either.
        var deferCuts = _rinseFirst && _rinses.Count > 0;
        _rinseFirst = false;
        var patient = body.GetParent<Patient>();
        if (!deferCuts && Pools.Count > 0 && (_drawnCutGeometry.Count != _drawnCuts.Count || CutPoolsChanged(body))) {
            var surface = _cutBuffer;
            surface.Clear();
            foreach (var (uv, floor, fill) in Pools) {
                AddWoundSurface(surface, body, patient.NearestWound(uv, float.PositiveInfinity, false), uv, floor, fill);
            }
            _cutPoolMesh.Mesh = surface.Upload(_cutPoolMesh.Mesh as ArrayMesh);
            var captureAll = _drawnCutGeometry.Count != _drawnCuts.Count
                || _drawnCutGeometry.Count != Pools.Count
                || _cutGeometryNeedsCapture.Count != Pools.Count;
            _drawnCuts.Clear();
            _drawnCuts.AddRange(Pools);
            if (captureAll) {
                _drawnCutGeometry.Clear();
                foreach (var pool in Pools) {
                    _drawnCutGeometry.Add(CaptureWoundGeometry(body, patient.NearestWound(pool.Uv, float.PositiveInfinity, false), pool.Uv));
                }
            } else {
                for (var i = 0; i < Pools.Count; i++) {
                    if (!_cutGeometryNeedsCapture[i]) { continue; }
                    var pool = Pools[i];
                    _drawnCutGeometry[i] = CaptureWoundGeometry(body, patient.NearestWound(pool.Uv, float.PositiveInfinity, false), pool.Uv);
                }
            }
            _cutGeometryNeedsCapture.Clear();
            CutPoolMeshRefreshes++;
            if (_rinses.Count > 0) {
                _rinseFirst = true;
                _cutClock = 0.1f;
                return;
            }
        } else if (Pools.Count == 0 && _drawnCuts.Count > 0) {
            ((ArrayMesh)_cutPoolMesh.Mesh).ClearSurfaces();
            _drawnCuts.Clear();
            _drawnCutGeometry.Clear();
            _cutGeometryNeedsCapture.Clear();
        }
        if (deferCuts) { _cutClock = 0.1f; }
        foreach (var (id, rinse) in _rinses) {
            if (rinse.Ml <= 0f) { continue; }
            if (rinse.Ml < 0.02f) {
                // Return drained wound surfaces to the prewarmed slot pool. Releasing GPU surfaces can stall the render thread.
                rinse.Mesh.Visible = false;
                ((ArrayMesh)rinse.Mesh.Mesh).ClearSurfaces();
                rinse.Ml = rinse.Blood = 0f;
                rinse.DrawnFill = -1f;
                rinse.DrawnFloor = -1f;
                rinse.DrawnGeometry = null;
                _drainedRinseIds.Add(id);
                continue;
            }
            var floor = body.OpeningDepth(rinse.Uv, 0.015f);
            if (body.Tissue.IsOpen(rinse.Uv)) { floor = body.SurfaceHeight(rinse.Uv) - body.CavityFloor(rinse.Uv); }
            if (floor <= 0f) { rinse.Mesh.Visible = false; continue; }
            var wound = patient.Wounds.FirstOrDefault(w => w.Id == id);
            var gap = Mathf.Max(body.Tissue.GapAt(rinse.Uv, 0.04f), 0.001f);
            var length = (wound?.LengthUv ?? 0.02f) * Mathf.Max(body.SiteSize.X, body.SiteSize.Y);
            var capacity = Mathf.Max(0.25f, length * gap * floor * 500000f);
            var fill = Mathf.Clamp(rinse.Ml / capacity, 0f, 1f);
            var geometryChanged = GeometryChanged(rinse.DrawnGeometry, body, rinse.Uv);
            if (geometryChanged
                || VisibleFillChange(fill, rinse.DrawnFill, floor, gap)
                || Mathf.Abs(floor - rinse.DrawnFloor) > WoundLiquidGeometryTolerance) {
                var water = rinse.Buffer;
                water.Clear();
                AddWoundSurface(water, body, wound, rinse.Uv, floor, fill);
                rinse.Mesh.Mesh = water.Upload(rinse.Mesh.Mesh as ArrayMesh);
                rinse.DrawnFill = fill;
                rinse.DrawnFloor = floor;
                if (geometryChanged) {
                    rinse.DrawnGeometry = CaptureWoundGeometry(body, wound, rinse.Uv);
                }
                rinse.MeshRefreshes++;
            }
            rinse.Mesh.Visible = true;
            var fraction = Mathf.Clamp(rinse.Blood / Mathf.Max(rinse.Ml, 0.01f), 0f, 1f);
            var material = (StandardMaterial3D)rinse.Mesh.MaterialOverride;
            material.AlbedoColor = new Color(0.08f, 0.15f, 0.17f, 0.2f).Lerp(new Color(0.22f, 0.01f, 0.025f, 0.9f), fraction);
        }
        foreach (var id in _drainedRinseIds) {
            var rinse = _rinses[id];
            _rinses.Remove(id);
            rinse.Mesh.Name = "IrrigationPoolSlot";
            _drainedRinses[id] = rinse;
        }
    }

    /// <summary>The visible part of irrigation gathers briefly in the opening while the treatment flushes it.
    /// Called with the stream event on every peer; it doesn't change blood loss or treatment volume.</summary>
    internal void Irrigate(Vector3 at, float ml, float bloodShare) {
        if (_body is null || ml <= 0f) { return; }
        if (_body.Probe(at).Zone is not (SiteZone.Site or SiteZone.Cavity)) { return; }
        var uv = _body.WorldToUv(at);
        if (uv.X < 0f || uv.X > 1f || uv.Y < 0f || uv.Y > 1f || !_body.OnBody(uv)
            || !_body.Tissue.IsOpen(uv, TissueDepth.Skin)) { return; }
        if (_body.GetParent<Patient>().NearestWound(uv, 0.05f, false) is not { } wound) { return; }
        if (!_rinses.TryGetValue(wound.Id, out var rinse)) {
            if (!_drainedRinses.Remove(wound.Id, out rinse!)) {
                if (_availableRinses.Count > 0) { rinse = _availableRinses.Dequeue(); } else if (_drainedRinses.Count > 0) {
                    var reusable = _drainedRinses.First();
                    _drainedRinses.Remove(reusable.Key);
                    rinse = reusable.Value;
                } else { return; }
            }
            rinse.Uv = wound.BleedPoint;
            rinse.Mesh.Name = "IrrigationPool";
            rinse.Mesh.Visible = true;
            _rinses.Add(wound.Id, rinse);
        }
        if (rinse.Ml <= 0f) {
            rinse.Uv = wound.BleedPoint;
            rinse.DrawnFill = -1f;
            rinse.DrawnFloor = -1f;
            rinse.DrawnGeometry = null;
        }
        var total = rinse.Ml + ml;
        var retain = Mathf.Min(1f, 50f / total);
        rinse.Ml = total * retain;
        rinse.Blood = (rinse.Blood + ml * Mathf.Clamp(bloodShare, 0f, 1f)) * retain;
    }

    internal int RinseMeshRefreshes(int woundId) =>
        _rinses.TryGetValue(woundId, out var rinse) ? rinse.MeshRefreshes : 0;

    private static void AddWoundSurface(LiquidSurface surface, PatientBody body, Wound? wound, Vector2 uv, float floor, float fill) {
        var margin = new Vector2(0.008f / body.SiteSize.X, 0.008f / body.SiteSize.Y);
        var low = uv;
        var high = uv;
        if (wound is not null) {
            foreach (var p in wound.Points) { low = low.Min(p); high = high.Max(p); }
        }
        var gap = body.Tissue.GapAt(uv, 0.04f);
        margin += new Vector2(gap / body.SiteSize.X, gap / body.SiteSize.Y);
        var bounds = new Rect2(low - margin, high - low + margin * 2f).Intersection(new Rect2(Vector2.Zero, Vector2.One));
        if (!bounds.HasArea()) { return; }
        var depth = CutPoolDepth(floor, fill);
        var height = body.SurfaceHeight(uv) - depth;
        var inset = gap * 0.2f * (1f - Mathf.Sqrt(Mathf.Clamp(fill, 0f, 1f)));
        var cavity = floor > PatientBody.SkinThickness + body.FatThickness + 0.005f;
        float Inside(Vector2 p) {
            if (!body.OnBody(p)) { return -0.001f; }
            var opening = body.Tissue.OpeningDistance(p) - inset;
            if (opening < 0f) { return opening; }
            var skin = body.SurfaceHeight(p);
            var ground = cavity ? body.CavityFloor(p) : skin - floor;
            return Mathf.Min(opening, Mathf.Min(height - ground - 0.00005f, skin - height - 0.0001f));
        }
        Vector3 Point(Vector2 p, float clearance) {
            var rim = 0.00012f * Mathf.Exp(-Mathf.Max(clearance, 0f) / 0.001f);
            return body.SitePoint(p, Mathf.Min(height + rim, body.SurfaceHeight(p) - 0.00005f));
        }
        if (wound is { Points.Count: >= 2 }) {
            surface.Ribbon(wound.Points.Select(p => p * body.SiteSize).ToArray(), Mathf.Max(gap * 0.8f + 0.002f, 0.004f),
                p => Inside(p / body.SiteSize), (p, clearance) => Point(p / body.SiteSize, clearance));
        } else {
            surface.Field(bounds, 16, 16, Inside, Point);
        }
    }

    /// <summary>
    /// Paints every source's spot afresh, so one that stopped bleeding is gone: one bleeding onto the skin from a hole on
    /// the skin, one inside the opening in the middle of the wall of the layer it bleeds from. A wound that spills over onto
    /// the skin as well as bleeding inside keeps its one spot inside. A spot that would overlap a faster bleed's in
    /// another layer isn't painted: blending the two would smear their depths into a streak down the wall.
    /// </summary>
    private void Spots(PatientBody body) {
        var map = body.WoundMap;
        foreach (var (uv, radius, _) in _spots) {
            map.DiskOps(WoundMap.Layer.Bleeds, uv, radius, WipeSpot, soft: false);
        }
        _spots.Clear();
        _insideUvs.Clear();
        _byRate.Clear();
        for (var i = 0; i < Sources.Count; i++) {
            if (Sources[i].Inside) {
                _insideUvs.Add(Sources[i].Uv);
            }
            _byRate.Add(i);
        }
        _byRate.Sort(_fasterFirst);
        foreach (var index in _byRate) {
            var (uv, rate, depth, point) = Sources[index];
            if (depth > 0f ? !body.Tissue.IsOpen(uv, TissueDepth.Skin) : !point || _insideUvs.Contains(uv)) {
                continue;
            }
            // Inside, it sits on the wall of the layer it bleeds from, no bigger than that wall is high, so it never runs
            // off it.
            var (top, thickness) = LayerAt(body, uv, depth);
            var radius = Mathf.Min(SpotMin + Mathf.Sqrt(rate) * SpotGrowth, WoundMap.MaxSpotRadius);
            if (depth > 0f) {
                radius = Mathf.Min(radius, thickness * 0.5f);
            }
            var size = body.MetersToUv(radius);
            // A cut's own edge bleeds from just under the skin: at the top of the fat's wall, where a lip lifted with the
            // skin shows it. A vessel deeper down, in the middle of its layer's wall.
            var center = depth <= 0f ? 0f : depth <= PatientBody.SkinThickness && top > 0f ? top + radius : top + thickness * 0.5f;
            // Blood pooled in the opening over it: up the wall to the pool's surface, so its top still shows above it.
            // Not past the skin, though: a pool brimming over the lips hides it.
            if (depth > 0f && PoolSurfaceDepth(body, uv) is var surface && surface > 0f) {
                center = Mathf.Min(center, Mathf.Max(surface + radius * PoolPeek, radius));
            }
            if (Overlaps(uv, size, center)) {
                continue;
            }
            if (depth <= 0f) {
                map.Disk(WoundMap.Layer.Bleeds, WoundMap.SkinSpot, uv, size, 1f, WoundMap.Mode.Max);
            } else {
                map.DiskOps(WoundMap.Layer.Bleeds, uv, size,
                [
                    new(WoundMap.Spot, 1f, WoundMap.Mode.Max),
                    new(WoundMap.SpotRadius, radius / WoundMap.MaxSpotRadius, WoundMap.Mode.Max),
                    new(WoundMap.SpotDepth, center / WoundMap.MaxSpotDepth, WoundMap.Mode.Max),
                ]);
            }
            _spots.Add((uv, size, center));
        }
        foreach (var uv in _punctures) {
            PaintPuncture(body, uv);
        }
    }

    /// <summary>A needle went into the skin at <paramref name="uv"/>: its spot shows at once, not with the next repaint,
    /// so it's there in the very frame the needle comes out.</summary>
    internal void AddPuncture(Vector2 uv) {
        _punctures.Add(uv);
        if (_body is not null) {
            PaintPuncture(_body, uv);
        }
    }

    /// <summary>The spot at <paramref name="uv"/> dried or was wiped away: the next repaint leaves it out.</summary>
    internal void RemovePuncture(Vector2 uv) => _punctures.Remove(uv);

    private void PaintPuncture(PatientBody body, Vector2 uv) {
        // Cut open since, it would split across the lips like a cut's own spot. It has only the skin's channel, so it
        // needs no check against spots at other depths.
        if (body.Tissue.IsOpen(uv, TissueDepth.Skin)) {
            return;
        }
        var size = body.MetersToUv(PunctureRadius);
        body.WoundMap.Disk(WoundMap.Layer.Bleeds, WoundMap.SkinSpot, uv, size, 1f, WoundMap.Mode.Max);
        _spots.Add((uv, size, 0f));
    }

    /// <summary>How far down (meters, from the spot's middle) a spot covered by a pool lies under its surface: its top
    /// shows above it.</summary>
    private const float PoolPeek = 0.2f;

    /// <summary>How deep (meters) under the skin at <paramref name="uv"/> the surface of the blood pooled in the opening
    /// there lies: the cavity's pool where the cut reaches into the cavity, or the pool held in the cut it's in.
    /// Infinitely deep without either.</summary>
    private float PoolSurfaceDepth(PatientBody body, Vector2 uv) {
        var skin = body.SurfaceHeight(uv);
        var reach = body.MetersToUv(body.Tissue.OpenReach * 2f);
        var surface = body.Tissue.DeepestCut(uv, reach) == TissueDepth.Muscle ? skin - body.CavityPoolHeight : float.PositiveInfinity;
        // A cut's pool is keyed by its wound's bleed point and fills the whole cut at one height.
        if (body.GetParent<Patient>().NearestWound(uv, reach, false) is { } wound) {
            foreach (var pool in Pools) {
                if (pool.Uv == wound.BleedPoint) {
                    surface = Mathf.Min(surface, skin - body.SurfaceHeight(pool.Uv) + CutPoolDepth(pool.Floor, pool.Fill));
                }
            }
        }
        return surface;
    }

    /// <summary>How deep (meters) under the skin at its bleed point a cut's pool stands, <paramref name="fill"/> of
    /// the way up from its <paramref name="floor"/>.</summary>
    private static float CutPoolDepth(float floor, float fill) =>
        Mathf.Lerp(floor - CutPoolLift, Mathf.Max(floor * CutPoolBrim, CutPoolFull), Mathf.Clamp(fill, 0f, 1f));

    /// <summary>Whether a spot of <paramref name="size"/> at <paramref name="uv"/> would overlap one already painted
    /// at another depth.</summary>
    private bool Overlaps(Vector2 uv, float size, float center) {
        foreach (var spot in _spots) {
            if (spot.Center != center && spot.Uv.DistanceTo(uv) < spot.Radius + size) {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The top (meters under the skin) and thickness of the layer whose wall a bleed at <paramref name="uv"/>,
    /// <paramref name="depth"/> deep, shows on: the one it lies in, the muscle for anything under it, but no deeper than
    /// the cut there goes. A cut bleeding from its own edges (as deep as the skin) shows on the fat under the skin, or
    /// the skin's own cut face where only the skin is cut.
    /// </summary>
    private static (float Top, float Thickness) LayerAt(PatientBody body, Vector2 uv, float depth) {
        var cut = body.Tissue.DeepestCut(uv, body.MetersToUv(body.Tissue.OpenReach * 2f)) switch {
            TissueDepth.Muscle => 2,
            TissueDepth.Fat => 1,
            _ => 0,
        };
        // A cut's own edge (as deep as the skin) shows on the fat's wall, a vessel on its own layer's.
        var lies = depth <= PatientBody.SkinThickness + body.FatThickness ? 1 : 2;
        return Math.Min(lies, cut) switch {
            0 => (0f, PatientBody.SkinThickness),
            1 => (PatientBody.SkinThickness, body.FatThickness),
            _ => (PatientBody.SkinThickness + body.FatThickness, body.MuscleThickness),
        };
    }

    /// <summary>Spreads a bruise over a bleed under closed skin as blood collects there, keyed like puddles.</summary>
    private void Bruise(PatientBody body, Vector2 uv, float ml) {
        var key = (Vector2I)(uv * 100f).Round();
        var pooled = _bruised.GetValueOrDefault(key) + ml;
        _bruised[key] = pooled;
        if (_paintPuddles) {
            var radius = Mathf.Min(BruiseStart + Mathf.Sqrt(pooled) * BruiseGrowth, BruiseMax);
            body.WoundMap.Disk(WoundMap.Layer.Wounds, WoundMap.Bruise, uv, radius, Mathf.Min(0.3f + pooled * 0.02f, 0.9f),
                WoundMap.Mode.Max);
        }
    }

    /// <summary>Pressure spreads a connected film sideways while gravity transports it downhill. The film retains
    /// thin residue, coalesces without separate path heads, and loses volume when a swab removes its painted blood.</summary>
    private void Flow(PatientBody body, float delta) {
        // What runs off the site's edges goes on down the body's own skin.
        var siteToMesh = body.LiquidContact.SiteToMesh;
        // Until the body's film is built, runoff waits in the site's queue.
        if (Built(_bodyFilmBuild) is { } film) {
            while (_surface.TakeRunoff(out var uv, out var runoffMl)) {
                film.Add(film.Nearest(siteToMesh * body.Site.ToLocal(body.UvToWorld(uv))), runoffMl);
            }
            // The pose is read only when a step starts: it's what the step and the drips it lets go need.
            if (film.Tick(delta)) {
                var world = BodyToWorld();
                // Skin pressed on the mattress (lying on the back or turned) hands its blood to the bed's film.
                var bed = _bed;
                film.Start(_skinMesh.GlobalBasis.Inverse() * Vector3.Down, node => bed.Touches(world(node, film.At(node))));
                ReleaseBodyDrips(world);
            }
        }
        if (_surface.HasFilm) {
            _surfaceGeometryClock += delta;
            if (_surfaceGeometryClock >= 0.1f) {
                _surfaceGeometryClock %= 0.1f;
                var tissueSteps = body.Tissue.StepsDone;
                var topology = body.Tissue.TopologyVersion;
                if ((tissueSteps != _surfaceGeometrySteps || topology != _surfaceGeometryTopology)
                    && _surface.BeginGeometryRefit()) {
                    _surfaceGeometrySteps = tissueSteps;
                    _surfaceGeometryTopology = topology;
                }
            }
        }
        _surface.ProcessGeometryRefit(32);
        // A pristine or fully wiped site has no transport or texture work. A final pending paint still runs.
        if (!_surface.HasFilm && !_surface.NeedsPaint && !_surfaceNeedsPaint) { _surfaceClock = 0f; return; }
        _surfaceClock += delta;
        // Alternate transport and texture work so they do not land together on the same gameplay frame.
        if (_surfaceNeedsPaint) {
            _surface.ReconcileWipes(body.WoundMap);
            if (_surface.NeedsPaint) { _surface.Paint(body.WoundMap); }
            _surfaceNeedsPaint = false;
            return;
        }
        const float Step = 1f / 30f;
        if (_surfaceClock < Step) {
            return;
        }
        _surface.ReconcileWipes(body.WoundMap);
        // Substeps limit outflow so steep skin cannot empty a cell below zero.
        var elapsed = Mathf.Min(_surfaceClock, 0.1f);
        _surfaceClock = 0f;
        var steps = Mathf.CeilToInt(elapsed * 60f);
        var basis = body.Site.GlobalBasis;
        for (var i = 0; i < steps; i++) { _surface.Step(basis, elapsed / steps); }
        // Painted after every other step: the skin's blood redrawn 15 times a second reads as smooth, at half the cost.
        // A step that skips it leaves NeedsPaint set for the next.
        _surfaceNeedsPaint = _surface.NeedsPaint && ++_surfaceSteps % 2 == 0;
    }

    /// <summary>Throws <paramref name="count"/> droplets up and out from <paramref name="at"/> (an impact, a saw),
    /// <paramref name="speed"/> in m/s.</summary>
    public void Spray(Vector3 at, int count, float speed) {
        if (_body is null) {
            return;
        }
        var up = _body.Site.GlobalBasis.Y;
        for (var i = 0; i < count; i++) {
            var outward = new Vector3(_rng.RandfRange(-1f, 1f), 0f, _rng.RandfRange(-1f, 1f)) * 0.6f;
            AddDrop(at + up * 0.004f, (up + outward).Normalized() * speed * _rng.RandfRange(0.5f, 1f));
        }
        SurfaceSuppliedMl += count * DropMl;
        Splash(at, count * speed * 0.05f);
    }

    /// <summary>Closer and more head-on hits more.</summary>
    private void Splash(Vector3 at, float amount) {
        if (GetViewport().GetCamera3D() is not { } camera) {
            return;
        }
        var to = at - camera.GlobalPosition;
        var facing = to.Normalized().Dot(-camera.GlobalBasis.Z);
        if (to.Length() < SplashReach && facing > 0.7f) {
            EmitSignal(SignalName.Splashed, amount * (1f - to.Length() / SplashReach) * 2f);
        }
    }

    private void AddDrop(Vector3 at, Vector3 velocity, float ml = DropMl) {
        if (_drops.Count >= MaxDrops) {
            // No room to draw another: the newest drop in flight carries this liquid too, so none is lost.
            var newest = _drops[^1];
            newest.Shape *= Mathf.Pow((newest.Ml + ml) / newest.Ml, 1f / 3f);
            newest.Ml += ml;
            return;
        }
        var shape = new Vector3(_dripRng.RandfRange(0.6f, 1.15f), _dripRng.RandfRange(1.1f, 2.7f), _dripRng.RandfRange(0.65f, 1.1f));
        var amount = Mathf.Pow(ml / DropMl / (shape.X * shape.Y * shape.Z), 1f / 3f) * _dropVolumeScale;
        _drops.Add(new Drop(at, velocity, ml, shape * amount, _dripRng.RandfRange(-Mathf.Pi, Mathf.Pi)));
    }

    /// <summary>Places a body film point on the body as it's posed now. Safe to call from the film's worker.</summary>
    private Func<int, Vector3, Vector3> BodyToWorld() {
        var pose = _skeleton is null ? null : BodyLiquidSurface.CaptureSkinPose(_skinMesh, _skeleton, _bindBones);
        var film = BodyFilm;
        var mesh = _skinMesh.GlobalTransform;
        return (node, at) => pose?.Deform(at, film.SkinAt(node)) ?? mesh * at;
    }

    private void ReleaseBodyDrips(Func<int, Vector3, Vector3> world) {
        // Until the bed's film is built, what the skin presses into the mattress falls as drops and waits above it.
        var bedFilm = Built(_bedFilmBuild);
        foreach (var drip in BodyFilm.Released) {
            var at = world(drip.Node, drip.At);
            if (bedFilm is not null && _bed.Touches(at)) {
                bedFilm.Add(bedFilm.Nearest(at with { Y = _bed.HeightAt(new Vector2(at.X, at.Z))!.Value }, Vector3.Up), drip.Ml);
            } else {
                AddDrop(at, Vector3.Zero, drip.Ml);
            }
        }
        BodyFilm.ClearReleased();
    }

    private void ReleaseBedDrips() {
        foreach (var drip in BedFilm.Released) { AddDrop(drip.At, Vector3.Zero, drip.Ml); }
        BedFilm.ClearReleased();
    }

    /// <summary>The body's skin round the site, the bed and the floor, each with its own film, drawn by its own mesh.
    /// </summary>
    private void SetupFilms(PatientBody body) {
        _skinMesh = body.SkinMesh;
        _skeleton = _skinMesh.GetNodeOrNull<Skeleton3D>(_skinMesh.Skeleton);
        _bindBones = _skeleton is null ? [] : BodyLiquidSurface.ResolveBinds(_skinMesh, _skeleton);
        _bed = body.GetParent<Patient>().GetParent<Surgery>().Room.BedSurface;
        var skin = body.LiquidContact;
        var siteToMesh = skin.SiteToMesh;
        var half = body.SiteSize * 0.5f;
        // The site's own skin holds its film's liquid (SurfaceBlood); the body's film takes over at its edges and on its
        // steep flanks, and holds the far side under it, which the site's film doesn't reach. The site's film says
        // which skin it holds, so no liquid is held by both films or neither: judged by the model's own normal instead,
        // a strip along the flank was. The model's normal only tells the skin the site's heights were measured on
        // (facing the site's up) from the far side under it. Both films draw through one layer (BodyBloodLayer).
        var surface = _surface;
        bool OverSite(Vector3 sitePoint, float margin = 0f) => Mathf.Abs(sitePoint.X) < half.X + margin && Mathf.Abs(sitePoint.Z) < half.Y + margin;
        bool OffSite(Vector3 sitePoint, Vector3 siteNormal) =>
            !(OverSite(sitePoint) && siteNormal.Y > 0f && surface.Carries(body.LocalToUv(sitePoint), sitePoint.Y));
        var meshToSite = siteToMesh.AffineInverse();
        // Transport covers the entire body. Its samples feed the same field as the detailed site solver;
        // there is no separately drawn runoff mesh or skin/site rendering handover.
        _bodyFilmBuild = Build(() => new SurfaceFilm(skin.Corners.Select(p => siteToMesh * p).ToArray(), BodyFilmSettings,
            skin.Normals.Select(n => siteToMesh.Basis * n).ToArray(), skin.Skin,
            carries: (at, normal) => OffSite(meshToSite * at, (meshToSite.Basis * normal).Normalized()), drawMesh: false));
        foreach (var material in body.BloodMaterials) { Layer.Bind(material); }
        var bed = _bed;
        var bedMesh = new ArrayMesh();
        _bedFilmBuild = Build(() => new SurfaceFilm(bed.Triangles, BedFilmSettings, keep: bed.Exposed, mesh: bedMesh));
        var (foot, head, side) = (Room.TableFoot - FloorFilmMargin, Room.TableHead + FloorFilmMargin, Room.TableHalfWidth + FloorFilmMargin);
        Vector3[] floor = [new(foot, FloorY, -side), new(head, FloorY, -side), new(head, FloorY, side),
            new(foot, FloorY, -side), new(head, FloorY, side), new(foot, FloorY, side)];
        var floorMesh = new ArrayMesh();
        _floorFilmBuild = Build(() => new SurfaceFilm(floor, FloorFilmSettings, Enumerable.Repeat(Vector3.Up, 6).ToArray(),
            mesh: floorMesh, fadesAt: _ => true));
        // The frame's long thin triangles would turn a crisp edge into teeth; the floor's even grid keeps one.
        foreach (var (name, mesh, edge) in new[] { ("BedFilm", bedMesh, new Vector2(0.05f, 0.7f)), ("FloorFilm", floorMesh, new Vector2(0.15f, 0.6f)) }) {
            AddChild(new MeshInstance3D {
                Name = name,
                Mesh = mesh,
                MaterialOverride = Materials.BloodFilm(edge: edge),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                TopLevel = true,
            });
        }
    }

    /// <summary>Droplets fall until they meet the mattress's or frame's top, or the floor, and join that surface's
    /// film. The bed's film drips onto the floor's in turn.</summary>
    private void Fall(float delta) {
        var bedFilm = Built(_bedFilmBuild);
        var floorFilm = Built(_floorFilmBuild);
        for (var i = 0; i < _drops.Count; i++) {
            var drop = _drops[i];
            drop.Velocity += Vector3.Down * 9.8f * delta;
            var position = drop.Position + drop.Velocity * delta;
            // A film still being built keeps the drop waiting just above it, rather than stall the frame for the build.
            if (_bed.Landing(drop.Position, position) is { } onBed) {
                if (bedFilm is null) { drop.Velocity = Vector3.Zero; continue; }
                _drops.RemoveAt(i--);
                bedFilm.Add(bedFilm.Nearest(onBed, Vector3.Up), drop.Ml);
            } else if (position.Y <= FloorY) {
                if (floorFilm is null) { drop.Velocity = Vector3.Zero; continue; }
                var hit = drop.Position.Lerp(position, Mathf.Clamp((drop.Position.Y - FloorY) / (drop.Position.Y - position.Y), 0f, 1f));
                _drops.RemoveAt(i--);
                floorFilm.Add(floorFilm.Nearest(hit with { Y = FloorY }, Vector3.Up), drop.Ml);
            } else {
                drop.Position = position;
            }
        }
        if (bedFilm is not null && bedFilm.Tick(delta)) {
            bedFilm.Start(Vector3.Down);
            ReleaseBedDrips();
        }
        if (floorFilm is not null && floorFilm.Tick(delta)) { floorFilm.Start(Vector3.Down); }
        var multimesh = _dropMesh.Multimesh;
        multimesh.VisibleInstanceCount = _drops.Count;
        for (var i = 0; i < _drops.Count; i++) {
            var drop = _drops[i];
            var up = -drop.Velocity.Normalized();
            var x = Vector3.Right.Slide(up).Normalized();
            if (x.IsZeroApprox()) { x = Vector3.Forward; }
            var shape = drop.Shape * DropRadius;
            var stretch = 1f + Mathf.Min(drop.Velocity.Length(), 4f) * 0.15f;
            shape.Y *= stretch;
            shape.X /= Mathf.Sqrt(stretch);
            shape.Z /= Mathf.Sqrt(stretch);
            var basis = new Basis(x, up, x.Cross(up)).Rotated(up, drop.Twist).Scaled(shape);
            multimesh.SetInstanceTransform(i, new Transform3D(basis, drop.Position));
        }
    }

    /// <summary>Puts <paramref name="ml"/> straight onto whatever surface is at <paramref name="at"/> (world): the
    /// bed's top within a few millimeters, else the floor near its level, else as a drop to fall there.</summary>
    internal void Deposit(Vector3 at, float ml) {
        if (_bed.Landing(at + Vector3.Up * 0.002f, at - Vector3.Up * 0.003f) is { } onBed && Built(_bedFilmBuild) is { } bed) {
            bed.Add(bed.Nearest(onBed, Vector3.Up), ml);
        } else if (at.Y <= FloorY + 0.01f && Built(_floorFilmBuild) is { } floor) {
            floor.Add(floor.Nearest(at with { Y = FloorY }, Vector3.Up), ml);
        } else {
            AddDrop(at, Vector3.Zero, ml);
        }
    }

    /// <summary>Copies what the layer's samples are built from, for the worker (<see cref="GatherLayerSamples"/>):
    /// the site's fluid map and skin as drawn now, the body film's wet points and the skin's pose. The field lies in
    /// the site's frame, without its scale: breathing moves the site and the trunk's skin together, so nothing in it.
    /// </summary>
    private void PrepareLayerInput(PatientBody body) {
        var siteToWorld = body.Site.GlobalTransform;
        _layerWorldToField = siteToWorld.Orthonormalized().AffineInverse();
        _layerSiteToField = _layerWorldToField * siteToWorld;
        _layerMeshToWorld = _skinMesh.GlobalTransform;
        Layer.Place(_layerWorldToField);
        _layerSitePainted = body.WoundMap.HasPainted(WoundMap.Layer.Fluids, WoundMap.Blood);
        if (_layerSitePainted) {
            body.SnapshotSkin(_layerSkin);
            body.WoundMap.CopyLayer(WoundMap.Layer.Fluids, ref _layerFluids);
            _layerFluidsSize = body.WoundMap.Size;
            _layerSiteSize = body.SiteSize;
        }
        var film = Built(_bodyFilmBuild);
        _layerFilm = film is { Samples.Count: > 0 } ? film : null;
        _layerFilmSamples.Clear();
        if (_layerFilm is { } wet) { _layerFilmSamples.AddRange(wet.Samples); }
        _layerPose = _layerFilm is null || _skeleton is null ? null : BodyLiquidSurface.CaptureSkinPose(_skinMesh, _skeleton, _bindBones);
    }

    /// <summary>The samples the skin's blood is drawn from, on the worker from <see cref="PrepareLayerInput"/>: one
    /// per voxel of the layer over the site with the mean of its fluid map texels, on the skin as it's drawn and
    /// facing the body's own face there, or the simulated skin's where that is drawn (a pulled flap); plus the body
    /// film's wet points in the skin's pose. Dry samples ring the wet ones, so a pool ends between them.</summary>
    private List<BodyBloodLayer.Sample> GatherLayerSamples() {
        _layerSamples.Clear();
        if (_layerSitePainted) {
            var size = _layerSiteSize * _layerSiteToField.Basis.Scale.X;
            var nx = Math.Min(Mathf.CeilToInt(size.X / BodyBloodLayer.CellSize), MaxSiteSamples);
            var ny = Math.Min(Mathf.CeilToInt(size.Y / BodyBloodLayer.CellSize), MaxSiteSamples);
            Buffers.Sized(ref _siteCoverage, nx * ny);
            var step = new Vector2(1f / nx, 1f / ny);
            for (var j = 0; j < ny; j++) {
                for (var i = 0; i < nx; i++) {
                    _siteCoverage[j * nx + i] = WoundMap.BoxMean(_layerFluids, _layerFluidsSize, WoundMap.Blood, new Vector2(i, j) * step, new Vector2(i + 1, j + 1) * step);
                }
            }
            var radius = Mathf.Max(Mathf.Max(size.X / nx, size.Y / ny) * 1.5f, LayerBlendRadius);
            for (var j = 0; j < ny; j++) {
                for (var i = 0; i < nx; i++) {
                    var wet = false;
                    for (var dj = Math.Max(j - 1, 0); dj <= Math.Min(j + 1, ny - 1) && !wet; dj++) {
                        for (var di = Math.Max(i - 1, 0); di <= Math.Min(i + 1, nx - 1) && !wet; di++) { wet = _siteCoverage[dj * nx + di] > 0f; }
                    }
                    var uv = new Vector2(i + 0.5f, j + 0.5f) * step;
                    if (!wet || _layerSkin.Support(uv) < 0f) { continue; }
                    // The map interpolates paint beyond its solver's cells. There the body's samples own the
                    // film: reading that fringe again dilutes them and draws the handover as a red band.
                    if (!_surface.Carries(uv) && !_layerSkin.Simulated(uv)) { continue; }
                    var at = _layerSiteToField * _layerSkin.Point(uv);
                    var normal = _layerSkin.Simulated(uv)
                        ? (_layerSkin.Point(uv + step with { X = 0f }) - _layerSkin.Point(uv - step with { X = 0f }))
                            .Cross(_layerSkin.Point(uv + step with { Y = 0f }) - _layerSkin.Point(uv - step with { Y = 0f }))
                        : _surface.SkinNormalAt(uv);
                    _layerSamples.Add(new BodyBloodLayer.Sample(at, (_layerSiteToField.Basis * normal).Normalized(), _siteCoverage[j * nx + i], radius));
                }
            }
        }
        if (_layerFilm is { } film) {
            var pose = _layerPose;
            foreach (var sample in _layerFilmSamples) {
                var skin = film.SkinAt(sample.Node);
                var at = pose is null ? _layerMeshToWorld * sample.At : pose.Deform(sample.At, skin);
                var normal = pose is null ? _layerMeshToWorld.Basis * sample.Normal
                    : pose.Deform(sample.At + sample.Normal, skin) - pose.Deform(sample.At, skin);
                _layerSamples.Add(new BodyBloodLayer.Sample(_layerWorldToField * at, (_layerWorldToField.Basis * normal).Normalized(),
                    sample.Coverage, LayerBlendRadius));
            }
        }
        return _layerSamples;
    }

    private void UpdateLayer(float delta) {
        if (_body is not { } body) { return; }
        Layer.Place(body.Site.GlobalTransform.Orthonormalized().AffineInverse());
        Layer.PublishFinished();
        _layerClock += delta;
        // The body film's samples follow the bones and drift with the breath, so a wet film is sampled at the resting
        // rate whatever else holds. A dry one leaves only the site, which is sampled again once its blood or skin
        // changed: a bleeding site with a thrashing patient costs no field until blood runs off it.
        var filmWet = Built(_bodyFilmBuild) is { Samples.Count: > 0 };
        var fluids = body.WoundMap.Version(WoundMap.Layer.Fluids);
        var moved = SkinMoved(body, filmWet);
        _layerDue |= moved || fluids != _layerFluidsVersion;
        _layerMoving = moved ? 1f : Mathf.Max(_layerMoving - delta, 0f);
        if (!_layerDue && !filmWet) { return; }
        if (_layerClock < (_layerMoving > 0f ? MovingLayerInterval : LayerInterval) || Layer.Busy) { return; }
        (_layerClock, _layerDue, _layerFluidsVersion) = (0f, false, fluids);
        PrepareLayerInput(body);
        Layer.Request(_gatherLayer);
    }

    /// <summary>A bone counts as moving past this much in one frame. Breathing shifts the trunk, and holds the limbs
    /// against it, by a hundredth of this a frame: the resting rate and the samples' own tolerance follow that drift.
    /// A thrashing or turned limb goes much farther.</summary>
    private const float MovingBoneDistance = 0.001f;
    private const float MovingBoneAngle = 0.5f * Mathf.Pi / 180f;

    /// <summary>Whether the skin may have moved since the last call: the sheet is held or was cut, or, when the body
    /// film's blood rides them (<paramref name="bones"/>), a bone moved faster than breathing.</summary>
    private bool SkinMoved(PatientBody body, bool bones) {
        var moved = body.Tissue.Gripped || body.Tissue.TopologyVersion != _layerTopology;
        _layerTopology = body.Tissue.TopologyVersion;
        if (_skeleton is not { } skeleton || !bones) { return moved; }
        if (_layerBonePoses.Length != _bindBones.Length) { _layerBonePoses = new Transform3D[_bindBones.Length]; moved = true; }
        for (var i = 0; i < _bindBones.Length; i++) {
            if (_bindBones[i] < 0) { continue; }
            var pose = skeleton.GetBoneGlobalPose(_bindBones[i]);
            var was = _layerBonePoses[i];
            _layerBonePoses[i] = pose;
            moved |= pose.Origin.DistanceSquaredTo(was.Origin) > MovingBoneDistance * MovingBoneDistance
                || pose.Basis.GetRotationQuaternion().AngleTo(was.Basis.GetRotationQuaternion()) > MovingBoneAngle;
        }
        return moved;
    }

    /// <summary>Refreshes the shared skin field after a paused action, so captures show current blood and tissue.</summary>
    internal void FinishLayer() {
        if (_body is not { } body) { return; }
        Layer.Finish();
        PrepareLayerInput(body);
        Layer.Rebuild(_gatherLayer);
    }

    public override void _Notification(int what) {
        if (what == NotificationPredelete) { Layer.Dispose(); }
    }

    /// <summary>Waits for every film's running step and publishes it; for tests.</summary>
    internal void FinishFilms() {
        BodyFilm.Finish();
        ReleaseBodyDrips(BodyToWorld());
        BedFilm.Finish();
        ReleaseBedDrips();
        FloorFilm.Finish();
        FinishLayer();
    }
}
