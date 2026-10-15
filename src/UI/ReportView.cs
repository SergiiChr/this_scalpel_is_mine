namespace Scalpel.UI;

/// <summary>Post-op report: pass or fail, stars, what you did and when, what happened to the patient afterwards.
/// Shown as a surgery ends and again from the main menu's history.</summary>
public static class ReportView {
    /// <summary>The report as the surgery ends, with the way on from it.</summary>
    public static Control Build(SurgeryReport report) {
        var box = Content(report);
        var buttons = Ui.HBox(12);
        if (Net.Instance.IsHost) {
            buttons.AddChild(Ui.Button("New run (back to lobby)", Net.Instance.ReturnToLobby));
        }
        buttons.AddChild(Ui.Button("Main menu", Net.Instance.BackToMenu));
        box.AddChild(buttons);
        return Ui.CenteredPanel(box, new Vector2(1000f, 800f));
    }

    /// <summary>The report itself, without buttons.</summary>
    public static VBoxContainer Content(SurgeryReport report) {
        var title = Db.Scenario(report.Scenario)?.Title ?? report.Scenario;
        var box = Ui.VBox(10);
        box.AddChild(Ui.Label($"POST-OP REPORT: {title.ToUpperInvariant()}", 30, Ui.Pip));
        var verdict = report.Success ? "PATIENT SURVIVED" : $"FAILED: {report.Reason}";
        box.AddChild(Ui.Label(verdict, 26, report.Success ? Ui.Good : Ui.Alert));
        var stars = new string('★', report.Stars) + new string('☆', 3 - report.Stars);
        var time = (int)report.Time;
        box.AddChild(Ui.Label($"{stars}   score {report.Score}   time {time / 60}:{time % 60:00}", 24));

        var details = Ui.VBox(4);
        details.AddChild(Ui.Label("During the operation", 20, Ui.Pip));
        foreach (var line in report.Events) {
            details.AddChild(Line(line.Text + (line.Count > 1 ? $"  x{line.Count}" : ""), line.Points));
        }
        details.AddChild(Ui.Label("Timeline", 20, Ui.Pip));
        // Time, points and what happened in columns: the font's digits aren't all as wide.
        var timeline = new GridContainer { Columns = 3 };
        timeline.AddThemeConstantOverride("h_separation", 16);
        foreach (var entry in report.Timeline) {
            var color = entry.Good ? Ui.Good : Ui.Alert;
            var at = (int)entry.Time;
            var count = entry.Count > 1 ? $"  x{entry.Count}" : "";
            foreach (var number in (string[])[$"{at / 60}:{at % 60:00}", entry.Points != 0 ? $"{entry.Points:+0;-0}" : ""]) {
                var cell = Ui.Label(number, 16, color);
                cell.HorizontalAlignment = HorizontalAlignment.Right;
                timeline.AddChild(cell);
            }
            timeline.AddChild(Ui.Label(entry.Text + count, 16, color, true));
        }
        details.AddChild(timeline);
        if (report.Consequences.Count > 0) {
            details.AddChild(Ui.Label("Afterwards", 20, Ui.Pip));
            foreach (var line in report.Consequences) {
                details.AddChild(Line(line.Text, line.Points));
            }
        }
        details.AddChild(Ui.Label("The patient had", 20, Ui.Pip));
        if (report.PatientQuirks.Count == 0) {
            details.AddChild(Ui.Label("Nothing unusual. Lucky.", 16, Ui.Dim));
        }
        foreach (var roll in report.PatientQuirks) {
            if (Db.Quirk(QuirkKind.Patient, roll.Id) is { } quirk) {
                details.AddChild(Ui.QuirkLine(quirk, roll.Variant, false));
            }
        }
        box.AddChild(Ui.Scroll(details));
        return box;
    }

    private static Label Line(string text, int points) =>
        Ui.Label($"{points,4:+0;-0;+0}   {text}", 16, points >= 0 ? Ui.Good : Ui.Alert, true);
}
