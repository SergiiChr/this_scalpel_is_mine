namespace Scalpel.Tests.Tools;

/// <summary>Surgical tape as a player uses it: ordered from the nurse and pressed along a cut until it's closed.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tool_surgical_tape")]
[GodotArgs("--fixed-fps", "60")]
public class TapeTest
{
    /// <summary>
    /// A roll has 15 charges (tools.cfg) and every wound bin it closes costs one, but the hand stitch cut has 29 bins
    /// (Wound.BinLengthUv). The tape closes the first half and then silently does nothing: no toast says it's used up
    /// and the HUD doesn't show charges. The needle, for comparison, closes the same cut in about 22 s. To fix: enough
    /// charges for the cuts tape is meant for (or charges per centimeter), and say when a roll runs out.
    /// </summary>
    [TestCase(Timeout = Limits.Slow,
        Description = "BROKEN: one roll of tape closes only 15 of the hand stitch cut's 29 bins, then does nothing without saying why.")]
    [TestCategory("broken")]
    public async Task TapeClosesTheHandStitchCut()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("hand_stitch");
        var surgery = driver.Surgery;
        await driver.PlayerSanitizesSite(0.5f);
        await driver.PlayerNumbsSite();
        await driver.PlayerClosesWounds("surgical_tape");
        AssertFloat(driver.Patient.SkinClosure()).OverrideFailureMessage("the cut is closed with tape").IsGreaterEqual(0.9f);
        await driver.PlayerStopsBleeding(0.2f);
        await Frames.Until(() => surgery.Finished, 60f);
        AssertBool(surgery.Report?.Success == true)
            .OverrideFailureMessage($"hand stitch ends in success: {string.Join(", ", surgery.Objectives.Snapshot())}")
            .IsTrue();
        await driver.Stop();
    }
}
