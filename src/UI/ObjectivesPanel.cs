namespace Scalpel.UI;

/// <summary>Top right: the steps the game checks, as far as the player's difficulty tells them. "I'm just a baby" lists
/// every main task, optional ones marked, with the subtasks the host sends for the next ones to do; Intern the required
/// main tasks and the optional ones marked as hints (an X-ray); MD none. Debug mode adds the score and the latest scored actions.</summary>
public partial class ObjectivesPanel : VBoxContainer {
    private const int TaskSize = 18;
    private const int SubtaskSize = 15;
    private const float SubtaskIndent = 24f;

    /// <summary>One label's text, font size, color and left indent, which its wrapped lines keep too.</summary>
    internal readonly record struct Line(string Text, int Size, Color Color, float Indent = 0f);

    private readonly StyleBoxFlat _backing = Hud.Backing();

    public ObjectivesPanel() => AddThemeConstantOverride("separation", 4);

    /// <summary>The backing reaches past the labels by its margin, as it does round a label that has it as its own.</summary>
    public override void _Draw() =>
        DrawStyleBox(_backing, new Rect2(Vector2.Zero, Size).Grow(_backing.GetMargin(Side.Left)));

    /// <summary>Shows the host's last status. Labels only change when their text or look does.</summary>
    public void Refresh(SurgeryStatus status) {
        var lines = Lines(status, Settings.Difficulty ?? Difficulty.Md, Settings.Debug);
        Visible = lines.Count > 0;
        while (GetChildCount() < lines.Count) {
            var label = Ui.Label("", TaskSize, Ui.Ink, true);
            // The theme's own box leaves its margins unset (-1): with one of its own the indent check below holds.
            label.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
            AddChild(label);
        }
        while (GetChildCount() > lines.Count) {
            var extra = GetChild(GetChildCount() - 1);
            RemoveChild(extra);
            extra.QueueFree();
        }
        for (var i = 0; i < lines.Count; i++) {
            var line = lines[i];
            var label = GetChild<Label>(i);
            if (label.Text != line.Text) {
                label.Text = line.Text;
            }
            // Reapplying the same override invalidates the label's layout and font shaping every frame.
            if (label.GetThemeFontSize("font_size") != line.Size) {
                label.AddThemeFontSizeOverride("font_size", line.Size);
            }
            if (label.GetThemeColor("font_color") != line.Color) {
                label.AddThemeColorOverride("font_color", line.Color);
            }
            if (label.GetThemeStylebox("normal").ContentMarginLeft != line.Indent) {
                label.AddThemeStyleboxOverride("normal", new StyleBoxEmpty { ContentMarginLeft = line.Indent });
            }
        }
    }

    internal static List<Line> Lines(SurgeryStatus status, Difficulty difficulty, bool debug) {
        var lines = new List<Line>();
        if (difficulty != Difficulty.Md) {
            foreach (var row in status.Objectives.Where(row => difficulty == Difficulty.Baby || !row.Optional || row.Hint)) {
                var mark = row.Spoiled ? "☒" : row.Done ? "☑" : "☐";
                var optional = row.Optional ? "[optional] " : "";
                lines.Add(new Line($"{mark} {optional}{row.Label}", TaskSize, row.Spoiled ? Ui.Alert : row.Done ? Ui.Dim : Ui.Ink));
                if (difficulty == Difficulty.Baby) {
                    lines.AddRange(row.Subtasks.Select(subtask => new Line("• " + subtask, SubtaskSize, Ui.Dim, SubtaskIndent)));
                }
            }
        }
        if (debug) {
            var scoreLines = new List<string> { "", $"Score {status.Score}" };
            scoreLines.AddRange(status.Log.Select(entry => $"{entry.Points:+0;-0;+0}  {entry.Text}"));
            lines.Add(new Line(string.Join("\n", scoreLines), TaskSize, Ui.Ink));
        }
        return lines;
    }
}
