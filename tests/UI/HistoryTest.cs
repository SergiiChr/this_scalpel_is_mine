namespace Scalpel.Tests.UI;

/// <summary>
/// The main menu's history lists the surgeries played on this computer, with scenario, outcome, stars and score. Clicking one opens its post-op report with the timeline, and the way back leads to the list. The game
/// stays in the save file. With key frames also the list and the opened report.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class HistoryTest {
    [TestCase]
    public async Task PlayedGameOpensItsReportFromTheMainMenu() {
        // A game played earlier, recorded directly: what's checked is the menu, not the surgery.
        var timeline = new List<TimelineEntry> {
            new(42f, "Done: Set up an IV line", 0, true),
            new(95f, "Done: Put the patient under", 0, true),
            new(180f, "Jagged incision", -6, false, 2),
            new(181f, "Spoiled: Open the abdomen, \"Sanitize the abdomen\" wasn't in place", 0, false),
            new(610f, "Counted: Set up an IV line", 10, true),
            new(610f, "Missed: Open the abdomen", -10, false),
        };
        var report = new SurgeryReport("appendectomy", false, "Discharged before the job was done.", 44, 0,
            [new ReportLine("Jagged incision", -6, 2)], timeline, [], [], 610f);
        // Only this game, at a fixed time: the list reads the same in every run.
        Progress.ForgetHistory();
        Progress.FixedPlayedAt = "2026-10-15 11:40:00";
        Progress.RecordGame(report);
        Progress.FixedPlayedAt = null;
        var game = Progress.History[0];

        var menu = AutoFree(GD.Load<PackedScene>("res://scenes/ui/main_menu.tscn").Instantiate<MainMenu>())!;
        Frames.Root.AddChild(menu);
        await Frames.Process(2);
        await Click(Find<Button>(menu, "History"));
        var row = Find<Button>(menu, game.PlayedAt);
        AssertString(row.Text).OverrideFailureMessage("the newest row names the scenario, outcome, stars and score")
            .IsEqual($"{game.PlayedAt}   Appendectomy   failed   ☆☆☆   score 44");
        await Capture("00_list", "the history list in the main menu: the newest row reads failed, three empty stars, score 44");

        await Click(row);
        foreach (var entry in timeline) {
            Find<Label>(menu, entry.Text);
        }
        var cells = Find<Label>(menu, "3:00").GetParent().GetChildren().OfType<Label>().Select(label => label.Text).ToList();
        var at = cells.IndexOf("3:00");
        AssertArray(cells.GetRange(at, 3)).OverrideFailureMessage("a timeline line has its time, points and count")
            .IsEqual(new List<string> { "3:00", "-6", "Jagged incision  x2" });
        await Capture("01_report", "the post-op report of that game: failed, a Timeline section with times, green done "
            + "lines and red spoiled and missed lines, and a Back to history button");

        await Click(Find<Button>(menu, "Back to history"));
        Find<Button>(menu, game.PlayedAt);

        var file = new ConfigFile();
        AssertInt((int)file.Load(Progress.Path)).OverrideFailureMessage("the save file loads").IsEqual((int)Error.Ok);
        var saved = file.GetValue("history", "games").AsGodotArray()[0].AsGodotDictionary();
        AssertArray(SurgeryReport.FromVariant(saved["report"].AsGodotDictionary()).Timeline.ToList())
            .OverrideFailureMessage("the game's timeline is in the save file as it was").IsEqual(timeline);
    }

    /// <summary>The first control of type <typeparamref name="T"/> whose text has <paramref name="text"/> in it; fails the
    /// case when there's none.</summary>
    private static T Find<T>(Node menu, string text) where T : Control {
        var found = menu.FindChildren("*", "", true, false).OfType<T>()
            .FirstOrDefault(control => control is Button button ? button.Text.Contains(text, StringComparison.Ordinal)
                : control is Label label && label.Text.Contains(text, StringComparison.Ordinal));
        AssertObject(found).OverrideFailureMessage($"the menu shows \"{text}\"").IsNotNull();
        return found!;
    }

    private static async Task Click(Control control) {
        PlayerInput.Click(control);
        await PlayerInput.Delivered();
        // The panel it replaces is freed at the end of the frame.
        await Frames.Process(2);
    }

    private static async Task Capture(string name, string description) {
        if (KeyFrames.Wanted()) {
            await KeyFrames.SaveViewport(KeyFrames.Folder("history").PathJoin($"{name}.png"), description);
        }
    }
}
