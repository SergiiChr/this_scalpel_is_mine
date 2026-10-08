namespace Scalpel.UI;

/// <summary>Title screen. Left: sections. Right: the selected section's panel.</summary>
public partial class MainMenu : Control
{
    private MarginContainer _content = null!;
    private ScenarioDef? _selected;
    private Label? _status;

    public override void _Ready()
    {
        Theme = Ui.Theme;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        AddChild(Backdrop());
        var layout = Ui.HBox(32);
        layout.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect, LayoutPresetMode.Minsize, 48);
        AddChild(layout);

        var nav = Ui.VBox(12);
        nav.CustomMinimumSize = new Vector2(340f, 0f);
        nav.AddChild(Ui.Label("THIS SCALPEL\nIS MINE", 52, Ui.Pip));
        nav.AddChild(Ui.Label("a co-op surgery thriller", 18, Ui.Dim));
        nav.AddChild(new Control());
        nav.AddChild(Ui.Button("Scenarios", ShowScenarios));
        nav.AddChild(Ui.Button("Multiplayer", ShowMultiplayer));
        nav.AddChild(Ui.Button("Quirk codex", ShowCodex));
        nav.AddChild(Ui.Button("Settings", ShowSettings));
        nav.AddChild(Ui.Button("Quit", () => GetTree().Quit()));
        layout.AddChild(nav);

        _content = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        layout.AddChild(_content);
        _selected = Db.Scenarios.Count > 0 ? Db.Scenarios[0] : null;
        Net.Instance.ConnectionFailed += ShowStatus;
        var net = Net.Instance;
        if (net.LastError.Length > 0)
        {
            ShowMultiplayer();
            ShowStatus(net.LastError);
            net.LastError = "";
        }
        else
        {
            ShowScenarios();
        }
    }

    public override void _ExitTree() => Net.Instance.ConnectionFailed -= ShowStatus;

    /// <summary>The dark background behind the menu screens.</summary>
    public static ColorRect Backdrop()
    {
        var background = new ColorRect { Color = new Color(0.03f, 0.04f, 0.04f) };
        background.SetAnchorsPreset(LayoutPreset.FullRect);
        return background;
    }

    private void SetContent(Control panel)
    {
        foreach (var child in _content.GetChildren())
        {
            child.QueueFree();
        }
        _status = null;
        _content.AddChild(Ui.PanelAround(panel));
    }

    // --- Scenarios ---------------------------------------------------------------------------------------------

    /// <summary>A hidden scenario stays a secret until it's been played through once.</summary>
    private static bool IsSecret(ScenarioDef scenario) =>
        scenario.Hidden && Progress.BestStars.GetValueOrDefault(scenario.Id) == 0;

    private void ShowScenarios()
    {
        var split = Ui.HBox(24);
        var list = Ui.VBox(4);
        var details = Ui.VBox(12);
        details.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        foreach (var scenario in Db.Scenarios)
        {
            var best = Progress.BestStars.GetValueOrDefault(scenario.Id);
            var title = IsSecret(scenario) ? "???" : scenario.Title;
            var result = best > 0 ? "   best " + new string('★', best) : "";
            var button = Ui.Button($"{scenario.Order:00}  {title}   {scenario.StarsText}  {scenario.Group}{result}", () =>
            {
                _selected = scenario;
                FillDetails(details);
            });
            button.Alignment = HorizontalAlignment.Left;
            list.AddChild(button);
        }
        var scroll = Ui.Scroll(list);
        scroll.CustomMinimumSize = new Vector2(460f, 0f);
        split.AddChild(scroll);
        split.AddChild(details);
        FillDetails(details);
        SetContent(split);
    }

    private void FillDetails(VBoxContainer details)
    {
        foreach (var child in details.GetChildren())
        {
            child.QueueFree();
        }
        if (_selected is not { } scenario)
        {
            return;
        }
        var secret = IsSecret(scenario);
        details.AddChild(Ui.Label(secret ? "???" : scenario.Title, 34, Ui.Pip));
        details.AddChild(Ui.Label($"Difficulty {scenario.StarsText}    Group: {scenario.Group}", 18, Ui.Dim));
        details.AddChild(Ui.Label(secret ? "Description hidden. Trust us." : scenario.Description, 20, Ui.Ink, true));
        var timeLimit = scenario.TimeLimit > 0f ? $"{(int)(scenario.TimeLimit / 60f)} min" : "none";
        details.AddChild(Ui.Label($"Time limit: {timeLimit}   Nurse: {(scenario.Nurse ? "yes" : "no")}", 16, Ui.Dim));
        var family = Family(scenario);
        var similar = Db.Scenarios
            .Where(other => other != scenario && Family(other) == family && !other.Hidden)
            .Select(other => other.Title)
            .ToList();
        if (similar.Count > 0)
        {
            details.AddChild(Ui.Label("Similar: " + string.Join(", ", similar), 16, Ui.Dim, true));
        }
        var buttons = Ui.HBox(12);
        buttons.AddChild(Ui.Button("Play solo", () => Net.Instance.PlaySolo(scenario.Id)));
        buttons.AddChild(Ui.Button("Host co-op", () => Host(scenario.Id, Net.DefaultPort)));
        details.AddChild(buttons);
    }

    /// <summary>The first word of a scenario's group: scenarios that share it are alike.</summary>
    private static string Family(ScenarioDef scenario) => scenario.Group.Split(' ', 2)[0];

    // --- Multiplayer -------------------------------------------------------------------------------------------

    private void ShowMultiplayer()
    {
        var box = Ui.VBox(12);
        box.AddChild(Ui.Label("MULTIPLAYER", 28, Ui.Pip));
        var nameRow = Ui.HBox(8);
        nameRow.AddChild(Ui.Label("Your name", 18));
        var nameField = new LineEdit { Text = Progress.PlayerName, CustomMinimumSize = new Vector2(280f, 0f) };
        nameField.TextChanged += text =>
        {
            Progress.PlayerName = text;
            Progress.Save();
        };
        nameRow.AddChild(nameField);
        box.AddChild(nameRow);

        box.AddChild(Ui.Label($"Host (uses the scenario picked under Scenarios: {_selected?.Title ?? "-"})", 20, Ui.Pip));
        var hostRow = Ui.HBox(8);
        var hostPort = PortField();
        hostRow.AddChild(Ui.Label("Port", 18));
        hostRow.AddChild(hostPort);
        hostRow.AddChild(Ui.Button("Host", () =>
        {
            if (_selected is { } scenario)
            {
                Host(scenario.Id, (int)hostPort.Value);
            }
        }));
        box.AddChild(hostRow);
        box.AddChild(Ui.Label("Your partner joins your public IP (forward the port) or LAN IP.", 16, Ui.Dim, true));

        box.AddChild(Ui.Label("Join", 20, Ui.Pip));
        var joinRow = Ui.HBox(8);
        var ip = new LineEdit { PlaceholderText = "IP address", CustomMinimumSize = new Vector2(260f, 0f) };
        var joinPort = PortField();
        joinRow.AddChild(ip);
        joinRow.AddChild(joinPort);
        joinRow.AddChild(Ui.Button("Join", () => Join(ip.Text.StripEdges(), (int)joinPort.Value)));
        joinRow.AddChild(Ui.Button("Save host", () =>
        {
            Progress.RememberHost(ip.Text.StripEdges(), ip.Text.StripEdges(), (int)joinPort.Value);
            ShowMultiplayer();
        }));
        box.AddChild(joinRow);

        box.AddChild(Ui.Label("Known hosts", 20, Ui.Pip));
        foreach (var host in Progress.KnownHosts)
        {
            var row = Ui.HBox(8);
            row.AddChild(Ui.Button($"{host.Ip}:{host.Port}", () => Join(host.Ip, host.Port)));
            row.AddChild(Ui.Button("Forget", () =>
            {
                Progress.ForgetHost(host.Ip, host.Port);
                ShowMultiplayer();
            }));
            box.AddChild(row);
        }
        var status = Ui.Label("", 18, Ui.Alert);
        box.AddChild(status);
        SetContent(box);
        _status = status;
    }

    private static SpinBox PortField() => new() { MinValue = 1024, MaxValue = 65535, Value = Net.DefaultPort };

    private void Host(string scenarioId, int port)
    {
        var error = Net.Instance.Host(scenarioId, port);
        if (error != Error.Ok)
        {
            ShowStatus($"Could not host on port {port} ({error}).");
        }
    }

    private void Join(string ip, int port)
    {
        if (ip.Length == 0)
        {
            return;
        }
        var error = Net.Instance.Join(ip, port);
        ShowStatus(error != Error.Ok ? $"Could not connect ({error})." : $"Connecting to {ip}:{port}...");
    }

    /// <summary>A connection message under the multiplayer panel, when it's open.</summary>
    private void ShowStatus(string text)
    {
        if (_status is not null)
        {
            _status.Text = text;
        }
    }

    // --- Codex -------------------------------------------------------------------------------------------------

    internal void ShowSettings() => SetContent(new SettingsPanel());

    internal void ShowCodex()
    {
        var box = Ui.VBox(10);
        box.AddChild(Ui.Label("QUIRK CODEX", 28, Ui.Pip));
        box.AddChild(Ui.Label("Quirks unlock the first time they happen to you.", 16, Ui.Dim));
        var tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        foreach (var (name, table) in (Codex[])[new("Surgeon quirks", Db.SurgeonQuirks), new("Patient quirks", Db.PatientQuirks)])
        {
            var list = Ui.VBox(14);
            foreach (var quirk in table.Values)
            {
                if (!Progress.IsUnlocked(quirk))
                {
                    list.AddChild(Ui.Label("???  (locked)", 18, Ui.Dim));
                    continue;
                }
                foreach (var variant in quirk.Variants.Count > 0 ? quirk.Variants : [""])
                {
                    list.AddChild(Ui.QuirkLine(quirk, variant, true));
                }
            }
            var scroll = Ui.Scroll(list);
            scroll.Name = name;
            tabs.AddChild(scroll);
        }
        box.AddChild(tabs);
        SetContent(box);
    }

    private readonly record struct Codex(string Name, IReadOnlyDictionary<string, QuirkDef> Table);
}
