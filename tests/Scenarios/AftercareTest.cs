namespace Scalpel.Tests.Scenarios;

/// <summary>
/// How the patient leaves the table, scored at the discharge. Put under, then the anesthesia stopped on the card's page,
/// the patient comes round and signed out awake scores; signed out still asleep doesn't. Blood wiped off the skin with
/// gauze scores even with a smear left on the border of the site, where a swab barely reaches; left on, it doesn't.
/// With key frames also the toast as the anesthesia is stopped.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("tool_gauze"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class AftercareTest {
    private const string Awake = "Brought the patient round from anesthesia before the discharge";
    private const string Clean = "Cleaned the blood off the skin";

    [TestCase(true)]
    [TestCase(false)]
    public async Task PatientWokenBeforeTheDischargeScores(bool stopped) {
        // Under a display a software renderer would draw every frame: only the key frame is drawn.
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        await driver.Start("hand_stitch");
        var surgery = driver.Surgery;
        var patient = driver.Patient;
        KeyFrames? shots = null;
        if (stopped && KeyFrames.Wanted()) {
            shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(surgery, "aftercare");
        }
        SurgeryState.PatientIsAsleep(patient);
        AssertBool(await Frames.Until(() => patient.WentUnder && !patient.Vitals.IsAwake, 20f))
            .OverrideFailureMessage($"the patient goes under (anesthesia {patient.Vitals.Anesthesia:0.00})").IsTrue();
        if (stopped) {
            var toasts = new List<string>();
            surgery.Hud.Toasted += toasts.Add;
            await driver.PlayerStopsAnesthesia();
            surgery.Hud.Toasted -= toasts.Add;
            AssertBool(patient.AnesthesiaStopped).OverrideFailureMessage("the button stops the anesthesia").IsTrue();
            AssertBool(toasts.Contains("Anesthesia stopped. The patient is coming round."))
                .OverrideFailureMessage($"everyone is told: {string.Join(", ", toasts)}").IsTrue();
            await SurgeryDriver.PlayerPutsCardBack(surgery);
            if (shots is not null) {
                await shots.CaptureView("stopped",
                    "back at the table, the toast \"Anesthesia stopped. The patient is coming round.\" readable on screen");
            }
            var started = surgery.Elapsed;
            AssertBool(await Frames.Until(() => patient.Vitals.IsAwake, Patient.WakeSeconds + 1f))
                .OverrideFailureMessage($"stopped, the patient wakes within {Patient.WakeSeconds} s (anesthesia {patient.Vitals.Anesthesia:0.00})")
                .IsTrue();
            driver.Note($"the patient woke after {surgery.Elapsed - started:0.0} s");
        }
        await driver.PlayerDischarges();
        var report = surgery.Report!;
        AssertBool(report.Events.Any(line => line.Text == Awake))
            .OverrideFailureMessage($"the report scores the patient woken only when the anesthesia was stopped (stopped: {stopped})")
            .IsEqual(stopped);
        shots?.End();
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task SkinWipedCleanBeforeTheDischargeScores(bool wiped) {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var surgery = driver.Surgery;
        var patient = driver.Patient;
        var body = driver.Body;
        var spot = new Vector2(0.5f, 0.5f);
        var radius = body.MetersToUv(0.03f);
        SurgeryState.SkinIsBloody(patient, spot, radius);
        // A smear on the border, under the drape's edge: it's left out, so it doesn't stand in the way.
        SurgeryState.SkinIsBloody(patient, new Vector2(PatientBody.CleanMargin * 0.5f, 0.5f), PatientBody.CleanMargin * 0.4f);
        var bloody = body.SkinBlood();
        AssertFloat(bloody).OverrideFailureMessage("the stain is blood to clean").IsGreater(Surgery.SkinBloodSeen);
        await Frames.Seconds(Surgery.StatusInterval + 0.1f);
        if (wiped) {
            await driver.PlayerRequestsItem("gauze");
            await driver.PlayerWalksTo(driver.SitePoint(spot));
            for (var row = -radius; row <= radius; row += radius / 3f) {
                await driver.PlayerWorksAlong(
                    [driver.SitePoint(spot + new Vector2(-radius * 1.2f, row)), driver.SitePoint(spot + new Vector2(radius * 1.2f, row))], 1, 0.06f);
            }
        }
        var left = body.SkinBlood();
        AssertBool(left <= bloody * Surgery.SkinBloodLeft)
            .OverrideFailureMessage($"wiped, most of the blood is off the skin ({left:0.0000} of {bloody:0.0000}, wiped: {wiped})")
            .IsEqual(wiped);
        await driver.PlayerDischarges();
        var report = surgery.Report!;
        AssertBool(report.Events.Any(line => line.Text == Clean))
            .OverrideFailureMessage($"the report scores the skin cleaned only when it was wiped (wiped: {wiped})").IsEqual(wiped);
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }
}
