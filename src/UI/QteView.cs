namespace Scalpel.UI;

/// <summary>Turning the patient: press the shown movement keys in time. A wrong key is a critical fail.</summary>
public partial class QteView : Control
{
    private IReadOnlyList<string> _sequence = [];
    private float _window;
    private int _index;
    private float _timer;
    private int _hits;
    private bool _critical;
    private Action<int, bool> _done = (_, _) => { };
    private Label _label = null!;
    private ProgressBar _bar = null!;

    /// <summary>Shows each key of <paramref name="sequence"/> for <paramref name="window"/> seconds;
    /// <paramref name="done"/> gets how many were hit and whether a wrong key was pressed.</summary>
    public void Start(IReadOnlyList<string> sequence, float window, Action<int, bool> done)
    {
        _sequence = sequence;
        _window = window;
        _done = done;
        _timer = window;
        SetAnchorsPreset(LayoutPreset.Center);
        var box = Ui.VBox(8);
        box.AddChild(Ui.Label("TURN THE PATIENT, TOGETHER", 26, Ui.Pip));
        _label = Ui.Label("", 64, Ui.Ink);
        _label.HorizontalAlignment = HorizontalAlignment.Center;
        box.AddChild(_label);
        _bar = Ui.Bar(Ui.Pip);
        _bar.CustomMinimumSize = new Vector2(400f, 14f);
        box.AddChild(_bar);
        var panel = Ui.PanelAround(box);
        panel.Position = new Vector2(-240f, -120f);
        AddChild(panel);
        ShowKey();
    }

    public override void _Process(double delta)
    {
        _timer -= (float)delta;
        _bar.Value = _timer / _window;
        if (_timer <= 0f)
        {
            Advance(false);
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false })
        {
            return;
        }
        GetViewport().SetInputAsHandled();
        if (@event.IsAction(_sequence[_index]))
        {
            Advance(true);
        }
        else if (Surgery.QteKeys.Any(action => @event.IsAction(action)))
        {
            _critical = true;
            Advance(false);
        }
    }

    private void Advance(bool hit)
    {
        _hits += hit ? 1 : 0;
        _index++;
        _timer = _window;
        if (_index >= _sequence.Count)
        {
            _done(_hits, _critical);
            QueueFree();
        }
        else
        {
            ShowKey();
        }
    }

    /// <summary>The action whose key is to be pressed now.</summary>
    public string ShownKey => _sequence[_index];

    private void ShowKey() => _label.Text = InputActions.BindingText(_sequence[_index]);
}
