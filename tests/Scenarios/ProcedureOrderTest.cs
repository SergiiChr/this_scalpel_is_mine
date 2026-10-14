namespace Scalpel.Tests.Scenarios;

/// <summary>
/// The scenario's steps count in whatever order a player does them, except where one has to come first: the hand
/// stitched with the area numbed before it's cleaned ends well, the order the scenario lists is
/// <see cref="ScenarioFlowsTest.PlaysThrough"/>. Stitched before it's cleaned, the closure doesn't count and the patient
/// leaves with the job undone.
/// </summary>
[TestSuite, RequireGodotRuntime, IsolateCases]
[TestCategory("slow"), TestCategory("scenario")]
[GodotArgs("--fixed-fps", "60")]
public class ProcedureOrderTest
{
    [TestCase("local_block,sanitize,close,stop_bleeding", true, Timeout = Limits.Slow)]
    [TestCase("local_block,close,sanitize,stop_bleeding", false, Timeout = Limits.Slow)]
    public async Task HandStitchInAnotherOrder(string order, bool success)
    {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        await driver.Start("hand_stitch");
        var surgery = driver.Surgery;
        var objectives = surgery.Objectives;
        foreach (var type in order.Split(','))
        {
            await driver.PlayerCompletes(objectives.Steps.First(step => step.Type == type));
        }
        var required = objectives.Steps.Select((step, i) => (step, state: objectives.States[i])).Where(pair => !pair.step.Optional).ToList();
        await Frames.Until(() => required.All(pair => pair.state.Done || pair.state.Spoiled), 30f);
        await driver.PlayerDischarges();
        var report = surgery.Report;
        AssertBool(report?.Success == success)
            .OverrideFailureMessage($"{order} ends in {(success ? "success" : "failure")}: {report?.Reason ?? "not finished"}, "
                + $"{string.Join(", ", objectives.Snapshot())}. Last steps:\n{driver.Recent()}")
            .IsTrue();
        AssertBool(required.Single(pair => pair.step.Type == "close").state.Spoiled)
            .OverrideFailureMessage($"the closure is spoiled only when stitched before cleaning ({order})").IsEqual(!success);
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }
}
