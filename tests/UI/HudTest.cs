namespace Scalpel.Tests.UI;

/// <summary>Objectives show as the difficulty tells, update immediately when their state changes and keep their layout
/// while unchanged. A conditional subtask is listed only while its condition holds.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
public class HudTest {
    [TestCase]
    public async Task SurgeryStartsWithObjectiveStatusBeforeTheFirstTick() {
        using var debug = SurgeryState.DebugHudIsEnabled();
        using var difficulty = SurgeryState.DifficultyIs(Difficulty.Baby);
        var driver = SurgeryDriver.Create();
        try {
            // Still reading the card: putting it back would take past the first status update.
            await driver.Start("appendectomy", readingCard: true);
            var surgery = driver.Surgery;
            // A fresh HUD has not had a process frame: setup itself must build the rows.
            var hud = AutoFree(new Hud())!;
            surgery.AddChild(hud);
            // Keep the setup-only HUD out of processing and input; it is freed with its surgery.
            hud.ProcessMode = Node.ProcessModeEnum.Disabled;
            hud.Setup(surgery);
            var panel = hud.FindChildren("*", "", true, false).OfType<ObjectivesPanel>().Single();
            AssertInt(panel.GetChildCount())
                .OverrideFailureMessage("setup creates objective rows, the discharge, the first one's subtasks and the score before any HUD frame")
                .IsEqual(surgery.Scenario.Steps.Count + 1 + surgery.Status.Objectives[0].Subtasks.Length + 1);
            AssertString(panel.GetChild<Label>(0).Text).IsEqual("□ " + surgery.Scenario.Steps[0].Label);
            AssertString(panel.GetChild<Label>(1).Text).IsEqual("• " + surgery.Status.Objectives[0].Subtasks[0]);
            AssertBool(panel.Visible).IsTrue();
            AssertFloat(surgery.Elapsed).OverrideFailureMessage("still before the first periodic status update")
                .IsLess(Surgery.StatusInterval);
            AssertArray(surgery.Status.Objectives.Select(view => view.Label).ToList())
                .OverrideFailureMessage("setup supplies all scenario steps to the HUD immediately, then the discharge")
                .IsEqual(surgery.Scenario.Steps.Select(step => step.Label).Append(Objectives.DischargeLabel).ToList());
            AssertBool(surgery.Status.Objectives.Any(view => view.Done))
                .OverrideFailureMessage("the initial snapshot doesn't complete any steps").IsFalse();
        } finally {
            await driver.Stop();
        }
    }

    [TestCase]
    public async Task ObjectivesChangeWithoutInvalidatingUnchangedLabelThemes() {
        using var debug = SurgeryState.DebugHudIsEnabled();
        using var difficulty = SurgeryState.DifficultyIs(Difficulty.Intern);
        var panel = AutoFree(new ObjectivesPanel())!;
        Frames.Root.AddChild(panel);
        var active = new SurgeryStatus([new ObjectiveView("Close the incision", false, false, false, [])], 0, [], 0f, null, 0f, 0f);
        panel.Refresh(active);
        await Frames.NextProcess();
        var objective = panel.GetChild<Label>(0);
        AssertString(objective.Text).OverrideFailureMessage("the open objective is shown").IsEqual("□ Close the incision");
        AssertThat(objective.GetThemeColor("font_color")).OverrideFailureMessage("the open objective is in plain ink")
            .IsEqual(Ui.Ink);
        var changes = 0;
        objective.ThemeChanged += () => changes++;
        for (var frame = 0; frame < 5; frame++) {
            panel.Refresh(active);
            await Frames.NextProcess();
        }
        AssertInt(changes).OverrideFailureMessage("unchanged objectives keep their shaped text and layout").IsEqual(0);
        panel.Refresh(active with { Objectives = [new ObjectiveView("Close the incision", true, false, false, [])], Score = 10 });
        await Frames.NextProcess();
        AssertString(objective.Text).OverrideFailureMessage("completion updates immediately").IsEqual("✓ Close the incision");
        AssertThat(objective.GetThemeColor("font_color")).OverrideFailureMessage("completed objectives are dimmed")
            .IsEqual(Ui.Dim);
        AssertString(panel.GetChild<Label>(1).Text).OverrideFailureMessage("the score updates alongside the objective")
            .Contains("Score 10");
        AssertInt(changes).OverrideFailureMessage("a changed objective refreshes its theme").IsGreater(0);
        panel.Refresh(active with { Objectives = [new ObjectiveView("Close the incision", false, false, true, [])] });
        await Frames.NextProcess();
        AssertString(objective.Text).OverrideFailureMessage("a spoiled objective is crossed out").IsEqual("✗ Close the incision");
        AssertThat(objective.GetThemeColor("font_color")).OverrideFailureMessage("a spoiled objective shows in red")
            .IsEqual(Ui.Alert);
    }

    /// <summary>"I'm just a baby" lists every main task, optional ones marked, with the subtasks the host sent;
    /// Intern only the required main tasks; MD nothing, and debug mode only adds the score.</summary>
    [TestCase]
    public void DifficultyDecidesWhatTheObjectivesShow() {
        var status = new SurgeryStatus([
            new ObjectiveView("Set up an IV line", true, false, false, []),
            new ObjectiveView("Mark the incision", false, true, false, ["Pick up the surgical marker"]),
            new ObjectiveView("Find the bullet on an X-ray", false, true, false, [], true),
            new ObjectiveView("Open the abdomen", false, false, false, ["Pick up the scalpel", "Cut through the muscle"]),
            new ObjectiveView("Close the incision", false, false, false, []),
        ], 7, [], 0f, null, 0f, 0f);
        List<string> Texts(Difficulty difficulty, bool debug) =>
            [.. ObjectivesPanel.Lines(status, difficulty, debug).Select(line => line.Text)];
        AssertArray(Texts(Difficulty.Baby, false)).OverrideFailureMessage("baby: every task, the subtasks under theirs")
            .IsEqual(new List<string> {
                "✓ Set up an IV line", "□ [optional] Mark the incision", "• Pick up the surgical marker", "□ [optional] Find the bullet on an X-ray",
                "□ Open the abdomen", "• Pick up the scalpel", "• Cut through the muscle", "□ Close the incision",
            });
        AssertFloat(ObjectivesPanel.Lines(status, Difficulty.Baby, false)[2].Indent)
            .OverrideFailureMessage("subtasks sit indented, wrapped lines too").IsGreater(0f);
        AssertArray(Texts(Difficulty.Intern, false)).OverrideFailureMessage("intern: the required main tasks and the hints")
            .IsEqual(new List<string> {
                "✓ Set up an IV line", "□ [optional] Find the bullet on an X-ray", "□ Open the abdomen", "□ Close the incision",
            });
        AssertArray(Texts(Difficulty.Md, false)).OverrideFailureMessage("MD: no objectives").IsEmpty();
        AssertArray(Texts(Difficulty.Md, true)).OverrideFailureMessage("debug mode adds only the score")
            .IsEqual(new List<string> { "\nScore 7" });
    }

    /// <summary>The closure's muscle line is listed only once a wound goes through the muscle, the extraction's line
    /// about cutting it free while the appendix is attached.</summary>
    [TestCase]
    public async Task ConditionalSubtasksShowOnlyWhileTheyApply() {
        var driver = SurgeryDriver.Create();
        try {
            await driver.Start("appendectomy");
            var objectives = driver.Surgery.Objectives;
            var steps = objectives.Steps;
            List<string> Listed(string type) {
                var i = steps.FindIndex(step => step.Type == type);
                return [.. Subtasks.Listed(objectives.StepSubtasks[i], driver.Patient, steps[i].Parameters)];
            }
            AssertBool(Listed("extract").Any(line => line.Contains("still attached")))
                .OverrideFailureMessage("the appendix is attached until cut free").IsTrue();
            bool MuscleListed() => Listed("close").Any(line => line.Contains("muscle is cut"));
            AssertBool(MuscleListed()).OverrideFailureMessage("no muscle to sew before anything is cut").IsFalse();
            SurgeryState.SkinIsCut(driver.Patient, new Vector2(0.45f, 0.5f), new Vector2(0.55f, 0.5f), Wound.MuscleDepth);
            AssertBool(MuscleListed()).OverrideFailureMessage("cut through the muscle, it's sewn first").IsTrue();
        } finally {
            await driver.Stop();
        }
    }
}
