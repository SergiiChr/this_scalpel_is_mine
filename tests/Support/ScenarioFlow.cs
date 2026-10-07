namespace Scalpel.Tests.Support;

/// <summary>
/// A scenario played through its positive flow, like a player: each required objective done in order with the
/// scenario's tools and the controls a player has (<see cref="SurgeryDriver"/>), and the game itself ending the surgery
/// with a successful report. A step that doesn't register fails the case with what the driver did last. A case that
/// asks for key frames, in a run with key frames, also saves the site before anything is done and right after every
/// objective, and fails when a frame takes longer than the frame budget (not on CI). Rendering stays off in between,
/// also under a display: the game runs at full speed, and a software renderer only draws the key frames.
/// </summary>
public static class ScenarioFlow
{
    /// <summary>Game seconds an objective gets to register once its step is done, on top of how long it has to hold.
    /// </summary>
    private const float Settle = 20f;

    /// <summary>Plays <paramref name="scenarioId"/> through. <paramref name="budgetBroken"/> says why its frames are
    /// known to go over the budget (see <see cref="FrameBudget.Check"/>).</summary>
    public static async Task Play(string scenarioId, bool keyFrames = false, string budgetBroken = "")
    {
        RenderingServer.RenderLoopEnabled = false;
        GD.Print($"{scenarioId}: loading operation");
        var driver = SurgeryDriver.Create();
        await driver.Start(scenarioId);
        var surgery = driver.Surgery;
        var objectives = surgery.Objectives;
        KeyFrames? shots = null;
        if (keyFrames && KeyFrames.Wanted())
        {
            shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(surgery, "scenarios".PathJoin(scenarioId));
            driver.OnKeyFrame = async keyFrame => AssertBool(await shots.Capture(keyFrame))
                .OverrideFailureMessage($"{scenarioId}: saved key frame {keyFrame}").IsTrue();
            await driver.Capture("untouched");
        }
        // Loading the room isn't gameplay: the frame budget counts from here.
        driver.Budget.Clear();
        for (var index = 0; index < objectives.Steps.Count; index++)
        {
            var step = objectives.Steps[index];
            if (step.Optional)
            {
                continue;
            }
            await driver.PlayerCompletes(step);
            var hold = step.Parameters.Float("seconds") + (step.Type == "calm" ? 60f : 0f);
            // A drug counts once enough has soaked in to work: through a line, most of it by 1.5 times its onset.
            if (step.Type == "inject" && Db.Drug(step.Parameters.String("drug")) is { } drug)
            {
                hold += drug.Onset * 1.5f;
            }
            var state = objectives.States[index];
            await Frames.Until(() => state.Done || surgery.Finished, hold + Settle);
            AssertBool(state.Done)
                .OverrideFailureMessage($"{scenarioId}: \"{step.Label}\" ({step.Type}) didn't complete. Last steps:\n{driver.Recent()}")
                .IsTrue();
            await driver.Capture($"{step.Type}_done");
        }
        await Frames.Until(() => surgery.Finished, 10f);
        var report = surgery.Report;
        AssertBool(report?.Success == true)
            .OverrideFailureMessage($"{scenarioId} ends in success: {report?.Reason ?? "not finished"}, {surgery.Elapsed:0} s, {report?.Stars ?? 0} stars")
            .IsTrue();
        driver.Budget.Check(shots is not null, scenarioId, budgetBroken);
        if (shots is not null)
        {
            GD.Print($"{scenarioId} key frames: {shots.OutDir}");
            shots.End();
        }
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }
}
