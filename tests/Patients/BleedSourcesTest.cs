namespace Scalpel.Tests.Patients;

/// <summary>
/// Every bleed shows where it comes from: a cut bleeding however little puddles on the skin at itself, with no spot that
/// the cut would split across its lips, a puncture shows a spot on the skin in its puddle, a vessel under skin that
/// isn't cut bruises it instead, and an opened incision and every vessel in it show a spot in the middle of the wall of
/// the deepest layer they bleed from, never on the skin over it, however many there are. The spot grows with how fast
/// it bleeds, the slightest bleed still shows, it's gone once the bleeding stops, and pulling the skin takes it along
/// instead of leaving it hanging in the air. The blood pooled in the cavity hides it, with nothing standing on the pool.
/// With key frames review the puddles and the puncture's spot, the vein-like spots within the incision's layer walls,
/// the spot riding the pulled edge and the plain pool over the vessels.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("liquid_contours")]
[TestCategory("smoke"), TestCategory("slow"), TestCategory("liquids"), TestCategory("visual_confirmation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class BleedSourcesTest {
    // Temporary local budget override: recorded CPU/render overruns remain measured; functional assertions still fail normally.
    // Measured in the cloud release run: a garbage collection during the long wait for the pool and single slow frames
    // took up to 17.2 times the budget there.
    private static readonly BudgetAllowance BudgetOverride = new("a garbage collection during the long wait for the pool and "
        + "single slow frames go over the budget", 22.5f);

    [TestCase(Timeout = Limits.Slow)]
    public async Task EveryBleedShowsWhereItComesFrom() {
        var session = await ToolSession.Start("appendectomy", "bleed_sources", captureUntouched: true);
        var driver = session.Driver;
        var patient = driver.Patient;
        var body = driver.Body;
        var tiny = SurgeryState.SkinIsCut(patient, new Vector2(0.3f, 0.3f), new Vector2(0.3f, 0.3f) + new Vector2(body.MetersToUv(0.003f), 0f), 0.1f);
        await Frames.Seconds(2f);
        AssertFloat(tiny.Bleeding).OverrideFailureMessage($"a tiny shallow cut bleeds a little: {driver.Bleeders()}")
            .IsBetween(0.001f, 0.05f);
        AssertFloat(Blood(body, tiny.BleedPoint)).OverrideFailureMessage("however little it bleeds, it puddles on the skin at itself")
            .IsGreater(0.5f);
        AssertString(Spot(body, tiny.BleedPoint)).OverrideFailureMessage("with no spot the cut would split across its lips").IsEqual("none");
        var puncture = SurgeryState.SkinIsPunctured(patient, new Vector2(0.3f, 0.4f), 0.05f);
        await Frames.Seconds(1f);
        AssertString(Spot(body, puncture.Points[0])).OverrideFailureMessage("a puncture shows a spot on the skin where it bleeds")
            .IsEqual("on the skin");
        await driver.Capture("tiny_cut", "a tiny shallow cut bleeding a little, blood puddled on the skin at it with no spot; below it a puncture, its puddle with a vein-like spot (blood-red middle, dark bluish rim) where it bleeds");

        var closed = SurgeryState.VesselBleeds(patient, new Vector2(0.7f, 0.3f), 0.01f);
        await Frames.Seconds(1f);
        AssertFloat(body.WoundMap.Value(WoundMap.Layer.Wounds, WoundMap.Bruise, closed.Points[0]))
            .OverrideFailureMessage("a vessel bleeding under skin that isn't cut spreads a bruise over itself").IsGreater(0.2f);
        AssertString(Spot(body, closed.Points[0])).OverrideFailureMessage("with no spot on the skin over it").IsEqual("none");
        SurgeryState.WoundIsClamped(closed);

        var at = new Vector2(0.5f, 0.62f);
        var incision = SurgeryState.SkinIsCut(patient, at - new Vector2(0.15f, 0f), at + new Vector2(0.15f, 0f), 1f);
        await Frames.Seconds(1f);
        AssertString(Spot(body, incision.BleedPoint)).OverrideFailureMessage("the opened incision shows a spot on its walls").IsEqual("inside");
        var shown = SpotCenter(body, incision.BleedPoint);
        AssertFloat(shown).OverrideFailureMessage($"on the deepest layer it's cut through, not the skin's thin edge ({shown * 1000f:0.0} mm deep)")
            .IsGreater(PatientBody.SkinThickness + body.FatThickness);
        // Off the cut's own bleed point and its spot.
        var middle = at + new Vector2(0.03f, 0f);
        AssertBool(body.Tissue.IsOpen(middle)).OverrideFailureMessage("the vessel lies in the opening").IsTrue();
        var vessel = SurgeryState.VesselBleeds(patient, middle, body.CavityDepth() * 0.5f);
        var strong = SurgeryState.VesselBleeds(patient, at + new Vector2(0.07f, 0f), vessel.DepthM, 1f);
        var weak = SurgeryState.VesselBleeds(patient, at - new Vector2(0.07f, 0f), vessel.DepthM, 0.01f);
        await Frames.Seconds(1f);
        AssertString(Spot(body, middle)).OverrideFailureMessage("the vessel shows its spot on the cut's walls, not on the skin over it")
            .IsEqual("inside");
        AssertFloat(weak.Bleeding).OverrideFailureMessage("the weak vessel bleeds the least a vessel bleeds")
            .IsLess(strong.Bleeding * 0.05f);
        AssertString(Spot(body, weak.Points[0])).OverrideFailureMessage($"however little it bleeds ({weak.Bleeding:0.000} ml/s), its spot shows")
            .IsEqual("inside");
        await driver.Unbudgeted(() => {
            var (strongSpot, weakSpot) = (SpotArea(body, strong.Points[0]), SpotArea(body, weak.Points[0]));
            AssertFloat(strongSpot).OverrideFailureMessage($"the spot grows with the bleeding ({strongSpot:0.0} mm² against {weakSpot:0.0} mm²)")
                .IsGreater(weakSpot * 2f);
            return Task.CompletedTask;
        });
        foreach (var layer in (int[])[1, 2]) {
            WallsKeepTheirSpotsInPlace(body, layer, at, 0.1f);
        }
        await CloseUp(session, "vessels_close", "close on the incision: round vein-like spots within the walls of the layers they bleed from, none running off a layer, the right one biggest, the left one smallest but clear, nothing on the skin around the cut", at);

        SurgeryState.WoundIsClamped(weak);
        await Frames.Seconds(0.5f);
        AssertString(Spot(body, weak.Points[0])).OverrideFailureMessage($"stopped bleeding ({weak.Bleeding:0.000} ml/s), its spot is gone")
            .IsEqual("none");

        // The edge holding the vessel pulled two centimeters across the cut: the spot goes with it.
        var k = SurgeryState.TissueIsPulled(patient, middle, new Vector3(0f, 0f, 0.02f));
        var before = body.LayerPoint(0, k);
        await Frames.Seconds(1f);
        var moved = body.LayerPoint(0, k).DistanceTo(before);
        AssertFloat(moved).OverrideFailureMessage($"the edge is pulled ({moved * 1000f:0.0} mm)").IsGreater(0.012f);
        AssertString(Spot(body, middle)).OverrideFailureMessage("the spot stays on the pulled tissue").IsEqual("inside");
        await CloseUp(session, "pulled_close", "close on the pulled edge: the middle vessel's spot on the wall of the edge that moved, nothing left where it was", middle);

        var covered = await Frames.Until(() => UnderPool(body, middle), 180f);
        AssertBool(covered).OverrideFailureMessage($"the cavity pool rises over the vessel ({patient.CavityBloodMl:0} ml)").IsTrue();
        await driver.Capture("vessel_under_pool", "the vessels covered by the pool: its surface plain dark blood, nothing standing on it");

        // Many vessels at once along the incision: each shows its spot inside where the incision gapes, or lies right by a
        // faster one's in another layer, and puddles on the skin where it's shut.
        var vessels = Enumerable.Range(0, 20)
            .Select(i => SurgeryState.VesselBleeds(patient, at + new Vector2((i - 10) * 0.012f, 0f), vessel.DepthM)).ToList();
        await Frames.Seconds(1f);
        // Texture scans are assertions, not a frame of the game's blood simulation.
        await driver.Unbudgeted(async () => {
            var unseen = vessels.Where(v => SpotArea(body, v.Points[0], 2f * WoundMap.MaxSpotRadius) == 0f && Blood(body, v.Points[0]) < 0.5f)
                .Select(v => v.Points[0]).ToList();
            await driver.AssertAndCapture(unseen.Count == 0,
                $"every vessel shows where it bleeds (unseen at {string.Join(", ", unseen)})", "many_vessels");
        });
        await session.Finish(BudgetOverride);
    }

    /// <summary>
    /// An opened cut that doesn't reach the cavity holds its blood itself, resting on the first layer it hasn't cut
    /// through: on the fat under a cut through the skin, lower on the muscle once the fat is cut too. It rises as it
    /// fills and spills onto the skin once full. With key frames review the pool in the opening on the fat, then lower
    /// on the muscle, then brimming with blood running onto the skin.
    /// </summary>
    [TestCase(Timeout = Limits.Slow)]
    public async Task ACutHoldsItsBloodOnTheFirstLayerItHasntCutThrough() {
        var session = await ToolSession.Start("appendectomy", "cut_pools");
        var driver = session.Driver;
        var patient = driver.Patient;
        var body = driver.Body;
        var at = new Vector2(0.5f, 0.5f);
        var (from, to) = (at - new Vector2(0.06f, 0f), at + new Vector2(0.06f, 0f));
        var cut = SurgeryState.SkinIsCut(patient, from, to, 0.3f);
        // One lip held back, so it gapes like an opened incision.
        SurgeryState.TissueIsPulled(patient, at + new Vector2(0f, body.MetersToUv(0.002f)), new Vector3(0f, 0f, 0.008f));
        var opened = await Frames.Until(() => cut.Opened > 0.3f && PoolAt(body, at) is not null, 10f);
        AssertBool(opened).OverrideFailureMessage($"held open ({cut.Opened:0.00}), the skin cut holds a pool").IsTrue();
        var onFat = PoolAt(body, at)!.Value;
        AssertFloat(onFat.Floor).OverrideFailureMessage($"on the fat, just under the skin ({onFat.Floor * 1000f:0.0} mm)")
            .IsEqualApprox(PatientBody.SkinThickness, 0.0001f);
        AssertFloat(patient.CavityBloodMl).OverrideFailureMessage("none of it reaches the cavity").IsEqual(0f);
        await Frames.Seconds(1f);
        await CloseUp(session, "on_fat", "the skin cut held open: blood pooled in the opening on the yellow fat under it", at);

        SurgeryState.SkinIsCut(patient, from, to, 0.5f);
        var deeper = await Frames.Until(() => PoolAt(body, at) is { } pool && pool.Floor > onFat.Floor, 10f);
        var onMuscle = PoolAt(body, at);
        AssertBool(deeper && onMuscle is not null).OverrideFailureMessage($"cut through the fat, it pools lower ({onMuscle})").IsTrue();
        AssertFloat(onMuscle!.Value.Floor).OverrideFailureMessage("on the muscle, under the fat")
            .IsEqualApprox(PatientBody.SkinThickness + body.FatThickness, 0.0001f);
        await Frames.Seconds(1f);
        await CloseUp(session, "on_muscle", "cut through the fat too: the blood pooled lower in the opening, on the red muscle", at);

        var full = await Frames.Until(() => PoolAt(body, at) is { Fill: >= 1f }, 120f);
        AssertBool(full).OverrideFailureMessage($"it fills ({PoolAt(body, at)?.Fill:0.00})").IsTrue();
        // Thicker than the film on the lip where it bleeds from: a puddle.
        var spilled = await Frames.Until(() => Blood(body, at) > 0.8f, 10f);
        await driver.AssertAndCapture(spilled, "full, the cut brims with blood that spills over its lips onto the skin", "spills");
        await session.Finish(BudgetOverride);
    }

    /// <summary>How far (meters) along a straight cut a point of a wall's lip may lie from where its neighbours along
    /// the lip put its uv.</summary>
    private const float LipSlide = 0.0001f;

    /// <summary>
    /// Along a straight cut along the site's x, within <paramref name="reach"/> (uv) of <paramref name="at"/>, each
    /// point at the top of a layer's walls lies along the lip where its uv puts it, as far from its neighbours as their
    /// uvs are apart (give or take the lip's own even stretch): a bleed spot (painted by uv) on the wall is as round as
    /// painted, not stretched or squeezed along the lip.
    /// </summary>
    private static void WallsKeepTheirSpotsInPlace(PatientBody body, int layer, Vector2 at, float reach) {
        var name = body.Layers[layer].Name;
        var mesh = body.Layers[layer].Mesh;
        AssertInt(mesh.GetSurfaceCount()).OverrideFailureMessage($"the incision has {name} walls").IsGreaterEqual(2);
        var arrays = mesh.SurfaceGetArrays(mesh.GetSurfaceCount() - 1);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        var middle = body.SitePoint(at, 0f).Z;
        // Per side of the cut, each lip point's uv along the cut and how far along it lies from where that uv is.
        var sides = new[] { new SortedDictionary<float, float>(), new SortedDictionary<float, float>() };
        // Each wall is drawn in columns down from its lip, PatientBody.WallRows + 1 vertices each, top first.
        for (var v = 0; v < vertices.Length; v += PatientBody.WallRows + 1) {
            if (Mathf.Abs(uvs[v].X - at.X) <= reach && Mathf.Abs(uvs[v].Y - at.Y) <= reach) {
                sides[vertices[v].Z > middle ? 0 : 1][uvs[v].X] = vertices[v].X - body.SitePoint(uvs[v], 0f).X;
            }
        }
        var (worst, where) = (0f, 0f);
        foreach (var lip in sides.Select(side => side.ToArray())) {
            AssertInt(lip.Length).OverrideFailureMessage($"{name} walls along both lips of the incision").IsGreaterEqual(3);
            for (var n = 1; n < lip.Length - 1; n++) {
                var (before, here, after) = (lip[n - 1], lip[n], lip[n + 1]);
                var expected = Mathf.Lerp(before.Value, after.Value, (here.Key - before.Key) / (after.Key - before.Key));
                if (Mathf.Abs(here.Value - expected) > worst) {
                    (worst, where) = (Mathf.Abs(here.Value - expected), here.Key);
                }
            }
        }
        AssertFloat(worst).OverrideFailureMessage(
                $"the {name} walls' lips lie where their uvs put them, not slid along away from them: {worst * 1000f:0.00} mm off at uv x {where:0.000}")
            .IsLessEqual(LipSlide);
    }

    /// <summary>The pool held in a cut at uv, if any.</summary>
    private static CutPool? PoolAt(PatientBody body, Vector2 uv) =>
        body.Blood.Pools.Where(pool => pool.Uv.DistanceTo(uv) < 0.02f).Select(pool => (CutPool?)pool).FirstOrDefault();

    /// <summary>Key frame <paramref name="keyFrame"/> close on the site at <paramref name="uv"/>, from above and
    /// obliquely.</summary>
    private static async Task CloseUp(ToolSession session, string keyFrame, string description, Vector2 uv) {
        if (session.Shots is { } shots) {
            await session.Driver.Unbudgeted(() => shots.CaptureAt(keyFrame, description, session.Driver.SitePoint(uv), 0.1f));
        }
    }

    /// <summary>How deep (meters) under the skin the center of the bleed spot inside a cut at uv is.</summary>
    private static float SpotCenter(PatientBody body, Vector2 uv) =>
        body.WoundMap.Value(WoundMap.Layer.Bleeds, WoundMap.SpotDepth, uv)
        / Mathf.Max(body.WoundMap.Value(WoundMap.Layer.Bleeds, WoundMap.Spot, uv), 0.001f) * WoundMap.MaxSpotDepth;

    private static float Blood(PatientBody body, Vector2 uv) => body.WoundMap.Value(WoundMap.Layer.Fluids, WoundMap.Blood, uv);

    /// <summary>"on the skin", "inside" (on the walls of a cut, under the skin) or "none": the bleed spot at uv.</summary>
    private static string Spot(PatientBody body, Vector2 uv) =>
        body.WoundMap.Value(WoundMap.Layer.Bleeds, WoundMap.Spot, uv) > 0.5f ? "inside"
        : body.WoundMap.Value(WoundMap.Layer.Bleeds, WoundMap.SkinSpot, uv) > 0.5f ? "on the skin" : "none";

    /// <summary>Area (mm²) around <paramref name="uv"/>, within <paramref name="within"/> meters, that a bleed spot of
    /// either kind covers.</summary>
    private static float SpotArea(PatientBody body, Vector2 uv, float within = 0.01f) {
        var texel = 1f / body.WoundMap.Size;
        var reach = Mathf.CeilToInt(body.MetersToUv(within) / texel);
        var covered = 0;
        for (var j = -reach; j <= reach; j++) {
            for (var i = -reach; i <= reach; i++) {
                var at = uv + new Vector2(i, j) * texel;
                var spot = Mathf.Max(body.WoundMap.Value(WoundMap.Layer.Bleeds, WoundMap.Spot, at),
                    body.WoundMap.Value(WoundMap.Layer.Bleeds, WoundMap.SkinSpot, at));
                covered += spot > 0f ? 1 : 0;
            }
        }
        var mm = body.UvToMeters(texel) * 1000f;
        return covered * mm * mm;
    }

    /// <summary>Whether the cavity pool's surface lies over where the bleed at <paramref name="uv"/> comes from.</summary>
    private static bool UnderPool(PatientBody body, Vector2 uv) =>
        body.Blood.Sources.Where(source => source.Inside).OrderBy(source => source.Uv.DistanceTo(uv)).Take(1)
            .Any(source => body.Site.ToLocal(body.UvToWorld(source.Uv, source.Depth)).Y < body.CavityPoolHeight);
}
