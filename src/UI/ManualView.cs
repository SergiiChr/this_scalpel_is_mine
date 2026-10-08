namespace Scalpel.UI;

/// <summary>
/// The in-game manual. Takes the whole screen on purpose: reading it means not operating. Divine knowledge points a
/// hand at the pages that match the patient's hidden conditions. Styled as a Victorian surgical guide: parchment sheets
/// in ruled frames, red and black ink, hand written notes. Page markup on top of BBCode: a line starting with "## " is a
/// section heading, one starting with "> " a note. Sub-pages (chronic conditions) are listed under their page only
/// while it or one of them is open.
/// </summary>
public sealed class ManualView
{
    private static readonly Color Ink = new("231c16");
    private static readonly Color Red = new("b3241c");
    private static readonly Color Glow = new("9a6a00");
    private const string BodyFont = "res://assets/fonts/IMFellEnglish-Regular.ttf";
    private const string ItalicFont = "res://assets/fonts/IMFellEnglish-Italic.ttf";
    private const string CapsFont = "res://assets/fonts/IMFellEnglishSC-Regular.ttf";
    private const string ScriptFont = "res://assets/fonts/LaBelleAurore-Regular.ttf";
    private const string PaperTexture = "res://assets/manual/paper.jpg";
    private const string FrameTexture = "res://assets/manual/frame.svg";
    /// <summary>Width of the frame texture's border, matches FRAME_MARGIN in tools/assetgen/manual.py.</summary>
    private const int FrameMargin = 44;

    /// <summary>A line of the table of contents: the page it opens, the section it's listed under (its own page for a
    /// section) and whether the divine hand points at it.</summary>
    public sealed record Entry(Button Button, ManualPage Page, ManualPage Section, bool Glowing);

    /// <summary>The whole overlay.</summary>
    public Control View { get; }
    public IReadOnlyList<Entry> Entries => _entries;
    private readonly List<Entry> _entries = [];
    private readonly Label _title;
    private readonly RichTextLabel _page;

    public ManualView(IReadOnlyCollection<string> highlightKeys, Action close)
    {
        var book = Ui.HBox(28);
        book.Theme = BookTheme();
        _title = FontLabel("", 40, Red, CapsFont);
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _page = new RichTextLabel { BbcodeEnabled = true, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var content = Ui.VBox(16);
        content.AddChild(TitleBox(_title));
        content.AddChild(_page);

        var toc = Ui.VBox(0);
        var heading = FontLabel("Operating Theatre\nProcedures", 30, Red, CapsFont);
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        toc.AddChild(heading);
        var revision = FontLabel("~ Revision the Seventh ~", 18, Ink, ItalicFont);
        revision.HorizontalAlignment = HorizontalAlignment.Center;
        toc.AddChild(revision);
        toc.AddChild(new Control());
        foreach (var section in Db.Manual)
        {
            foreach (var page in section.Children.Prepend(section))
            {
                var glowing = highlightKeys.Count > 0 && page.Matches(highlightKeys);
                var indent = page == section ? "" : "      ";
                var button = Ui.Button(indent + (glowing ? "☞ " : "") + page.Title, () => Show(page, section));
                button.Alignment = HorizontalAlignment.Left;
                button.Visible = page == section;
                _entries.Add(new Entry(button, page, section, glowing));
                toc.AddChild(button);
            }
        }
        var closeButton = Ui.Button("Close  [Esc]", close);
        closeButton.AddThemeFontOverride("font", GD.Load<Font>(CapsFont));
        closeButton.AddThemeColorOverride("font_color", Red);
        // Outside the scrolling list, so an open section's long list never pushes it out of view.
        var tocColumn = Ui.VBox(12);
        tocColumn.AddChild(Ui.Scroll(toc));
        tocColumn.AddChild(TitleBox(closeButton));
        var tocSheet = Sheet(tocColumn);
        tocSheet.CustomMinimumSize = new Vector2(440f, 0f);
        book.AddChild(tocSheet);
        var pageSheet = Sheet(content);
        pageSheet.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        book.AddChild(pageSheet);
        if (Db.Manual.Count > 0)
        {
            Show(Db.Manual[0], Db.Manual[0]);
        }
        View = Ui.Fullscreen(book, new Color(0.02f, 0.02f, 0.02f, 0.97f));
    }

    /// <summary>The page's own markup as BBCode: "## 4.1 Name" headings and "> " notes.</summary>
    public static string ToBbcode(string body) => string.Join("\n", body.Split('\n').Select(line =>
    {
        var red = Red.ToHtml(false);
        if (line.StartsWith("## ", StringComparison.Ordinal))
        {
            var number = line[3..].Split(' ', 2)[0];
            var name = line[(4 + number.Length)..];
            return $"[font={ScriptFont}][font_size=30][color=#{red}]{number}[/color][/font_size][/font]"
                + $"  [font={CapsFont}][font_size=28]{name}[/font_size][/font]";
        }
        return line.StartsWith("> ", StringComparison.Ordinal)
            ? $"[indent][font={ScriptFont}][font_size=24][color=#{red}]{line[2..]}[/color][/font_size][/font][/indent]"
            : line;
    }));

    /// <summary>Opens <paramref name="page"/>, listed under <paramref name="section"/>, and shows that section's
    /// sub-pages.</summary>
    public void Show(ManualPage page, ManualPage section)
    {
        // The heading shows the name only: "4. Incision and exposure" becomes "Incision and exposure".
        _title.Text = page.Title.Split(". ")[^1];
        _page.Text = ToBbcode(page.Body);
        _page.ScrollToLine(0);
        foreach (var entry in _entries)
        {
            entry.Button.Visible = entry.Section == entry.Page || entry.Section == section;
            var color = entry.Page == page ? Red : entry.Glowing ? Glow : Ink;
            entry.Button.AddThemeColorOverride("font_color", color);
            entry.Button.AddThemeColorOverride("font_focus_color", color);
        }
        Sfx.Play("page_turn");
    }

    /// <summary>A parchment sheet in the ruled frame.</summary>
    private static PanelContainer Sheet(Control child)
    {
        var sheet = new PanelContainer();
        sheet.AddThemeStyleboxOverride("panel", new StyleBoxTexture { Texture = GD.Load<Texture2D>(PaperTexture) });
        sheet.AddChild(new NinePatchRect
        {
            Texture = GD.Load<Texture2D>(FrameTexture),
            PatchMarginLeft = FrameMargin,
            PatchMarginTop = FrameMargin,
            PatchMarginRight = FrameMargin,
            PatchMarginBottom = FrameMargin,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });
        var margin = new MarginContainer();
        foreach (var side in (string[])["left", "right", "top", "bottom"])
        {
            margin.AddThemeConstantOverride("margin_" + side, FrameMargin + 20);
        }
        margin.AddChild(child);
        sheet.AddChild(margin);
        return sheet;
    }

    /// <summary>Black rule inside a red one, like the title plates of old printed guides.</summary>
    private static PanelContainer TitleBox(Control child)
    {
        var outer = new PanelContainer();
        outer.AddThemeStyleboxOverride("panel", Rule(Red, 1, 4));
        var inner = new PanelContainer();
        inner.AddThemeStyleboxOverride("panel", Rule(Ink, 2, 6));
        inner.AddChild(child);
        outer.AddChild(inner);
        return outer;
    }

    private static StyleBoxFlat Rule(Color color, int width, int padding)
    {
        var box = new StyleBoxFlat { DrawCenter = false, BorderColor = color };
        box.SetBorderWidthAll(width);
        box.SetContentMarginAll(padding);
        return box;
    }

    private static Label FontLabel(string text, int size, Color color, string font)
    {
        var label = Ui.Label(text, size, color);
        label.AddThemeFontOverride("font", GD.Load<Font>(font));
        return label;
    }

    private static Theme BookTheme()
    {
        var theme = new Theme { DefaultFont = GD.Load<Font>(BodyFont), DefaultFontSize = 22 };
        var flat = new StyleBoxEmpty();
        flat.SetContentMarginAll(3);
        foreach (var state in (string[])["normal", "hover", "pressed", "focus", "disabled"])
        {
            theme.SetStylebox(state, "Button", flat);
        }
        theme.SetColor("font_color", "Button", Ink);
        foreach (var state in (string[])["font_hover_color", "font_pressed_color", "font_hover_pressed_color"])
        {
            theme.SetColor(state, "Button", Red);
        }
        theme.SetColor("default_color", "RichTextLabel", Ink);
        theme.SetFont("normal_font", "RichTextLabel", GD.Load<Font>(BodyFont));
        theme.SetFont("bold_font", "RichTextLabel", GD.Load<Font>(CapsFont));
        theme.SetFont("italics_font", "RichTextLabel", GD.Load<Font>(ItalicFont));
        foreach (var size in (string[])["normal_font_size", "bold_font_size", "italics_font_size"])
        {
            theme.SetFontSize(size, "RichTextLabel", 22);
        }
        theme.SetColor("table_odd_row_bg", "RichTextLabel", new Color(0f, 0f, 0f, 0f));
        theme.SetColor("table_even_row_bg", "RichTextLabel", Ink with { A = 0.06f });
        theme.SetConstant("table_h_separation", "RichTextLabel", 16);
        theme.SetConstant("line_separation", "RichTextLabel", 4);
        theme.SetStylebox("normal", "RichTextLabel", new StyleBoxEmpty());
        var grabber = new StyleBoxFlat { BgColor = Ink with { A = 0.35f }, ContentMarginLeft = 3, ContentMarginRight = 3 };
        theme.SetStylebox("scroll", "VScrollBar", new StyleBoxEmpty());
        foreach (var state in (string[])["grabber", "grabber_highlight", "grabber_pressed"])
        {
            theme.SetStylebox(state, "VScrollBar", grabber);
        }
        return theme;
    }
}
