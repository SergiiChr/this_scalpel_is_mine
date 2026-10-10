namespace Scalpel.Tests.Tools;

/// <summary>
/// Gauze as a player uses it: pressed on a cut it stops the bleeding, holds it for a while after it comes off, then the
/// cut bleeds again. Packed into an incision, it holds the vessel it reaches the same way. Pressed long enough on a cut
/// too small to sew, it stops that one for good. Debug mode says when a wound stops bleeding and whether for good.
/// Gauze also wipes away the beads of blood a needle leaves. With key frames review the gauze lying on the cut while
/// pressed, the skin wiped dry while the pressure holds, the blood welling back, the gauze in the incision over the
/// vessel, and the bead on the arm gone after wiping.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("tool_gauze"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class GauzeTest
{
    private readonly List<string> _toasts = [];
    private IDisposable? _debugHud;

    [BeforeTest]
    public void DebugHud() => _debugHud = SurgeryState.DebugHudIsEnabled();

    [AfterTest]
    public void NoDebugHud() => _debugHud?.Dispose();

    [TestCase]
    public async Task GauzeStopsABleedForAWhileThenItComesBack()
    {
        var session = await ToolSession.Start("appendectomy", "gauze_holds");
        var driver = session.Driver;
        Listen(driver);
        var patient = driver.Patient;
        var cut = SurgeryState.SkinIsCut(patient, new Vector2(0.4f, 0.5f), new Vector2(0.6f, 0.5f), 0.3f);
        await Frames.Seconds(3f);
        var before = cut.Bleeding;
        AssertFloat(before).OverrideFailureMessage($"the cut bleeds: {driver.Bleeders()}").IsGreater(0.1f);
        await driver.Capture("bleeding");

        await driver.PlayerRequestsItem("gauze");
        var on = driver.SitePoint(cut.Midpoint);
        await driver.PlayerWalksTo(on);
        await driver.PlayerReaches(on);
        await driver.SetLevel(3);
        SurgeryDriver.Use();
        await Frames.Seconds(2f);
        AssertFloat(cut.Bleeding).OverrideFailureMessage($"pressed with gauze, the cut stops bleeding: {driver.Bleeders()}")
            .IsLess(0.01f);
        AssertBool(Told("[temporarily]")).OverrideFailureMessage($"debug mode says it stopped for now ({Toasts()})").IsTrue();
        await driver.Capture("pressed");
        SurgeryDriver.Use(false);
        await driver.PlayerPutsDown();

        await Frames.Seconds(Wound.PressureHold - 3f);
        AssertFloat(cut.Bleeding).OverrideFailureMessage($"the pressure holds a while after the gauze comes off: {driver.Bleeders()}")
            .IsLess(0.01f);
        AssertFloat(patient.LastingBleedRate)
            .OverrideFailureMessage("held only by gauze, it doesn't count as stopped for good").IsGreater(before * 0.9f);
        await driver.Capture("held");

        var back = await Frames.Until(() => cut.Bleeding > before * 0.9f, Wound.PressureFade + 6f);
        AssertBool(back).OverrideFailureMessage($"then the cut bleeds as before: {driver.Bleeders()}").IsTrue();
        await Frames.Seconds(3f);
        await driver.Capture("bleeding_again");
        await session.Finish();
    }

    [TestCase]
    public async Task GauzePressedTenSecondsStopsASmallCutForGood()
    {
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
        await driver.Capture("bleeding");
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
        await driver.Capture("small_cut_stopped");
        await session.Finish();
    }

    [TestCase]
    public async Task GauzePackedIntoAnIncisionStopsTheVesselInIt()
    {
        var session = await ToolSession.Start("appendectomy", "gauze_packed");
        var driver = session.Driver;
        var patient = driver.Patient;
        var body = driver.Body;
        var at = new Vector2(0.5f, 0.62f);
        SurgeryState.SkinIsCut(patient, at - new Vector2(0.1f, 0f), at + new Vector2(0.1f, 0f), 1f);
        await Frames.Seconds(1f);
        var vessel = SurgeryState.VesselBleeds(patient, at, body.CavityDepth() * 0.5f);
        await Frames.Seconds(1f);
        AssertFloat(vessel.Bleeding).OverrideFailureMessage($"the vessel in the incision bleeds: {driver.Bleeders()}").IsGreater(0.1f);
        await driver.Capture("vessel_bleeding");

        await driver.PlayerRequestsItem("gauze");
        var on = driver.SitePoint(at);
        await driver.PlayerWalksTo(on);
        await driver.PlayerReaches(on);
        await driver.SetLevel(3);
        SurgeryDriver.Use();
        await Frames.Seconds(2f);
        AssertString(body.Probe(driver.Me.HeldTool(driver.Me.Active)!.TipPosition()).Zone.ToString())
            .OverrideFailureMessage("the gauze is in the opening").IsEqual(nameof(SiteZone.Cavity));
        AssertFloat(vessel.Bleeding).OverrideFailureMessage($"packed with gauze, the vessel stops bleeding: {driver.Bleeders()}")
            .IsLess(0.01f);
        AssertFloat(vessel.LastingBleeding).OverrideFailureMessage("but only while the pressure holds").IsGreater(0.1f);
        await driver.Capture("packed");
        SurgeryDriver.Use(false);
        await driver.PlayerPutsDown();
        await session.Finish();
    }

    [TestCase]
    public async Task GauzeWipesAwayTheBloodBeadANeedleLeaves()
    {
        var session = await ToolSession.Start("appendectomy", "gauze_bead");
        var driver = session.Driver;
        var effects = driver.Surgery.Effects;
        await driver.PlayerGivesDrug("vial_propofol", 1f, SurgeryDriver.Route.Vein);
        var beads = effects.Beads();
        AssertInt(beads.Count).OverrideFailureMessage($"the needle leaves a bead of blood on the arm\n{driver.Recent()}").IsEqual(1);
        await CaptureAt(session, "bead", beads[0]);

        await driver.PlayerRequestsItem("gauze");
        foreach (var bead in beads)
        {
            await driver.PlayerWalksTo(bead);
            await driver.PlayerWorksAt(bead, 3, 1f);
        }
        await Frames.Physics(5);
        AssertInt(effects.Beads().Count).OverrideFailureMessage("gauze wipes it away like any blood").IsEqual(0);
        await driver.PlayerPutsDown();
        await CaptureAt(session, "wiped", beads[0]);
        await session.Finish();
    }

    /// <summary>In a run with key frames, saves views of <paramref name="at"/> on the arm, off the site.</summary>
    private static async Task CaptureAt(ToolSession session, string keyFrame, Vector3 at)
    {
        if (session.Shots is { } shots)
        {
            await session.Driver.Unbudgeted(async () => AssertBool(await shots.CaptureAt(keyFrame, at, 0.15f))
                .OverrideFailureMessage($"saved key frame {keyFrame}").IsTrue());
        }
    }

    private void Listen(SurgeryDriver driver)
    {
        _toasts.Clear();
        driver.Surgery.Hud.Toasted += _toasts.Add;
    }

    /// <summary>Debug mode said a wound stopped bleeding, ending with <paramref name="how"/>.</summary>
    private bool Told(string how) => _toasts.Any(toast =>
        toast.StartsWith("[debug] Bleeding stopped: cut", StringComparison.Ordinal) && toast.EndsWith(how, StringComparison.Ordinal));

    private string Toasts() => string.Join(", ", _toasts);

    /// <summary>Holds the gauze down on the middle of <paramref name="wound"/> for <paramref name="seconds"/>.</summary>
    private static async Task Press(SurgeryDriver driver, Wound wound, float seconds)
    {
        var on = driver.SitePoint(wound.Midpoint);
        await driver.PlayerWalksTo(on);
        await driver.PlayerWorksAt(on, 3, seconds);
    }
}
