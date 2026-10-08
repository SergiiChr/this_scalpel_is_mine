namespace Scalpel.Tests.UI;

/// <summary>Debug objectives update immediately when their state changes and keep their layout while unchanged.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
public class HudTest
{
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
