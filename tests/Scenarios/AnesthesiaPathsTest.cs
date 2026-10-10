namespace Scalpel.Tests.Scenarios;

/// <summary>Anesthesia given through player controls: the preinstalled IV and its objective, and alternative
/// anesthesia paths through complete surgeries.</summary>
[TestSuite, RequireGodotRuntime, IsolateCases]
[TestCategory("scenario"), TestCategory("smoke"), TestCategory("slow"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class AnesthesiaPathsTest
{
    [TestCase("first", Timeout = Limits.Slow)]
    [TestCase("second", Timeout = Limits.Slow)]
    [TestCase("third", Timeout = Limits.Slow)]
    public async Task PreinstalledIvDeliversAnesthesiaAndCompletesTheObjective(string patientRun)
    {
        using var debug = SurgeryState.DebugHudIsEnabled();
        var driver = SurgeryDriver.Create();
        var seed = patientRun switch { "first" => 1u, "second" => 2u, _ => 3u };
        await driver.Start("bullet_muscle", seed: seed);
        var patient = driver.Patient;
        var dressing = driver.Surgery.Room.IvLine.Dressing!;
        var vein = driver.Body.Veins[0];
        var insertion = vein.ToGlobal(vein.Line[1]);
        AssertFloat(dressing.GlobalPosition.DistanceTo(insertion))
            .OverrideFailureMessage("the preinstalled catheter sits on the vein near the elbow").IsLess(0.004f);
        AssertBool(patient.Vitals.IsAwake).OverrideFailureMessage("the patient starts awake").IsTrue();
        AssertBool(driver.Surgery.Objectives.States[0].Done).OverrideFailureMessage("anesthesia is initially incomplete").IsFalse();
        var panel = driver.Surgery.Hud.FindChildren("*", "", true, false).OfType<ObjectivesPanel>().Single();
        AssertString(panel.GetChild<Label>(0).Text)
            .OverrideFailureMessage("the debug objective shows actual and required anesthesia depth")
            .Contains("0% / 70%");
        KeyFrames? shots = null;
        if (KeyFrames.Wanted())
        {
            shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(driver.Surgery, $"preop_anesthesia/{seed}");
            AssertBool(await shots.CaptureAt("installed_iv", dressing.GlobalPosition, 0.24f)).IsTrue();
            AssertBool(await shots.CaptureView("awake")).IsTrue();
        }
        await driver.PlayerGivesDrug("vial_propofol", driver.DoseMl("vial_propofol"), SurgeryDriver.Route.Drip);
        await Frames.Until(() => patient.Vitals.Anesthesia >= 0.7f, 30f);
        await Frames.Seconds(Surgery.StatusInterval + 0.1f);
        AssertBool(patient.Vitals.Anesthesia >= 0.7f && !patient.Vitals.IsAwake)
            .OverrideFailureMessage($"the chart dose puts the patient under: anesthesia {patient.Vitals.Anesthesia}").IsTrue();
        AssertBool(driver.Surgery.Objectives.States[0].Done)
            .OverrideFailureMessage("Put the patient under checks off after the dose works").IsTrue();
        AssertBool(driver.Surgery.Status.Objectives[0].Done)
            .OverrideFailureMessage("the HUD receives the completed anesthesia objective").IsTrue();
        if (shots is not null)
        {
            AssertBool(await shots.CaptureView("anesthetized")).IsTrue();
            await Frames.Seconds(2f);
            AssertBool(await shots.CaptureAt("iv_after_anesthesia", dressing.GlobalPosition, 0.24f)).IsTrue();
            shots.End();
        }
        await driver.Stop();
    }

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
