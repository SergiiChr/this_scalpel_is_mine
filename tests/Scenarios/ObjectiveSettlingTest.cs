namespace Scalpel.Tests.Scenarios;

/// <summary>How the steps are judged, set up directly: what's being checked is the judging, not the work. A patient who
/// dies is scored for the steps left undone, as one who's discharged is. A step's prerequisite counts only while it
/// still holds: the amputation cut made after the tourniquet came off again is spoiled, one made with it on is not.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
[GodotArgs("--fixed-fps", "60")]
public class ObjectiveSettlingTest
{
    [TestCase]
    public async Task DeathSettlesTheSteps()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("hand_stitch");
        var surgery = driver.Surgery;
        surgery.Patient.EmitSignal(Patient.SignalName.Died, "Flatline.");
        await Frames.Physics(2);
        var report = surgery.Report!;
        AssertBool(report.Success).OverrideFailureMessage("the patient died").IsFalse();
        var missed = report.Events.SingleOrDefault(line => line.Text == "Discharged the patient with part of the job undone");
        AssertInt(missed.Count).OverrideFailureMessage("every required step left undone at death is scored")
            .IsEqual(surgery.Scenario.Steps.Count(step => !step.Optional));
        await driver.Stop();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task CutCountsOnlyWhileTheTourniquetIsOn(bool keptOn)
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("gangrene_amputation");
        var surgery = driver.Surgery;
        var patient = driver.Patient;
        var objectives = surgery.Objectives;
        SurgeryState.PatientIsAsleep(patient);
        patient.ApplyTourniquet();
        var anesthesia = objectives.States[objectives.Steps.FindIndex(step => step.Type == "anesthesia")];
        var tourniquet = objectives.States[objectives.Steps.FindIndex(step => step.Type == "tourniquet")];
        await Frames.Until(() => anesthesia.Holds && tourniquet.Holds, 30f);
        if (!keptOn)
        {
            patient.RemoveTourniquet();
        }
        var cut = objectives.States[objectives.Steps.FindIndex(step => step.Type == "incise")];
        var length = objectives.Steps.First(step => step.Type == "incise").Parameters.Float("length");
        var span = driver.Body.MetersToUv(length * 1.2f);
        SurgeryState.SkinIsCut(patient, new Vector2(0.5f - (span / 2f), 0.5f), new Vector2(0.5f + (span / 2f), 0.5f), 1f);
        await Frames.Until(() => cut.Reached, 5f);
        AssertBool(cut.Reached).OverrideFailureMessage("the cut is long and deep enough").IsTrue();
        AssertBool(tourniquet.Done).OverrideFailureMessage("applying the tourniquet stays done").IsTrue();
        AssertBool(cut.Spoiled).OverrideFailureMessage($"the cut is spoiled only with the tourniquet off (kept on: {keptOn})")
            .IsEqual(!keptOn);
        await driver.Stop();
    }
}
