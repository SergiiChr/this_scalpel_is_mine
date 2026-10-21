namespace Scalpel.UI;

/// <summary>Pre-op briefing. Shows the scenario, everyone's rolled quirks and, in a column beside them, every run
/// modifier: this run's, available and locked. Everyone readies up, the host starts.
/// </summary>
public partial class Lobby : Control {
    private static readonly Color ModifierColor = new(0.95f, 0.8f, 0.35f);

    private VBoxContainer _roster = null!;
    private VBoxContainer _header = null!;
    private Button _ready = null!;
    private Button? _start;
    private OptionButton? _scenarioPicker;
    private VBoxContainer _modifiers = null!;
    private bool _waiting;

    private static Net Net => Net.Instance;

    public override void _Ready() {
        Theme = Ui.Theme;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        AddChild(MainMenu.Backdrop());
        var box = Ui.VBox(16);
        box.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.Minsize, 48);
        AddChild(box);
        // Empty until the host leaves the report too, which opens the lobby anew for everyone.
        _waiting = Net.WaitingForHost;
        if (_waiting) {
            box.AddChild(Ui.Label("Waiting for host to finish the game.", 24, Ui.Dim));
            box.AddChild(Ui.Button("Exit lobby", Net.BackToMenu));
            return;
        }
        _header = Ui.VBox(6);
        box.AddChild(_header);
        if (Net.IsHost) {
            var row = Ui.HBox(8);
            row.AddChild(Ui.Label("Scenario", 18));
            var picker = new OptionButton();
            foreach (var scenario in Db.Scenarios) {
                picker.AddItem($"{scenario.Order:00}  {(scenario.Hidden ? "???" : scenario.Title)}");
            }
            picker.ItemSelected += index => Net.ChangeScenario(Db.Scenarios[(int)index].Id);
            row.AddChild(picker);
            box.AddChild(row);
            _scenarioPicker = picker;
        }
        var columns = Ui.HBox(20);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        _roster = Ui.VBox(20);
        var roster = Ui.Scroll(_roster);
        roster.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        columns.AddChild(roster);
        _modifiers = Ui.VBox(4);
        var modifiers = Ui.PanelAround(_modifiers);
        modifiers.CustomMinimumSize = new Vector2(640f, 0f);
        modifiers.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        columns.AddChild(modifiers);
        box.AddChild(columns);
        var buttons = Ui.HBox(12);
        _ready = Ui.Button("Ready", ToggleReady);
        buttons.AddChild(_ready);
        if (Net.IsHost) {
            _start = Ui.Button("Start surgery", Net.StartSession);
            buttons.AddChild(_start);
            if (Net.IsOnline) {
                buttons.AddChild(Ui.Label(
                    $"Hosting on port {Net.DefaultPort}. Waiting for your partner is optional.", 16, Ui.Dim));
            }
        }
        buttons.AddChild(Ui.Button("Leave", Net.BackToMenu));
        box.AddChild(buttons);
        Net.RosterChanged += Refresh;
        Net.ScenarioChanged += Refresh;
        Refresh();
    }

    public override void _ExitTree() {
        if (_waiting) {
            return;
        }
        Net.RosterChanged -= Refresh;
        Net.ScenarioChanged -= Refresh;
    }

    private void Refresh() {
        foreach (var child in _header.GetChildren()) {
            child.QueueFree();
        }
        if (Net.Scenario is { } scenario) {
            _header.AddChild(Ui.Label($"PRE-OP BRIEFING: {(scenario.Hidden ? "???" : scenario.Title)}", 32, Ui.Pip));
            var description = scenario.Hidden ? "One injection. Then you listen." : scenario.Description;
            _header.AddChild(Ui.Label($"{scenario.StarsText}   {description}", 18, Ui.Ink, true));
            _scenarioPicker?.Select(Db.Scenarios.ToList().IndexOf(scenario));
        }
        foreach (var child in _roster.GetChildren()) {
            child.QueueFree();
        }
        foreach (var (peer, player) in Net.Roster.OrderBy(entry => entry.Key)) {
            var mine = peer == Net.LocalId;
            var card = Ui.VBox(8);
            card.AddChild(Ui.Label($"{player.Name}{(mine ? " (you)" : "")}   {(player.Ready ? "READY" : "not ready")}", 24,
                player.Ready ? Ui.Good : Ui.Ink));
            var weight = SurgeonStatus.WeightOf(Modifiers.FromRolls(player.Quirks, Db.SurgeonQuirks));
            card.AddChild(Ui.Label($"Weight {(int)weight} kg", 18, Ui.Dim));
            foreach (var roll in player.Quirks) {
                if (Db.Quirk(QuirkKind.Surgeon, roll.Id) is { } quirk) {
                    card.AddChild(Ui.QuirkLine(quirk, roll.Variant, mine));
                }
            }
            _roster.AddChild(Ui.PanelAround(card));
        }
        // Rerolled modifiers (a new scenario) reach the lobby with the roster.
        ListModifiers();
        _ready.Text = IsReady ? "Not ready" : "Ready";
        if (_start is not null) {
            _start.Disabled = !Net.AllReady;
        }
    }

    /// <summary>Every run modifier: this run's, the ones seen in earlier runs and the locked rest.</summary>
    private void ListModifiers() {
        foreach (var child in _modifiers.GetChildren()) {
            child.QueueFree();
        }
        var unlocked = Db.RunModifiers.GetSections().Where(id => !Net.RunModifiers.Contains(id))
            .ToLookup(Progress.IsModifierUnlocked);
        _modifiers.AddChild(Ui.Label("RUN MODIFIERS", 24, Ui.Pip));
        _modifiers.AddChild(Ui.Label(
            "The host rolls 1-2 for every run, and again with a new scenario. Any of them can come up, locked ones too.",
            16, Ui.Dim, true));
        AddModifiers(_modifiers, "ACTIVE THIS RUN", Net.RunModifiers, ModifierColor);
        AddModifiers(_modifiers, "AVAILABLE", unlocked[true], Ui.Ink);
        if (unlocked[false].Count() is var locked and > 0) {
            _modifiers.AddChild(Ui.Label("LOCKED", 20, Ui.Pip));
            _modifiers.AddChild(Ui.Label($"{locked} more. Each unlocks the first time a run starts with it.", 16, Ui.Dim, true));
        }
    }

    private static void AddModifiers(VBoxContainer list, string title, IEnumerable<string> ids, Color color) {
        list.AddChild(Ui.Label(title, 20, Ui.Pip));
        if (!ids.Any()) {
            list.AddChild(Ui.Label("None", 16, Ui.Dim));
        }
        foreach (var id in ids) {
            list.AddChild(Ui.Label(ModifierName(id), 20, color));
            list.AddChild(Ui.Label(new ConfigReader(Db.RunModifiers, id).String("description"), 16, Ui.Dim, true));
        }
    }

    private static string ModifierName(string id) => new ConfigReader(Db.RunModifiers, id).String("name", id);

    private static bool IsReady => Net.Roster.GetValueOrDefault(Net.LocalId)?.Ready ?? false;

    private void ToggleReady() => Net.SetReady(!IsReady);
}
