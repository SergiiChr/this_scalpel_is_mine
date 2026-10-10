namespace Scalpel.Tests.Patients;

/// <summary>Stopping a bleed with what the nurse brings, as a player does it: cautery, a hemostat, tranexamic acid and
/// gauze.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tissue_modification")]
[GodotArgs("--fixed-fps", "60")]
public class BleedingControlTest
{
    /// <summary>A tear as long as the one the colon cancer flow leaves bleeding (meters).</summary>
    private const float Tear = 0.077f;

    /// <summary>Skin torn by overstretching (Patient.Tear()) bleeds hard, and the tools stop it for good. The disabled
    /// scenarios that end with a tear still bleeding get there through the driver, not the game: its one cautery pass
    /// comes before the tear opens.</summary>
    [TestCase]
    public async Task ASkinTearCanBeStopped()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var patient = driver.Patient;
        SurgeryState.PatientIsAsleep(patient);
        SurgeryState.SkinIsTorn(patient, new Vector2(0.45f, 0.5f), Vector2.Right, driver.Body.MetersToUv(Tear));
        await Frames.Seconds(2f);
        AssertFloat(patient.Vitals.BleedRate).OverrideFailureMessage($"the tear bleeds: {driver.Bleeders()}").IsGreater(0.3f);
        await driver.PlayerStopsBleeding(0.3f);
        await Frames.Seconds(10f);
        AssertFloat(patient.LastingBleedRate).OverrideFailureMessage($"the bleeding is under control: {driver.Bleeders()}")
            .IsLessEqual(0.3f);
        await driver.Stop();
    }
}
