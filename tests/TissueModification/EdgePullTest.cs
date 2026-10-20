namespace Scalpel.Tests.TissueModification;

/// <summary>
/// The edge of a 10 cm open wound in the middle of each cuttable body part, held in the middle and pulled away from
/// the cut in slow stages with the player's hand controls until they give no more. The skin should give easily at
/// first and hold the hand back more and more, never shake, and show its three stages: everything moving together,
/// the skin lifting with the fat stretched under it, then the skin peeled off the fat.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tissue_modification"), TestCategory("visual_confirmation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class EdgePullTest {
    private const float Length = 0.10f;
    /// <summary>How far the hand is steered in each stage, over <see cref="StageFrames"/>.</summary>
    private const float Stage = 0.005f;
    private const int StageFrames = 20;
    /// <summary>Frames the hand is held still after each stage to see the tool settle.</summary>
    private const int HoldFrames = 30;
    private const float MaxPull = 0.08f;
    /// <summary>A stage that moves the tool less than this (meters) counts as the controls not giving.</summary>
    private const float Stalled = 0.0005f;
    /// <summary>How far (meters) the edge may come back toward the cut from the furthest it got.</summary>
    private const float SettleBack = 0.006f;
    private static readonly BudgetAllowance BudgetOverride =
        new("ordering and picking the tool up beside a fresh 10 cm wound, and holding its edge, exceed the recorded CPU budget", 9.5f);

    [TestCase("appendectomy", "forceps")]
    [TestCase("appendectomy", "retractor")]
    [TestCase("knife_back", "forceps")]
    [TestCase("broken_ribs", "forceps")]
    [TestCase("brain_tumor", "forceps")]
    [TestCase("slit_throat", "forceps")]
    [TestCase("leg_extension", "forceps")]
    public async Task EdgePullsInStages(string scenario, string toolId) => await PullEdge(scenario, toolId);

    [TestCase("hand_stitch", "forceps", "stay under the skin", Description = "KNOWN ISSUE: on the narrow forearm the skin's cut wall at the "
        + "wound's ends pokes through the skin beside them once the edge is pulled 10 mm along the body, before it lifts "
        + "(the same before the skin held the hand back gradually).")]
    [TestCase("ambulance_bullet", "forceps", "before it peels off the fat", Description = "KNOWN ISSUE: on the shoulder the held edge's fat and muscle "
        + "are drawn right under its skin (PatientBody.LayersApart() is false there), so the skin never shows peeling "
        + "off its fat.")]
    public async Task EdgePullsInStagesToKnownIssue(string scenario, string toolId, string failure) =>
        await KnownIssue.Expect(failure, () => PullEdge(scenario, toolId));

    [TestCase("gangrene_amputation", "forceps", Description = "BROKEN: on the narrow lower leg the skin's cut wall at one "
        + "of the wound's ends pokes through the skin beside it once the edge is peeled 20 mm up in a run without key "
        + "frames; with them it passes.")]
    [TestCase("leg_extension", "retractor", Description = "BROKEN: shows only in the key frames: a few centimeters from "
        + "the hook the thigh's fat wall folds over itself in a pleat while the edge is drawn aside (the same with the fat "
        + "walls unstretched).")]
    [TestCategory("broken")]
    public async Task EdgePullsInStagesBroken(string scenario, string toolId) => await PullEdge(scenario, toolId);

    private static async Task PullEdge(string scenario, string toolId) {
        var session = await ToolSession.Start(scenario, $"edge_pull/{scenario}_{toolId}");
        var driver = session.Driver;
        var body = driver.Body;
        if (!body.SiteActive) {
            await driver.PlayerTurnsPatient(PatientPose.FaceDown);
        }
        var length = Mathf.Min(Length, body.SiteSize.X * 0.6f);
        var half = new Vector2(length * 0.5f / body.SiteSize.X, 0f);
        var middle = ClearMiddle(driver, half);
        await driver.Unbudgeted(async () => {
            SurgeryState.SkinIsCut(driver.Patient, middle - half, middle + half, ToolActions.DepthByLevel[2]);
            await Frames.Seconds(1f);
        });
        var wounds = driver.Patient.Wounds.Count;
        // The lip: the first skin beside the opening, and a little into it so the jaws close on skin.
        var across = new Vector2(0f, 0.001f / body.SiteSize.Y);
        var lip = middle;
        for (var i = 0; i < 60 && body.LayerAt(lip) != "skin"; i++) {
            lip += across;
        }
        AssertString(body.LayerAt(lip)).OverrideFailureMessage("the wound's edge lies within 6 cm of its middle").IsEqual("skin");
        lip += across * 2f;
        var k = body.Tissue.Nearest(lip);
        var at = driver.SitePoint(lip);
        var along = (driver.SitePoint(middle + half) - driver.SitePoint(middle - half)).Normalized();
        var away = (driver.SitePoint(lip + across * 10f) - at).Slide(Vector3.Up).Normalized();
        session.Along = along;
        // The key frames look at the held edge across the opening, a little off square: whatever stands right across
        // the cut (a knife in the back) would hide it. The last one looks down from behind the edge, past the surgeon.
        var facing = (-away).Rotated(Vector3.Up, 0.5f);

        var tool = (await driver.PlayerRequestsItem(toolId))!;
        await driver.PlayerWalksTo(at);
        await driver.PlayerReaches(at);
        SurgeryDriver.Use();
        await Frames.Physics(10);
        SurgeryDriver.Use(false);
        await Frames.Physics(3);
        AssertBool(tool.Hold is SkinHold).OverrideFailureMessage($"the {toolId} holds the wound's edge ({tool.Hold})").IsTrue();
        await session.CaptureOblique("grabbed", $"the {toolId} holding the middle of the wound's edge, seen across the "
            + "opening: skin, fat and muscle walls lie flat and even along the cut", at, facing, 0.12f, 30f);

        var start = tool.TipPosition();
        var rise0 = Rises(body, k);
        var hand = PlayerInput.HandKey(driver.Me);
        var input = 0f;
        var travel = 0f;
        var lastTip = start;
        var gave = new List<float>();
        var stalled = 0;
        var stretched = -1;
        var peeled = -1;
        while (input < MaxPull - 0.0001f && stalled < 2) {
            SurgeryDriver.Press(hand);
            var pulled = new List<Vector3>();
            for (var f = 0; f < StageFrames; f++) {
                PlayerInput.Mouse(PlayerInput.HandMotion(driver.Me, away * (Stage / StageFrames)));
                await Frames.NextPhysics();
                pulled.Add(tool.TipPosition());
            }
            await Frames.NextPhysics();
            SurgeryDriver.Release(hand);
            input += Stage;
            var held = new List<Vector3>();
            for (var f = 0; f < HoldFrames; f++) {
                await Frames.NextPhysics();
                held.Add(tool.TipPosition());
            }
            var dragShake = Shake(pulled);
            var shake = Shake(held);
            var tip = tool.TipPosition();
            var nowTravel = (tip - start).Dot(away);
            var rise = Rises(body, k) - rise0;
            var (skin, fat, muscle) = (rise.X, rise.Y, rise.Z);
            var when = $"at {input * 1000f:0} mm";
            GD.Print($"edge pull {input * 1000f:0} mm: travel {nowTravel * 1000f:0.0} mm, lift {(tip - start).Y * 1000f:0.0} mm, "
                + $"skin/fat/muscle rise {skin * 1000f:0.0}/{fat * 1000f:0.0}/{muscle * 1000f:0.0} mm, shake {dragShake * 1000f:0.00}/{shake * 1000f:0.00} mm");

            AssertBool(tool.Hold is SkinHold).OverrideFailureMessage($"the {toolId} keeps hold of the edge {when}").IsTrue();
            AssertFloat(dragShake).OverrideFailureMessage($"the hand pulling on {when} doesn't shake ({dragShake * 1000f:0.00} mm)").IsLess(0.001f);
            AssertFloat(shake).OverrideFailureMessage($"the hand held still {when} doesn't shake ({shake * 1000f:0.00} mm)").IsLess(0.001f);
            // Peeled up, the edge comes back toward the cut a little: the skin lifted off has that much less to give.
            AssertFloat(nowTravel).OverrideFailureMessage($"the edge settles back toward the cut no more than {SettleBack * 1000f:0} mm "
                + $"{when} ({nowTravel * 1000f:0.0} mm, was {travel * 1000f:0.0} mm)").IsGreater(travel - SettleBack);
            AssertInt(driver.Patient.Wounds.Count).OverrideFailureMessage($"the pull doesn't tear the skin {when}").IsEqual(wounds);
            // Not gameplay: the check walks every triangle of the opened layers.
            await driver.Unbudgeted(() => {
                TissueLayers.AssertUnderSkin(body);
                return Task.CompletedTask;
            });

            gave.Add(Mathf.Max(nowTravel - travel, 0f) / Stage);
            travel = Mathf.Max(travel, nowTravel);
            if (gave.Count == 1) {
                await driver.AssertAndCapture(Mathf.Abs(skin - fat) < 0.001f && Mathf.Abs(skin - muscle) < 0.001f,
                    "pulled a little, the edge slides along the body as one: skin, fat and muscle walls together, the skin "
                    + $"bending lightly ({skin * 1000f:0.0}/{fat * 1000f:0.0}/{muscle * 1000f:0.0} mm risen)", "together");
            }
            // Apart from the skin only where they're drawn apart from it (PatientBody.LayersApart()).
            if (stretched < 0 && body.LayersApart(k) && skin - muscle > PatientBody.FatStretch * 0.5f && skin - fat < 0.0015f) {
                stretched = gave.Count;
                await session.CaptureOblique("fat_stretched", "the edge lifted, its fat wall stretched up with the skin: "
                    + "no gap between skin and fat, the skin bends smoothly, nothing pokes through it", at, facing, 0.12f, 30f);
            }
            if (peeled < 0 && body.LayersApart(k) && skin - fat > PatientBody.FatStretch * 0.75f) {
                peeled = gave.Count;
                await session.CaptureOblique("skin_peeled", "the skin peeled off the fat: a thin band of skin lifted in "
                    + "the jaws, the fat left lying on the muscle under it, no fat or muscle through the skin", at, facing, 0.12f, 30f);
            }
            stalled = tip.DistanceTo(lastTip) < Stalled ? stalled + 1 : 0;
            lastTip = tip;
        }
        var gaveText = string.Join(", ", gave.Select(g => g.ToString("0.00")));
        AssertFloat(gave[0]).OverrideFailureMessage($"pulled a little, the edge follows the hand ({gaveText} of each stage's input)").IsGreaterEqual(0.9f);
        // Up to where the hand stops, each stage gives no more than the one before (within the solver's settling), and
        // some give part of theirs.
        var giving = gave.TakeWhile(g => g > 0.05f).ToList();
        var rises = giving.Zip(giving.Skip(1), (before, after) => after - before).DefaultIfEmpty(0f).Max();
        AssertBool(rises < 0.15f && gave.Any(g => g is > 0.1f and < 0.95f)).OverrideFailureMessage("the skin holds the "
            + $"hand back more and more the further it's pulled ({gaveText} of each stage's input)").IsTrue();
        AssertFloat(travel).OverrideFailureMessage($"the edge can be pulled at least 13 mm away from the cut ({travel * 1000f:0.0} mm)").IsGreaterEqual(0.013f);
        AssertBool(stretched > 0 && peeled > stretched).OverrideFailureMessage("the skin lifts with the fat stretched under it "
            + $"before it peels off the fat (stretched at stage {stretched}, peeled at stage {peeled})").IsTrue();
        await session.CaptureOblique("final", "the edge pulled as far as the hand goes, from above: the opening drawn "
            + "wide, the peeled skin lifted in the jaws, fat and muscle lying evenly below it", at, -facing, 0.2f, 70f);
        await session.Finish(BudgetOverride);
    }

    /// <summary>The middle of the site, or the nearest spot clear of the scenario's own wounds and targets with room for
    /// the cut and to pull its edge. Where there is none (the neck's own slit runs across it), the middle: the cut
    /// opens the wound there further.</summary>
    private static Vector2 ClearMiddle(SurgeryDriver driver, Vector2 half) {
        var body = driver.Body;
        var room = body.MetersToUv(0.03f);
        bool Clear(Vector2 uv) => driver.Patient.Wounds.All(w => w.DistanceTo(uv) > room)
            && driver.Patient.Targets.All(t => t.Uv.DistanceTo(uv) > room);
        return Enumerable.Range(0, 81).Select(i => new Vector2(0.3f + i % 9 * 0.05f, 0.3f + i / 9 * 0.05f))
            .Where(uv => body.OnBody(uv - half) && body.OnBody(uv + half) && Clear(uv) && Clear(uv - half) && Clear(uv + half))
            .OrderBy(uv => uv.DistanceTo(new Vector2(0.5f, 0.5f)))
            .DefaultIfEmpty(new Vector2(0.5f, 0.5f)).First();
    }

    /// <summary>How high the skin, fat and muscle stand over particle <paramref name="k"/> (x, y, z).</summary>
    private static Vector3 Rises(PatientBody body, int k) =>
        new(body.LayerPoint(0, k).Y, body.LayerPoint(1, k).Y, body.LayerPoint(2, k).Y);

    /// <summary>How far (meters) the tip went back against its way from the first spot to the last, all together: going
    /// one way, however it bends or slows, isn't shaking.</summary>
    private static float Shake(List<Vector3> tip) {
        var way = (tip[^1] - tip[0]).Normalized();
        return tip.Zip(tip.Skip(1), (before, after) => Mathf.Max(-(after - before).Dot(way), 0f)).Sum();
    }
}
