namespace Scalpel.Tests.UI;

/// <summary>
/// Signing the patient out on the card, as a player does it. Holding Interact over the card's page writes the signature
/// along the discharge line under a see-through pen; let go part way, it unwrites and the surgery goes on. Held the
/// full time, the patient is discharged, and with the job not done yet the report says it failed and why.
/// With key frames also the page before, part way through and once signed.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class DischargeTest
{
    [TestCase]
    public async Task SignatureWritesWhileHeldRollsBackAndSignsOutTooEarly()
    {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        await driver.Start("hand_stitch");
        var surgery = driver.Surgery;
        KeyFrames? shots = null;
        if (KeyFrames.Wanted())
        {
            shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(surgery, "discharge");
            driver.OnKeyFrame = shots.CaptureView;
        }
        driver.Budget.Clear();

        await driver.PlayerTakesCard();
        var signature = surgery.Hud.Signature!;
        await driver.AssertAndCapture(signature.Progress == 0f,
            "the card's page with an empty discharge line at its foot and the hint to hold Interact to sign", "unsigned");

        SurgeryDriver.Press(InputActions.Interact);
        await PlayerInput.Delivered();
        await Frames.Seconds(SignatureView.SignSeconds / 2f);
        await driver.AssertAndCapture(signature.Progress is > 0.4f and < 0.6f,
            "half way: the first half of a looping blue signature on the discharge line, a see-through pen at its end",
            "signing");
        SurgeryDriver.Release(InputActions.Interact);
        await PlayerInput.Delivered();
        await Frames.Seconds(1f);
        AssertFloat(signature.Progress).OverrideFailureMessage("let go part way, the signature unwrites").IsEqual(0f);
        AssertBool(surgery.Finished).OverrideFailureMessage("a part signature discharges nobody").IsFalse();

        SurgeryDriver.Press(InputActions.Interact);
        await PlayerInput.Delivered();
        AssertBool(await Frames.Until(() => signature.Complete, SignatureView.SignSeconds + 0.5f))
            .OverrideFailureMessage("held the full time, the signature completes").IsTrue();
        await driver.AssertAndCapture(!surgery.Finished,
            "the whole signature on the discharge line, a flourish swung back under it, no pen", "signed");
        AssertBool(await Frames.Until(() => surgery.Finished, Hud.SignedPause + 0.5f))
            .OverrideFailureMessage("a moment after signing, the patient is discharged").IsTrue();
        SurgeryDriver.Release(InputActions.Interact);
        await PlayerInput.Delivered();

        var report = surgery.Report!;
        AssertBool(report.Success).OverrideFailureMessage("discharged with the cut still open, the surgery failed").IsFalse();
        AssertString(report.Reason).OverrideFailureMessage("the report says the patient left too early")
            .IsEqual("Discharged before the job was done.");
        var missed = report.Events.SingleOrDefault(line => line.Text == "Discharged the patient with part of the job undone");
        AssertInt(missed.Count).OverrideFailureMessage("every required step left undone is scored")
            .IsEqual(surgery.Scenario.Steps.Count(step => !step.Optional));
        driver.Budget.Check();
        shots?.End();
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }
}
