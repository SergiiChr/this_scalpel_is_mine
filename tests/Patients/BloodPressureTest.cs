namespace Scalpel.Tests.Patients;

/// <summary>Blood pressure as a player meets it: a stimulant pushes systolic pressure above 140 mmHg, closures leak
/// while it stays there or while heparin acts, cautery holds regardless, and a patient with an aneurysm bursts a
/// vessel, which bruises the skin over it.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
[GodotArgs("--fixed-fps", "60")]
public class BloodPressureTest
{
    [TestCase]
    public async Task AdrenalineBurstsAnAneurysmThatStaysCalmWithoutIt()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy", patientQuirks: [new QuirkRoll("aneurysm", "")]);
        var patient = driver.Patient;
        var before = patient.Wounds.Count;
        await Frames.Seconds(20f);
        AssertFloat(patient.Vitals.Systolic).OverrideFailureMessage("a calm patient's pressure stays below the limit")
            .IsLess(Patient.HighPressure);
        AssertInt(patient.Wounds.Count).OverrideFailureMessage("no vessel gives way at normal pressure").IsEqual(before);
        await driver.PlayerGivesDrug("vial_adrenaline", driver.DoseMl("vial_adrenaline"), SurgeryDriver.Route.Vein);
        var burst = await Frames.Until(() => patient.Flags.ContainsKey("revealed_aneurysm"), 60f);
        AssertBool(burst)
            .OverrideFailureMessage($"adrenaline raises pressure until the aneurysm bursts (systolic {patient.Vitals.Systolic:0})\n{driver.Recent()}")
            .IsTrue();
        var newWounds = patient.Wounds.Skip(before).ToList();
        AssertInt(newWounds.Count).OverrideFailureMessage("one vessel gives way").IsEqual(1);
        if (newWounds.Count > 0)
        {
            AssertBool(newWounds[0].IsInternal).OverrideFailureMessage("the burst vessel bleeds inside the site").IsTrue();
            await Frames.Seconds(1f);
            AssertFloat(patient.Body.WoundMap.Value(WoundMap.Layer.Wounds, WoundMap.Bruise, newWounds[0].Points[0]))
                .OverrideFailureMessage("under closed skin, a bruise spreads over where it bleeds").IsGreater(0.2f);
        }
        await driver.Stop();
    }

    [TestCase]
    public async Task ClosuresLeakUnderHighPressureAndHeparinButCauteryHolds()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var patient = driver.Patient;
        var vitals = patient.Vitals;
        SurgeryState.PatientIsNumb(patient);
        var closed = SurgeryState.SkinIsCut(patient, new Vector2(0.3f, 0.35f), new Vector2(0.3f, 0.65f), 0.3f);
        var seared = SurgeryState.SkinIsCut(patient, new Vector2(0.7f, 0.35f), new Vector2(0.7f, 0.65f), 0.3f);
        SurgeryState.WoundIsClosed(patient, closed);
        SurgeryState.WoundIsCauterized(seared);
        await Frames.Seconds(3f);
        var searedRate = seared.Bleeding;
        AssertFloat(closed.Bleeding).OverrideFailureMessage("a sewn wound is dry at normal pressure").IsLess(0.001f);

        await driver.PlayerGivesDrug("vial_adrenaline", driver.DoseMl("vial_adrenaline"), SurgeryDriver.Route.Vein);
        var high = await Frames.Until(() => vitals.Systolic > 150f, 30f);
        AssertBool(high).OverrideFailureMessage($"adrenaline raises systolic pressure above 150 mmHg (now {vitals.Systolic:0})")
            .IsTrue();
        AssertFloat(closed.Bleeding).OverrideFailureMessage("the sewn wound leaks under high pressure").IsGreater(0.01f);
        AssertFloat(seared.Bleeding).OverrideFailureMessage("the cauterized wound holds under high pressure")
            .IsEqualApprox(searedRate, searedRate * 0.05f);

        var settled = await Frames.Until(() => vitals.Systolic < 130f, 150f);
        AssertBool(settled).OverrideFailureMessage($"pressure falls back once adrenaline wears off (now {vitals.Systolic:0})")
            .IsTrue();
        AssertFloat(closed.Bleeding).OverrideFailureMessage("the sewn wound seals again at normal pressure").IsLess(0.001f);

        await driver.PlayerGivesDrug("vial_heparin", driver.DoseMl("vial_heparin"), SurgeryDriver.Route.Vein);
        var thinned = await Frames.Until(() => closed.Bleeding > 0.01f, 30f);
        AssertBool(thinned).OverrideFailureMessage($"the sewn wound leaks while heparin acts (systolic {vitals.Systolic:0})")
            .IsTrue();
        // Heparin thins all bleeding; the seal itself is what a closure loses and cautery keeps.
        AssertFloat(seared.Bleeding).OverrideFailureMessage("the cauterized wound bleeds no more than heparin's thinning")
            .IsLess(searedRate * 1.7f);
        await driver.Stop();
    }
}
