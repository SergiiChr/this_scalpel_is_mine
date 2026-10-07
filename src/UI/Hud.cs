namespace Scalpel.UI;

/// <summary>
/// Everything drawn on screen during surgery, plus the full screen overlays (manual, card, menus, report).
/// While an overlay is open the surgeon's input is locked and the mouse is free.
/// </summary>
public partial class Hud : CanvasLayer
{
    private const string PostFxShader = "res://assets/shaders/post_grime.gdshader";
    private const float ToastTime = 5f;
    private const float SubtitleTime = 6f;
    /// <summary>Blood on the view clears in about this many seconds.</summary>
    private const float LensClearSeconds = 8f;
    /// <summary>A sedative blurs the view by this much (the screen's mip level) at the right dose.</summary>
    private const float SedatedBlur = 1f;
    /// <summary>Past the right dose the view darkens, up to this just short of a knockout.</summary>
    private const float OverdoseDaze = 0.6f;
    /// <summary>Lying knocked out the view is this dark: still enough to see by.</summary>
    private const float KnockedOutDaze = 0.8f;
    private static readonly string[] GaugeNames = ["stress", "sickness", "breath", "sweat"];

    public Surgery Surgery { get; private set; } = null!;
    private Control _root = null!;
    private Label _clock = null!;
    private ObjectivesPanel _objectives = null!;
    private Label _hands = null!;
    private HBoxContainer _belt = null!;
    private Label _prompt = null!;
    private Label _netWarning = null!;
    /// <summary>Controls for what the player is doing right now, bottom right. Changes while a hand key or a tool is
    /// held.</summary>
    private Label _hint = null!;
    private VBoxContainer _toasts = null!;
    private Label _subtitle = null!;
    private float _subtitleTimer;
    private readonly Dictionary<string, (HBoxContainer Row, ProgressBar Bar)> _gauges = [];
    private ShaderMaterial _post = null!;
    /// <summary>Blood on the view (0..1).</summary>
    private float _lensBlood;
    /// <summary>How long since the view was clean.</summary>
    private float _lensAge;
    public NurseShop Shop { get; private set; } = null!;
    /// <summary>The open overlay, null for none.</summary>
    public Control? Overlay { get; private set; }
    /// <summary>The open manual, null when it isn't.</summary>
    public ManualView? Manual { get; private set; }
    /// <summary>Overlays Esc can't close (the report).</summary>
    private bool _overlayLocked;

    public void Setup(Surgery surgery)
    {
        Surgery = surgery;
        BuildPostFx();
        surgery.Patient.Body.Blood.Splashed += OnBloodSplashed;
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Theme = Ui.Theme };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(_root);
        _clock = CornerLabel(Control.LayoutPreset.CenterTop, 26, Ui.Ink);
        _clock.HorizontalAlignment = HorizontalAlignment.Center;
        _objectives = new ObjectivesPanel();
        _objectives.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
        _objectives.Position = new Vector2(-460f, 20f);
        _objectives.CustomMinimumSize = new Vector2(440f, 0f);
        _root.AddChild(_objectives);
        _toasts = Ui.VBox(4);
        _toasts.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
        _toasts.Position = new Vector2(-400f, 70f);
        _toasts.CustomMinimumSize = new Vector2(800f, 0f);
        _root.AddChild(_toasts);
        _prompt = CornerLabel(Control.LayoutPreset.Center, 22, Ui.Ink);
        _prompt.Position += new Vector2(-200f, 40f);
        _prompt.CustomMinimumSize = new Vector2(400f, 0f);
        _prompt.HorizontalAlignment = HorizontalAlignment.Center;
        _subtitle = CornerLabel(Control.LayoutPreset.CenterBottom, 24, new Color(1f, 1f, 0.9f));
        _subtitle.Position = new Vector2(-500f, -190f);
        _subtitle.CustomMinimumSize = new Vector2(1000f, 0f);
        _subtitle.HorizontalAlignment = HorizontalAlignment.Center;
        _netWarning = CornerLabel(Control.LayoutPreset.CenterTop, 20, Ui.Alert);
        _netWarning.Position = new Vector2(-400f, 44f);
        _netWarning.CustomMinimumSize = new Vector2(800f, 0f);
        _netWarning.HorizontalAlignment = HorizontalAlignment.Center;
        BuildAim();
        BuildBottomBar();
        BuildGauges();
        BuildControlsHint();
        BuildShop();
    }

    public void Begin()
    {
        CaptureMouse();
        Toast(Surgery.Scenario.Title);
    }

    public override void _Process(double delta)
    {
        if (Surgery?.LocalSurgeon is not { } me)
        {
            return;
        }
        var dt = (float)delta;
        var left = Surgery.TimeLeft();
        var clock = left >= 0f ? $"{(int)left / 60}:{(int)left % 60:00}" : "no time limit";
        _clock.Text = $"{Surgery.Scenario.Title}   {clock}";
        UpdateObjectives();
        if (Shop.View.Visible)
        {
            UpdateShop();
        }
        UpdateNetWarning();
        UpdateAim(me);
        UpdateHands(me);
        UpdateGauges(me);
        var hint = me.Status.IsOut ? "" : string.Join("\n", ControlLines(me));
        if (_hint.Text != hint)
        {
            _hint.Text = hint;
        }
        _prompt.Text = me.Focused is { } focused && Overlay is null
            ? $"[{InputActions.BindingText(InputActions.Interact)}] {focused.Prompt}"
            : "";
        if (_prompt.Text.Length == 0 && me.HeldTool(me.Active) is not null && me.PassTarget(me.Active) is { } partner)
        {
            _prompt.Text = $"[{InputActions.BindingText(InputActions.Grab)}] Pass to {partner.Surgeon.DisplayName}";
        }
        _subtitleTimer -= dt;
        if (_subtitleTimer <= 0f)
        {
            _subtitle.Text = "";
        }
        UpdatePostFx(me.Status, dt);
    }

    private void UpdatePostFx(SurgeonStatus status, float delta)
    {
        _post.SetShaderParameter("blackout", status.PassedOut > 0f ? 1f : 0f);
        _post.SetShaderParameter("daze", status.IsKnockedOut ? KnockedOutDaze : status.Overdose * OverdoseDaze);
        _post.SetShaderParameter("wobble", status.Sickness);
        _post.SetShaderParameter("blur", Mathf.Max(status.Sickness * 1.5f, status.Calm * SedatedBlur));
        if (_lensBlood > 0f)
        {
            _lensBlood = Mathf.Max(_lensBlood - (delta / LensClearSeconds), 0f);
            _lensAge += delta;
            _post.SetShaderParameter("lens_blood", _lensBlood);
            _post.SetShaderParameter("lens_age", _lensAge);
        }
    }

    /// <summary>Blood on the view gets a new pattern of drops each time it starts from clean.</summary>
    private void OnBloodSplashed(float amount)
    {
        if (_lensBlood <= 0f)
        {
            _lensAge = 0f;
            _post.SetShaderParameter("lens_seed", GD.Randf() * 100f);
        }
        _lensBlood = Mathf.Min(_lensBlood + amount, 1f);
    }

    /// <summary>A lag spike shows up after a moment; the game carries on and the connection only drops after the
    /// network timeout.</summary>
    private void UpdateNetWarning()
    {
        var (peer, seconds) = Net.Instance.WorstSilence();
        if (seconds < 1.5f)
        {
            _netWarning.Text = "";
            return;
        }
        var who = peer == Net.HostId ? "the host" : Net.Instance.Roster.GetValueOrDefault(peer)?.Name ?? "your partner";
        _netWarning.Text = $"Connection to {who} is unstable ({(int)seconds} s). Waiting...";
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!@event.IsActionPressed(InputActions.Pause))
        {
            return;
        }
        GetViewport().SetInputAsHandled();
        if (_overlayLocked)
        {
            return;
        }
        if (Overlay is not null)
        {
            CloseOverlay();
        }
        else
        {
            Open(PauseMenu());
        }
    }

    /// <summary>A toast was shown, with its text.</summary>
    public event Action<string>? Toasted;

    public void Toast(string text)
    {
        if (text.Length == 0)
        {
            return;
        }
        Toasted?.Invoke(text);
        var label = Ui.Label(text, 20, Ui.Ink, true);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        _toasts.AddChild(label);
        if (_toasts.GetChildCount() > 4)
        {
            _toasts.GetChild(0).QueueFree();
        }
        var tween = label.CreateTween();
        tween.TweenInterval(ToastTime);
        tween.TweenProperty(label, "modulate:a", 0.0, 0.8);
        tween.TweenCallback(Callable.From(label.QueueFree));
    }

    public void Subtitle(string text)
    {
        _subtitle.Text = $"\"{text}\"";
        _subtitleTimer = SubtitleTime;
    }

    // --- Overlays ------------------------------------------------------------------------------------------------

    public void OpenManual()
    {
        var keys = new List<string>();
        if (Surgery.LocalSurgeon?.Mods.Flag("manual_highlight") == true)
        {
            var patient = Surgery.Patient;
            keys.AddRange(patient.Mods.Keys);
            keys.AddRange(patient.Mods.List("allergen"));
            foreach (var roll in patient.Rolls)
            {
                keys.Add(roll.Id);
                if (roll.Variant.Length > 0)
                {
                    keys.Add($"{roll.Id}.{roll.Variant}");
                }
            }
        }
        Manual = new ManualView(keys, CloseOverlay);
        Open(Manual.View);
    }

    public void OpenCard() => Open(PatientCardView.Build(Surgery.Patient, Net.Instance.SessionSeed, CloseOverlay));

    public void OpenNurse()
    {
        UpdateShop();
        Open(Shop.View);
    }

    public void OpenLab()
    {
        var choices = Lab.Panels.Select(entry => ($"{entry.Value.Label}  ({(int)entry.Value.Seconds} s)", entry.Key));
        var cooldown = Surgery.Status.LabCooldown;
        var subtitle = cooldown > 0f ? $"Lab is busy for {Mathf.CeilToInt(cooldown)} s." : "Narrow panels come back faster.";
        Open(Ui.ChoiceMenu("Blood work", subtitle, choices, kind =>
        {
            Surgery.OrderLab(kind);
            CloseOverlay();
        }, CloseOverlay));
    }

    private void UpdateShop() => Shop.Update(Surgery.Status.NurseBoard, Surgery.Status.NurseReady);

    public void OpenXray(XrayCart cart)
    {
        if (cart.Print is not { } print)
        {
            return;
        }
        var box = Ui.VBox(12);
        box.AddChild(new XrayPhoto(cart, print));
        var close = Ui.Button("Put it down  [Esc]", CloseOverlay);
        close.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        box.AddChild(close);
        var center = new CenterContainer();
        center.AddChild(box);
        Open(Ui.Fullscreen(center, new Color(0f, 0f, 0f, 0.8f)));
    }

    /// <summary>Starts the turning quick time event: <paramref name="done"/> gets the hits and whether a wrong key was
    /// pressed.</summary>
    public void RunQte(IReadOnlyList<string> sequence, float window, Action<int, bool> done)
    {
        var qte = new QteView();
        _root.AddChild(qte);
        qte.Start(sequence, window, done);
    }

    public void ShowReport(SurgeryReport report)
    {
        Open(ReportView.Build(report, Surgery.Scenario));
        _overlayLocked = true;
    }

    public void CloseOverlay()
    {
        if (Overlay is not null)
        {
            Dismiss(Overlay);
            Overlay = null;
            Manual = null;
            _overlayLocked = false;
        }
        if (Surgery.LocalSurgeon is { } me)
        {
            me.InputLocked = false;
        }
        CaptureMouse();
    }

    private void Open(Control overlay)
    {
        if (Overlay is not null && Overlay != overlay)
        {
            Dismiss(Overlay);
        }
        Overlay = overlay;
        if (overlay.GetParent() is null)
        {
            AddChild(overlay);
        }
        overlay.Show();
        if (Surgery.LocalSurgeon is { } me)
        {
            me.InputLocked = true;
            me.Hands[me.Active].Lowered = false;
            me.Hands[me.Active].Trigger = false;
        }
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    /// <summary>The nurse's shop stays built between visits; other overlays are made fresh each time.</summary>
    private void Dismiss(Control overlay)
    {
        if (overlay == Shop.View)
        {
            overlay.Hide();
        }
        else
        {
            overlay.QueueFree();
        }
    }

    private Control PauseMenu()
    {
        var box = Ui.VBox(12);
        box.AddChild(Ui.Label("PAUSED (the patient isn't)", 28, Ui.Pip));
        box.AddChild(Ui.Button("Back to the table", CloseOverlay));
        box.AddChild(Ui.Button("Leave to main menu", Net.Instance.BackToMenu));
        var center = new CenterContainer();
        center.AddChild(Ui.PanelAround(box));
        return Ui.Fullscreen(center, new Color(0f, 0f, 0f, 0.6f));
    }

    /// <summary>Headless there's no mouse to capture: the only motion is what a test sends.</summary>
    private void CaptureMouse()
    {
        if (Surgery.Running && DisplayServer.GetName() != "headless")
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
    }

    // --- Building ------------------------------------------------------------------------------------------------

    private void BuildShop()
    {
        var groups = Db.Tools.Values
            .Where(def => def.Orderable)
            .GroupBy(def => def.Category)
            .Select(group => (group.Key, (IReadOnlyList<ToolDef>)[.. group]));
        Shop = new NurseShop(groups, ids =>
        {
            Surgery.OrderTools(ids);
            CloseOverlay();
        }, CloseOverlay);
        Shop.View.Hide();
        AddChild(Shop.View);
    }

    private void BuildPostFx()
    {
        var layer = new CanvasLayer { Layer = 0 };
        AddChild(layer);
        _post = new ShaderMaterial { Shader = GD.Load<Shader>(PostFxShader) };
        var rect = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore, Material = _post };
        rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(rect);
    }

    private Label CornerLabel(Control.LayoutPreset preset, int size, Color color)
    {
        var label = Ui.Label("", size, color);
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        label.SetAnchorsAndOffsetsPreset(preset, Control.LayoutPresetMode.Minsize, 20);
        _root.AddChild(label);
        return label;
    }

    private void BuildBottomBar()
    {
        var bar = Ui.VBox(4);
        bar.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        bar.Position = new Vector2(-300f, -110f);
        bar.CustomMinimumSize = new Vector2(600f, 0f);
        _hands = Ui.Label("", 20, Ui.Ink);
        _hands.HorizontalAlignment = HorizontalAlignment.Center;
        bar.AddChild(_hands);
        _belt = Ui.HBox(6);
        _belt.Alignment = BoxContainer.AlignmentMode.Center;
        bar.AddChild(_belt);
        _root.AddChild(bar);
    }

    private void BuildGauges()
    {
        var box = Ui.VBox(4);
        box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
        box.Position = new Vector2(20f, -180f);
        foreach (var key in GaugeNames)
        {
            var row = Ui.HBox(8);
            var name = Ui.Label(key.Capitalize(), 16, Ui.Dim);
            name.CustomMinimumSize = new Vector2(80f, 0f);
            row.AddChild(name);
            var bar = Ui.Bar(key switch
            {
                "stress" => Ui.Alert,
                "sickness" => new Color(0.6f, 0.75f, 0.2f),
                "breath" => new Color(0.5f, 0.75f, 1f),
                _ => new Color(0.8f, 0.8f, 0.6f),
            });
            row.AddChild(bar);
            box.AddChild(row);
            _gauges[key] = (row, bar);
        }
        _root.AddChild(box);
    }

    private void BuildControlsHint()
    {
        _hint = Ui.Label("", 14, Ui.Hint);
        _hint.AutowrapMode = TextServer.AutowrapMode.Off;
        _hint.HorizontalAlignment = HorizontalAlignment.Right;
        _hint.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight, Control.LayoutPresetMode.Minsize, 16);
        _hint.GrowHorizontal = Control.GrowDirection.Begin;
        _hint.GrowVertical = Control.GrowDirection.Begin;
        _root.AddChild(_hint);
    }

    /// <summary>The controls that do something right now. Holding a hand key swaps looking for moving that hand.
    /// </summary>
    public static List<string> ControlLines(Surgeon me)
    {
        static string Key(string action) => InputActions.BindingText(action);
        var moving = Surgeon.MovingHand();
        var hand = me.Hands[me.Active];
        var tool = me.HeldTool(me.Active);
        var lines = new List<string>();
        if (moving < 0)
        {
            lines.Add("Mouse  Look around");
            lines.Add($"{Key(InputActions.MoveLeftHand)} / {Key(InputActions.MoveRightHand)} (hold)  Move left / right hand");
        }
        else
        {
            lines.Add($"Mouse  Move {(me.Active == 0 ? "left" : "right")} hand");
        }
        if (tool is not null)
        {
            lines.AddRange(ToolLines(me, tool, hand));
        }
        else
        {
            var hovered = IsInstanceValid(me.Hovered) ? " " + me.Hovered!.Label() : "";
            lines.Add($"{Key(InputActions.Grab)}  Pick up{hovered}");
        }
        var seeThrough = Surgeon.IsNeedle(tool) && me.Zoom == Surgeon.ZoomFov.Length - 1;
        lines.Add($"{Key(InputActions.Zoom)}  Zoom {me.Zoom + 1}/{Surgeon.ZoomFov.Length}{(seeThrough ? ", hands see-through" : "")}");
        lines.Add($"{Key(InputActions.Lift)} (hold)  {(hand.Attached ? "Pull up" : "Lift hand over")}");
        lines.Add($"{Key(InputActions.Steady)} (hold)  Hold breath");
        if (moving < 0)
        {
            var move = string.Join("/", new[]
            {
                InputActions.MoveForward, InputActions.MoveLeft, InputActions.MoveBack, InputActions.MoveRight,
            }.Select(Key));
            lines.Add($"{move}  Move");
            lines.Add($"{Key(InputActions.Crouch)} (hold)  Crouch");
            lines.Add($"{Key(InputActions.Interact)}  Interact");
            lines.Add($"{Key(InputActions.Drink)}  Drink / wear");
            lines.Add($"{Key(InputActions.BeltSlot(1))}-{Key(InputActions.BeltSlot(4))}  Belt slots");
        }
        return lines;
    }

    /// <summary>The controls for the tool in the active hand.</summary>
    private static IEnumerable<string> ToolLines(Surgeon me, SurgicalTool tool, SurgeonHand hand)
    {
        static string Key(string action) => InputActions.BindingText(action);
        var action = tool.Def.Action;
        var use = Key(InputActions.UseTool);
        yield return action == "sew"
            ? $"{use}  Stitch (click), tie off (hold)"
            : $"{use} (hold)  {ToolActions.TriggerNames.GetValueOrDefault(action, action == "syringe" ? "Press in" : "Use")} {tool.Label()}";
        var (up, down) = (Key(InputActions.LevelUp), Key(InputActions.LevelDown));
        switch (action)
        {
            case "syringe":
                yield return $"{down}  Pull plunger 1 ml";
                yield return $"{up}  Push plunger 1 ml";
                break;
            case "sew":
                yield return $"{up} / {down}  Loosen / tighten thread";
                break;
            case "spread":
                yield return $"{up} / {down}  Open / close";
                break;
            default:
                if (me.UsesLevel(me.Active))
                {
                    yield return $"Wheel  {ToolActions.LevelNames[action]}";
                }
                break;
        }
        // A syringe turns its scale to the eyes on its own: rolling it does nothing.
        var roll = action == "syringe" ? "" : $"   {Key(InputActions.TwistLeft)} / {Key(InputActions.TwistRight)}  Rotate";
        yield return $"{Key(InputActions.AimTool)} (hold) + mouse  Turn tool{roll}";
        yield return $"{Key(InputActions.Inspect)} (hold)  Look at it";
        var put = hand.Attached && tool.Def.SelfRetaining ? "Let go (it keeps its hold)"
            : tool.Def.Tray == "bottles" ? "Put down (hold: stand up)"
            : "Put down";
        var pass = me.PassTarget(me.Active) is not null && !hand.Attached;
        yield return $"{Key(InputActions.Grab)}  {(pass ? "Pass" : put)}";
    }

    // --- Updating ------------------------------------------------------------------------------------------------

    private void UpdateObjectives()
    {
        _objectives.Visible = Settings.Debug;
        if (Settings.Debug)
        {
            _objectives.Refresh(Surgery.Status);
        }
    }

    private void UpdateHands(Surgeon me)
    {
        var parts = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var tool = me.HeldTool(i);
            var text = $"{(i == 0 ? "L" : "R")}: {tool?.Label() ?? "empty"}";
            if (tool is { Sterile: false } && me.Mods.Flag("contamination_vision"))
            {
                text += " (dirty)";
            }
            if (me.Hands[i].Attached)
            {
                text += " [holding]";
            }
            parts.Add(i == me.Active ? $"▶ {text} ◀" : text);
        }
        var active = me.HeldTool(me.Active);
        if (active?.Def.Action == "sew")
        {
            parts.Add($"thread: {SewAction.ThreadState(active)}");
        }
        if (active?.Def.Action == "spread")
        {
            parts.Add($"open: {active.Spread * 100f:0.0} cm");
        }
        if (LevelSteps(active) is { } steps)
        {
            var level = me.Hands[me.Active].Level;
            parts.Add($"{ToolActions.LevelNames[active!.Def.Action].ToLowerInvariant()}: {level} {steps[level]}");
        }
        _hands.Text = string.Join("   ", parts);
        var capacity = me.BeltCapacity();
        while (_belt.GetChildCount() < capacity)
        {
            var slot = Ui.Label("", 16, Ui.Dim);
            slot.AutowrapMode = TextServer.AutowrapMode.Off;
            _belt.AddChild(slot);
        }
        for (var i = 0; i < capacity; i++)
        {
            var tool = Surgery.Tools.ToolOnBelt(me.PeerId, i);
            _belt.GetChild<Label>(i).Text = $"[{i + 1}] {tool?.Label() ?? "—"}";
        }
    }

    private void UpdateGauges(Surgeon me)
    {
        var status = me.Status;
        foreach (var key in GaugeNames)
        {
            var value = key switch
            {
                "stress" => status.Stress,
                "sickness" => status.Sickness,
                "breath" => status.Breath,
                _ => status.Sweat,
            };
            var (row, bar) = _gauges[key];
            bar.Value = value;
            row.Visible = key == "stress" || (value > 0.01f && (key != "breath" || value < 0.99f));
        }
    }
}
