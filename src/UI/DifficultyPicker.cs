namespace Scalpel.UI;

/// <summary>One difficulty as the picker and the settings show it.</summary>
public sealed record DifficultyChoice(Difficulty Difficulty, string Name, string Description, string Icon);

/// <summary>The difficulty screen the main menu shows while none is saved (a new player, or the first start since
/// difficulties came in): a row per difficulty with its icon, a click picks it and saves it.</summary>
public partial class DifficultyPicker : VBoxContainer {
    public const string Note = "This can be changed in the settings at any time.";

    public static readonly IReadOnlyList<DifficultyChoice> Choices =
    [
        new(Difficulty.Baby, "I'm just a baby",
            "The game will tell you what to do, including the exact steps required, optional and required conditions, "
            + "and exact drug dosages.",
            "res://assets/icons/difficulty/baby.png"),
        new(Difficulty.Intern, "Intern",
            "The game will tell you the general steps required for the treatment. Refer to the manual for the exact "
            + "steps.",
            "res://assets/icons/difficulty/intern.png"),
        new(Difficulty.Md, "MD",
            "You're on your own, and the manual is your only help. We are praying for your patient.",
            "res://assets/icons/difficulty/md.png"),
    ];

    /// <summary>A difficulty was picked and saved.</summary>
    public event Action? Picked;

    public DifficultyPicker() {
        AddThemeConstantOverride("separation", 14);
        AddChild(Ui.Label("CHOOSE YOUR DIFFICULTY", 28, Ui.Pip));
        foreach (var choice in Choices) {
            AddChild(Row(choice));
        }
        AddChild(Ui.Label(Note, 16, Ui.Dim));
    }

    /// <summary>A button the size of its icon and text, which ignore the mouse so the click reaches it.</summary>
    private Button Row(DifficultyChoice choice) {
        var button = new Button { CustomMinimumSize = new Vector2(0f, 120f) };
        button.Pressed += () => Pick(choice.Difficulty);
        var row = Ui.HBox(20);
        row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.Minsize, 12);
        var icon = Ui.Icon(GD.Load<Texture2D>(choice.Icon), 96);
        icon.Modulate = Ui.Ink;
        row.AddChild(icon);
        var text = Ui.VBox(4);
        text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        text.Alignment = AlignmentMode.Center;
        text.AddChild(Ui.Label(choice.Name, 26, Ui.Pip));
        text.AddChild(Ui.Label(choice.Description, 18, Ui.Ink, true));
        row.AddChild(text);
        foreach (var control in row.FindChildren("*", "", true, false).OfType<Control>().Append(row)) {
            control.MouseFilter = MouseFilterEnum.Ignore;
        }
        button.AddChild(row);
        return button;
    }

    private void Pick(Difficulty difficulty) {
        Settings.Difficulty = difficulty;
        Settings.Save();
        Picked?.Invoke();
    }
}
