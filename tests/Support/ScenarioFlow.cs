namespace Scalpel.Tests.Support;

/// <summary>
/// A scenario played through its positive flow, like a player: each required objective done in the order the scenario
/// lists it with the scenario's tools and the controls a player has (<see cref="SurgeryDriver"/>), then the patient
/// signed out on the card, and the game ending the surgery with a successful report. A step that doesn't register fails the case with what the driver did last. A case that
/// asks for key frames, in a run with key frames, saves the untouched site and distinct visible actions and results.
/// Every run checks the frame budget (<see cref="FrameBudget.Check"/>). Rendering stays off in between,
/// also under a display: the game runs at full speed, and a software renderer only draws the key frames.
/// </summary>
public static class ScenarioFlow {
    /// <summary>Game seconds an objective gets to register once its step is done, on top of how long it has to hold.
    /// </summary>
    private const float Settle = 20f;

    /// <summary>Plays <paramref name="scenarioId"/> through. <paramref name="budgetOverride"/> says why its frames are
    /// known to go over the budget, and by how much (see <see cref="FrameBudget.Check"/>).</summary>
    public static async Task Play(string scenarioId, bool keyFrames = false, BudgetAllowance? budgetOverride = null,
        Func<SurgeryDriver, ObjectiveStep, Task>? playStep = null, Action<SurgeryDriver>? validate = null,
        bool includeOptional = false) {
        FrameBudget.RenderingOff();
        GD.Print($"{scenarioId}: loading operation");
        var driver = SurgeryDriver.Create();
        KeyFrames? shots = null;
        try {
            await driver.Start(scenarioId);
            var surgery = driver.Surgery;
            await InitialSceneChecks.Inspect(surgery);
            var objectives = surgery.Objectives;
            if (keyFrames && KeyFrames.Wanted()) {
                shots = new KeyFrames();
                driver.AddChild(shots);
                shots.Begin(surgery, "scenarios".PathJoin(scenarioId));
                driver.OnKeyFrame = (keyFrame, description) => shots.Capture(keyFrame, description);
                await driver.Capture("untouched", "the site as the scenario starts, before any step");
            }
            // Loading the room isn't gameplay: the frame budget counts from here.
            driver.Budget.Clear();
            for (var index = 0; index < objectives.Steps.Count; index++) {
                var step = objectives.Steps[index];
                if (step.Optional && !includeOptional) {
                    continue;
                }
                await (playStep is null ? driver.PlayerCompletes(step) : playStep(driver, step));
                var hold = step.Parameters.Float("seconds") + (step.Type == "calm" ? 60f : 0f);
                // A drug counts once enough has soaked in to work: through a line, most of it by 1.5 times its onset.
                if (step.Type == "inject" && Db.Drug(step.Parameters.String("drug")) is { } drug) {
                    hold += drug.Onset * 1.5f;
                }
                var state = objectives.States[index];
                await Frames.Until(() => state.Done || surgery.Finished, hold + Settle);
                AssertBool(state.Done)
                    .OverrideFailureMessage($"{scenarioId}: \"{step.Label}\" ({step.Type}) didn't complete. Last steps:\n{driver.Recent()}")
                    .IsTrue();
                // Action helpers capture cut/extraction/closure; internal flags have no unique site photograph.
                if (step.Type is "sanitize" or "flip") {
                    await driver.Capture($"{step.Type}_done", $"\"{step.Label}\" just completed, and the site shows it done");
                }
                if (step.Type == "flip" && surgery.Patient.Body.SiteActive) {
                    var (rise, apart) = await SkinSeam.OverBreath(surgery.Patient.Body);
                    AssertBool(rise > 0.001f && apart < SkinSeam.Max)
                        .OverrideFailureMessage($"{scenarioId}: turned to the site, it rides the breathing trunk: the trunk rose {rise * 1000f:0.0} mm, the site moved {apart * 1000f:0.0} mm apart from it")
                        .IsTrue();
                }
            }
            AssertBool(surgery.Finished)
                .OverrideFailureMessage($"{scenarioId}: the surgery waits for the discharge: {surgery.Report?.Reason}").IsFalse();
            await driver.PlayerDischarges();
            var report = surgery.Report;
            AssertBool(report?.Success == true)
                .OverrideFailureMessage($"{scenarioId} ends in success: {report?.Reason ?? "not finished"}, {surgery.Elapsed:0} s, {report?.Stars ?? 0} stars")
                .IsTrue();
            CheckTimeline(report!, objectives.Steps);
            validate?.Invoke(driver);
            driver.Budget.Check(scenarioId, budgetOverride);
            if (shots is not null) {
                GD.Print($"{scenarioId} key frames: {shots.OutDir}");
            }
        } finally {
            shots?.End();
            await driver.Stop();
            RenderingServer.RenderLoopEnabled = true;
        }
    }

    /// <summary>The report's timeline tells when each required step was done and that it counted at the discharge, in
    /// order, and the game is the newest one in this computer's history.</summary>
    private static void CheckTimeline(SurgeryReport report, List<ObjectiveStep> steps) {
        var texts = report.Timeline.Select(entry => entry.Text).ToList();
        foreach (var step in steps.Where(step => !step.Optional)) {
            AssertBool(texts.Contains($"Done: {step.Label}") && texts.Contains($"Counted: {step.Label}"))
                .OverrideFailureMessage($"the timeline has \"{step.Label}\" done and counted:\n{string.Join('\n', texts)}").IsTrue();
        }
        AssertBool(report.Timeline.Zip(report.Timeline.Skip(1)).All(pair => pair.First.Time <= pair.Second.Time))
            .OverrideFailureMessage("the timeline is in time order").IsTrue();
        AssertObject(Progress.History.Count > 0 ? Progress.History[0].Report : null).OverrideFailureMessage("the game is the newest in the history")
            .IsSame(report);
    }
}
