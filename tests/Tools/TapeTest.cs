namespace Scalpel.Tests.Tools;

/// <summary>Surgical tape as a player uses it: ordered from the nurse and pressed along a cut until it's closed.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tool_surgical_tape")]
[GodotArgs("--fixed-fps", "60")]
public class TapeTest
{
    /// <summary>One roll of tape closes the hand stitch cut, and signed out, the surgery ends in success.</summary>
    [TestCase(Timeout = Limits.Slow)]
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
        await Frames.Until(() => surgery.Objectives.AllDone, 60f);
        await driver.PlayerDischarges();
        AssertBool(surgery.Report?.Success == true)
            .OverrideFailureMessage($"hand stitch ends in success: {string.Join(", ", surgery.Objectives.Snapshot())}")
            .IsTrue();
        await driver.Stop();
    }
}
