namespace Scalpel.UI;

/// <summary>Debug mode only: the steps the game checks and the latest scored actions, top right. Normal play shows
/// neither.</summary>
public partial class ObjectivesPanel : VBoxContainer
{
    public ObjectivesPanel() => AddThemeConstantOverride("separation", 4);

    /// <summary>Shows the host's last status. Labels only change when their text or state does.</summary>
    public void Refresh(SurgeryStatus status)
    {
        var scoreLines = new List<string> { "", $"Score {status.Score}" };
        scoreLines.AddRange(status.Log.Select(entry => $"{entry.Points:+0;-0;+0}  {entry.Text}"));
        var rows = status.Objectives.Append(new ObjectiveView(string.Join("\n", scoreLines), false, false, false)).ToList();
        while (GetChildCount() < rows.Count)
        {
            AddChild(Ui.Label("", 18, Ui.Ink, true));
        }
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var label = GetChild<Label>(i);
            var mark = row.Done ? "☑" : row.Current ? "▶" : "☐";
            var text = $"{mark} {row.Label}{(row.Optional ? "  (bonus)" : "")}";
            if (label.Text != text)
            {
                label.Text = text;
            }
            var color = row.Done ? Ui.Dim : row.Current ? Ui.Pip : Ui.Ink;
            // Reapplying the same override invalidates the label's layout and font shaping every frame.
            if (label.GetThemeColor("font_color") != color)
            {
                label.AddThemeColorOverride("font_color", color);
            }
        }
    }
}
