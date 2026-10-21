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
/// wall at its depth where it bleeds inside the opening (never on the skin over it), hidden once the cavity pool covers
/// it. A needle's puncture of the skin shows the same spot as a bleed onto it.
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
    private const int GroundBuildFailureLimit = 5;
    /// <summary>Each pool is one surface of the shared ground meshes, which Godot caps at 256 surfaces.</summary>
    private const int MaxGroundPools = 256;
    private const float DropRadius = 0.0035f;
    private float _dropVolumeScale;
    private float _unitDropMl;
    internal float DropShapeMl { get; private set; }
    /// <summary>Bleeding faster than this (ml/s) spurts.</summary>
    private const float SpurtRate = 3f;
    internal const float PoolMerge = 0.03f;
    private const float PoolMaxRadius = 0.18f;
    /// <summary>Reference detached drop volume in milliliters.</summary>
    internal const float DropMl = 0.15f;
    internal const float BedAreaVolumeRatio = BedLiquidSurface.FilmDepth / (PoolThickness * LiquidSurface.PatchMeanDepth);
    /// <summary>Footprint area divided by pi, calibrated to the floor mesh's integrated depth.</summary>
    internal const float PoolAreaPerDrop = DropMl * 0.000001f / (Mathf.Pi * PoolThickness * LiquidSurface.PatchMeanDepth);
    /// <summary>How high a pool stands.</summary>
    internal const float PoolThickness = 0.003f;
    internal const float FloorY = 0.004f;
    internal const float TableTop = Room.TableHeight + 0.001f;
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

    internal sealed class Drop(Vector3 position, Vector3 velocity, float area, Vector3 shape, float twist) {
        public Vector3 Position = position;
        public Vector3 Velocity = velocity;
        public float Area = area;
        public readonly Vector3 Shape = shape;
        public readonly float Twist = twist;
    }

    private static readonly PaintOp[] WipeSpot =
    [
        new(WoundMap.SkinSpot, 1f, WoundMap.Mode.Sub), new(WoundMap.Spot, 1f, WoundMap.Mode.Sub),
        new(WoundMap.SpotRadius, 1f, WoundMap.Mode.Sub), new(WoundMap.SpotDepth, 1f, WoundMap.Mode.Sub),
    ];

    private SurfaceBlood _surface = null!;
    private BodyRunoff _bodyRunoff = null!;
    internal BodyRunoff ModelRunoff => _bodyRunoff;
    internal float SurfaceSuppliedMl { get; private set; }
    internal float SurfaceAccountedMl => _surface.HeldMl + _surface.QueuedRunoffMl + _surface.RemovedMl
        + _bodyRunoff.HeldMl + GroundLiquidArea * DropMl / PoolAreaPerDrop;
    /// <summary>Physically squeezes existing surface film away from a newly closed seam.</summary>
    internal float DisplaceSurfaceFilm(Vector2[] path, float radius) => _surface.DisplaceStroke(path, radius);
    private float _surfaceClock;
    private float _surfaceGeometryClock;
    private int _surfaceGeometrySteps;
    private int _surfaceGeometryTopology;
    private bool _surfaceNeedsPaint;
    private readonly List<Drop> _drops = [];
    internal IReadOnlyList<Drop> Drops => _drops;
    internal IEnumerable<Vector3> AttachedDrips => _pools.SelectMany(pool => pool.Outlets)
        .Where(outlet => outlet.Active && outlet.Pulse.Pending > 0f && outlet.Progress >= 1f && outlet.DrawnProgress >= 1f).Select(outlet => outlet.Position);
    internal sealed class Outlet(int seed) {
        public readonly LiquidPulse Pulse = new(seed);
        public Vector3 Position;
        public bool Active;
        public BedLiquidSurface.Flow Flow;
        public float Progress;
        public float DrawnProgress;
        public float AllowedProgress;
        public float CoatingMl;
        public float CapacityMl;
        public readonly float Speed = 0.07f + 0.035f * (0.5f + 0.5f * Mathf.Sin(seed * 2.13f));
        internal void Uploaded(BedLiquidSurface.RivuletState state) {
            DrawnProgress = state.DrawnProgress;
            AllowedProgress = Mathf.Max(AllowedProgress, state.AllowedProgress);
            CapacityMl = state.CapacityMl;
        }
    }

    internal sealed record GroundFootprint(float Incoming, float Radius, Vector2[] Outline, float Area, BedLiquidSurface.Flow[] Outlets, bool FrameOnly = false);
    internal readonly record struct GroundDrawing(LiquidSurface? Top, LiquidSurface? Side, GroundFootprint? Footprint = null);

    internal sealed class GroundPool(Vector3 center, int seed) {
        public Vector3 Center = center;
        public bool OnBed;
        public readonly float Phase = seed * 0.7548777f;
        public readonly Outlet[] Outlets = Enumerable.Range(0, 12).Select(i => new Outlet(seed * 31 + i)).ToArray();
        public Vector2[] Outline = [];
        public float Radius;
        public float Area;
        public float Runoff;
        public float Incoming;
        public float Clock = seed * 0.013f % (1f / 15f);
        public bool HasOutlet;
        public bool FrameOnly;
        public int Support = -1;
        public bool TopDirty;
        public bool SideDirty;
        public Task<GroundDrawing>? Building;
        public float RetryIn;
        public float RetryDelay = 0.25f;
        public int Faults;
        internal int TopSurface = -1;
        internal int SideSurface = -1;
        public readonly LiquidSurface TopBuffer = new();
        public readonly LiquidSurface SideBuffer = new();
        internal IReadOnlyList<Vector3> TopVertices => TopBuffer.Vertices;
        internal IReadOnlyList<Vector3> SideVertices => SideBuffer.Vertices;
        public readonly BedLiquidSurface.RivuletScratch RivuletBuffer = new();
        public readonly BedLiquidSurface.Flow[] FlowBuffer = new BedLiquidSurface.Flow[12];

        public void Activate(Vector3 at, bool onBed, bool frameOnly) {
            OnBed = onBed;
            Center = at;
            FrameOnly = frameOnly;
            Outline = [];
            Radius = Area = Runoff = Incoming = 0f;
            HasOutlet = TopDirty = SideDirty = false;
            RetryIn = 0f;
            RetryDelay = 0.25f;
            Faults = 0;
            Clock = 0f;
            foreach (var outlet in Outlets) {
                outlet.Active = false;
                outlet.Progress = outlet.DrawnProgress = outlet.AllowedProgress = outlet.CoatingMl = outlet.CapacityMl = 0f;
                outlet.Pulse.Drain();
            }
        }
    }

    private readonly List<GroundPool> _pools = [];
    private readonly List<GroundPool> _grownFloorPools = [];
    private static readonly LiquidSurface NoSurface = new();
    internal IEnumerable<GroundPool> GroundDeposits => _pools;
    private MeshInstance3D _groundTopBatch = null!;
    private MeshInstance3D _groundSideBatch = null!;
    private ArrayMesh _groundTopMesh = null!;
    private ArrayMesh _groundSideMesh = null!;
    private int _uploadCursor;
    private readonly StandardMaterial3D _poolMaterial = Materials.BloodPool();
    private readonly RandomNumberGenerator _dripRng = new() { Seed = 83 };
    private float _cutClock;
    private MultiMeshInstance3D _neckMesh = null!;
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

    public BloodFlow() => _fasterFirst = new FasterFirst(this);

    public void Setup(PatientBody patientBody) {
        _body = patientBody;
        _surface = new SurfaceBlood(patientBody.SiteSize, patientBody.SkinHeight, uv => patientBody.LiquidSupport(uv) >= 0f, patientBody.Site.GlobalBasis.Scale.X);
        _surfaceGeometrySteps = patientBody.Tissue.StepsDone;
        _surfaceGeometryTopology = patientBody.Tissue.TopologyVersion;
        patientBody.WoundMap.SetBloodFilmSupport(patientBody.LiquidSupport);
        _groundTopMesh = new ArrayMesh();
        _groundTopBatch = new MeshInstance3D {
            Name = "GroundPoolTops",
            Mesh = _groundTopMesh,
            MaterialOverride = _poolMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            TopLevel = true,
        };
        _groundSideMesh = new ArrayMesh();
        _groundSideBatch = new MeshInstance3D {
            Name = "GroundPoolSides",
            Mesh = _groundSideMesh,
            MaterialOverride = _poolMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            TopLevel = true,
        };
        AddChild(_groundTopBatch);
        AddChild(_groundSideBatch);
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
        var skinMesh = patientBody.SkinMesh;
        var skeleton = skinMesh.GetNodeOrNull<Skeleton3D>(skinMesh.Skeleton);
        var runoff = new MeshInstance3D {
            Name = "BodyRunoff",
            Mesh = LiquidSurface.Primed(withUv: true, withSkin: true, withUv2: true),
            MaterialOverride = patientBody.SkinMaterial,
            Skin = skinMesh.Skin,
            Skeleton = skinMesh.Skeleton,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        runoff.Transform = skinMesh.Transform;
        runoff.SetInstanceShaderParameter("rivulet_depth", BodyLiquidSurface.FilmDepth);
        skinMesh.GetParent().AddChild(runoff);
        _bodyRunoff = new BodyRunoff(patientBody.LiquidContact, patientBody.Site, Bed, runoff, skinMesh, skeleton, patientBody.SiteSize);
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
        _neckMesh = new MultiMeshInstance3D {
            Name = "BloodNecks",
            Multimesh = new MultiMesh {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = _dropMesh.Multimesh.Mesh,
                InstanceCount = MaxDrops,
                VisibleInstanceCount = 0
            },
            MaterialOverride = _poolMaterial,
            TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_neckMesh);
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
            AddDrop(at, spray, ml * PoolAreaPerDrop / DropMl);
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

    private static Wound? NearestWound(PatientBody body, Vector2 uv) =>
        body.GetParent<Patient>().Wounds.Where(wound => !wound.IsInternal).MinBy(wound => wound.DistanceTo(uv));

    private sealed record WoundGeometry(int Topology, float Gap, Vector2[] Uvs, float[] Values) {
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

    private bool _rinseFirst;

    private void PoolInCuts(PatientBody body) {
        // A tissue pull can invalidate blood and irrigation simultaneously. Spread their contour builds
        // across consecutive frames, while alternating priority so continued filling cannot starve either.
        var deferCuts = _rinseFirst && _rinses.Count > 0;
        _rinseFirst = false;
        if (!deferCuts && Pools.Count > 0 && (_drawnCutGeometry.Count != _drawnCuts.Count || CutPoolsChanged(body))) {
            var surface = _cutBuffer;
            surface.Clear();
            foreach (var (uv, floor, fill) in Pools) { AddWoundSurface(surface, body, uv, floor, fill); }
            _cutPoolMesh.Mesh = surface.Upload(_cutPoolMesh.Mesh as ArrayMesh);
            var captureAll = _drawnCutGeometry.Count != _drawnCuts.Count
                || _drawnCutGeometry.Count != Pools.Count
                || _cutGeometryNeedsCapture.Count != Pools.Count;
            _drawnCuts.Clear();
            _drawnCuts.AddRange(Pools);
            if (captureAll) {
                _drawnCutGeometry.Clear();
                foreach (var pool in Pools) {
                    _drawnCutGeometry.Add(CaptureWoundGeometry(body, NearestWound(body, pool.Uv), pool.Uv));
                }
            } else {
                for (var i = 0; i < Pools.Count; i++) {
                    if (!_cutGeometryNeedsCapture[i]) { continue; }
                    var pool = Pools[i];
                    _drawnCutGeometry[i] = CaptureWoundGeometry(body, NearestWound(body, pool.Uv), pool.Uv);
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
            var wound = body.GetParent<Patient>().Wounds.FirstOrDefault(w => w.Id == id);
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
                AddWoundSurface(water, body, rinse.Uv, floor, fill);
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
        var wound = _body.GetParent<Patient>().Wounds.Where(w => !w.IsInternal).MinBy(w => w.DistanceTo(uv));
        if (wound is null || wound.DistanceTo(uv) > 0.05f) { return; }
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

    private static void AddWoundSurface(LiquidSurface surface, PatientBody body, Vector2 uv, float floor, float fill) {
        var wound = body.GetParent<Patient>().Wounds.Where(w => !w.IsInternal).MinBy(w => w.DistanceTo(uv));
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
        var depth = Mathf.Lerp(floor - CutPoolLift, Mathf.Max(floor * CutPoolBrim, CutPoolFull), Mathf.Clamp(fill, 0f, 1f));
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
            // Inside, it sits in the middle of the wall of the layer it bleeds from, no bigger than that wall is high, so
            // it never runs off it.
            var (top, thickness) = LayerAt(body, uv, depth);
            var radius = Mathf.Min(SpotMin + Mathf.Sqrt(rate) * SpotGrowth, WoundMap.MaxSpotRadius);
            if (depth > 0f) {
                radius = Mathf.Min(radius, thickness * 0.5f);
            }
            var size = body.MetersToUv(radius);
            var center = depth > 0f ? top + thickness * 0.5f : 0f;
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
    /// the cut there goes. A cut bleeding from its own edges (as deep as the skin) shows on the deepest layer it's cut
    /// through.
    /// </summary>
    private static (float Top, float Thickness) LayerAt(PatientBody body, Vector2 uv, float depth) {
        var cut = body.Tissue.DeepestCut(uv, body.Tissue.OpenReach * 2f) switch {
            TissueDepth.Muscle => 2,
            TissueDepth.Fat => 1,
            _ => 0,
        };
        // As deep as the skin is a cut's own edge: any layer the cut reaches.
        var lies = depth <= PatientBody.SkinThickness ? 2 : depth <= PatientBody.SkinThickness + body.FatThickness ? 1 : 2;
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
        while (_surface.TakeRunoff(out var uv, out var runoffMl)) {
            _bodyRunoff.Add(uv, body.Site.ToLocal(body.UvToWorld(uv)), runoffMl);
        }
        _bodyRunoff.Tick(delta);
        while (_drops.Count < MaxDrops && _bodyRunoff.TryRelease(out var at, out var ml, out var onBed)) {
            var area = ml * PoolAreaPerDrop / DropMl;
            if (onBed) { Pool(at, area); } else { AddDrop(at, Vector3.Zero, area); }
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
        _surfaceNeedsPaint = _surface.NeedsPaint;
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

    private void AddDrop(Vector3 at, Vector3 velocity, float area = PoolAreaPerDrop) {
        if (_drops.Count >= MaxDrops) {
            // No room to draw another: the newest drop in flight carries this liquid too, so none is lost.
            _drops[^1].Area += area;
            return;
        }
        var shape = new Vector3(_dripRng.RandfRange(0.6f, 1.15f), _dripRng.RandfRange(1.1f, 2.7f), _dripRng.RandfRange(0.65f, 1.1f));
        var amount = Mathf.Pow(area / PoolAreaPerDrop / (shape.X * shape.Y * shape.Z), 1f / 3f) * _dropVolumeScale;
        _drops.Add(new Drop(at, velocity, area, shape * amount, _dripRng.RandfRange(-Mathf.Pi, Mathf.Pi)));
    }

    internal bool GroundMeshesReady => _pools.All(pool => pool.Incoming <= 0f && !pool.TopDirty && !pool.SideDirty && pool.Building is null);

    private BedLiquidSurface Bed => _body!.GetParent<Patient>().GetParent<Surgery>().Room.BedSurface;

    /// <summary>Droplets intersect the actual upper mattress or frame triangles; below them they keep falling to the floor.</summary>
    private void Fall(float delta) {
        var landingBed = _drops.Count > 0 ? Bed : null;
        for (var i = 0; i < _drops.Count; i++) {
            var drop = _drops[i];
            drop.Velocity += Vector3.Down * 9.8f * delta;
            var position = drop.Position + drop.Velocity * delta;
            var bedHit = landingBed?.Contact(drop.Position, position);
            if (bedHit is not null || position.Y <= FloorY) {
                var hit = bedHit?.At ?? drop.Position.Lerp(position,
                    Mathf.Clamp((drop.Position.Y - FloorY) / (drop.Position.Y - position.Y), 0f, 1f));
                if (bedHit is null) { hit.Y = FloorY; }
                _drops.RemoveAt(i--);
                Pool(hit, drop.Area, bedHit?.Frame ?? false, bedHit?.Support ?? -1);
            } else {
                drop.Position = position;
            }
        }
        ReleaseRunoff(delta);
        // Task completion can bunch up even when submissions are staggered. Upload one pool per frame.
        for (var n = 0; n < _pools.Count; n++) {
            var index = (_uploadCursor + n) % _pools.Count;
            var pool = _pools[index];
            if (!WorkerTask.TryTakeResult(ref pool.Building, out var drawing, out var failure)) { continue; }
            if (failure is not null) {
                pool.TopDirty = true;
                pool.SideDirty = true;
                pool.Faults++;
                if (pool.Faults == 1) { GD.PushWarning($"Blood ground-pool mesh build failed; retrying with backoff: {failure}"); }
                pool.RetryIn = pool.Faults >= GroundBuildFailureLimit ? float.PositiveInfinity : pool.RetryDelay;
                pool.RetryDelay = Mathf.Min(pool.RetryDelay * 2f, 8f);
                continue;
            }
            pool.Faults = 0;
            pool.RetryDelay = 0.25f;
            var completedDrawing = drawing!;
            if (completedDrawing.Top is { } top) { ReplaceTopSurface(pool, top); }
            if (completedDrawing.Footprint is { } footprint) {
                // Cover() returns projected footprint area; a thin bed film holds only its calibrated share of
                // the equivalent flat-pool volume. Convert before reconciling the incoming dose.
                var heldArea = footprint.Area * BedAreaVolumeRatio;
                pool.Runoff += Mathf.Max(0f, footprint.Incoming - Mathf.Max(0f, heldArea - pool.Area));
                pool.Incoming = Mathf.Max(0f, pool.Incoming - footprint.Incoming);
                pool.Radius = footprint.Radius;
                pool.Outline = footprint.Outline;
                pool.Area = heldArea;
                pool.FrameOnly = footprint.FrameOnly;
                OutletsAtEdges(pool, footprint.Outlets);
                pool.SideDirty = true;
            }
            if (completedDrawing.Side is { } side) {
                ReplaceSideSurface(pool, side);
                for (var i = 0; i < pool.Outlets.Length; i++) {
                    var outlet = pool.Outlets[i];
                    outlet.Uploaded(pool.RivuletBuffer!.States[i]);
                }
            }
            _uploadCursor = (index + 1) % _pools.Count;
            break;
        }
        foreach (var pool in _pools) {
            // Spread the 15 Hz work across frames when several pools are growing together.
            pool.RetryIn = Mathf.Max(0f, pool.RetryIn - delta);
            pool.Clock += delta;
            if (pool.Clock < 1f / 15f) { continue; }
            pool.Clock %= 1f / 15f;
            if (pool.RetryIn > 0f) { continue; }
            if (pool.Building is not null) { continue; }
            if (pool.Incoming > 0f) {
                var radius = GrowingRadius(pool, pool.Incoming);
                if (pool.OnBed && radius != pool.Radius) {
                    var amount = pool.Incoming;
                    var outline = LiquidSurface.Outline(radius, pool.Phase);
                    var center = pool.Center;
                    var bed = Bed;
                    var flows = SnapshotRivulets(pool);
                    pool.TopDirty = pool.SideDirty = false;
                    pool.Building = Task.Run(() => {
                        var top = pool.TopBuffer;
                        top.Clear();
                        // One intersection supplies both the displayed footprint and its conserved capacity.
                        var frameOnly = pool.FrameOnly;
                        var area = frameOnly ? bed.CoverFrame(outline, center, top, pool.Support) : bed.Cover(outline, center, top, pool.Support);
                        var outlets = frameOnly ? bed.FrameOutlets(outline, center, pool.Support) : bed.Outlets(outline, center, pool.Support);
                        top.PrepareUpload(center);
                        var side = DrawRivulets(pool, bed, flows, center);
                        return new GroundDrawing(top, side, new GroundFootprint(amount, radius, outline, area, outlets, frameOnly));
                    });
                    continue;
                }
                if (pool.OnBed) {
                    // An already draining footprint keeps its size; every additional milliliter feeds its outlets.
                    pool.Runoff += pool.Incoming;
                } else {
                    pool.Radius = radius;
                    pool.Outline = LiquidSurface.Outline(radius, pool.Phase);
                    pool.Area = LiquidSurface.Area(pool.Outline);
                    pool.TopDirty = true;
                    _grownFloorPools.Add(pool);
                }
                pool.Incoming = 0f;
            }
            if ((!pool.TopDirty && !pool.SideDirty) || pool.Building is not null) { continue; }
            if (pool.OnBed) {
                // A growing rivulet does not change the pool above it. Build each surface only when it changed;
                // freeze its packed arrays on the worker so uploads do not copy geometry on the gameplay thread.
                var bed = Bed;
                var outline = pool.Outline;
                var center = pool.Center;
                var topDirty = pool.TopDirty;
                var flows = SnapshotRivulets(pool);
                pool.Building = Task.Run(() => {
                    LiquidSurface? top = null;
                    LiquidSurface? side = null;
                    if (topDirty) {
                        top = pool.TopBuffer;
                        top.Clear();
                        if (pool.FrameOnly) { bed.CoverFrame(outline, center, top, pool.Support); } else { bed.Cover(outline, center, top, pool.Support); }
                        top.PrepareUpload(center);
                    }
                    side = DrawRivulets(pool, bed, flows, center);
                    return new GroundDrawing(top, side);
                });
            } else {
                var surface = pool.TopBuffer;
                var outline = pool.Outline;
                var center = pool.Center;
                pool.Building = Task.Run(() => {
                    surface.Clear();
                    surface.Patch(outline, PoolThickness);
                    surface.PrepareUpload(center);
                    return new GroundDrawing(surface, null);
                });
            }
            pool.TopDirty = pool.SideDirty = false;
        }
        CoalesceFloorPools();
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

    /// <summary>A floor pool that has spread over a smaller one takes its volume: the smaller one's stain lies inside
    /// it already, so nothing moves or uncovers, and a long bleed doesn't keep one draw call per landing spot. Pools
    /// that only overlap stay apart; drawn together, they already read as one stain.</summary>
    private void CoalesceFloorPools() {
        // A pool that took another's volume grows on its next tick and may cover more, so it is checked again.
        while (_grownFloorPools.Count > 0) {
            var pool = _grownFloorPools[^1];
            _grownFloorPools.RemoveAt(_grownFloorPools.Count - 1);
            if (!_pools.Contains(pool)) { continue; }
            foreach (var other in _pools) {
                if (ReferenceEquals(other, pool) || other.OnBed
                    || Mathf.Abs(other.Center.Y - pool.Center.Y) > 0.001f) { continue; }
                var (into, from) = pool.Radius >= other.Radius ? (pool, other) : (other, pool);
                var apart = new Vector2(from.Center.X - into.Center.X, from.Center.Z - into.Center.Z).Length();
                if (apart + from.Radius > into.Radius) { continue; }
                into.Incoming += from.Area + from.Incoming;
                into.TopDirty = true;
                ReplaceTopSurface(from, NoSurface);
                _pools.Remove(from);
                _grownFloorPools.Add(into);
                break;
            }
        }
    }

    private static BedLiquidSurface.Flow[]? SnapshotRivulets(GroundPool pool) {
        if (!pool.SideDirty) { return null; }
        var flows = pool.FlowBuffer;
        for (var i = 0; i < pool.Outlets.Length; i++) {
            var outlet = pool.Outlets[i];
            flows[i] = outlet.Flow with { Progress = outlet.Active ? outlet.Progress : 0f };
            pool.RivuletBuffer!.AvailableMl[i] = outlet.CoatingMl;
        }
        return flows;
    }

    private void ReplaceTopSurface(GroundPool pool, LiquidSurface surface) =>
        ReplaceBatchSurface(_groundTopMesh, pool, surface, ref pool.TopSurface, false);

    private void ReplaceSideSurface(GroundPool pool, LiquidSurface surface) =>
        ReplaceBatchSurface(_groundSideMesh, pool, surface, ref pool.SideSurface, true);

    private void ReplaceBatchSurface(ArrayMesh mesh, GroundPool pool, LiquidSurface surface, ref int index, bool side) {
        if (index >= 0) {
            var removed = index;
            mesh.SurfaceRemove(removed);
            foreach (var other in _pools) {
                if (ReferenceEquals(other, pool)) { continue; }
                ref var otherIndex = ref (side ? ref other.SideSurface : ref other.TopSurface);
                if (otherIndex > removed) { otherIndex--; }
            }
            index = -1;
        }
        if (surface.VertexCount == 0) { return; }
        index = surface.AppendUpload(mesh);
    }

    private static LiquidSurface? DrawRivulets(GroundPool pool, BedLiquidSurface bed, BedLiquidSurface.Flow[]? flows, Vector3 center) {
        if (flows is null) { return null; }
        var side = pool.SideBuffer!;
        side.Clear();
        bed.Rivulets(side, flows, center, pool.RivuletBuffer!);
        side.PrepareUpload(center);
        return side;
    }

    private void ReleaseRunoff(float delta) {
        var necks = _neckMesh.Multimesh;
        var count = 0;
        foreach (var pool in _pools) {
            if (!pool.OnBed) { continue; }
            var active = pool.Outlets.Count(outlet => outlet.Active);
            var supplied = pool.Runoff * DropMl / PoolAreaPerDrop / Math.Max(active, 1);
            foreach (var outlet in pool.Outlets) {
                if (!outlet.Active) { continue; }
                var pulse = outlet.Pulse;
                outlet.CoatingMl += supplied;
                if (supplied > 0f && outlet.Progress < 1f) { pool.SideDirty = true; }
                if (outlet.Progress < outlet.AllowedProgress) {
                    outlet.Progress = Mathf.Min(outlet.AllowedProgress, outlet.Progress + delta * outlet.Speed / Mathf.Max(outlet.Flow.Length, 0.001f));
                    pool.SideDirty = true;
                }
                if (outlet.Progress < 1f || outlet.DrawnProgress < 1f) { continue; }
                // The attached coating stays on the bed. Only liquid beyond its actual mesh capacity can drip.
                var excess = Mathf.Max(0f, outlet.CoatingMl - outlet.CapacityMl);
                pulse.Add(excess * PoolAreaPerDrop / DropMl);
                if (excess > 0f) { outlet.CoatingMl = outlet.CapacityMl; }
                var length = pulse.Length;
                var released = pulse.Advance(delta, _drops.Count < MaxDrops);
                if (released > 0f) {
                    AddDrop(outlet.Position + Vector3.Down * length, new Vector3(_dripRng.RandfRange(-0.025f, 0.025f),
                        -_dripRng.RandfRange(0.08f, 0.18f), _dripRng.RandfRange(-0.025f, 0.025f)), released);
                }
                if (pulse.Pending <= 0f || count >= MaxDrops) { continue; }
                // A thin pending dose may stretch a neck, but cannot create a full-width ligament for free.
                var width = Mathf.Min(pulse.Width, Mathf.Sqrt(pulse.Pending * DropMl / PoolAreaPerDrop / (_unitDropMl * pulse.Length * 0.4f)));
                var shape = new Vector3(width, pulse.Length * 0.5f, width * 0.8f);
                necks.SetInstanceTransform(count++, new Transform3D(Basis.Identity.Scaled(shape),
                    outlet.Position + Vector3.Down * pulse.Length * 0.5f));
            }
            if (active > 0) { pool.Runoff = 0f; }
        }
        necks.VisibleInstanceCount = count;
    }

    private static void OutletsAtEdges(GroundPool pool, BedLiquidSurface.Flow[] positions) {
        pool.HasOutlet = positions.Length > 0;
        for (var i = 0; i < pool.Outlets.Length; i++) {
            var outlet = pool.Outlets[i];
            outlet.Active = i < positions.Length || outlet.CoatingMl > 0f || outlet.Pulse.Pending > 0f;
            // A wetted streak stays anchored as its upstream pool grows over it. Its retained capacity then
            // remains valid instead of moving already deposited film to a different outlet on every repaint.
            if (i < positions.Length && outlet.CoatingMl <= 0f && outlet.DrawnProgress <= 0f) {
                outlet.Flow = positions[i] with { Phase = pool.Phase + i * 2.17f };
                outlet.Position = positions[i].End;
            }
        }
    }

    private static float GrowingRadius(GroundPool pool, float incoming) {
        var radius = Mathf.Sqrt(pool.Radius * pool.Radius + incoming / (pool.OnBed ? BedAreaVolumeRatio : 1f));
        // Retaining a footprint only makes sense once there is a real drain. Interior pools must reach one;
        // a floor has no unsupported area into which excess volume can disappear.
        return pool.OnBed && pool.HasOutlet ? Mathf.Min(radius, Mathf.Max(pool.Radius, PoolMaxRadius)) : radius;
    }

    internal float PendingDripMl => _pools.Sum(p => p.Outlets.Sum(o => o.Pulse.Pending)) * DropMl / PoolAreaPerDrop;
    internal float DrawnNeckMl {
        get {
            var mesh = _neckMesh.Multimesh;
            var total = 0f;
            for (var i = 0; i < mesh.VisibleInstanceCount; i++) { total += Mathf.Abs(mesh.GetInstanceTransform(i).Basis.Determinant()) * _unitDropMl; }
            return total;
        }
    }
    internal float GroundLiquidArea => _drops.Sum(drop => drop.Area)
        + _pools.Sum(pool => pool.Area + pool.Incoming + pool.Runoff + pool.Outlets.Sum(outlet => outlet.CoatingMl * PoolAreaPerDrop / DropMl + outlet.Pulse.Pending));

    /// <summary>Pool footprint area grows with deposited blood. Bed pools narrow at the supporting edges; the
    /// unsupported share becomes runoff with its own area, which falls and grows a separate floor pool.</summary>
    internal void Pool(Vector3 at, float area = PoolAreaPerDrop, bool? frameOnly = null, int support = -1) {
        BedLiquidSurface.Hit? landing = support >= 0 ? new BedLiquidSurface.Hit(at, frameOnly == true, support) : null;
        if (landing is null && at.Y > FloorY + 0.01f) {
            // Legacy/manual deposits on the reference plane are resolved to the real support. Physical
            // impacts already carry their exact support and height, including lower overlapping shelves.
            var reach = Mathf.Abs(at.Y - TableTop) < 0.0015f ? 0.2f : 0.003f;
            landing = Bed.Contact(at + Vector3.Up * 0.002f, at - Vector3.Up * reach);
            if (landing is null) { AddDrop(at, Vector3.Zero, area); return; }
        }
        var onBed = landing.HasValue;
        var onFrame = landing?.Frame ?? false;
        support = landing?.Support ?? -1;
        at = landing?.At ?? at;
        GroundPool? target = null;
        foreach (var pool in _pools) {
            if (pool.OnBed == onBed && pool.Support == support && (onBed || Mathf.Abs(pool.Center.Y - at.Y) < 0.001f)
                && pool.Center.DistanceTo(at) < PoolMerge + GrowingRadius(pool, pool.Incoming)) {
                target = pool;
                break;
            }
        }
        if (target is null && _pools.Count >= MaxGroundPools) {
            float Distance(GroundPool pool) => pool.Center.DistanceSquaredTo(at);
            target = _pools.Where(pool => pool.OnBed == onBed && pool.Support == support).MinBy(Distance)
                ?? _pools.MinBy(Distance);
        }
        if (target is null) {
            target = new GroundPool(at, _pools.Count + 1);
            target.Activate(at, onBed, onFrame);
            target.Support = support;
            _pools.Add(target);
        }
        target.Incoming += area;
    }
}
