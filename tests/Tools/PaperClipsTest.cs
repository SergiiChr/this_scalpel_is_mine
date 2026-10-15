namespace Scalpel.Tests.Tools;

/// <summary>
/// Paper clips as a player uses them: the wheel pulls them like the needle's thread (down tighter, up looser) instead
/// of setting an effort level, the hand status reads the pull, and clips pressed along a cut close it as far as the
/// pull allows: loose ones leave the edges apart, closed ones shut it. Key frames: the two cuts clipped, loose and
/// closed, and what the surgeon sees with the clips pulled closed; review them for the loose cut's edges still apart,
/// the closed cut's edges meeting, and the hand status reading closed.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("tool_paper_clips"), TestCategory("tissue_modification"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class PaperClipsTest {
    // Temporary local budget override: recorded CPU/render overruns remain measured; functional assertions still fail normally.
    private const string BudgetOverride = "known to go over the frame budget: each clip changes the tissue, and rebuilding "
        + "its layers takes about 6 ms (PatientBody.RebuildLayers()); not optimized yet";

    [TestCase]
    public async Task TheWheelPullsTheClips() {
        var session = await ToolSession.Start("appendectomy", "paper_clips/wheel", views: true, captureUntouched: false);
        var driver = session.Driver;
        var me = driver.Me;
        var patient = driver.Patient;
        AssertBool(await Frames.Until(() => !patient.Vitals.IsAwake, Db.Drug("propofol")!.Onset * DrugDef.DirectOnset * 2f))
            .OverrideFailureMessage("the fixture is asleep before its cuts are made").IsTrue();
        var loose = SurgeryState.SkinIsCut(patient, new Vector2(0.3f, 0.35f), new Vector2(0.37f, 0.35f), 0.5f);
        var closed = SurgeryState.SkinIsCut(patient, new Vector2(0.3f, 0.6f), new Vector2(0.37f, 0.6f), 0.5f);
        await Frames.Seconds(1f);
        SurgeryState.ToolIsOnTray(driver.Surgery, "paper_clips");
        var clips = (await driver.PlayerRequestsItem("paper_clips"))!;
        AssertBool(me.UsesLevel(me.Active)).OverrideFailureMessage("the clips take no effort level from the wheel").IsFalse();
        var hints = string.Join("\n", Hud.ControlLines(me));
        AssertBool(hints.Contains("Loosen / tighten paper clips"))
            .OverrideFailureMessage("the controls shown name the pull on the wheel:\n" + hints).IsTrue();
        AssertString(SewAction.ThreadState(clips)).OverrideFailureMessage("fresh clips read loose").IsEqual("loose");

        var before = clips.Suture.Tension;
        await SurgeryDriver.Notch(false);
        AssertFloat(clips.Suture.Tension).OverrideFailureMessage("wheel down pulls the clips one step tighter")
            .IsEqualApprox(before - ToolActions.SutureTensionStep, 0.001f);
        await SurgeryDriver.Notch(true);
        AssertFloat(clips.Suture.Tension).OverrideFailureMessage("wheel up lets them out one step again")
            .IsEqualApprox(before, 0.001f);

        await driver.PlayerTapes(loose, "loose");
        await Frames.Seconds(1f);
        AssertString(SewAction.ThreadState(clips)).OverrideFailureMessage("the clips still read loose").IsEqual("loose");
        AssertFloat(loose.Closure).OverrideFailureMessage($"loose clips close the cut only part way ({loose.Closure:0.00})\n{driver.Recent()}")
            .IsBetween(0.5f, 0.95f);
        await driver.AssertAndCapture(driver.Body.Tissue.GapAlong(loose.Points, 0.03f, TissueDepth.Skin) > TissueSim.ClosedGap,
            "the loose clips leave the top cut's edges apart", "clipped_loose");

        await driver.PlayerTapes(closed);
        await Frames.Seconds(1f);
        AssertString(SewAction.ThreadState(clips)).OverrideFailureMessage("pulled closed, the clips read closed").IsEqual("closed");
        AssertBool(driver.Surgery.Hud.HandsLine.Contains("pull: closed"))
            .OverrideFailureMessage("the hand status reads the pull: " + driver.Surgery.Hud.HandsLine).IsTrue();
        await driver.AssertAndCapture(closed.Closure >= Patient.ClosedEnough,
            $"the clips pulled closed shut the lower cut (closure {closed.Closure:0.00}), its edges meeting, the hand status reading closed",
            "clipped_closed");

        await driver.PlayerPullsThread("too tight");
        AssertString(SewAction.ThreadState(clips)).OverrideFailureMessage("pulled on, the clips read too tight").IsEqual("too tight");
        await session.Finish(BudgetOverride);
    }
}
