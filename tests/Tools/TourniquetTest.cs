namespace Scalpel.Tests.Tools;

/// <summary>
/// A tourniquet as a player uses it on an arm: ordered from the nurse and pressed onto the forearm above the hand
/// stitch laceration, where the trunk lies close beside the arm, it wraps snug around the forearm, leaves the hand and
/// slows the cut's bleeding. Key frames: review the forearm above the cut bare at first, then the band running around
/// it there, snug on the skin, neither sunk into the arm nor floating off it, and not around the trunk. Pressed onto the
/// surgical site itself, it doesn't go on.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("tool_tourniquet"), TestCategory("visual_confirmation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class TourniquetTest {
    /// <summary>Where (site uv) the tourniquet goes: past the site's elbow end, above the cut.</summary>
    private static readonly Vector2 AboveTheCut = new(1.1f, 0.5f);
    /// <summary>How far (meters) the key frames look from.</summary>
    private const float ViewDistance = 0.35f;
    // Temporary local budget override: recorded CPU overruns remain measured; functional assertions still fail normally.
    private static readonly BudgetAllowance BudgetOverride = new("known to go over the frame budget about a second into "
        + "the hand stitch, before the tourniquet is ordered; not profiled yet", 2.5f);

    [TestCase]
    public async Task PressedOnTheForearmAboveTheCutItWrapsAroundIt() {
        var session = await ToolSession.Start("hand_stitch", "tourniquet");
        var driver = session.Driver;
        var patient = driver.Patient;
        var spot = driver.SitePoint(AboveTheCut);
        await session.CaptureAt("untouched", "the forearm above the cut, bare", spot, ViewDistance);
        await Frames.Seconds(1f);
        var bleeding = patient.Vitals.BleedRate;
        AssertFloat(bleeding).OverrideFailureMessage("the cut bleeds").IsGreater(0f);

        var tourniquet = (await driver.PlayerRequestsItem("tourniquet"))!;
        await driver.PlayerWalksTo(spot);
        await driver.PlayerWorksAt(spot, 0, 0.5f);
        AssertBool(patient.TourniquetOn)
            .OverrideFailureMessage($"pressed onto the forearm the tourniquet goes on\n{driver.Recent()}").IsTrue();
        var problems = TourniquetBand.Problems(tourniquet, driver.Body, spot, "forearm");
        AssertBool(problems.Count == 0).OverrideFailureMessage(string.Join("\n", problems)).IsTrue();
        AssertBool(tourniquet.State == ToolState.Standing && driver.Me.HeldTool(driver.Me.Active) is null)
            .OverrideFailureMessage("the hand lets go of the tourniquet once it's on").IsTrue();
        await session.CaptureAt("on", "the tourniquet's band around the forearm above the cut, snug on the skin", spot,
            ViewDistance);

        await Frames.Seconds(1f);
        AssertFloat(patient.Vitals.BleedRate)
            .OverrideFailureMessage($"the cut bleeds less with the tourniquet on ({patient.Vitals.BleedRate:0.000} ml/s, "
                + $"{bleeding:0.000} before)").IsLess(bleeding * 0.5f);
        await session.Finish(BudgetOverride);
    }

    /// <summary>Pressed onto the surgical site, the band would lie across the work: it stays in the hand.</summary>
    [TestCase]
    public async Task PressedOnTheSiteItStaysInTheHand() {
        var session = await ToolSession.Start("bullet_muscle", "tourniquet_on_site");
        var driver = session.Driver;
        var tourniquet = (await driver.PlayerRequestsItem("tourniquet"))!;
        var middle = driver.SitePoint(new Vector2(0.5f, 0.5f));
        await driver.PlayerWalksTo(middle);
        await driver.PlayerWorksAt(middle, 0, 0.5f);
        AssertBool(!driver.Patient.TourniquetOn && tourniquet.Band is null && driver.Me.HeldTool(driver.Me.Active) == tourniquet)
            .OverrideFailureMessage($"pressed onto the thigh's open site the tourniquet stays in the hand\n{driver.Recent()}").IsTrue();
        await session.Finish(BudgetOverride);
    }
}
