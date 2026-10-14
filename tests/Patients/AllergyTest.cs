namespace Scalpel.Tests.Patients;

/// <summary>
/// A drug allergy as a player meets it: the chart names the drug, or only says there is one. The lab's allergy screen
/// names it either way. The drug brings on swelling and reveals the allergy, and an antihistamine takes the swelling
/// down well before it would wear off on its own.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
[GodotArgs("--fixed-fps", "60")]
public class AllergyTest
{
    [TestCase("lidocaine", "Allergic to lidocaine.")]
    [TestCase("unknown_lidocaine", "Claims a drug allergy but forgot which one.")]
    public async Task ChartLabReactionAndAntihistamine(string variant, string chartLine)
    {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        await driver.Start("hand_stitch", patientQuirks: [new QuirkRoll("allergy", variant)], readingCard: true);
        var surgery = driver.Surgery;
        var patient = surgery.Patient;
        KeyFrames? shots = null;
        if (KeyFrames.Wanted())
        {
            shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(surgery, $"allergy/{variant}");
            driver.OnKeyFrame = shots.CaptureView;
        }

        var page = string.Join("\n", surgery.Hud.Overlay!.FindChildren("*", nameof(Label), true, false)
            .OfType<Label>().Select(label => label.Text));
        await driver.AssertAndCapture(page.Contains(chartLine), $"the patient card reads \"{chartLine}\"", "card");
        AssertBool(page.Contains("lidocaine", StringComparison.OrdinalIgnoreCase))
            .OverrideFailureMessage("the card names lidocaine only when the allergy is documented")
            .IsEqual(!variant.StartsWith("unknown_", StringComparison.Ordinal));
        await SurgeryDriver.PlayerPutsCardBack(surgery);

        var lab = await driver.PlayerOrdersLab("allergy");
        await driver.PlayerLooksAt(surgery.Room.Monitor.GlobalPosition);
        await driver.AssertAndCapture(lab.Contains("Reacts to: lidocaine"),
            "the monitor's lab panel reads \"Reacts to: lidocaine\"", "lab");

        await driver.PlayerNumbsSite();
        AssertFloat(patient.Vitals.Swelling).OverrideFailureMessage($"lidocaine brings on swelling:\n{driver.Recent()}")
            .IsGreater(0.3f);
        AssertBool(patient.Flags.ContainsKey("revealed_allergy")).OverrideFailureMessage("the reaction reveals the allergy")
            .IsTrue();

        await driver.PlayerGivesDrug("vial_antihistamine", driver.DoseMl("vial_antihistamine"), driver.IvOrVein);
        // Alone, swelling goes down 0.004 a second: what is left would take well over a minute.
        AssertBool(await Frames.Until(() => patient.Vitals.Swelling <= 0f, 45f))
            .OverrideFailureMessage($"the antihistamine takes the swelling down, {patient.Vitals.Swelling:0.00} left:\n{driver.Recent()}")
            .IsTrue();
        AssertBool(patient.Alive).OverrideFailureMessage("the patient comes through the reaction").IsTrue();
        shots?.End();
        await driver.Stop();
    }
}
