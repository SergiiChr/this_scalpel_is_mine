namespace Scalpel.UI;

/// <summary>Small builders so every screen looks the same. Colors and sizes live here.</summary>
public static class Ui
{
    public static readonly Color Ink = new(0.82f, 0.9f, 0.84f);
    public static readonly Color Dim = new(0.55f, 0.62f, 0.58f);
    public static readonly Color Hint = new(0.7f, 0.7f, 0.7f, 0.45f);
    public static readonly Color Alert = new(0.95f, 0.35f, 0.3f);
    public static readonly Color Good = new(0.45f, 0.95f, 0.55f);
    public static readonly Color Pip = new(0.35f, 1f, 0.5f);
    public static readonly Color Panel = new(0.05f, 0.07f, 0.07f, 0.88f);
    public static readonly Color Paper = new(0.86f, 0.82f, 0.72f);
    public static readonly Color PaperInk = new(0.12f, 0.1f, 0.09f);
    private static readonly Color Mixed = new(0.95f, 0.8f, 0.35f);

    public static Theme Theme { get; } = BuildTheme();

    private static Theme BuildTheme()
    {
        var theme = new Theme { DefaultFontSize = 20 };
        theme.SetColor("font_color", "Label", Ink);
        theme.SetColor("font_color", "Button", Ink);
        theme.SetColor("font_hover_color", "Button", Pip);
        theme.SetColor("font_pressed_color", "Button", Pip);
        theme.SetColor("font_disabled_color", "Button", Dim);
        foreach (var state in (string[])["normal", "hover", "pressed", "disabled", "focus"])
        {
            var box = new StyleBoxFlat
            {
                BgColor = state == "hover" ? new Color(0.15f, 0.22f, 0.18f, 0.95f) : new Color(0.1f, 0.13f, 0.12f, 0.9f),
                BorderColor = state is "hover" or "focus" ? Pip : new Color(0.25f, 0.32f, 0.28f),
                DrawCenter = state != "focus",
            };
            box.SetBorderWidthAll(1);
            box.SetContentMarginAll(8);
            theme.SetStylebox(state, "Button", box);
        }
        var panel = new StyleBoxFlat { BgColor = Panel, BorderColor = new Color(0.2f, 0.28f, 0.24f) };
        panel.SetBorderWidthAll(1);
        panel.SetContentMarginAll(16);
        theme.SetStylebox("panel", "PanelContainer", panel);
        return theme;
    }

    /// <summary><paramref name="wrap"/>: break long text over lines. Only for labels whose container sets the width
    /// (VBox, not HBox).</summary>
    public static Label Label(string text, int size = 20, Color? color = null, bool wrap = false)
    {
        var label = new Label { Text = text };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color ?? Ink);
        if (wrap)
        {
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        }
        return label;
    }

    public static Button Button(string text, Action pressed)
    {
        var button = new Button { Text = text };
        button.Pressed += pressed;
        return button;
    }

    public static VBoxContainer VBox(int separation = 8)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", separation);
        return box;
    }

    public static HBoxContainer HBox(int separation = 8)
    {
        var box = new HBoxContainer();
        box.AddThemeConstantOverride("separation", separation);
        return box;
    }

    public static PanelContainer PanelAround(Control child)
    {
        var panel = new PanelContainer();
        panel.AddChild(child);
        return panel;
    }

    public static ProgressBar Bar(Color color)
    {
        var bar = new ProgressBar { MaxValue = 1.0, ShowPercentage = false, CustomMinimumSize = new Vector2(160f, 10f) };
        bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = color });
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color(0.1f, 0.1f, 0.1f, 0.6f) });
        return bar;
    }

    public static TextureRect Icon(Texture2D? texture, int size = 48) => new()
    {
        Texture = texture,
        CustomMinimumSize = new Vector2(size, size),
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
    };

    public static ScrollContainer Scroll(Control child)
    {
        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        child.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        scroll.AddChild(child);
        return scroll;
    }

    /// <summary>Full screen dark backdrop with a centered child.</summary>
    public static Control Fullscreen(Control child, Color? backdrop = null)
    {
        var root = new Control { Theme = Theme };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var background = new ColorRect { Color = backdrop ?? new Color(0f, 0f, 0f, 0.85f) };
        background.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        root.AddChild(background);
        var margin = new MarginContainer();
        margin.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        foreach (var side in (string[])["left", "right", "top", "bottom"])
        {
            margin.AddThemeConstantOverride("margin_" + side, 48);
        }
        margin.AddChild(child);
        root.AddChild(margin);
        return root;
    }

    /// <summary>A panel of at least <paramref name="minimumSize"/> centered over a dimmed screen.</summary>
    public static Control CenteredPanel(Control content, Vector2 minimumSize, Color? backdrop = null)
    {
        var holder = new CenterContainer();
        var panel = PanelAround(content);
        panel.CustomMinimumSize = minimumSize;
        holder.AddChild(panel);
        return Fullscreen(holder, backdrop);
    }

    public static Control QuirkLine(QuirkDef quirk, string variant, bool detailed)
    {
        var row = HBox(12);
        row.AddChild(Icon(quirk.Icon(), detailed ? 56 : 36));
        var text = VBox(2);
        var polarity = quirk.Polarity(variant);
        var color = polarity switch { "positive" => Good, "negative" => Alert, "mixed" => Mixed, _ => Ink };
        text.AddChild(Label($"{quirk.DisplayName(variant)}  [{polarity}]", 20, color));
        if (detailed)
        {
            text.AddChild(Label(quirk.Text("lore", variant), 16, Dim, true));
            text.AddChild(Label("+ " + quirk.Text("pros", variant), 16, Good, true));
            text.AddChild(Label("- " + quirk.Text("cons", variant), 16, Alert, true));
            text.AddChild(Label(quirk.Text("specifics", variant), 16, Ink, true));
        }
        text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(text);
        return row;
    }

    /// <summary>A popup list of buttons (the blood panel). <paramref name="pick"/> receives the chosen value.</summary>
    public static Control ChoiceMenu<T>(
        string title, string subtitle, IEnumerable<(string Label, T Value)> choices, Action<T> pick, Action close)
    {
        var list = VBox(6);
        list.AddChild(Label(title, 28, Pip));
        list.AddChild(Label(subtitle, 16, Dim, true));
        var grid = new GridContainer { Columns = 3 };
        foreach (var (text, value) in choices)
        {
            var button = Button(text, () => pick(value));
            button.CustomMinimumSize = new Vector2(320f, 0f);
            grid.AddChild(button);
        }
        list.AddChild(Scroll(grid));
        list.AddChild(Button("Never mind  [Esc]", close));
        return CenteredPanel(list, new Vector2(1040f, 640f), new Color(0f, 0f, 0f, 0.6f));
    }
}
