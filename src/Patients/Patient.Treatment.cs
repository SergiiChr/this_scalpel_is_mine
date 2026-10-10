namespace Scalpel.Patients;

/// <summary>Damage and treatment: what tools do to the body (host only, called by tool actions).</summary>
public partial class Patient
{
    /// <summary>Closures whose stitch tension follows the pressure level: loose leaks, tight can tear through.</summary>
    public static readonly IReadOnlyList<string> TensionedClosures = ["paper_clips"];
    /// <summary>Stitch rest length per pressure level (1 loose, 2 right, 3 tight), relative to the skin's own springs.
    /// </summary>
    public static readonly float[] StitchTension = [0.95f, 1.25f, 0.95f, 0.8f];
    /// <summary>How much load a running thread's spans take before they snap, per layer it's sewn in.</summary>
    public static readonly float[] ThreadStrength = [0f, 2f, 2.15f, 3f];
    /// <summary>How much of the skin's wounds (by length, <see cref="SkinClosure"/>) has to be closed for them to count
    /// as closed, in every scenario: a few millimeters left open per ten centimeters of cut.</summary>
    public const float ClosedEnough = 0.9f;
    /// <summary>Bleeding (ml/s) from a vessel a staple goes through.</summary>
    public const float StapleNick = 0.6f;
    /// <summary>How far (uv) from a point of a wound its muscle counts as underneath it.</summary>
    public const float MuscleReach = 0.03f;
    /// <summary>Gap in meters that counts as a fully opened wound.</summary>
    public const float FullGap = 0.012f;
    /// <summary>How much of a local block still dulls deep pain: it numbs the skin and what's under it, not the bone.
    /// </summary>
    public const float DeepBlock = 0.3f;
    /// <summary>Wound depth (0..1) below which a cut is only through the skin, see <see cref="TissueDepthOf"/>.
    /// </summary>
    public const float SkinDepth = 0.4f;
    /// <summary>Pain from the blade scraping bone: a jolt when it first touches.</summary>
    public const float BoneJolt = 0.35f;
    /// <summary>Pain per second while a blade grates on bone.</summary>
    public const float BonePain = 0.6f;
    /// <summary>How close (meters) to the blade's tip a bone has to be for the blade to grate on it.</summary>
    public const float BladeReach = 0.005f;
    /// <summary>How close (meters) to the blade's tip an organ has to be for it to be cut.</summary>
    public const float OrganReach = 0.001f;
    /// <summary>How high (meters) forceps lift a piece of skin cut out all round before it comes off whole, as a graft.
    /// </summary>
    public const float PieceLift = 0.01f;

    /// <summary>Which wound each continuous blade stroke grows (stroke key -> wound).</summary>
    private readonly Dictionary<long, Wound> _strokeWounds = [];

    /// <summary>The wound stroke <paramref name="strokeKey"/> made (<see cref="Cut"/>), null for none.</summary>
    internal Wound? StrokeWound(long strokeKey) => _strokeWounds.GetValueOrDefault(strokeKey);
    /// <summary>When (seconds) a blade last grated on a bone, so touching it again after a pause hurts with a jolt
    /// again.</summary>
    private double _boneTouched = double.NegativeInfinity;
    private ulong _tearNoticeMsec;
    /// <summary>
    /// Running sutures (host): per thread, per wound it crossed, the closure of each bin it holds (skin and muscle) as
    /// it was before the thread held it.
    /// </summary>
    private readonly Dictionary<int, Dictionary<int, (Dictionary<int, float> Skin, Dictionary<int, float> Muscle)>> _sutures = [];
    private int _nextSuture;
    /// <summary>The tissue's steps and topology the wounds' closure was last settled from (SettleClosures()).</summary>
    private (int Steps, int Topology) _settledFor = (-1, -1);

    /// <summary>
    /// A skin wound is as closed as its tissue looks. A bin whose edges meet and are held there
    /// (<see cref="TissueSim.ClosedAt"/>) is closed, whatever closed it; one a closure counted as closed while its edges
    /// still gape is only as closed as the gap allows. Thread tension, tape and staples claim closure, this has the last
    /// word. Only when the tissue changed.
    /// </summary>
    private void SettleClosures()
    {
        var tissue = Body.Tissue;
        var state = (tissue.StepsDone, tissue.TopologyVersion);
        if (state == _settledFor)
        {
            return;
        }
        _settledFor = state;
        foreach (var wound in Wounds.Where(w => w.IsSkinCut))
        {
            var points = wound.BinPositions();
            var gaps = tissue.GapsNear(points, tissue.SeamReach);
            var held = tissue.HeldNear(points);
            for (var i = 0; i < points.Count; i++)
            {
                if (gaps[i] <= TissueSim.ClosedGap && held[i])
                {
                    wound.Bins[i] = 1f;
                }
                else if (wound.Bins[i] >= 1f)
                {
                    wound.Bins[i] = Mathf.Clamp(1f - gaps[i] / FullGap, 0f, 0.95f);
                }
            }
        }
    }

    /// <summary>A needle dragged out sideways from <paramref name="from"/> to <paramref name="to"/> (world space): a
    /// short scratch that bleeds a little and hurts.</summary>
    public void NeedleTear(Vector3 from, Vector3 to)
    {
        var probe = Body.Probe(to);
        var onSite = probe.Zone == SiteZone.Site;
        if (onSite)
        {
            Paint(WoundMap.Layer.Wounds, WoundMap.Cut, Body.WorldToUv(from), probe.Uv, 0.003f, 0.3f, WoundMap.Mode.Max);
            Paint(WoundMap.Layer.Fluids, WoundMap.Blood, probe.Uv, probe.Uv, 0.01f, 0.5f, WoundMap.Mode.Max);
        }
        Hurt(0.15f, onSite ? probe.Uv : null);
        Session.Effect(ToolEffect.Bead, from, 0);
        Session.Sound("cut_skin", to);
    }

    /// <summary>One continuous scalpel stroke per tool grows one wound. Where there's no fat under the skin (a
    /// forearm), a cut deeper than the skin goes into the muscle.</summary>
    public void Cut(long strokeKey, Vector2 a, Vector2 b, float depth, float sharpness, bool dirty, float speed)
    {
        depth = Mathf.Clamp(depth - Mods.Num("fat_depth") * 0.25f, 0.1f, 1f);
        if (Body.FatThickness < 0.0005f && depth >= SkinDepth)
        {
            depth = Mathf.Max(depth, Wound.MuscleDepth);
        }
        if (!_strokeWounds.TryGetValue(strokeKey, out var wound) || wound.Points[^1].DistanceTo(a) > 0.02f)
        {
            wound = NewWound(WoundKind.Cut, a, depth);
            wound.MadeBySurgeon = true;
            _strokeWounds[strokeKey] = wound;
        }
        wound.Extend(b);
        wound.Depth = Mathf.Max(wound.Depth, depth);
        wound.Dirty = wound.Dirty || dirty;
        var jagged = speed > 0.25f || sharpness < 0.8f;
        var jitter = 0.004f * (1f - sharpness) + (speed > 0.25f ? 0.003f : 0f);
        Paint(WoundMap.Layer.Wounds, WoundMap.Cut, a, b, 0.004f + depth * 0.003f, Mathf.Min(depth * 0.75f, 0.7f), WoundMap.Mode.Max, jitter);
        Rpc(MethodName.TissueCut, a, b, (int)TissueDepthOf(depth));
        Hurt(0.12f * depth, a);
        if (dirty)
        {
            Contaminate();
        }
        Session.Scoring.Add(jagged ? "jagged_cut" : "clean_cut", true);
    }

    /// <summary>Cutting inside the body frees attached targets, or nicks whatever the blade touches. The blade grates on
    /// a bone it touches, which hurts through a local block.</summary>
    public void CutCavity(Vector2 tipUv, float depthM, float power, bool dirty, float dt)
    {
        if (dirty)
        {
            Contaminate();
        }
        var tip = Body.UvToWorld(tipUv, depthM);
        if (ScrapeBone(tipUv, tip, dt))
        {
            return;
        }
        foreach (var target in Targets)
        {
            if (!target.Extracted && target.Anchor > 0f && target.Uv.DistanceTo(tipUv) < 0.06f)
            {
                target.Anchor = Mathf.Max(target.Anchor - power * dt * 0.5f, 0f);
                if (!target.Opened)
                {
                    target.Opened = true;
                    NewWound(WoundKind.Internal, target.Uv, 0.6f).DepthM = target.Depth;
                }
                return;
            }
        }
        if (Body.OrganAt(tip, OrganReach) >= 0 && Rng.Randf() < dt * 0.8f)
        {
            var wound = NewWound(WoundKind.Internal, tipUv, 0.5f);
            wound.DepthM = depthM;
            wound.MadeBySurgeon = true;
            Session.Scoring.Add("organ_nick", true);
            Session.Announce("That wasn't the target. Something is bleeding in there.");
        }
    }

    /// <summary>A blade touching a bone at <paramref name="tip"/> (world space) grates on it, which hurts through a local
    /// block. False if there's no bone there.</summary>
    public bool ScrapeBone(Vector2 tipUv, Vector3 tip, float dt)
    {
        var bone = Body.BoneAt(tip, BladeReach);
        if (bone.Length == 0)
        {
            return false;
        }
        var now = Time.GetTicksMsec() * 0.001;
        if (now - _boneTouched > 0.5)
        {
            Hurt(BoneJolt, tipUv, deep: true);
        }
        _boneTouched = now;
        Hurt(BonePain * dt, tipUv, deep: true);
        AddFlag("bone_scraped", dt);
        if (!Flags.ContainsKey("bone_notice"))
        {
            AddFlag("bone_notice");
            var what = bone switch { "rib" => "a rib", "sternum" => "the breastbone", _ => "bone" };
            Session.Announce($"The blade grates on {what}.");
        }
        if (Rng.Randf() < dt * 2f)
        {
            Session.Sound("saw_bone", tip);
        }
        return true;
    }

    public void Tear(Vector2 from, Vector2 direction, float lengthUv)
    {
        var to = from + direction.Normalized() * lengthUv;
        Rpc(MethodName.TissueCut, from, to, (int)TissueDepth.Fat);
        AddTear(from, to);
    }

    private void AddTear(Vector2 from, Vector2 to)
    {
        var wound = NewWound(WoundKind.Tear, from, 0.8f);
        wound.Extend(to);
        PaintWound(wound);
        Hurt(0.5f, from);
        AddFlag("tears");
        Session.Scoring.Add("skin_tear");
        Session.Sound("tear_skin", Body.UvToWorld(from));
        Reveal("thin_skin");
    }

    /// <summary>A new running thread for the needle: its id for <see cref="PlaceSutureAnchor"/> and the rest.</summary>
    public int NewSuture() => ++_nextSuture;

    /// <summary>
    /// The tissue layer a thread started at uv goes through: the skin from a hole in it beside the wound, the deepest
    /// layer still open from a hole inside the wound's opening or on its line (within half a grid cell), and the muscle
    /// under a stab or bullet hole too small to reach into (the needle goes in through the hole and sews it first).
    /// </summary>
    public TissueDepth SutureLayerAt(Vector2 uv, Wound wound)
    {
        var line = wound.ClosestPoint(uv);
        var at = wound.BinPosition(wound.BinAt(uv));
        var hole = wound.IsHole && !Body.IsOpen(at);
        var inside = Body.LayerAt(uv) != "skin" || uv.DistanceTo(line) < GridCell(uv - line) * 0.5f;
        if (!inside && !hole)
        {
            return TissueDepth.Skin;
        }
        if (Body.Tissue.MuscleOpenNear(at, MuscleReach))
        {
            return TissueDepth.Muscle;
        }
        return !hole && Body.Tissue.FatOpenNear(at, MuscleReach) ? TissueDepth.Fat : TissueDepth.Skin;
    }

    /// <summary>
    /// One click with the needle makes one puncture in a continuous thread. The first click, beside a wound, only
    /// anchors it in the layer under it (<see cref="SutureLayerAt"/>); later ones pass the same thread through the same
    /// layer at the new hole, making spans that are all pulled from the free end. The thread closes every wound it
    /// crosses. Returns false when no hole was made (no wound near the first, the same hole again, a thread already tied
    /// off or torn).
    /// </summary>
    public bool PlaceSutureAnchor(int threadId, Vector2 uv, float tension)
    {
        var tissue = Body.Tissue;
        var info = tissue.Thread(threadId);
        if (info is { Final: true })
        {
            return false;
        }
        // Holes sit beside the incision, not on its line: a bite reaches four grid cells (about 2.5 cm) from it. In grid
        // cells, not uv: a narrow site like a forearm has few cells across, each wide in uv.
        var wound = NearestWound(uv, 4f / Math.Min(tissue.ResX, tissue.ResY), false);
        if (wound is null && info is null)
        {
            return false;
        }
        var layer = info?.Layer ?? SutureLayerAt(uv, wound!);
        if (wound is not null)
        {
            uv = HoleUv(uv, wound);
        }
        var before = tissue.ThreadUvs(threadId).Count;
        Rpc(MethodName.SutureAnchor, threadId, uv, (int)layer, tension, ThreadStrength[(int)layer], tissue.ThreadSlack(threadId, uv));
        if (tissue.ThreadUvs(threadId).Count == before)
        {
            return false;
        }
        _sutures.TryAdd(threadId, []);
        if (layer == TissueDepth.Skin)
        {
            Paint(WoundMap.Layer.Wounds, WoundMap.Stitch, uv, uv, 0.0035f, 0.9f, WoundMap.Mode.Max);
        }
        Hurt(layer == TissueDepth.Skin ? 0.035f : 0.06f, uv);
        ApplyThreadClosure(threadId);
        return true;
    }

    /// <summary>The wheel on the free end of a live thread: tension is the needle's.</summary>
    public void SetSutureTension(int threadId, float tension)
    {
        if (Body.Tissue.Thread(threadId) is null || SutureDone(threadId))
        {
            return;
        }
        Rpc(MethodName.SutureTension, threadId, tension);
        ApplyThreadClosure(threadId);
    }

    /// <summary>Ties the thread off and cuts it: the edges it holds are joined for good at the tension it has, sewn as
    /// neatly as <paramref name="quality"/> (the needle's <see cref="ToolDef.Quality"/>) allows.</summary>
    public void FinishSuture(int threadId, float quality)
    {
        if (Body.Tissue.Thread(threadId) is null || SutureDone(threadId))
        {
            return;
        }
        Rpc(MethodName.SutureFinish, threadId);
        ApplyThreadClosure(threadId, quality);
    }

    /// <summary>True once the thread can take no more holes: tied off, or torn through the tissue.</summary>
    public bool SutureDone(int threadId) => Body.Tissue.Thread(threadId) is { Final: true };

    /// <summary>
    /// Applies the closure the thread makes where it crosses wounds. Loose thread leaves a gap; pulling harder closes
    /// it, then raises the pressed edges into a lip; past ThreadTear it cuts through. Skin over open muscle won't meet,
    /// and pulled shut over it the thread tears through. A tied-off thread (quality &gt;= 0) joins the edges for good.
    /// Its closure is laid over what other closures left, so loosening it never undoes a staple.
    /// </summary>
    private void ApplyThreadClosure(int threadId, float quality = -1f)
    {
        var tissue = Body.Tissue;
        var info = tissue.Thread(threadId)!;
        var before = _sutures[threadId];
        var layer = (int)info.Layer;
        var tension = info.Tension;
        var loose = TissueSim.ThreadLoose[layer];
        var closed = TissueSim.ThreadClosed[layer];
        var closure = Mathf.Clamp((loose - tension) / (loose - closed), 0f, 1f);
        var points = tissue.ThreadUvs(threadId);
        var crossed = new List<(Wound Wound, List<Vector2> Crossings)>();
        var all = new List<Vector2>();
        var overMuscle = false;
        foreach (var wound in Wounds.Where(w => w.IsSkinCut))
        {
            var crossings = ThreadCrossings(points, wound);
            if (crossings.Count == 0)
            {
                continue;
            }
            crossed.Add((wound, crossings));
            all.AddRange(crossings);
            overMuscle = overMuscle || (info.Layer == TissueDepth.Skin && wound.ThroughMuscle
                && crossings.Any(c => tissue.MuscleOpenNear(c, MuscleReach)));
        }
        if (crossed.Count == 0)
        {
            return;
        }
        if (tension < TissueSim.ThreadTear[layer] || (overMuscle && closure >= 0.99f))
        {
            SnapSuture(threadId, all, (points[1] - points[0]).Orthogonal(), overMuscle);
            return;
        }
        if (overMuscle)
        {
            TearNotice("The skin won't meet over the open muscle. Sew the muscle first.");
            closure = Mathf.Min(closure, 0.15f);
        }
        // Pressed edges rise into a small lip rather than sliding through one another, higher the harder it's pulled.
        var overpull = Mathf.Clamp((closed - tension) / (closed - TissueSim.ThreadTear[layer]), 0f, 1f);
        float[] lipScales = [0f, 1f, 0.7f, 0.55f];
        var lip = (Mathf.Clamp((closure - 0.5f) * 2f, 0f, 1f) * 0.0008f + overpull * 0.0015f) * lipScales[layer];
        Rpc(MethodName.TissuePucker, all.ToArray(), 0.018f, lip);
        var tied = quality >= 0f && closure >= 0.99f;
        foreach (var (wound, crossings) in crossed)
        {
            var bins = SupportedBins(wound, crossings);
            if (!before.TryGetValue(wound.Id, out var held))
            {
                held = ([], []);
                before[wound.Id] = held;
            }
            if (info.Layer == TissueDepth.Skin)
            {
                LayClosure(wound.Bins, held.Skin, bins, closure);
            }
            else if (info.Layer == TissueDepth.Muscle)
            {
                LayClosure(wound.Muscle, held.Muscle, bins, closure);
            }
            if (tied)
            {
                TieOff(wound, crossings, bins, info.Layer, quality);
            }
        }
    }

    /// <summary>A tied off thread joins the edges of <paramref name="wound"/> it holds (<paramref name="bins"/>,
    /// crossing it at <paramref name="crossings"/>) for good: the skin with a seam, or the muscle or fat under it.
    /// </summary>
    private void TieOff(Wound wound, List<Vector2> crossings, List<int> bins, TissueDepth layer, float quality)
    {
        var path = bins.Select(wound.BinPosition).ToArray();
        if (layer != TissueDepth.Skin)
        {
            Rpc(MethodName.TissueCloseLayer, path, MuscleReach, (int)layer);
            return;
        }
        // The thread gathers the edges at its holes: every severed edge the bites hold is joined too, not only the
        // exact crossings, which are a little shorter so the thread dimples the skin where it pulls.
        foreach (var crossing in crossings)
        {
            Rpc(MethodName.TissueStitch, crossing, 0.98f, ThreadStrength[(int)layer]);
        }
        Rpc(MethodName.TissueStitchPath, path, Wound.BinLengthUv * 1.2f, 1f, ThreadStrength[(int)layer]);
        PaintSeam(path);
        ClosedWith(wound, quality);
    }

    /// <summary>Meeting edges squeeze the broad wet groove and blood out, but a narrow pink incision line remains along
    /// <paramref name="path"/> (uv). A separate seam mask reveals that line without overloading closure quality in the
    /// stitch channel.</summary>
    private void PaintSeam(IReadOnlyList<Vector2> path)
    {
        for (var i = 0; i < path.Count; i++)
        {
            var previous = path[Math.Max(i - 1, 0)];
            Paint(WoundMap.Layer.Wounds, WoundMap.Cut, previous, path[i], 0.012f, 0f, WoundMap.Mode.Min);
            Paint(WoundMap.Layer.Wounds, WoundMap.Cut, previous, path[i], 0.004f, 0.09f, WoundMap.Mode.Max);
            Paint(WoundMap.Layer.Seams, WoundMap.ClosedSeam, previous, path[i], 0.0045f, 1f, WoundMap.Mode.Max);
            Paint(WoundMap.Layer.Fluids, WoundMap.Blood, previous, path[i], 0.026f, 1f, WoundMap.Mode.Sub);
        }
    }

    /// <summary>Part of <paramref name="wound"/> was just joined for good, as neatly as <paramref name="quality"/>: a
    /// whole wound closed neatly scores.</summary>
    private void ClosedWith(Wound wound, float quality)
    {
        wound.ClosureQuality = Mathf.Lerp(wound.ClosureQuality, quality, 0.5f);
        ScoreNeatClosure(wound);
    }

    private void ScoreNeatClosure(Wound wound)
    {
        if (wound.Closure >= 0.99f && wound.ClosureQuality > 0.9f)
        {
            AddFlag("neat_closure");
            Session.Scoring.Add("good_suture", true);
        }
    }

    /// <summary>The grid point (uv) a hole clicked at uv goes through: the nearest one on the clicked side of the wound,
    /// never one on its line, so every bite goes across it.</summary>
    private Vector2 HoleUv(Vector2 uv, Wound wound)
    {
        var tissue = Body.Tissue;
        var line = wound.ClosestPoint(uv);
        var away = (uv - line).Normalized();
        if (away == Vector2.Zero)
        {
            return uv;
        }
        var cell = GridCell(away);
        var hole = tissue.UvOf(tissue.Nearest(uv));
        for (var i = 0; i < 3 && (hole - line).Dot(away) <= cell * 0.3f; i++)
        {
            uv += away * cell * 0.5f;
            hole = tissue.UvOf(tissue.Nearest(uv));
        }
        return hole;
    }

    /// <summary>The tissue grid's cell size (uv) along <paramref name="direction"/>: cells are square in meters, not in
    /// uv.</summary>
    private float GridCell(Vector2 direction)
    {
        direction = direction.Normalized();
        return Mathf.Abs(direction.X) / Body.Tissue.ResX + Mathf.Abs(direction.Y) / Body.Tissue.ResY;
    }

    /// <summary>Where a thread's spans (holes <paramref name="points"/>) cross its wound. Each end of the wound counts a
    /// grid cell further: a bite round the end of a short stab or bullet hole goes past it, and the holes snap to the
    /// grid.</summary>
    private List<Vector2> ThreadCrossings(List<Vector2> points, Wound wound)
    {
        var line = wound.Points.ToList();
        if (line.Count < 2)
        {
            // A puncture or gunshot can be a single point. Give it a short virtual incision axis perpendicular to the
            // first bite so a span across the hole supports its one closure bin instead of intersecting a zero-length
            // segment.
            var bite = points.Count >= 2 ? points[1] - points[0] : Vector2.Right;
            var along = bite.LengthSquared() > 0.000001f ? bite.Orthogonal().Normalized() : Vector2.Right;
            var half = GridCell(along);
            line[0] -= along * half;
            line.Add(wound.Points[0] + along * half);
        }
        foreach (var (end, inner) in new[] { (0, 1), (line.Count - 1, line.Count - 2) })
        {
            var outward = (line[end] - line[inner]).Normalized();
            line[end] += outward * GridCell(outward);
        }
        var crossings = new List<Vector2>();
        for (var i = 1; i < points.Count; i++)
        {
            for (var n = 1; n < line.Count; n++)
            {
                var crossing = Geometry2D.SegmentIntersectsSegment(points[i - 1], points[i], line[n - 1], line[n]);
                if (crossing.VariantType != Variant.Type.Nil
                    && (crossings.Count == 0 || crossings[^1].DistanceTo(crossing.AsVector2()) > 0.001f))
                {
                    crossings.Add(crossing.AsVector2());
                }
            }
        }
        return crossings;
    }

    /// <summary>
    /// The wound's bins the thread holds, in order: each bite holds the edge halfway to its neighbours (and as far past
    /// the first and last), so they run on unbroken. Bins are much finer than practical stitch spacing: the bins at the
    /// exact crossings alone would leave a dotted closure.
    /// </summary>
    private static List<int> SupportedBins(Wound wound, List<Vector2> crossings)
    {
        var at = crossings.Select(wound.BinAt).Order().ToList();
        var reach = 1;
        for (var i = 1; i < at.Count; i++)
        {
            reach = Math.Max(reach, Mathf.CeilToInt((at[i] - at[i - 1]) * 0.5f));
        }
        // A bite within one stitch spacing of an end holds that end too. Holes snap to the tissue grid, so the first
        // crossing can land a bin or two in even when the player clicks right beside the end of the incision.
        var first = at[0] <= reach * 2 ? 0 : at[0] - reach;
        var last = wound.Bins.Length - 1 - at[^1] <= reach * 2 ? wound.Bins.Length : at[^1] + reach + 1;
        return [.. Enumerable.Range(first, last - first)];
    }

    /// <summary>Sets <paramref name="closure"/> on <paramref name="bins"/> of <paramref name="values"/> (a wound's
    /// closure per bin), over the value each had before this thread first held it (remembered in
    /// <paramref name="before"/>).</summary>
    private static void LayClosure(float[] values, Dictionary<int, float> before, List<int> bins, float closure)
    {
        foreach (var bin in bins)
        {
            if (!before.TryGetValue(bin, out var under))
            {
                under = values[bin];
                before[bin] = under;
            }
            values[bin] = Mathf.Max(under, closure);
        }
    }

    /// <summary>The thread cut through the tissue (where it first crosses a wound, of <paramref name="crossings"/>): it
    /// lets go everywhere, and what it held is as open as before it.</summary>
    private void SnapSuture(int threadId, List<Vector2> crossings, Vector2 across, bool overMuscle)
    {
        foreach (var (id, held) in _sutures[threadId])
        {
            if (WoundWithId(id) is not { } wound)
            {
                continue;
            }
            foreach (var (bin, value) in held.Skin)
            {
                wound.Bins[bin] = value;
            }
            foreach (var (bin, value) in held.Muscle)
            {
                wound.Muscle[bin] = value;
            }
        }
        Rpc(MethodName.TissuePucker, crossings.ToArray(), 0.018f, 0f);
        Rpc(MethodName.SutureSnap, threadId);
        var layer = Body.Tissue.Thread(threadId)!.Layer;
        if (layer == TissueDepth.Skin)
        {
            Tear(crossings[0], across, 0.018f);
            Session.Scoring.Add("suture_tear_through");
            TearNotice(overMuscle
                ? "The stitch tore through: the muscle under it is still open."
                : "The thread was pulled too tight and tore through the skin.");
        }
        else
        {
            Session.Announce($"The thread cut through the {(layer == TissueDepth.Fat ? "fat" : "muscle")}.", true);
        }
    }

    /// <summary>
    /// A staple from a stapler (<paramref name="def"/>) with its legs at a and b (uv, <see cref="TissueSim.StapleSpot"/>).
    /// It joins the skin edges for good around where it crosses the cut, or, where the muscle under it is still open,
    /// the muscle: the cut muscle holds the skin apart, so it's stapled first. An improvised one can tear out of the skin
    /// or catch a vessel (<see cref="ToolDef.TearChance"/>, <see cref="ToolDef.BleedChance"/>). Returns whether a staple
    /// went in.
    /// </summary>
    public bool Staple(Vector2 a, Vector2 b, ToolDef def, float improvisedMult)
    {
        var tissue = Body.Tissue;
        var at = tissue.StapleSpot(a, b);
        var wound = at.X >= 0f ? NearestWound(at, 0.03f, false) : null;
        if (wound is null)
        {
            return false;
        }
        // The wound's bins under the staple. Whether they're closed is up to the tissue (SettleClosures()).
        var bins = Enumerable.Range(0, wound.Bins.Length)
            .Where(i => ((wound.BinPosition(i) - at) * Body.SiteSize).Length() < TissueSim.StitchReach)
            .ToList();
        if (wound.ThroughMuscle && tissue.MuscleOpenNear(at, MuscleReach))
        {
            Rpc(MethodName.TissueCloseLayer, new[] { at }, MuscleReach, (int)TissueDepth.Muscle);
            foreach (var i in bins.Where(i => !tissue.MuscleOpenNear(wound.BinPosition(i), MuscleReach)))
            {
                wound.Muscle[i] = 1f;
            }
            Rpc(MethodName.TissueStaple, a, b, (int)TissueDepth.Muscle);
            Hurt(0.06f, at);
            return true;
        }
        var leg = Rng.Randi() % 2 == 0 ? a : b;
        if (Rng.Randf() < def.TearChance)
        {
            Tear(leg, leg - at, Body.MetersToUv(0.012f));
            Session.Announce("The staple tore out through the skin.", true);
            return true;
        }
        var quality = def.Improvised ? Mathf.Lerp(def.Quality, 1f, 1f - improvisedMult) : def.Quality;
        Rpc(MethodName.TissueStitch, at, StitchTension[0], 1.2f + quality);
        PaintSeam(bins.Select(wound.BinPosition).ToList());
        ClosedWith(wound, quality);
        Rpc(MethodName.TissueStaple, a, b, (int)TissueDepth.Skin);
        if (def.Id == "office_stapler")
        {
            AddFlag("office_staples");
        }
        if (Rng.Randf() < def.BleedChance)
        {
            StapleBleed(wound, leg);
        }
        Hurt(0.06f, at);
        return true;
    }

    /// <summary>A staple's leg at uv went through a vessel beside <paramref name="wound"/>: it bleeds through the
    /// closure until it's cauterized, clamped or pressed (<see cref="Wound.Nicked"/>). More of them along the same wound
    /// bleed no faster: they're sealed together.</summary>
    private void StapleBleed(Wound wound, Vector2 uv)
    {
        wound.Nicked = StapleNick;
        Paint(WoundMap.Layer.Fluids, WoundMap.Blood, uv, uv, 0.02f, 0.9f, WoundMap.Mode.Max);
        Session.Sound("blood_spurt", Body.UvToWorld(uv));
        Session.Announce("The staple went through a vessel. It's bleeding.", true);
    }

    /// <summary>Closes the skin at uv with a closure tool (tape, paper clips, a quick stitch). Returns true when a bin
    /// closed.</summary>
    public bool CloseAt(Vector2 uv, ToolDef def, float dt, float improvisedMult, int pressure)
    {
        var wound = NearestWound(uv, 0.02f, false);
        if (wound is null)
        {
            return false;
        }
        var bin = wound.BinAt(uv);
        if (wound.Bins[bin] >= 1f)
        {
            return false;
        }
        var tensioned = TensionedClosures.Contains(def.Id);
        var quality = def.Quality;
        if (def.Action == "suture" && def.Id is "surgical_tape" or "duct_tape" && wound.Depth > 0.6f)
        {
            quality *= 0.5f;
        }
        if (def.Improvised)
        {
            quality = Mathf.Lerp(quality, 1f, 1f - improvisedMult);
        }
        var cap = 1f;
        if (tensioned)
        {
            if (pressure == 1)
            {
                quality *= 0.6f;
                cap = 0.9f;
            }
            else if (pressure == 3)
            {
                quality = Mathf.Min(quality * 1.05f, 1f);
            }
        }
        // Skin pulled shut over open muscle carries the muscle's pull: the edges won't meet, and a tight stitch gets
        // them there only to tear through.
        if (wound.ThroughMuscle && Body.Tissue.MuscleOpenNear(wound.BinPosition(bin), MuscleReach))
        {
            // A stab or a bullet hole is too small to reach into: the needle goes in through it and sews the muscle
            // first.
            if (wound.IsHole && !Body.IsOpen(uv))
            {
                return CloseMuscleAt(uv, def, dt);
            }
            if (!(tensioned && pressure == 3))
            {
                TearNotice("The skin won't meet over the open muscle. Sew the muscle first.");
                return false;
            }
            wound.Bins[bin] = Mathf.Min(wound.Bins[bin] + def.Power * 1.5f * dt, cap);
            if (wound.Bins[bin] >= cap)
            {
                wound.Bins[bin] = 0f;
                Tear(wound.BinPosition(bin), RandomDirection(), 0.02f);
                Session.Scoring.Add("suture_tear_through");
                TearNotice("The stitch tore through: the muscle under it is still open.");
            }
            return false;
        }
        var before = wound.Bins[bin];
        wound.Bins[bin] = Mathf.Min(before + def.Power * 1.5f * dt, cap);
        wound.ClosureQuality = Mathf.Lerp(wound.ClosureQuality, quality, 0.2f);
        var reachedCap = before < cap && wound.Bins[bin] >= cap;
        if (tensioned && pressure == 3 && reachedCap && Rng.Randf() < 0.2f / Mods.Mult("tear_threshold_mult"))
        {
            wound.Bins[bin] = 0.3f;
            Tear(wound.BinPosition(bin), RandomDirection(), 0.02f);
            Rpc(MethodName.TissueBurst, wound.BinPosition(bin), 0.03f);
            Session.Scoring.Add("suture_tear_through");
            Session.Announce("Pulled too tight. The stitch tore through the skin.", true);
            return false;
        }
        if (!reachedCap)
        {
            return false;
        }
        var p = wound.BinPosition(bin);
        Paint(WoundMap.Layer.Wounds, WoundMap.Cut, p, p, 0.012f, 0.35f, WoundMap.Mode.Min);
        Paint(WoundMap.Layer.Wounds, WoundMap.Stitch, p - new Vector2(0.006f, 0f), p + new Vector2(0.006f, 0f), 0.002f, 1f, WoundMap.Mode.Max);
        var tension = tensioned ? StitchTension[pressure] : StitchTension[0];
        Rpc(MethodName.TissueStitch, p, tension, 1.2f + quality);
        if (def.Id == "duct_tape")
        {
            AddFlag("duct_tape");
        }
        ScoreNeatClosure(wound);
        Hurt(0.06f, uv);
        return true;
    }

    private Vector2 RandomDirection() => new(Rng.RandfRange(-1f, 1f), Rng.RandfRange(-1f, 1f));

    /// <summary>Sewing inside the opening of a wound through the muscle closes the muscle, bin by bin. Tape can't.
    /// Returns true when a bin of muscle closed.</summary>
    public bool CloseMuscleAt(Vector2 uv, ToolDef def, float dt)
    {
        var wound = NearestWound(uv, 0.03f, false);
        if (wound is null || !wound.ThroughMuscle || def.Id is "surgical_tape" or "duct_tape")
        {
            return false;
        }
        var bin = wound.BinAt(uv);
        var before = wound.Muscle[bin];
        wound.Muscle[bin] = Mathf.Min(before + def.Power * 1.5f * dt, 1f);
        if (before < 1f && wound.Muscle[bin] >= 1f)
        {
            Rpc(MethodName.TissueMuscle, wound.BinPosition(bin), MuscleReach);
            Hurt(0.06f, uv);
            return true;
        }
        return false;
    }

    /// <summary>Returns true while it's sewing an internal wound that isn't closed yet.</summary>
    public bool CloseInternalAt(Vector2 uv, ToolDef def, float dt)
    {
        var sewing = false;
        foreach (var wound in Wounds.Where(w => w.IsInternal && Reaches(w, uv) && w.Bins[0] < 1f))
        {
            wound.Bins[0] = Mathf.Min(wound.Bins[0] + def.Power * 0.4f * dt, 1f);
            wound.ClosureQuality = Mathf.Lerp(wound.ClosureQuality, def.Quality, 0.1f);
            sewing = true;
        }
        return sewing;
    }

    /// <summary>A tool tip at uv works inside the body: in an opening, or through a stab or bullet hole in the skin
    /// there.</summary>
    private bool Inside(SiteZone zone, Vector2 uv) =>
        zone == SiteZone.Cavity || (zone == SiteZone.Site && NearestWound(uv, 0.02f, false) is { IsHole: true });

    /// <summary>A tool tip inside the opening at uv reaches an internal wound under it, whatever its depth: a tool can't
    /// hover in the cavity, it comes down onto whatever lies there (the organ the wound is in, or the floor once a
    /// target is out).</summary>
    private static bool Reaches(Wound wound, Vector2 uv) => wound.Points[0].DistanceTo(uv) < 0.05f;

    public void CauterizeAt(SiteZone zone, Vector2 uv, ToolDef def, float dt)
    {
        var radius = Body.MetersToUv(def.Radius);
        var sealedAny = false;
        foreach (var wound in Wounds)
        {
            // An opened skin wound's edges lie apart from where it was cut: up to the gap of a fully open one.
            var near = wound.IsInternal
                ? Reaches(wound, uv)
                : wound.DistanceTo(uv) < radius + 0.01f + Body.MetersToUv(FullGap) * wound.Opened;
            if (near && (wound.IsInternal ? Inside(zone, uv) : zone == SiteZone.Site))
            {
                wound.Cauterized = Mathf.Min(wound.Cauterized + def.Power * 0.6f * dt, 0.95f);
                // A vessel a staple went through is sealed for good.
                wound.Nicked = Mathf.Max(wound.Nicked - def.Power * StapleNick * dt, 0f);
                sealedAny = true;
            }
        }
        if (zone == SiteZone.Site)
        {
            Paint(WoundMap.Layer.Wounds, WoundMap.Burn, uv, uv, radius * 1.5f, def.Power * dt * 3f, WoundMap.Mode.Add);
            Hurt(0.25f * dt * 10f, uv);
            if (def.Id == "lighter")
            {
                AddFlag("lighter_burns", dt);
            }
            if (!sealedAny)
            {
                Session.Scoring.Add("burn_damage", true);
            }
        }
        if (Mods.Flag("pacemaker") && Scenario.Site is "chest" or "abdomen" && Rng.Randf() < dt * 0.3f)
        {
            Vitals.HeartRate += Rng.RandfRange(-25f, 35f);
            Session.Announce("The monitor stutters. Pacemaker interference.");
            Reveal("pacemaker");
        }
    }

    public void Mark(Vector2 a, Vector2 b)
    {
        MarkedUv += a.DistanceTo(b);
        Paint(WoundMap.Layer.Fluids, WoundMap.Ink, a, b, 0.004f, 1f, WoundMap.Mode.Max);
    }

    /// <summary>Wipes the site with a swab. <paramref name="soakedIn"/>: what the swab is soaked in, when that's not
    /// fixed by the tool (a cotton pad dipped in iodine).</summary>
    public void SwabAt(SiteZone zone, Vector2 uv, ToolDef def, float dt, string soakedIn = "")
    {
        var drug = soakedIn.Length > 0 ? soakedIn : def.Drug;
        var radius = Body.MetersToUv(def.Radius);
        var sanitize = drug is "iodine" or "whiskey";
        if (zone == SiteZone.Site)
        {
            // One pass over the disk for every channel: a wipe runs every physics frame, so it has to be cheap.
            var ops = new List<PaintOp> { new(WoundMap.Blood, def.Power * dt * 2f, WoundMap.Mode.Sub) };
            if (sanitize)
            {
                MarkGrid(_sanitized, uv, radius, drug == "iodine" ? 1f : 0.5f);
                ops.Add(new PaintOp(WoundMap.Grime, dt * 2f, WoundMap.Mode.Sub));
                ops.Add(new PaintOp(drug == "iodine" ? WoundMap.Iodine : WoundMap.Grime, 0.3f * dt * 10f, WoundMap.Mode.Add));
                if (drug == "whiskey" && NearestWound(uv, 0.03f, false) is not null)
                {
                    Hurt(0.5f * dt * 10f, uv);
                }
            }
            PaintOps(WoundMap.Layer.Fluids, uv, radius, ops);
            if (def.Id == "gauze" && NearestWound(uv, 0.02f, false) is { } wound)
            {
                Press(wound, dt);
            }
        }
        else if (zone == SiteZone.Cavity)
        {
            CavityBloodMl = Mathf.Max(CavityBloodMl - def.Power * 15f * dt, 0f);
            // Packed into the opening, it presses on the vessel it reaches, else on the edges of the cut it's in.
            var vessel = Wounds.Where(w => w.IsInternal && Reaches(w, uv)).MinBy(w => w.Points[0].DistanceTo(uv));
            if (def.Id == "gauze" && (vessel ?? NearestWound(uv, 0.02f, false)) is { } wound)
            {
                Press(wound, dt);
            }
        }
    }

    /// <summary>Gauze pressed on a wound for <paramref name="dt"/> seconds: it stops the bleeding while the pressure
    /// holds, and stops a small cut for good once pressed long enough.</summary>
    private void Press(Wound wound, float dt)
    {
        wound.PressedFor = wound.SincePressed > Wound.PressBreak ? dt : wound.PressedFor + dt;
        wound.SincePressed = 0f;
        wound.Pressed = Mathf.Min(wound.Pressed + dt * 2f, 1f);
        if (!wound.Clotted && wound.PressedFor >= Wound.SmallCutPress && wound.IsSmall(Body.UvToMeters(1f)))
        {
            wound.Clotted = true;
            Session.Announce("The small cut has stopped bleeding.");
        }
    }

    public void SuctionAt(SiteZone zone, Vector2 uv, ToolDef def, float dt)
    {
        var radius = Body.MetersToUv(def.Radius);
        if (zone == SiteZone.Site)
        {
            Paint(WoundMap.Layer.Fluids, WoundMap.Blood, uv, uv, radius, def.Power * dt * 4f, WoundMap.Mode.Sub);
        }
        else if (zone == SiteZone.Cavity)
        {
            CavityBloodMl = Mathf.Max(CavityBloodMl - def.Power * 40f * dt, 0f);
        }
        foreach (var target in Targets.Where(t => t.IsSuctionTarget && !t.Extracted && t.Uv.DistanceTo(uv) < 0.08f).ToList())
        {
            target.Amount = Mathf.Max(target.Amount - def.Power * dt, 0f);
            if (target.Amount <= 0f)
            {
                Extract(target);
            }
        }
    }

    /// <summary>Saws at a bone target near the tip. Returns true while there's one to saw.</summary>
    public bool SawAt(Vector2 tipUv, ToolDef def, float dt)
    {
        var target = Targets.FirstOrDefault(t => t.IsBone && !t.Extracted && t.Uv.DistanceTo(tipUv) < 0.08f);
        if (target is null)
        {
            return false;
        }
        if (Mods.Num("bone_hardness") >= 2f && def.Power < 2f)
        {
            Session.Announce("The saw skids off. This bone is too hard for it.", true);
            Reveal("bones");
            return true;
        }
        target.Anchor = Mathf.Max(target.Anchor - def.Power * 0.12f * Mods.Mult("saw_speed_mult") * dt, 0f);
        Hurt(0.1f * dt * 10f, tipUv);
        if (target.Anchor <= 0f)
        {
            Extract(target);
        }
        return true;
    }

    public void SmashAt(Vector2 tipUv, ToolDef def)
    {
        Bruise(tipUv, Body.MetersToUv(0.04f), 0.5f * def.Power);
        Hurt(0.4f, tipUv);
        Session.Sound("mallet_hit", Body.UvToWorld(tipUv));
        foreach (var target in Targets.Where(t => t.IsBone && !t.Extracted && t.Uv.DistanceTo(tipUv) < 0.1f))
        {
            target.Anchor = Mathf.Max(target.Anchor - def.Power * 0.12f, 0f);
            if (target.Anchor <= 0f)
            {
                Extract(target);
                Session.Sound("bone_crack", Body.UvToWorld(tipUv));
            }
        }
    }

    public void Bruise(Vector2 uv, float radius, float strength) =>
        Paint(WoundMap.Layer.Wounds, WoundMap.Bruise, uv, uv, radius * Mods.Mult("bruise_mult"), strength, WoundMap.Mode.Add);

    public void ApplyTourniquet()
    {
        TourniquetOn = true;
        TourniquetTime = 0f;
        AddFlag("tourniquet");
    }

    public void RemoveTourniquet() => TourniquetOn = false;

    /// <summary>A catheter went into the arm at <paramref name="at"/> (world space): tubing now runs from the stand to
    /// there. <paramref name="inVein"/>: it hit a vein, so the line works. Host only (what runs through the line is
    /// decided here).</summary>
    public void SetIv(Vector3 at, bool inVein)
    {
        if (IvSet)
        {
            return;
        }
        IvSet = true;
        IvInVein = inVein;
        Hurt(0.1f);
        Rpc(MethodName.IvPlaced, Body.Root.ToLocal(at));
    }

    /// <summary>Someone walked into the tubing: the catheter rips out of the arm and the stand rattles. The catheter
    /// ends up on the floor at their feet, where it can be picked up, washed and used again.</summary>
    public void PullIv(Surgeon surgeon)
    {
        if (!IvSet)
        {
            return;
        }
        IvSet = false;
        IvInVein = false;
        Hurt(0.35f);
        AddFlag("iv_pulled");
        Session.Scoring.Add("iv_pulled");
        Session.Sound("cable_yank", surgeon.GlobalPosition);
        Session.Announce($"{surgeon.DisplayName} catches the IV line. It rips out of the arm.");
        Session.JoltPeer(surgeon.PeerId, 0.8f);
        // At their feet: a step ahead could be under the table, which is solid down to the floor.
        Session.Tools.DropNew("iv_catheter", surgeon.GlobalPosition + Vector3.Up * 0.1f);
        Rpc(MethodName.IvRemoved);
    }

    /// <summary>Runs the tubing to <paramref name="point"/> (local to the body's root). The dressing rides the forearm
    /// it's on (<see cref="PatientBody.IvSite"/>).</summary>
    private void ConnectIv(Vector3 point)
    {
        if (Surgery.Current?.Room.IvLine is not null)
        {
            var site = Body.IvSite(Body.Root.ToGlobal(point));
            Surgery.Current.Room.ConnectIv(site.Node, site.Frame, site.Radius);
        }
    }

    public bool GraftAt(Vector2 uv, ToolDef def)
    {
        var cell = Cell(uv);
        if (cell < 0 || !_burnCells[cell] || _grafted[cell])
        {
            return false;
        }
        _grafted[cell] = true;
        Paint(WoundMap.Layer.Wounds, WoundMap.Burn, uv, uv, 1f / Grid, 0.25f, WoundMap.Mode.Min);
        Paint(WoundMap.Layer.Wounds, WoundMap.Stitch, uv, uv, 0.4f / Grid, 1f * def.Quality, WoundMap.Mode.Max);
        return true;
    }

    public void DebrideAt(Vector2 uv)
    {
        var cell = Cell(uv);
        if (cell < 0 || !_burnCells[cell] || _debrided[cell])
        {
            return;
        }
        _debrided[cell] = true;
        Paint(WoundMap.Layer.Wounds, WoundMap.Burn, uv, uv, 0.8f / Grid, 0.45f, WoundMap.Mode.Min);
        Paint(WoundMap.Layer.Fluids, WoundMap.Blood, uv, uv, 0.5f / Grid, 0.4f, WoundMap.Mode.Max);
        Hurt(0.2f, uv);
    }

    /// <summary>The site got dirty; <paramref name="reason"/> is announced when given.</summary>
    public void ContaminateSite(string reason)
    {
        Contaminate();
        if (reason.Length > 0)
        {
            Session.Announce(reason);
        }
    }

    private void Contaminate()
    {
        var opened = Wounds.Any(w => w.Opened > 0.2f || w.IsInternal);
        if (!opened)
        {
            AddFlag("dirty_skin");
        }
        else
        {
            AddFlag(Body.IsLimbSite ? "dirty_cavity_limb" : "dirty_cavity");
        }
    }

    /// <summary>Wound depth (0..1) to how many tissue layers the cut goes through.</summary>
    public static TissueDepth TissueDepthOf(float depth)
    {
        if (depth < SkinDepth)
        {
            return TissueDepth.Skin;
        }
        return depth < Wound.MuscleDepth ? TissueDepth.Fat : TissueDepth.Muscle;
    }

    private void TearNotice(string text)
    {
        var now = Time.GetTicksMsec();
        if (_tearNoticeMsec == 0 || now - _tearNoticeMsec > 3000)
        {
            _tearNoticeMsec = now;
            Session.Announce(text, true);
        }
    }

    private void PaintWound(Wound wound)
    {
        var jitter = wound.Kind == WoundKind.Tear ? 0.006f : 0f;
        for (var i = 1; i < wound.Points.Count; i++)
        {
            Paint(WoundMap.Layer.Wounds, WoundMap.Cut, wound.Points[i - 1], wound.Points[i], 0.005f,
                Mathf.Min(wound.Depth * 0.75f, 0.7f), WoundMap.Mode.Max, jitter);
        }
    }

    /// <summary>Setup-time painting, run locally on every peer (no RPC, clients may not be listening yet).</summary>
    private void PaintWoundLocal(Wound wound)
    {
        var map = Body.WoundMap;
        var depth = TissueDepthOf(wound.Depth);
        for (var i = 1; i < wound.Points.Count; i++)
        {
            var (a, b) = (wound.Points[i - 1], wound.Points[i]);
            map.Stroke(WoundMap.Layer.Wounds, WoundMap.Cut, a, b, 0.006f, Mathf.Min(wound.Depth * 0.75f, 0.7f), WoundMap.Mode.Max,
                wound.Kind == WoundKind.Tear ? 0.003f : 0f, (ulong)wound.Id);
            Body.Tissue.Cut(a, b, depth);
            map.Stroke(WoundMap.Layer.Fluids, WoundMap.Blood, a, b, 0.02f, 0.8f, WoundMap.Mode.Max);
        }
        if (wound.Points.Count == 1)
        {
            var center = wound.Points[0];
            map.Disk(WoundMap.Layer.Wounds, WoundMap.Cut, center, 0.012f, 0.7f, WoundMap.Mode.Max, soft: false);
            map.Disk(WoundMap.Layer.Fluids, WoundMap.Blood, center, 0.04f, 0.9f, WoundMap.Mode.Max);
            // A puncture is a small cross-shaped hole in the grid.
            foreach (var d in new[] { new Vector2(0.025f, 0f), new Vector2(0f, 0.025f) })
            {
                Body.Tissue.Cut(center - d, center + d, depth);
            }
        }
    }
}
