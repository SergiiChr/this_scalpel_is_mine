namespace Scalpel.UI;

/// <summary>Pre-op briefing. Shows the scenario and everyone's rolled quirks. Everyone readies up, the host starts.
/// </summary>
public partial class Lobby : Control
{
    private static readonly Color ModifierColor = new(0.95f, 0.8f, 0.35f);

    private VBoxContainer _roster = null!;
    private VBoxContainer _header = null!;
    private Button _ready = null!;
    private Button? _start;
    private OptionButton? _scenarioPicker;

    private static Net Net => Net.Instance;

    public override void _Ready()
    {
        Theme = Ui.Theme;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        AddChild(MainMenu.Backdrop());
        var box = Ui.VBox(16);
        box.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.Minsize, 48);
        AddChild(box);
        _header = Ui.VBox(6);
        box.AddChild(_header);
        if (Net.IsHost)
        {
            var row = Ui.HBox(8);
            row.AddChild(Ui.Label("Scenario", 18));
            var picker = new OptionButton();
            foreach (var scenario in Db.Scenarios)
            {
                picker.AddItem($"{scenario.Order:00}  {(scenario.Hidden ? "???" : scenario.Title)}");
            }
            picker.ItemSelected += index => Net.ChangeScenario(Db.Scenarios[(int)index].Id);
            row.AddChild(picker);
            box.AddChild(row);
            _scenarioPicker = picker;
        }
        _roster = Ui.VBox(20);
        box.AddChild(Ui.Scroll(_roster));
        var buttons = Ui.HBox(12);
        _ready = Ui.Button("Ready", ToggleReady);
        buttons.AddChild(_ready);
        if (Net.IsHost)
        {
            _start = Ui.Button("Start surgery", Net.StartSession);
            buttons.AddChild(_start);
            if (Net.IsOnline)
            {
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

    public override void _ExitTree()
    {
        Net.RosterChanged -= Refresh;
        Net.ScenarioChanged -= Refresh;
    }

    private void Refresh()
    {
        foreach (var child in _header.GetChildren())
        {
            child.QueueFree();
        }
        if (Net.Scenario is { } scenario)
        {
            _header.AddChild(Ui.Label($"PRE-OP BRIEFING: {(scenario.Hidden ? "???" : scenario.Title)}", 32, Ui.Pip));
            var description = scenario.Hidden ? "One injection. Then you listen." : scenario.Description;
            _header.AddChild(Ui.Label($"{scenario.StarsText}   {description}", 18, Ui.Ink, true));
            foreach (var id in Net.RunModifiers)
            {
                var modifier = new ConfigReader(Db.RunModifiers, id);
                _header.AddChild(Ui.Label(
                    $"RUN MODIFIER  {modifier.String("name", id)}: {modifier.String("description")}", 18, ModifierColor, true));
            }
            _scenarioPicker?.Select(Db.Scenarios.ToList().IndexOf(scenario));
        }
        foreach (var child in _roster.GetChildren())
        {
            child.QueueFree();
        }
        foreach (var (peer, player) in Net.Roster.OrderBy(entry => entry.Key))
        {
            var mine = peer == Net.LocalId;
            var card = Ui.VBox(8);
            card.AddChild(Ui.Label($"{player.Name}{(mine ? " (you)" : "")}   {(player.Ready ? "READY" : "not ready")}", 24,
                player.Ready ? Ui.Good : Ui.Ink));
            var weight = SurgeonStatus.WeightOf(Modifiers.FromRolls(player.Quirks, Db.SurgeonQuirks));
            card.AddChild(Ui.Label($"Weight {(int)weight} kg", 18, Ui.Dim));
            foreach (var roll in player.Quirks)
            {
                if (Db.Quirk(QuirkKind.Surgeon, roll.Id) is { } quirk)
                {
                    card.AddChild(Ui.QuirkLine(quirk, roll.Variant, mine));
                }
            }
            _roster.AddChild(Ui.PanelAround(card));
        }
        _ready.Text = IsReady ? "Not ready" : "Ready";
        if (_start is not null)
        {
            _start.Disabled = !Net.AllReady;
        }
    }

    private static bool IsReady => Net.Roster.GetValueOrDefault(Net.LocalId)?.Ready ?? false;

    private void ToggleReady() => Net.SetReady(!IsReady);
}
