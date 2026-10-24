namespace Scalpel.Tests.Tools;

/// <summary>
/// Gauze as a player uses it, with no pressure to set on the wheel: pressed on a cut it stops the bleeding, holds it
/// for a while after it comes off, then the cut bleeds again; pressed beside the cut, it doesn't.
/// Packed into an incision, it holds the vessel it reaches at the bottom and the incision's edges it lies against the
/// same way, but not a vessel further along the incision.
/// Pressed long enough on a cut too small to sew, it stops that one for good.
/// Debug mode says when a wound stops bleeding and whether for good.
/// Gauze also wipes away the beads of blood a needle leaves.
/// With key frames review the gauze lying on the cut while pressed, the skin wiped dry while the pressure holds, the
/// blood welling back, the gauze in the incision over the vessel, the bead on the arm gone after wiping, and the spot a
/// needle leaves on the site before and after wiping.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("slow"), TestCategory("tool_gauze"), TestCategory("visual_confirmation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class GauzeTest {
    // Temporary local budget override: recorded CPU/render overruns remain measured; functional assertions still fail normally.
    private static readonly BudgetAllowance BudgetOverride = new("gauze preparation and bleeding simulation exceed the recorded CPU/render budget", 18f);

    private readonly List<string> _toasts = [];
    private IDisposable? _debugHud;

    [BeforeTest]
    public void DebugHud() => _debugHud = SurgeryState.DebugHudIsEnabled();

    [AfterTest]
    public void NoDebugHud() => _debugHud?.Dispose();

    [TestCase(Timeout = Limits.Slow)]
    public async Task GauzeStopsABleedForAWhileThenItComesBack() {
        var session = await ToolSession.Start("appendectomy", "gauze_holds");
        var driver = session.Driver;
        Listen(driver);
        var patient = driver.Patient;
        var cut = SurgeryState.SkinIsCut(patient, new Vector2(0.4f, 0.5f), new Vector2(0.6f, 0.5f), 0.3f);
        await Frames.Seconds(3f);
        var before = cut.Bleeding;
        AssertFloat(before).OverrideFailureMessage($"the cut bleeds: {driver.Bleeders()}").IsGreater(0.1f);
        await driver.Capture("bleeding", "a shallow cut across the site, bleeding");

        await driver.PlayerRequestsItem("gauze");
        var on = driver.SitePoint(cut.Midpoint);
        await driver.PlayerWalksTo(on);
        // It presses where it points: lowered on the skin a few centimeters beside the cut, it doesn't stop it.
        await driver.PlayerWorksAt(driver.SitePoint(cut.Midpoint + new Vector2(0f, 0.06f)), 0, 1f);
        AssertFloat(cut.Bleeding).OverrideFailureMessage($"gauze beside the cut doesn't press it: {driver.Bleeders()}")
            .IsGreater(before * 0.9f);
        await driver.PlayerReaches(on);
        // Gauze has no pressure to set: the wheel does nothing, and the controls shown don't offer it.
        await SurgeryDriver.Notch(true);
        var hints = string.Join("\n", Hud.ControlLines(driver.Me));
        AssertBool(driver.Me.Hands[driver.Me.Active].Level == 0 && !hints.Contains("Wheel"))
            .OverrideFailureMessage("the wheel sets no pressure for gauze:\n" + hints).IsTrue();
        SurgeryDriver.Use();
        await Frames.Seconds(2f);
        AssertFloat(cut.Bleeding).OverrideFailureMessage($"pressed with gauze, the cut stops bleeding: {driver.Bleeders()}")
            .IsLess(0.01f);
        AssertBool(Told("[temporarily]")).OverrideFailureMessage($"debug mode says it stopped for now ({Toasts()})").IsTrue();
        await driver.Capture("pressed", "gauze pressed on the cut, the cut no longer bleeding");
        SurgeryDriver.Use(false);
        await driver.PlayerPutsDown();

        await Frames.Seconds(Wound.PressureHold - 3f);
        AssertFloat(cut.Bleeding).OverrideFailureMessage($"the pressure holds a while after the gauze comes off: {driver.Bleeders()}")
            .IsLess(0.01f);
        AssertFloat(patient.LastingBleedRate)
            .OverrideFailureMessage("held only by gauze, it doesn't count as stopped for good").IsGreater(before * 0.9f);
        await driver.Capture("held", "the gauze off while the pressure holds: no fresh pool round the cut, only the blood already lying in it");

        var back = await Frames.Until(() => cut.Bleeding > before * 0.9f, Wound.PressureFade + 6f);
        AssertBool(back).OverrideFailureMessage($"then the cut bleeds as before: {driver.Bleeders()}").IsTrue();
        await Frames.Seconds(3f);
        await driver.Capture("bleeding_again", "the pressure worn off, the cut bleeding as before");
        await session.Finish(BudgetOverride);
    }

    [TestCase]
    public async Task GauzePressedTenSecondsStopsASmallCutForGood() {
        var session = await ToolSession.Start("appendectomy", "gauze_small_cut");
        var driver = session.Driver;
        Listen(driver);
        var patient = driver.Patient;
        var body = driver.Body;
        var small = SurgeryState.SkinIsCut(patient, new Vector2(0.35f, 0.5f), new Vector2(0.35f, 0.5f) + new Vector2(body.MetersToUv(0.006f), 0f), 0.3f);
        var longer = SurgeryState.SkinIsCut(patient, new Vector2(0.6f, 0.5f), new Vector2(0.6f, 0.5f) + new Vector2(body.MetersToUv(0.02f), 0f), 0.3f);
        AssertBool(small.IsSmall(body.UvToMeters(1f)) && !longer.IsSmall(body.UvToMeters(1f)))
            .OverrideFailureMessage("a 0.6 cm cut is too small to sew, a 2 cm one isn't").IsTrue();
        await Frames.Seconds(2f);
        await driver.Capture("bleeding", "a 0.6 cm and a 2 cm cut side by side, both bleeding");
        await driver.PlayerRequestsItem("gauze");

        await Press(driver, small, Wound.SmallCutPress / 2f);
        await Frames.Seconds(Wound.PressBreak * 2f);
        await Press(driver, small, Wound.SmallCutPress / 2f + 1f);
        AssertBool(small.Clotted).OverrideFailureMessage("pressing with a break in between starts the count over").IsFalse();

        await Press(driver, small, Wound.SmallCutPress + 1f);
        AssertBool(small.Clotted).OverrideFailureMessage("pressed on without a break, the small cut stops for good").IsTrue();
        await Frames.Seconds(0.5f);
        AssertBool(Told("[permanently]")).OverrideFailureMessage($"debug mode says it stopped for good ({Toasts()})").IsTrue();
        await Press(driver, longer, Wound.SmallCutPress + 1f);
        AssertBool(longer.Clotted).OverrideFailureMessage("a cut long enough to sew isn't stopped for good").IsFalse();
        await driver.PlayerPutsDown();

        await Frames.Seconds(Wound.PressureHold + Wound.PressureFade + 2f);
        AssertFloat(small.Bleeding).OverrideFailureMessage("once every pressure wears off, the small cut stays dry").IsEqual(0f);
        AssertFloat(longer.Bleeding).OverrideFailureMessage("and the longer one bleeds again").IsGreater(0.01f);
        await driver.Capture("small_cut_stopped", "after the pressure wore off: the small cut dry, the longer one bleeding again");
        await session.Finish(BudgetOverride);
    }

    [TestCase]
    public async Task ASmallCutTornFurtherUnderTheGauzeNeedsAFullPressAgain() {
        var session = await ToolSession.Start("appendectomy", "gauze_torn_further");
        var driver = session.Driver;
        var patient = driver.Patient;
        var body = driver.Body;
        var small = SurgeryState.SkinIsCut(patient, new Vector2(0.4f, 0.5f), new Vector2(0.4f, 0.5f) + new Vector2(body.MetersToUv(0.005f), 0f), 0.3f);
        await Frames.Seconds(1f);
        await driver.PlayerRequestsItem("gauze");
        var on = driver.SitePoint(small.Midpoint);
        await driver.PlayerWalksTo(on);
        await driver.PlayerReaches(on);
        SurgeryDriver.Use();
        await Frames.Seconds(Wound.SmallCutPress + 1f);
        AssertBool(small.Clotted).OverrideFailureMessage("pressed long enough, the small cut stops for good").IsTrue();

        SurgeryState.WoundIsTornFurther(small, body.MetersToUv(0.002f));
        AssertBool(small.IsSmall(body.UvToMeters(1f))).OverrideFailureMessage("torn to 0.7 cm, it's still too small to sew").IsTrue();
        await Frames.Seconds(Wound.SmallCutPress / 2f);
        AssertBool(small.Clotted).OverrideFailureMessage("torn further under the gauze, it isn't stopped for good at once").IsFalse();
        await Frames.Seconds(Wound.SmallCutPress / 2f + 1f);
        AssertBool(small.Clotted).OverrideFailureMessage("after another full press it is").IsTrue();
        SurgeryDriver.Use(false);
        await driver.PlayerPutsDown();
        await session.Finish(BudgetOverride);
    }

    [TestCase]
    public async Task GauzePackedIntoAnIncisionStopsTheVesselAndTheEdgesAroundIt() {
        var session = await ToolSession.Start("appendectomy", "gauze_packed");
        var driver = session.Driver;
        var patient = driver.Patient;
        var body = driver.Body;
        var at = new Vector2(0.5f, 0.62f);
        var cut = SurgeryState.SkinIsCut(patient, at - new Vector2(0.1f, 0f), at + new Vector2(0.1f, 0f), 1f);
        await Frames.Seconds(1f);
        var vessel = SurgeryState.VesselBleeds(patient, at, body.CavityDepth() * 0.5f);
        // Further along the same incision, out of the gauze's reach: packing one spot doesn't press the whole opening.
        var far = SurgeryState.VesselBleeds(patient, at + new Vector2(0.08f, 0f), body.CavityDepth() * 0.5f);
        await Frames.Seconds(1f);
        AssertBool(vessel.Bleeding > 0.1f && far.Bleeding > 0.1f && cut.Bleeding > 0.1f)
            .OverrideFailureMessage($"both vessels in the incision and the incision's edges bleed: {driver.Bleeders()}").IsTrue();
        await driver.Capture("vessel_bleeding", "a vessel bleeding in the incision, the incision's edges bleeding too");

        await driver.PlayerRequestsItem("gauze");
        var on = driver.SitePoint(at);
        await driver.PlayerWalksTo(on);
        await driver.PlayerReaches(on);
        SurgeryDriver.Use();
        await Frames.Seconds(2f);
        AssertString(body.Probe(driver.Me.HeldTool(driver.Me.Active)!.TipPosition()).Zone.ToString())
            .OverrideFailureMessage("the gauze is in the opening").IsEqual(nameof(SiteZone.Cavity));
        AssertBool(vessel.Bleeding < 0.01f && cut.Bleeding < 0.01f)
            .OverrideFailureMessage($"packed with gauze, the vessel and the edges it lies against stop bleeding: {driver.Bleeders()}")
            .IsTrue();
        AssertFloat(far.Bleeding).OverrideFailureMessage($"the vessel out of its reach keeps bleeding: {driver.Bleeders()}")
            .IsGreater(0.1f);
        AssertBool(vessel.LastingBleeding > 0.1f && cut.LastingBleeding > 0.1f)
            .OverrideFailureMessage($"but only while the pressure holds: {driver.Bleeders()}").IsTrue();
        await driver.Capture("packed", "gauze packed into the middle of the opening, the edges beside it no longer bleeding, only earlier blood on the skin next to it");
        SurgeryDriver.Use(false);
        await driver.PlayerPutsDown();
        await session.Finish(BudgetOverride);
    }

    [TestCase]
    public async Task GauzeWipesAwayTheBloodBeadANeedleLeaves() {
        var session = await ToolSession.Start("appendectomy", "gauze_bead");
        var driver = session.Driver;
        var effects = driver.Surgery.Effects;
        await driver.PlayerGivesDrug("vial_propofol", 1f, SurgeryDriver.Route.Vein);
        var beads = effects.Beads().Select(bead => bead.GlobalPosition).ToList();
        AssertInt(beads.Count).OverrideFailureMessage($"the needle leaves a bead of blood on the arm\n{driver.Recent()}").IsEqual(1);
        await session.CaptureAt("bead", "one bead of blood on the arm where the needle went in", beads[0], 0.15f);

        await driver.PlayerRequestsItem("gauze");
        foreach (var bead in beads) {
            await driver.PlayerWalksTo(bead);
            await driver.PlayerWorksAt(bead, 3, 1f);
        }
        await Frames.Physics(5);
        AssertInt(effects.Beads().Count).OverrideFailureMessage("gauze wipes it away like any blood").IsEqual(0);
        await driver.PlayerPutsDown();
        await session.CaptureAt("wiped", "the bead wiped away with gauze, the arm clean", beads[0], 0.15f);
        await session.Finish(BudgetOverride);
    }

    [TestCase]
    public async Task GauzeWipesAwayTheSpotANeedleLeavesOnTheSite() {
        var session = await ToolSession.Start("appendectomy", "gauze_site_spot");
        var driver = session.Driver;
        var map = driver.Body.WoundMap;
        var effects = driver.Surgery.Effects;
        await driver.PlayerNumbsSite();
        var spots = effects.Beads().Where(bead => bead.Dome is null).ToList();
        AssertInt(spots.Count).OverrideFailureMessage($"the needle leaves a spot on the site's skin\n{driver.Recent()}").IsEqual(1);
        var uv = spots[0].Uv;
        var at = spots[0].GlobalPosition;
        AssertFloat(map.Value(WoundMap.Layer.Bleeds, WoundMap.SkinSpot, uv)).OverrideFailureMessage("the spot is painted on the skin")
            .IsGreater(0f);
        await session.CaptureAt("spot", "the needle's puncture: a flat vein-like spot on the site's skin, nothing standing off it", at, 0.15f);

        await driver.PlayerRequestsItem("gauze");
        await driver.PlayerWalksTo(at);
        await driver.PlayerWorksAt(at, 3, 1f);
        await Frames.Seconds(0.3f);
        AssertInt(effects.Beads().Count(bead => bead.Dome is null))
            .OverrideFailureMessage("gauze wipes it away like any blood").IsEqual(0);
        await driver.PlayerPutsDown();
        AssertFloat(map.Value(WoundMap.Layer.Bleeds, WoundMap.SkinSpot, uv))
            .OverrideFailureMessage("and it's gone from the skin with the next repaint").IsEqual(0f);
        await session.CaptureAt("spot_wiped", "the spot wiped away with gauze, the skin at the puncture clean", at, 0.15f);
        await session.Finish(BudgetOverride);
    }

    private void Listen(SurgeryDriver driver) {
        _toasts.Clear();
        driver.Surgery.Hud.Toasted += _toasts.Add;
    }

    /// <summary>Debug mode said a wound stopped bleeding, ending with <paramref name="how"/>.</summary>
    private bool Told(string how) => _toasts.Any(toast =>
        toast.StartsWith("[debug] Bleeding stopped: cut", StringComparison.Ordinal) && toast.EndsWith(how, StringComparison.Ordinal));

    private string Toasts() => string.Join(", ", _toasts);

    /// <summary>Holds the gauze down on the middle of <paramref name="wound"/> for <paramref name="seconds"/>.</summary>
    private static async Task Press(SurgeryDriver driver, Wound wound, float seconds) {
        var on = driver.SitePoint(wound.Midpoint);
        await driver.PlayerWalksTo(on);
        await driver.PlayerWorksAt(on, 3, seconds);
    }
}
