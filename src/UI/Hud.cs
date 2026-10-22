namespace Scalpel.UI;

/// <summary>
/// Everything drawn on screen during surgery, plus the full screen overlays (manual, card, menus, report).
/// While an overlay is open the surgeon's input is locked and the mouse is free.
/// </summary>
public partial class Hud : CanvasLayer {
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

    /// <summary>Remembers the state a label showed when its text was last built, so the text is built again only when
    /// that changes: building it allocates its strings, every frame otherwise.</summary>
    private struct Shown<T> where T : struct, IEquatable<T> {
        private T? _last;

        public bool Changed(T now) {
            if (_last is { } last && last.Equals(now)) {
                return false;
            }
            _last = now;
            return true;
        }
    }

    private readonly record struct ClockShown(string Title, int Seconds);
    private readonly record struct ObjectivesShown(SurgeryStatus Status, Difficulty? Difficulty, bool Debug);
    private readonly record struct BoardShown(string? Order, int Done, int SecondsLeft, int Cooldown);
    private readonly record struct HandShown(SurgicalTool? Tool, bool Full, bool Dirty, bool Holding);
    private readonly record struct HandsShown(HandShown Left, HandShown Right, int Active, string? Thread, int Spread, int Level);
    private readonly record struct HintShown(bool Out, int Moving, int Active, bool Attached, SurgicalTool? Tool, bool Full,
        bool Threaded, SurgicalTool? Hovered, bool HoveredFull, int Zoom, bool CanPass, bool Films, bool Cart,
        int Bindings);
    private readonly record struct PromptShown(string? Focused, StringName? Button, string? Wheel, bool Held, Surgeon? Partner, int Bindings);

    public Surgery Surgery { get; private set; } = null!;
    private Control _root = null!;
    private Label _clock = null!;
    private ObjectivesPanel _objectives = null!;
    private Label _hands = null!;
    private HBoxContainer _belt = null!;
    private Label _prompt = null!;
    private Label _netWarning = null!;
    /// <summary>Seconds left to restart a stopped heart, for its owner only.</summary>
    private Label _countdown = null!;
    /// <summary>Controls for what the player is doing right now, bottom right. Changes while a hand key or a tool is
    /// held.</summary>
    private Label _hint = null!;
    private VBoxContainer _toasts = null!;
    private Label _subtitle = null!;
    private float _subtitleTimer;
    private Shown<ClockShown> _clockShown;
    private Shown<ObjectivesShown> _objectivesShown;
    private Shown<BoardShown> _boardShown;
    private Shown<HandsShown> _handsShown;
    private Shown<(SurgicalTool? Tool, bool Full)>[] _beltShown = [];
    private Shown<HintShown> _hintShown;
    private Shown<PromptShown> _promptShown;
    private readonly Dictionary<string, (HBoxContainer Row, ProgressBar Bar)> _gauges = [];
    private ShaderMaterial _post = null!;
    private (float Blackout, float Daze, float Wobble, float Blur, float Eyelids)? _postState;
    /// <summary>Game time the view's moving effects run on (<see cref="ShaderParam.GameTime"/>), not the shaders' own
    /// TIME: it advances only with gameplay frames, so a scene looks the same whenever it is drawn.</summary>
    private float _effectsTime;
    /// <summary>Blood on the view (0..1).</summary>
    private float _lensBlood;
    /// <summary>How much blood is splashed on the view now (0..1).</summary>
    internal float LensBlood => _lensBlood;

    /// <summary>Wipes the blood off the view at once.</summary>
    internal void ClearLensBlood() {
        _lensBlood = 0f;
        _post.SetShaderParameter(ShaderParam.LensBlood, 0f);
    }
    /// <summary>Takes the film grain off the view for good. It differs in every frame, and key frames are compared.
    /// </summary>
    internal void RemoveFilmGrain() => _post.SetShaderParameter(ShaderParam.Grain, 0f);

    /// <summary>A uniform of the full screen post effect as the screen is drawn with it.</summary>
    internal float PostEffect(StringName name) => _post.GetShaderParameter(name).AsSingle();

    internal string Countdown => _countdown.Text;
    /// <summary>How long since the view was clean.</summary>
    private float _lensAge;
    public NurseShop Shop { get; private set; } = null!;
    /// <summary>The open overlay, null for none.</summary>
    public Control? Overlay { get; private set; }
    /// <summary>The open manual, null when it isn't.</summary>
    public ManualView? Manual { get; private set; }
    /// <summary>Overlays Esc can't close (the report).</summary>
    private bool _overlayLocked;
    /// <summary>The patient card's page, while it's open or until the next one.</summary>
    private Control? _card;
    internal bool CardOpen => Overlay is not null && Overlay == _card;
    /// <summary>The hand status line as shown: what each hand holds, and the active tool's thread or pull reading.
    /// </summary>
    internal string HandsLine => _hands.Text;
    /// <summary>The chart correction the card's page was built with.</summary>
    private bool _cardCorrected;
    private Tween? _cardFade;
    /// <summary>The discharge line on the card's page.</summary>
    internal SignatureView? Signature { get; private set; }
    /// <summary>Interact went down while the card's page showed, and is still held.</summary>
    private bool _signing;
    /// <summary>How long the card has shown a whole signature.</summary>
    private float _signedFor;
    /// <summary>A whole signature stays on the card this long before the patient goes.</summary>
    internal const float SignedPause = 0.6f;

    public void Setup(Surgery surgery) {
        Surgery = surgery;
        BuildPostFx();
        surgery.Patient.Body.Blood.Splashed += OnBloodSplashed;
        _root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Theme = HudTheme() };
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
        _countdown = CornerLabel(Control.LayoutPreset.Center, 40, Ui.Alert);
        _countdown.Position += new Vector2(-300f, -120f);
        _countdown.CustomMinimumSize = new Vector2(600f, 0f);
        _countdown.HorizontalAlignment = HorizontalAlignment.Center;
        BuildAim();
        BuildBottomBar();
        BuildGauges();
        BuildControlsHint();
        BuildShop();
        UpdateObjectives();
        foreach (var label in _root.FindChildren("*", nameof(Label), true, false).OfType<Label>()) {
            Ui.PrepareLabelText(label, "0123456789");
        }
    }

    /// <summary>The surgery opens on the patient card, already in hand: put back, it shows where it hangs.</summary>
    public void Begin() {
        Toast(Surgery.Scenario.Title);
        OpenCard(inHand: true);
    }

    public override void _Process(double delta) => RefreshFrame((float)delta);

    /// <summary>Refreshes overlays for the current camera and game state. A zero delta refreshes a frozen capture
    /// without advancing HUD timers or effects.</summary>
    internal void RefreshFrame(float dt) {
        _effectsTime += dt;
        RenderingServer.GlobalShaderParameterSet(ShaderParam.GameTime, _effectsTime);
        if (Surgery?.LocalSurgeon is not { } me) {
            return;
        }
        var left = Surgery.TimeLeft();
        if (_clockShown.Changed(new ClockShown(Surgery.Scenario.Title, left >= 0f ? (int)left : -1))) {
            var clock = left >= 0f ? $"{(int)left / 60}:{(int)left % 60:00}" : "no time limit";
            _clock.Text = $"{Surgery.Scenario.Title}   {clock}";
        }
        UpdateObjectives();
        UpdateSignature(dt);
        if (Shop.View.Visible) {
            UpdateShop();
        }
        UpdateNetWarning();
        UpdateAim(me);
        UpdateHands(me);
        UpdateGauges(me);
        UpdateHint(me);
        UpdatePrompt(me);
        var countdown = me.Status.IsArrested ? $"You're having a heart attack.\n{Mathf.CeilToInt(me.Status.ArrestLeft)} s" : "";
        if (_countdown.Text != countdown) {
            _countdown.Text = countdown;
        }
        _subtitleTimer -= dt;
        if (_subtitleTimer <= 0f) {
            _subtitle.Text = "";
        }
        UpdatePostFx(me.Status, dt);
    }

    private void UpdateHint(Surgeon me) {
        var tool = me.HeldTool(me.Active);
        var hovered = IsInstanceValid(me.Hovered) ? me.Hovered : null;
        var shown = new HintShown(me.Status.IsOut, Surgeon.MovingHand(), me.Active, me.Hands[me.Active].Attached, tool,
            tool?.Ml > 0f, tool?.Suture.Thread > 0, hovered, hovered?.Ml > 0f, me.Zoom,
            me.PassTarget(me.Active) is not null, me.HoldingFilms, me.PushedCart() is not null, InputActions.BindingsVersion);
        if (_hintShown.Changed(shown)) {
            _hint.Text = shown.Out ? "" : string.Join("\n", ControlLines(me));
        }
    }

    private void UpdatePrompt(Surgeon me) {
        var spot = Overlay is null && !me.HoldingFilms ? me.Focused : null;
        var partner = me.PassTarget(me.Active)?.Surgeon;
        var shown = new PromptShown(spot?.Prompt, spot?.Button, spot?.Wheel is null ? null : spot.WheelPrompt,
            me.HeldTool(me.Active) is not null, partner, InputActions.BindingsVersion);
        if (!_promptShown.Changed(shown)) {
            return;
        }
        _prompt.Text = shown.Focused is not null
            ? $"[{InputActions.BindingText(shown.Button!)}] {shown.Focused}"
                + (shown.Wheel is null ? "" : $"   [Wheel] {shown.Wheel}")
            : shown.Held && partner is not null ? $"[{InputActions.BindingText(InputActions.Grab)}] Pass to {partner.DisplayName}"
            : "";
    }

    private void UpdatePostFx(SurgeonStatus status, float delta) {
        var state = (Blackout: status.PassedOut > 0f || status.Dead ? 1f : 0f,
            Daze: status.IsKnockedOut ? KnockedOutDaze : status.Overdose * OverdoseDaze,
            Wobble: status.Sickness,
            Blur: Mathf.Max(status.Sickness * 1.5f, status.Calm * SedatedBlur),
            Eyelids: status.Eyelids);
        // Godot queues material uploads even for unchanged parameter values.
        if (_postState?.Blackout != state.Blackout) {
            _post.SetShaderParameter(ShaderParam.Blackout, state.Blackout);
        }
        if (_postState?.Daze != state.Daze) {
            _post.SetShaderParameter(ShaderParam.Daze, state.Daze);
        }
        if (_postState?.Wobble != state.Wobble) {
            _post.SetShaderParameter(ShaderParam.Wobble, state.Wobble);
        }
        if (_postState?.Blur != state.Blur) {
            _post.SetShaderParameter(ShaderParam.Blur, state.Blur);
        }
        if (_postState?.Eyelids != state.Eyelids) {
            _post.SetShaderParameter(ShaderParam.Eyelids, state.Eyelids);
        }
        _postState = state;
        if (_lensBlood > 0f) {
            _lensBlood = Mathf.Max(_lensBlood - (delta / LensClearSeconds), 0f);
            _lensAge += delta;
            _post.SetShaderParameter(ShaderParam.LensBlood, _lensBlood);
            _post.SetShaderParameter(ShaderParam.LensAge, _lensAge);
        }
    }

    /// <summary>Blood on the view gets a new pattern of drops each time it starts from clean.</summary>
    private void OnBloodSplashed(float amount) {
        if (_lensBlood <= 0f) {
            _lensAge = 0f;
            _post.SetShaderParameter(ShaderParam.LensSeed, GD.Randf() * 100f);
        }
        _lensBlood = Mathf.Min(_lensBlood + amount, 1f);
    }

    /// <summary>A lag spike shows up after a moment; the game carries on and the connection only drops after the
    /// network timeout.</summary>
    private void UpdateNetWarning() {
        var (peer, seconds) = Net.Instance.WorstSilence();
        if (seconds < 1.5f) {
            _netWarning.Text = "";
            return;
        }
        var who = peer == Net.HostId ? "the host" : Net.Instance.Roster.GetValueOrDefault(peer)?.Name ?? "your partner";
        _netWarning.Text = $"Connection to {who} is unstable ({(int)seconds} s). Waiting...";
    }

    public override void _UnhandledInput(InputEvent @event) {
        if (!@event.IsActionPressed(InputActions.Pause)) {
            return;
        }
        GetViewport().SetInputAsHandled();
        if (_overlayLocked) {
            return;
        }
        if (Overlay is not null) {
            CloseOverlay();
        } else {
            Open(PauseMenu());
        }
    }

    /// <summary>A toast was shown, with its text.</summary>
    public event Action<string>? Toasted;

    public void Toast(string text) {
        if (text.Length == 0) {
            return;
        }
        Toasted?.Invoke(text);
        var label = Ui.Label(text, 20, Ui.Ink);
        label.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        // A message fades out, and its outline with it: the backing keeps it readable over the window blinds.
        label.AddThemeStyleboxOverride("normal", Backing());
        _toasts.AddChild(label);
        if (_toasts.GetChildCount() > 4) {
            _toasts.GetChild(0).QueueFree();
        }
        var tween = label.CreateTween();
        tween.TweenInterval(ToastTime);
        tween.TweenProperty(label, "modulate:a", 0.0, 0.8);
        tween.TweenCallback(Callable.From(label.QueueFree));
    }

    public void Subtitle(string text) {
        _subtitle.Text = $"\"{text}\"";
        _subtitleTimer = SubtitleTime;
    }

    // --- Overlays ------------------------------------------------------------------------------------------------

    public void OpenManual() {
        var keys = new List<string>();
        if (Surgery.LocalSurgeon?.Mods.Flag("manual_highlight") == true) {
            var patient = Surgery.Patient;
            keys.AddRange(patient.Mods.Keys);
            keys.AddRange(patient.Mods.List("allergen"));
            foreach (var roll in patient.Rolls) {
                keys.Add(roll.Id);
                if (roll.Variant.Length > 0) {
                    keys.Add($"{roll.Id}.{roll.Variant}");
                }
            }
        }
        Manual = new ManualView(keys, CloseOverlay);
        Open(Manual.View);
    }

    /// <summary>Takes the card up to read. Unless it's already <paramref name="inHand"/>, the page shows once the card
    /// has travelled up to the face. The page is built once (again only for the corrected chart): building it takes a
    /// frame's worth of work.</summary>
    public void OpenCard(bool inHand = false) {
        if (_card is null || _cardCorrected != Surgery.ChartCorrected) {
            if (Overlay == _card) {
                Overlay = null;
            }
            _card?.QueueFree();
            Signature = new SignatureView();
            _card = PatientCardView.Build(Surgery.Patient, Net.Instance.SessionSeed, Signature, CloseOverlay);
            _cardCorrected = Surgery.ChartCorrected;
        }
        Signature!.Reset();
        _signing = false;
        _signedFor = 0f;
        Open(_card);
        _cardFade?.Kill();
        _card.Modulate = new Color(1f, 1f, 1f, inHand ? 1f : 0f);
        if (!inHand) {
            _cardFade = _card.CreateTween();
            _cardFade.TweenProperty(_card, "modulate:a", 1.0, 0.15).SetDelay(PatientCard.TravelSeconds);
        }
    }

    public void OpenNurse() {
        UpdateShop();
        Open(Shop.View);
    }

    public void OpenLab() {
        var choices = Lab.Panels.Select(entry => ($"{entry.Value.Label}  ({(int)entry.Value.Seconds} s)", entry.Key));
        var cooldown = Surgery.Status.LabCooldown;
        var subtitle = cooldown > 0f ? $"Lab is busy for {Mathf.CeilToInt(cooldown)} s." : "Narrow panels come back faster.";
        Open(Ui.ChoiceMenu("Blood work", subtitle, choices, kind => {
            Surgery.OrderLab(kind);
            CloseOverlay();
        }, CloseOverlay));
    }

    private void UpdateShop() {
        var status = Surgery.Status;
        Shop.SetNurseReady(status.NurseReady);
        var board = new BoardShown(status.Order?.Text, Mathf.RoundToInt((status.Order?.Progress ?? 0f) * NurseProgress.Steps),
            Mathf.CeilToInt(status.Order?.SecondsLeft ?? 0f), Mathf.CeilToInt(status.NurseCooldown));
        if (_boardShown.Changed(board)) {
            Shop.ShowBoard(status);
        }
    }

    /// <summary>Starts the turning quick time event: <paramref name="done"/> gets the hits and whether a wrong key was
    /// pressed.</summary>
    public void RunQte(IReadOnlyList<string> sequence, float window, Action<int, bool> done) {
        var qte = new QteView();
        _root.AddChild(qte);
        qte.Start(sequence, window, done);
    }

    public void ShowReport(SurgeryReport report) {
        Open(ReportView.Build(report));
        _overlayLocked = true;
    }

    public void CloseOverlay() {
        if (Overlay is not null) {
            Dismiss(Overlay);
            Overlay = null;
            Manual = null;
            _overlayLocked = false;
        }
        if (Surgery.LocalSurgeon is { } me) {
            me.InputLocked = false;
            me.ReadingCard = false;
        }
        CaptureMouse();
    }

    private void Open(Control overlay) {
        if (Overlay is not null && Overlay != overlay) {
            Dismiss(Overlay);
        }
        Overlay = overlay;
        if (overlay.GetParent() is null) {
            AddChild(overlay);
        }
        overlay.Show();
        if (Surgery.LocalSurgeon is { } me) {
            me.InputLocked = true;
            me.ReadingCard = overlay == _card;
            me.Hands[me.Active].Lowered = false;
            me.Hands[me.Active].Trigger = false;
        }
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    /// <summary>The nurse's shop and the card's page stay built between visits; other overlays are made fresh each time.
    /// </summary>
    private void Dismiss(Control overlay) {
        if (overlay == Shop.View || overlay == _card) {
            if (overlay == _card) {
                _cardFade?.Kill();
                _cardFade = null;
            }
            overlay.Hide();
        } else {
            overlay.QueueFree();
        }
    }

    private Control PauseMenu() {
        var box = Ui.VBox(12);
        box.AddChild(Ui.Label("PAUSED (the patient isn't)", 28, Ui.Pip));
        box.AddChild(Ui.Button("Back to the table", CloseOverlay));
        box.AddChild(Ui.Button("Leave to main menu", Net.Instance.BackToMenu));
        var center = new CenterContainer();
        center.AddChild(Ui.PanelAround(box));
        return Ui.Fullscreen(center, new Color(0f, 0f, 0f, 0.6f));
    }

    /// <summary>Headless there's no mouse to capture: the only motion is what a test sends.</summary>
    private void CaptureMouse() {
        if (Surgery.Running && DisplayServer.GetName() != "headless") {
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
    }

    // --- Building ------------------------------------------------------------------------------------------------

    private void BuildShop() {
        var groups = Db.Tools.Values
            .Where(def => def.Orderable)
            .GroupBy(def => def.Category)
            .Select(group => (group.Key, (IReadOnlyList<ToolDef>)[.. group]));
        Shop = new NurseShop(groups, ids => {
            Surgery.OrderTools(ids);
            CloseOverlay();
        }, CloseOverlay);
        Shop.View.Hide();
        AddChild(Shop.View);
        Shop.Prepare();
    }

    private void BuildPostFx() {
        var layer = new CanvasLayer { Layer = 0 };
        AddChild(layer);
        _post = new ShaderMaterial { Shader = GD.Load<Shader>(PostFxShader) };
        var rect = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore, Material = _post };
        rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(rect);
    }

    private Label CornerLabel(Control.LayoutPreset preset, int size, Color color) {
        var label = Ui.Label("", size, color);
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        label.SetAnchorsAndOffsetsPreset(preset, Control.LayoutPresetMode.Minsize, 20);
        _root.AddChild(label);
        return label;
    }

    private void BuildBottomBar() {
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

    private void BuildGauges() {
        var box = Ui.VBox(4);
        box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
        box.Position = new Vector2(20f, -180f);
        foreach (var key in GaugeNames) {
            var row = Ui.HBox(8);
            var name = Ui.Label(key.Capitalize(), 16, Ui.Dim);
            name.CustomMinimumSize = new Vector2(80f, 0f);
            row.AddChild(name);
            var bar = Ui.Bar(key switch {
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

    private void BuildControlsHint() {
        _hint = Ui.Label("", 14, Ui.Hint);
        _hint.AutowrapMode = TextServer.AutowrapMode.Off;
        _hint.HorizontalAlignment = HorizontalAlignment.Right;
        // Faint text reads on a backing whatever the room behind it; an outline would swallow it.
        _hint.AddThemeConstantOverride("outline_size", 0);
        _hint.AddThemeStyleboxOverride("normal", Backing());
        _hint.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight, Control.LayoutPresetMode.Minsize, 16);
        _hint.GrowHorizontal = Control.GrowDirection.Begin;
        _hint.GrowVertical = Control.GrowDirection.Begin;
        _root.AddChild(_hint);
    }

    /// <summary>A soft dark box behind HUD text that has to read over anything.</summary>
    internal static StyleBoxFlat Backing() {
        var box = new StyleBoxFlat { BgColor = new Color(0f, 0f, 0f, 0.55f) };
        box.SetCornerRadiusAll(4);
        box.SetContentMarginAll(6f);
        return box;
    }

    /// <summary>The game's theme with a dark outline round every label, so HUD text reads over pale walls and skin as
    /// well as dark drapes.</summary>
    private static Theme HudTheme() {
        var theme = (Theme)Ui.Theme.Duplicate();
        theme.SetConstant("outline_size", "Label", 4);
        theme.SetColor("font_outline_color", "Label", new Color(0f, 0f, 0f, 0.7f));
        return theme;
    }

    /// <summary>The controls that do something right now. Holding a hand key swaps looking for moving that hand.
    /// </summary>
    public static List<string> ControlLines(Surgeon me) {
        static string Key(StringName action) => InputActions.BindingText(action);
        var moving = Surgeon.MovingHand();
        var hand = me.Hands[me.Active];
        var tool = me.HeldTool(me.Active);
        var lines = new List<string>();
        if (moving < 0) {
            lines.Add("Mouse  Look around");
            lines.Add($"{Key(InputActions.MoveLeftHand)} / {Key(InputActions.MoveRightHand)} (hold)  Move left / right hand");
        } else {
            lines.Add($"Mouse  Move {(me.Active == 0 ? "left" : "right")} hand");
        }
        if (me.HoldingFilms) {
            lines.Add($"{Key(InputActions.Interact)}  Put the films back");
        }
        if (me.PushedCart() is not null) {
            lines.Add("Wheel  Raise / lower the arm");
            lines.Add($"{Key(InputActions.UseTool)}  Take an X-ray");
            lines.Add($"{Key(InputActions.Grab)}  Let go, locking the cart in place");
        } else if (tool is not null) {
            lines.AddRange(ToolLines(me, tool, hand));
        } else {
            var hovered = IsInstanceValid(me.Hovered) ? " " + me.Hovered!.Label() : "";
            lines.Add($"{Key(InputActions.Grab)}  Pick up{hovered}");
        }
        var seeThrough = Surgeon.IsNeedle(tool) && me.Zoom == Surgeon.ZoomFov.Length - 1;
        lines.Add($"{Key(InputActions.Zoom)}  Zoom {me.Zoom + 1}/{Surgeon.ZoomFov.Length}{(seeThrough ? ", hands see-through" : "")}");
        lines.Add($"{Key(InputActions.Lift)} (hold)  {(hand.Attached ? "Pull up" : "Lift hand over")}");
        lines.Add($"{Key(InputActions.Steady)} (hold)  Hold breath");
        if (moving < 0) {
            var move = string.Join("/", new[] {
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
    private static IEnumerable<string> ToolLines(Surgeon me, SurgicalTool tool, SurgeonHand hand) {
        static string Key(StringName action) => InputActions.BindingText(action);
        var action = tool.Def.Action;
        var use = Key(InputActions.UseTool);
        yield return action switch {
            "sew" => $"{use}  Stitch (click), tie off (hold)",
            "tape" => $"{use}  Stick (click), cut off (hold)",
            _ => $"{use} (hold)  {ToolActions.TriggerNames.GetValueOrDefault(action, action == "syringe" ? "Press in" : "Use")} {tool.Label()}",
        };
        var (up, down) = (Key(InputActions.LevelUp), Key(InputActions.LevelDown));
        // A needle's wheel pulls its thread, so it has nothing to do before the first hole.
        if (ToolActions.HasTensionWheel(tool.Def) && (action != "sew" || tool.Suture.Thread != 0)) {
            yield return $"{up} / {down}  Loosen / tighten {(action == "sew" ? "thread" : tool.Label().ToLowerInvariant())}";
        }
        switch (action) {
            case "syringe":
                yield return $"{down}  Pull plunger 1 ml";
                yield return $"{up}  Push plunger 1 ml";
                break;
            case "spread":
                yield return $"{up} / {down}  Open / close";
                break;
            case "clamp" when me.HoldsDepth(me.Active):
                yield return $"{up} / {down}  Raise / lower {(me.Hands[me.Active].Attached ? "what it holds" : "tip in an opening")}";
                break;
            default:
                if (me.UsesLevel(me.Active)) {
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

    private void UpdateObjectives() {
        if (_objectivesShown.Changed(new ObjectivesShown(Surgery.Status, Settings.Difficulty, Settings.Debug))) {
            _objectives.Refresh(Surgery.Status);
        }
    }

    /// <summary>Holding Interact over the card's page signs the discharge; a press from before the page showed (taking
    /// the card off its hook) doesn't count. Signed in full, the patient goes after a moment.</summary>
    private void UpdateSignature(float dt) {
        if (!CardOpen || Signature is null || dt <= 0f) {
            return;
        }
        if (Signature.Complete) {
            var before = _signedFor;
            _signedFor += dt;
            if (before < SignedPause && _signedFor >= SignedPause) {
                Surgery.SignDischarge();
            }
            return;
        }
        _signing = Input.IsActionPressed(InputActions.Interact)
            && (_signing || (Input.IsActionJustPressed(InputActions.Interact) && _card!.Modulate.A >= 1f));
        Signature.Advance(dt, _signing);
    }

    private void UpdateHands(Surgeon me) {
        var active = me.HeldTool(me.Active);
        var shown = new HandsShown(HandState(me, 0), HandState(me, 1), me.Active,
            active is not null && ToolActions.HasTensionWheel(active.Def) ? SewAction.ThreadState(active) : null,
            active?.Def.Action == "spread" ? Mathf.RoundToInt(active.Spread * 1000f) : 0, me.Hands[me.Active].Level);
        if (_handsShown.Changed(shown)) {
            _hands.Text = HandsText(me, shown);
        }
        var capacity = me.BeltCapacity();
        while (_belt.GetChildCount() < capacity) {
            var slot = Ui.Label("", 16, Ui.Dim);
            slot.AutowrapMode = TextServer.AutowrapMode.Off;
            _belt.AddChild(slot);
        }
        if (_beltShown.Length < capacity) {
            Array.Resize(ref _beltShown, capacity);
        }
        for (var i = 0; i < capacity; i++) {
            var tool = Surgery.Tools.ToolOnBelt(me.PeerId, i);
            if (_beltShown[i].Changed((tool, tool?.Ml > 0f))) {
                _belt.GetChild<Label>(i).Text = $"[{i + 1}] {tool?.Label() ?? "—"}";
            }
        }
    }

    private static HandShown HandState(Surgeon me, int hand) {
        var tool = me.HeldTool(hand);
        return new HandShown(tool, tool?.Ml > 0f, tool is { Sterile: false } && me.Mods.Flag("contamination_vision"),
            me.Hands[hand].Attached);
    }

    private static string HandsText(Surgeon me, HandsShown shown) {
        var parts = new List<string>();
        foreach (var (hand, state) in (ReadOnlySpan<(int, HandShown)>)[(0, shown.Left), (1, shown.Right)]) {
            var text = $"{(hand == 0 ? "L" : "R")}: {state.Tool?.Label() ?? "empty"}";
            if (state.Dirty) {
                text += " (dirty)";
            }
            if (state.Holding) {
                text += " [holding]";
            }
            parts.Add(hand == shown.Active ? $"▶ {text} ◀" : text);
        }
        var active = me.HeldTool(me.Active);
        if (shown.Thread is { } thread) {
            parts.Add($"{(active!.Def.Action == "sew" ? "thread" : "pull")}: {thread}");
        }
        if (active?.Def.Action == "spread") {
            parts.Add($"open: {active.Spread * 100f:0.0} cm");
        }
        if (LevelSteps(active) is { } steps) {
            parts.Add($"{ToolActions.LevelNames[active!.Def.Action].ToLowerInvariant()}: {shown.Level} {steps[shown.Level]}");
        }
        return string.Join("   ", parts);
    }

    private void UpdateGauges(Surgeon me) {
        var status = me.Status;
        foreach (var key in GaugeNames) {
            var value = key switch {
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
