namespace Scalpel.UI;

/// <summary>Resolution, audio, mouse and key rebinding. Changes apply immediately, Save writes them to disk.</summary>
public partial class SettingsPanel : VBoxContainer
{
    /// <summary>The action waiting for its new key, empty when none is.</summary>
    private string _waitingFor = "";
    private readonly Dictionary<string, Button> _bindingButtons = [];

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 10);
        AddChild(Ui.Label("SETTINGS", 28, Ui.Pip));

        var display = Ui.HBox(12);
        var resolution = new OptionButton();
        foreach (var size in Settings.Resolutions)
        {
            resolution.AddItem($"{size.X} x {size.Y}");
        }
        resolution.Select(Math.Max(Settings.Resolutions.ToList().IndexOf(Settings.Resolution), 0));
        resolution.ItemSelected += index =>
        {
            Settings.Resolution = Settings.Resolutions[(int)index];
            Settings.Apply();
        };
        display.AddChild(Ui.Label("Resolution", 18));
        display.AddChild(resolution);
        display.AddChild(CheckBox("Fullscreen", Settings.Fullscreen, on =>
        {
            Settings.Fullscreen = on;
            Settings.Apply();
        }));
        AddChild(display);

        foreach (var bus in Settings.Buses)
        {
            AddChild(SliderRow($"{bus} volume", Settings.Volumes[bus], 0f, 1f, value =>
            {
                Settings.Volumes[bus] = value;
                Settings.Apply();
            }));
        }
        AddChild(SliderRow("Mouse sensitivity", Settings.MouseSensitivity, 0.2f, 3f,
            value => Settings.MouseSensitivity = value));
        AddChild(CheckBox("Debug mode (show objectives and every scored action)", Settings.Debug,
            on => Settings.Debug = on));

        AddChild(Ui.Label("Controls (click, then press a key or mouse button)", 18, Ui.Pip));
        var grid = new GridContainer { Columns = 4 };
        foreach (var entry in InputActions.Defaults)
        {
            grid.AddChild(Ui.Label(entry.Label, 16));
            var button = Ui.Button(InputActions.BindingText(entry.Action), () => StartRebind(entry.Action));
            button.CustomMinimumSize = new Vector2(140f, 0f);
            _bindingButtons[entry.Action] = button;
            grid.AddChild(button);
        }
        AddChild(Ui.Scroll(grid));

        var buttons = Ui.HBox(12);
        buttons.AddChild(Ui.Button("Reset controls", () =>
        {
            Settings.ResetBindings();
            Refresh();
        }));
        buttons.AddChild(Ui.Button("Save", Settings.Save));
        AddChild(buttons);
    }

    private static CheckBox CheckBox(string text, bool on, Action<bool> toggled)
    {
        var box = new CheckBox { Text = text, ButtonPressed = on };
        box.Toggled += value => toggled(value);
        return box;
    }

    private static HBoxContainer SliderRow(string text, float value, float min, float max, Action<float> changed)
    {
        var row = Ui.HBox(12);
        var label = Ui.Label(text, 18);
        label.CustomMinimumSize = new Vector2(220f, 0f);
        row.AddChild(label);
        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = 0.01,
            Value = value,
            CustomMinimumSize = new Vector2(300f, 0f),
        };
        slider.ValueChanged += newValue => changed((float)newValue);
        row.AddChild(slider);
        return row;
    }

    private void StartRebind(string action)
    {
        _waitingFor = action;
        _bindingButtons[action].Text = "press...";
    }

    /// <summary>The next key or mouse button pressed becomes the waiting action's binding. Esc cancels, except when
    /// rebinding pause itself.</summary>
    public override void _Input(InputEvent @event)
    {
        if (_waitingFor.Length == 0 || @event is not (InputEventKey { Pressed: true } or InputEventMouseButton { Pressed: true }))
        {
            return;
        }
        GetViewport().SetInputAsHandled();
        var cancel = @event is InputEventKey { PhysicalKeycode: Key.Escape } && _waitingFor != InputActions.Pause;
        if (!cancel)
        {
            Settings.Rebind(_waitingFor, @event);
        }
        _waitingFor = "";
        Refresh();
    }

    private void Refresh()
    {
        foreach (var (action, button) in _bindingButtons)
        {
            button.Text = InputActions.BindingText(action);
        }
    }
}
