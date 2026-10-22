namespace Scalpel.Tests.UI;

/// <summary>
/// A player with no difficulty saved, new or with settings from before difficulties came in, gets the difficulty
/// screen over the main menu: three rows with their icons, and the note that the settings change it. A click picks
/// one and saves it, and the settings switch it later. In surgery the objectives follow it: "I'm just a baby" shows the
/// main tasks with their subtasks and the patient's dose, Intern only the main tasks, MD none. With key frames also
/// the screen, the settings and the HUD at each difficulty.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class DifficultyTest {
    private const string SettingsPath = "user://settings.cfg";

    [TestCase]
    public async Task FirstStartAsksForADifficultyAndTheSettingsChangeIt() {
        using var difficulty = SurgeryState.DifficultyIs(null);
        // The settings file is the game's own, read and written as a player's: a developer's is put back after.
        var kept = FileAccess.FileExists(SettingsPath) ? FileAccess.GetFileAsBytes(SettingsPath) : null;
        Settings.IgnoreFile = false;
        try {
            var old = new ConfigFile();
            old.SetValue("debug", "enabled", false);
            old.Save(SettingsPath);
            Settings.Load();
            AssertObject(Settings.Difficulty).OverrideFailureMessage("settings from before difficulties pick none").IsNull();

            var menu = await OpenMenu();
            var picker = menu.FindChildren("*", "", true, false).OfType<DifficultyPicker>().SingleOrDefault();
            AssertObject(picker).OverrideFailureMessage("the main menu asks for a difficulty first").IsNotNull();
            var rows = picker!.GetChildren().OfType<Button>().ToList();
            AssertArray(rows.Select(Texts).ToList()).OverrideFailureMessage("a row for each, easiest first, with its explanation")
                .IsEqual(DifficultyPicker.Choices.Select(choice => new List<string> { choice.Name, choice.Description }).ToList());
            AssertArray(rows.Select(row => row.FindChildren("*", "", true, false).OfType<TextureRect>().Single().Texture.ResourcePath).ToList())
                .OverrideFailureMessage("the hamster, stethoscope and scalpel icons")
                .IsEqual(DifficultyPicker.Choices.Select(choice => choice.Icon).ToList());
            AssertString(picker.GetChildren().OfType<Label>().Last().Text).OverrideFailureMessage("the note under the rows")
                .IsEqual("This can be changed in the settings at any time.");
            await Capture("00_picker", "the difficulty screen over the menu: CHOOSE YOUR DIFFICULTY, three rows with a "
                + "hamster, a stethoscope and a scalpel icon beside I'm just a baby, Intern and MD and their explanations, "
                + "the note about the settings below");

            await Click(rows[1]);
            AssertObject(Settings.Difficulty).OverrideFailureMessage("the click picks Intern").IsEqual(Difficulty.Intern);
            AssertString(Saved()).OverrideFailureMessage("and saves it").IsEqual("Intern");
            AssertBool(GodotObject.IsInstanceValid(picker)).OverrideFailureMessage("the screen goes, the menu stays").IsFalse();

            await Click(Find<Button>(menu, "Settings"));
            var choices = menu.FindChildren("*", "", true, false).OfType<OptionButton>()
                .Single(option => option.ItemCount == DifficultyPicker.Choices.Count);
            AssertString(choices.Text).OverrideFailureMessage("the settings show the difficulty picked").IsEqual("Intern");
            await Click(choices);
            // The list opens with nothing focused: down to the third item, MD, and take it.
            for (var step = 0; step < 3; step++) {
                PlayerInput.Tap("ui_down");
            }
            PlayerInput.Tap("ui_accept");
            await PlayerInput.Delivered();
            await Frames.Process(2);
            AssertObject(Settings.Difficulty).OverrideFailureMessage("the settings switch it to MD").IsEqual(Difficulty.Md);
            await Capture("01_settings", "the settings page with the Difficulty switch reading MD under the volumes and "
                + "mouse sensitivity, above the debug mode box");
            await Click(Find<Button>(menu, "Save"));
            AssertString(Saved()).OverrideFailureMessage("Save writes the new difficulty").IsEqual("Md");
            menu.QueueFree();

            menu = await OpenMenu();
            AssertBool(menu.FindChildren("*", "", true, false).OfType<DifficultyPicker>().Any())
                .OverrideFailureMessage("with one saved the menu doesn't ask again").IsFalse();
            menu.QueueFree();
        } finally {
            Settings.IgnoreFile = true;
            if (kept is null) {
                DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(SettingsPath));
            } else {
                using var file = FileAccess.Open(SettingsPath, FileAccess.ModeFlags.Write);
                file.StoreBuffer(kept);
            }
        }
    }

    [TestCase]
    public async Task ObjectivesFollowTheDifficulty() {
        using var difficulty = SurgeryState.DifficultyIs(Difficulty.Baby);
        var driver = SurgeryDriver.Create();
        KeyFrames? shots = null;
        try {
            await driver.Start("appendectomy");
            // Done, the IV line hands its subtasks on to the anesthesia, the next required task.
            SurgeryState.IvLineIsIn(driver.Surgery.Patient);
            if (KeyFrames.Wanted()) {
                shots = new KeyFrames();
                driver.AddChild(shots);
                shots.Begin(driver.Surgery, "difficulty");
            }
            await Frames.Seconds(Surgery.StatusInterval + 0.1f);
            var panel = driver.Surgery.Hud.FindChildren("*", "", true, false).OfType<ObjectivesPanel>().Single();
            var dose = Math.Round(driver.DoseMl("vial_propofol"), MidpointRounding.AwayFromZero);
            AssertBool(panel.Visible && Lines(panel).Any(line => line.StartsWith($"• Draw {dose} ml of Propofol into the ",
                    StringComparison.Ordinal)))
                .OverrideFailureMessage($"baby: the subtasks name this patient's dose: {string.Join(" | ", Lines(panel))}").IsTrue();
            await View(shots, "baby", "the player's view of the patient with the objectives top right on a dark backing: "
                + "the IV line ticked, the anesthesia's grey subtasks under it with the propofol dose, the marking "
                + "and the X-ray further down marked [optional] in front, the main tasks after the anesthesia without "
                + "subtasks, the discharge last");

            Settings.Difficulty = Difficulty.Intern;
            await Frames.Process(2);
            // A wound closed and nothing bleeding hold from the start, but don't show done before what comes first.
            AssertArray(Lines(panel))
                .OverrideFailureMessage($"intern: the required main tasks, the X-ray hint and the discharge, the IV line done: {string.Join(" | ", Lines(panel))}")
                .IsEqual(driver.Surgery.Scenario.Steps.Where(step => !step.Optional || step.Hint)
                    .Select(step => (step.Type == "iv" ? "✓ " : "□ ") + (step.Optional ? "[optional] " : "") + step.Label)
                    .Append("□ " + Objectives.DischargeLabel).ToList());
            await View(shots, "intern", "the same view, the objectives top right on a dark backing the required main "
                + "tasks, the IV line ticked, the X-ray marked [optional], the discharge last, no subtasks and no marking");

            var hint = driver.Surgery.Hud.FindChildren("*", "", true, false).OfType<Label>()
                .Single(label => label.Text.Contains("Hold breath"));
            AssertString(hint.Text).OverrideFailureMessage("the controls name the goals' key").Contains("F1  Hide goals");
            PlayerInput.Tap(InputActions.ToggleGoals);
            await PlayerInput.Delivered();
            await Frames.Process(2);
            AssertBool(!panel.Visible && hint.Text.Contains("F1  Show goals"))
                .OverrideFailureMessage($"F1 puts the goals away: visible {panel.Visible}, hint {hint.Text}").IsTrue();
            PlayerInput.Tap(InputActions.ToggleGoals);
            await PlayerInput.Delivered();
            await Frames.Process(2);
            AssertBool(panel.Visible).OverrideFailureMessage("F1 again brings them back").IsTrue();

            Settings.Difficulty = Difficulty.Md;
            await Frames.Process(2);
            AssertBool(panel.Visible).OverrideFailureMessage("MD: no objectives").IsFalse();
        } finally {
            shots?.End();
            await driver.Stop();
        }
    }

    private static List<string> Lines(ObjectivesPanel panel) =>
        [.. panel.GetChildren().OfType<Label>().Select(label => label.Text)];

    private static async Task View(KeyFrames? shots, string name, string description) {
        if (shots is not null) {
            await shots.CaptureView(name, description);
        }
    }

    private static async Task<MainMenu> OpenMenu() {
        var menu = GD.Load<PackedScene>("res://scenes/ui/main_menu.tscn").Instantiate<MainMenu>();
        Frames.Root.AddChild(menu);
        await Frames.Process(2);
        return menu;
    }

    /// <summary>The difficulty the settings file holds.</summary>
    private static string Saved() {
        var file = new ConfigFile();
        file.Load(SettingsPath);
        return file.GetValue("game", "difficulty", "").AsString();
    }

    /// <summary>The texts of a row's labels.</summary>
    private static List<string> Texts(Node row) =>
        [.. row.FindChildren("*", "", true, false).OfType<Label>().Select(label => label.Text)];

    /// <summary>The first control of type <typeparamref name="T"/> whose text is <paramref name="text"/>; fails the case
    /// when there's none.</summary>
    private static T Find<T>(Node menu, string text) where T : Control {
        var found = menu.FindChildren("*", "", true, false).OfType<T>()
            .FirstOrDefault(control => !control.IsQueuedForDeletion() && control is Button { Text: var shown } && shown == text);
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
            await KeyFrames.SaveViewport(KeyFrames.Folder("difficulty_menu").PathJoin($"{name}.png"), description);
        }
    }
}
