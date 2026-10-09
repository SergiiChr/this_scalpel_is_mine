namespace Scalpel.Tests.UI;

/// <summary>Debug objectives update immediately when their state changes and keep their layout while unchanged.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
public class HudTest
{
    [TestCase]
    public async Task SurgeryStartsWithObjectiveStatusBeforeTheFirstTick()
    {
        using var debug = SurgeryState.DebugHudIsEnabled();
        var driver = SurgeryDriver.Create();
        try
        {
            await driver.Start("appendectomy");
            var surgery = driver.Surgery;
            // A fresh HUD has not had a process frame: setup itself must build the rows.
            var hud = AutoFree(new Hud())!;
            surgery.AddChild(hud);
            // Keep the setup-only HUD out of processing and input; it is freed with its surgery.
            hud.ProcessMode = Node.ProcessModeEnum.Disabled;
            hud.Setup(surgery);
            var panel = hud.FindChildren("*", "", true, false).OfType<ObjectivesPanel>().Single();
            AssertInt(panel.GetChildCount()).OverrideFailureMessage("setup creates objective rows and score before any HUD frame")
                .IsEqual(surgery.Scenario.Steps.Count + 1);
            AssertString(panel.GetChild<Label>(0).Text).IsEqual("▶ " + surgery.Scenario.Steps[0].Label);
            AssertBool(panel.Visible).IsTrue();
            AssertFloat(surgery.Elapsed).OverrideFailureMessage("still before the first periodic status update")
                .IsLess(Surgery.StatusInterval);
            AssertArray(surgery.Status.Objectives.Select(view => view.Label).ToList())
                .OverrideFailureMessage("setup supplies all scenario steps to the HUD immediately")
                .IsEqual(surgery.Scenario.Steps.Select(step => step.Label).ToList());
            AssertBool(surgery.Status.Objectives[0].Current)
                .OverrideFailureMessage("the first required step is already highlighted").IsTrue();
            AssertBool(surgery.Status.Objectives.Any(view => view.Done))
                .OverrideFailureMessage("the initial snapshot doesn't complete any steps").IsFalse();
        }
        finally
        {
            await driver.Stop();
        }
    }

    [TestCase]
    public async Task ObjectivesChangeWithoutInvalidatingUnchangedLabelThemes()
    {
        var panel = AutoFree(new ObjectivesPanel())!;
        Frames.Root.AddChild(panel);
        var active = new SurgeryStatus([new ObjectiveView("Close the incision", false, false, true)], 0, [], 0f, null, 0f, 0f);
        panel.Refresh(active);
        await Frames.NextProcess();
        var objective = panel.GetChild<Label>(0);
        AssertString(objective.Text).OverrideFailureMessage("the active objective is shown").IsEqual("▶ Close the incision");
        AssertThat(objective.GetThemeColor("font_color")).OverrideFailureMessage("the active objective is highlighted")
            .IsEqual(Ui.Pip);
        var changes = 0;
        objective.ThemeChanged += () => changes++;
        for (var frame = 0; frame < 5; frame++)
        {
            panel.Refresh(active);
            await Frames.NextProcess();
        }
        AssertInt(changes).OverrideFailureMessage("unchanged objectives keep their shaped text and layout").IsEqual(0);
        panel.Refresh(active with { Objectives = [new ObjectiveView("Close the incision", true, false, false)], Score = 10 });
        await Frames.NextProcess();
        AssertString(objective.Text).OverrideFailureMessage("completion updates immediately").IsEqual("☑ Close the incision");
        AssertThat(objective.GetThemeColor("font_color")).OverrideFailureMessage("completed objectives are dimmed")
            .IsEqual(Ui.Dim);
        AssertString(panel.GetChild<Label>(1).Text).OverrideFailureMessage("the score updates alongside the objective")
            .Contains("Score 10");
        AssertInt(changes).OverrideFailureMessage("a changed objective refreshes its theme").IsGreater(0);
    }
}
