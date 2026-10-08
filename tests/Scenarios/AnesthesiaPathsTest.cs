namespace Scalpel.Tests.Scenarios;

/// <summary>Other ways of keeping the patient from feeling the surgery than the one a scenario's flow takes, played
/// through like a player: each still ends the surgery with the game's own success report.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("scenario")]
[GodotArgs("--fixed-fps", "60")]
public class AnesthesiaPathsTest
{
    /// <summary>
    /// A patient allergic to lidocaine: the manual (17_conditions/allergy.txt) says to use "General anesthesia, or
    /// topical cocaine" instead. Followed with general anesthesia, the cut gets closed and stops bleeding, but the
    /// surgery never ends: the "Numb the area" step only counts Vitals.LocalBlock, which only lidocaine and cocaine
    /// give, and cocaine can't be ordered. Hand stitch has no time limit, so nothing ever ends it. To fix: let the step
    /// count general anesthesia too, or have the manual and the nurse offer a local anesthetic that works.
    /// </summary>
    [TestCase(Timeout = Limits.Slow,
        Description = "BROKEN: with a lidocaine allergy, general anesthesia (the manual's alternative) never completes \"Numb the area\", the surgery never ends.")]
    [TestCategory("broken")]
    public async Task HandStitchLidocaineAllergyUnderGeneralAnesthesia()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("hand_stitch", patientQuirks: [new QuirkRoll("allergy", "lidocaine")]);
        var surgery = driver.Surgery;
        await driver.PlayerSetsIv();
        await driver.PlayerSanitizesSite(0.5f);
        await driver.PlayerAnesthetizes();
        await driver.PlayerClosesWounds();
        await driver.PlayerStopsBleeding(0.2f);
        await Frames.Until(() => surgery.Finished, 120f);
        AssertBool(surgery.Report?.Success == true)
            .OverrideFailureMessage($"hand stitch ends in success under general anesthesia: {string.Join(", ", surgery.Objectives.Snapshot())}")
            .IsTrue();
        await driver.Stop();
    }
}
