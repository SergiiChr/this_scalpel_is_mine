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
    private const float DropRadius = 0.0035f;
    /// <summary>Bleeding faster than this (ml/s) spurts.</summary>
    private const float SpurtRate = 3f;
    private const float PoolMerge = 0.03f;
    private const float PoolMaxRadius = 0.18f;
    /// <summary>Pool area (m²) added per drop.</summary>
    private const float PoolAreaPerDrop = 0.00008f;
    /// <summary>How high a pool stands.</summary>
    private const float PoolThickness = 0.003f;
    private const float FloorY = 0.004f;
    /// <summary>Puddle radius around a bleeding wound (uv) with no blood yet.</summary>
    private const float PuddleStart = 0.015f;
    /// <summary>How much a puddle grows per sqrt(ml).</summary>
    private const float PuddleGrowth = 0.012f;
    private const float PuddleMax = 0.14f;
    private const float PuddlePaintInterval = 0.2f;
    /// <summary>Blood thrown up closer than this (m) to the camera, while looking at it, can land on the view.</summary>
    private const float SplashReach = 0.75f;
    /// <summary>Radius (m) of a bleed's spot, and how much it grows per sqrt(ml/s): the slightest bleed still shows.
    /// </summary>
    private const float SpotMin = 0.0012f;
    private const float SpotGrowth = 0.0015f;
    /// <summary>Radius (m) of the spot a needle leaves where it went into the skin.</summary>
    private const float PunctureRadius = 0.003f;
    /// <summary>Radius (m) of the pool held in a cut when it starts, and full. The skin hides it beyond the opening.
    /// </summary>
    private const float CutPoolStart = 0.003f;
    private const float CutPoolReach = 0.008f;
    /// <summary>How far (m) a pool in a cut keeps off its floor, and how high it stands.</summary>
    private const float CutPoolLift = 0.0003f;
    /// <summary>How deep under the skin a full pool in a cut lies: this share of its floor's depth, at least
    /// CutPoolFull (m).</summary>
    private const float CutPoolBrim = 0.3f;
    private const float CutPoolFull = 0.0006f;
    /// <summary>Bruise radius (uv) over a bleed under closed skin, and how much it grows per sqrt(ml).</summary>
    private const float BruiseStart = 0.02f;
    private const float BruiseGrowth = 0.01f;
    private const float BruiseMax = 0.1f;

    private sealed class Drop(Vector3 position, Vector3 velocity) {
        public Vector3 Position = position;
        public Vector3 Velocity = velocity;
    }

    private static readonly PaintOp[] WipeSpot =
    [
        new(WoundMap.SkinSpot, 1f, WoundMap.Mode.Sub), new(WoundMap.Spot, 1f, WoundMap.Mode.Sub),
        new(WoundMap.SpotRadius, 1f, WoundMap.Mode.Sub), new(WoundMap.SpotDepth, 1f, WoundMap.Mode.Sub),
    ];

    private SurfaceBlood _surface = null!;
    private float _surfaceClock;
    private bool _surfaceNeedsPaint;
    private float _dripMl;
    private readonly List<Drop> _drops = [];
    private readonly List<MeshInstance3D> _pools = [];
    /// <summary>Blood (ml) welled up around each source, keyed by its rounded uv. Sources come and go and change
    /// order.</summary>
    private readonly Dictionary<Vector2I, float> _pooled = [];
    /// <summary>Blood (ml) collected under closed skin at each source, keyed like <see cref="_pooled"/>.</summary>
    private readonly Dictionary<Vector2I, float> _bruised = [];
    private readonly RandomNumberGenerator _rng = new() { Seed = 7 };
    private float _puddleTimer;
    /// <summary>This frame paints puddles and bruises.</summary>
    private bool _paintPuddles;
    private MultiMeshInstance3D _dropMesh = null!;
    private MultiMeshInstance3D _cutPoolMesh = null!;
    private readonly List<Vector3> _cutPools = [];
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
        _surface = new SurfaceBlood(patientBody.SiteSize, patientBody.SurfaceHeight, patientBody.OnBody);
        _dropMesh = new MultiMeshInstance3D {
            Multimesh = new MultiMesh {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = new SphereMesh {
                    Radius = DropRadius,
                    Height = DropRadius * 2.6f,
                    RadialSegments = 6,
                    Rings = 4,
                },
                InstanceCount = MaxDrops,
                VisibleInstanceCount = 0,
            },
            MaterialOverride = Materials.BloodPool(),
            TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_dropMesh);
        _cutPoolMesh = new MultiMeshInstance3D {
            Multimesh = new MultiMesh {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 20, Rings = 4, IsHemisphere = true },
                InstanceCount = 16,
                VisibleInstanceCount = 0,
            },
            MaterialOverride = Materials.BloodPool(),
            TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_cutPoolMesh);
    }

    public override void _Process(double delta) {
        if (_body is null || delta <= 0) {
            return;
        }
        Spawn(_body, (float)delta);
        Inside(_body, (float)delta);
        PoolInCuts(_body);
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
            Puddle(body, uv, rate * delta, _paintPuddles);
            _surface.Add(uv, rate * delta);
            Spurt(body, body.UvToWorld(uv), rate, delta);
        }
    }

    /// <summary>A strong bleed throws droplets up from <paramref name="at"/> now and then.</summary>
    private void Spurt(PatientBody body, Vector3 at, float rate, float delta) {
        if (rate > SpurtRate && _rng.Randf() < delta * (rate - SpurtRate) * 1.5f) {
            var spray = body.Site.GlobalBasis.Y * _rng.RandfRange(0.6f, 1.3f)
                + new Vector3(_rng.RandfRange(-0.4f, 0.4f), 0f, _rng.RandfRange(-0.4f, 0.4f));
            AddDrop(at, spray);
            Splash(at, 0.04f);
        }
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
            Spurt(body, body.Site.ToGlobal(local), rate, delta);
        }
    }

    /// <summary>
    /// Blood held in a cut that doesn't reach the cavity: a flat pool resting on the first layer the cut hasn't gone
    /// through, rising toward the skin as it fills and spreading along the cut. The skin around hides all but what lies
    /// in the opening. It doesn't move with the tissue, like the cavity's pool.
    /// </summary>
    private void PoolInCuts(PatientBody body) {
        var multimesh = _cutPoolMesh.Multimesh;
        _cutPools.Clear();
        if (Pools.Count > multimesh.InstanceCount) {
            multimesh.InstanceCount = Pools.Count * 2;
        }
        foreach (var (uv, floor, fill) in Pools) {
            // Just above the floor when it starts, so it never sinks into the layer it rests on, and full still a little
            // under the skin, which curves away from a flat pool around the cut.
            var depth = Mathf.Lerp(floor - CutPoolLift, Mathf.Max(floor * CutPoolBrim, CutPoolFull), Mathf.Clamp(fill, 0f, 1f));
            // No wider than the cut gapes: the skin beside it curves away, sharply on a limb, and would show it.
            var radius = Mathf.Min(Mathf.Lerp(CutPoolStart, CutPoolReach, Mathf.Sqrt(Mathf.Clamp(fill, 0f, 1f))),
                Mathf.Max(body.Tissue.GapAt(uv, 0.02f) * 0.5f, CutPoolLift * 2f));
            var at = body.UvToWorld(uv, depth);
            // Level with the skin over it, which a limb tilts away from the site's own plane.
            var e = body.MetersToUv(0.003f);
            var across = body.UvToWorld(uv + new Vector2(e, 0f)) - body.UvToWorld(uv - new Vector2(e, 0f));
            var along = body.UvToWorld(uv + new Vector2(0f, e)) - body.UvToWorld(uv - new Vector2(0f, e));
            var up = along.Cross(across).Normalized();
            if (up.Dot(body.Site.GlobalBasis.Y) < 0f) {
                up = -up;
            }
            var x = across.Slide(up).Normalized();
            var basis = new Basis(x, up, x.Cross(up)).Scaled(new Vector3(radius, CutPoolLift, radius));
            multimesh.SetInstanceTransform(_cutPools.Count, new Transform3D(basis, at));
            _cutPools.Add(at);
        }
        multimesh.VisibleInstanceCount = _cutPools.Count;
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

    /// <summary>
    /// Grows the puddle around a wound by <paramref name="ml"/> and returns its radius (uv). Swabbing it away (the
    /// fluid map comes back clean at the wound) starts it over. Painted a few times a second as overlapping blobs, so
    /// its edge is irregular. Anchored lobes grow with the pool instead of changing shape at each repaint.
    /// </summary>
    private float Puddle(PatientBody body, Vector2 uv, float ml, bool paint) {
        var key = (Vector2I)(uv * 100f).Round();
        var map = body.WoundMap;
        if (_pooled.ContainsKey(key) && map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, uv) < 0.3f) {
            _pooled[key] = 0f;
        }
        var pooled = _pooled.GetValueOrDefault(key) + ml;
        _pooled[key] = pooled;
        var radius = Mathf.Min(PuddleStart + Mathf.Sqrt(pooled) * PuddleGrowth, PuddleMax);
        if (paint) {
            map.Disk(WoundMap.Layer.Fluids, WoundMap.Blood, uv, radius * 0.8f, 0.95f, WoundMap.Mode.Max);
            var phase = key.X * 2.39f + key.Y * 3.71f;
            var lobes = 3 + Math.Abs((key.X * 7 + key.Y * 11) % 3);
            for (var lobe = 0; lobe < lobes; lobe++) {
                var angle = phase + lobe * Mathf.Tau / lobes;
                var blob = Vector2.FromAngle(angle) * radius * (0.17f + 0.12f * Mathf.Sin(angle * 3f));
                map.Disk(WoundMap.Layer.Fluids, WoundMap.Blood, uv + blob,
                    radius * (0.76f + 0.1f * Mathf.Cos(angle * 2f)), 0.95f, WoundMap.Mode.Max);
            }
        }
        return radius;
    }

    /// <summary>Pressure spreads a connected film sideways while gravity transports it downhill. The film retains
    /// thin residue, coalesces without separate path heads, and loses volume when a swab removes its painted blood.</summary>
    private void Flow(PatientBody body, float delta) {
        _surfaceClock += delta;
        // Alternate transport and texture work so they do not land together on the same gameplay frame.
        if (_surfaceNeedsPaint) {
            _surface.ReconcileWipes(body.WoundMap);
            _surface.Paint(body.WoundMap);
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
        var drained = _surface.RunoffMl;
        for (var i = 0; i < steps; i++) {
            _surface.Step(body.Site.GlobalBasis, elapsed / steps);
        }
        _dripMl += _surface.RunoffMl - drained;
        while (_dripMl >= 0.15f && _drops.Count < MaxDrops) {
            _dripMl -= 0.15f;
            AddDrop(body.UvToWorld(_surface.DrainUv), Vector3.Zero);
        }
        _surfaceNeedsPaint = true;
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

    private void AddDrop(Vector3 at, Vector3 velocity) {
        if (_drops.Count < MaxDrops) {
            _drops.Add(new Drop(at, velocity));
        }
    }

    /// <summary>Droplets fall under gravity and land on the table top or the floor, where they pool.</summary>
    private void Fall(float delta) {
        const float TableTop = Room.TableHeight + 0.07f;
        for (var i = 0; i < _drops.Count; i++) {
            var drop = _drops[i];
            drop.Velocity += Vector3.Down * 9.8f * delta;
            var position = drop.Position + drop.Velocity * delta;
            var onTable = position.X > Room.TableFoot && position.X < Room.TableHead && Mathf.Abs(position.Z) < 0.31f;
            var ground = onTable && drop.Position.Y >= TableTop ? TableTop : FloorY;
            if (position.Y <= ground) {
                Pool(new Vector3(position.X, ground, position.Z));
                _drops.RemoveAt(i--);
            } else {
                drop.Position = position;
            }
        }
        var multimesh = _dropMesh.Multimesh;
        multimesh.VisibleInstanceCount = _drops.Count;
        for (var i = 0; i < _drops.Count; i++) {
            multimesh.SetInstanceTransform(i, new Transform3D(Basis.Identity, _drops[i].Position));
        }
    }

    /// <summary>
    /// Adds a drop's worth of blood to the nearest pool, or starts a new one. Pools grow with the square root of
    /// volume. Each is a very flat dome, so its rounded rim catches the light like standing liquid.
    /// </summary>
    private void Pool(Vector3 at) {
        foreach (var pool in _pools) {
            if (pool.Position.DistanceTo(at) < PoolMerge + pool.Scale.X) {
                var radius = Mathf.Min(Mathf.Sqrt(pool.Scale.X * pool.Scale.X + PoolAreaPerDrop), PoolMaxRadius);
                pool.Scale = new Vector3(radius, PoolThickness, radius);
                return;
            }
        }
        var mesh = new MeshInstance3D {
            Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 28, Rings = 6, IsHemisphere = true },
            MaterialOverride = Materials.BloodPool(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            TopLevel = true,
        };
        AddChild(mesh);
        mesh.Position = at;
        mesh.Scale = new Vector3(0.012f, PoolThickness, 0.012f);
        _pools.Add(mesh);
    }
}
