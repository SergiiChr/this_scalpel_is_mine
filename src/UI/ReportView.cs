namespace Scalpel.UI;

/// <summary>Post-op report: pass or fail, stars, what you did, what happened to the patient afterwards.</summary>
public static class ReportView
{
    public static Control Build(SurgeryReport report, ScenarioDef scenario)
    {
        var box = Ui.VBox(10);
        box.AddChild(Ui.Label($"POST-OP REPORT: {scenario.Title.ToUpperInvariant()}", 30, Ui.Pip));
        var verdict = report.Success ? "PATIENT SURVIVED" : $"FAILED: {report.Reason}";
        box.AddChild(Ui.Label(verdict, 26, report.Success ? Ui.Good : Ui.Alert));
        var stars = new string('★', report.Stars) + new string('☆', 3 - report.Stars);
        var time = (int)report.Time;
        box.AddChild(Ui.Label($"{stars}   score {report.Score}   time {time / 60}:{time % 60:00}", 24));

        var details = Ui.VBox(4);
        details.AddChild(Ui.Label("During the operation", 20, Ui.Pip));
        foreach (var line in report.Events)
        {
            details.AddChild(Line(line.Text + (line.Count > 1 ? $"  x{line.Count}" : ""), line.Points));
        }
        if (report.Consequences.Count > 0)
        {
            details.AddChild(Ui.Label("Afterwards", 20, Ui.Pip));
            foreach (var line in report.Consequences)
            {
                details.AddChild(Line(line.Text, line.Points));
            }
        }
        details.AddChild(Ui.Label("The patient had", 20, Ui.Pip));
        if (report.PatientQuirks.Count == 0)
        {
            details.AddChild(Ui.Label("Nothing unusual. Lucky.", 16, Ui.Dim));
        }
        foreach (var roll in report.PatientQuirks)
        {
            if (Db.Quirk(QuirkKind.Patient, roll.Id) is { } quirk)
            {
                details.AddChild(Ui.QuirkLine(quirk, roll.Variant, false));
            }
        }
        box.AddChild(Ui.Scroll(details));

        var buttons = Ui.HBox(12);
        if (Net.Instance.IsHost)
        {
            buttons.AddChild(Ui.Button("New run (back to lobby)", Net.Instance.ReturnToLobby));
        }
        buttons.AddChild(Ui.Button("Main menu", Net.Instance.BackToMenu));
        box.AddChild(buttons);
        return Ui.CenteredPanel(box, new Vector2(1000f, 800f));
    }

    private static Label Line(string text, int points) =>
        Ui.Label($"{points,4:+0;-0;+0}   {text}", 16, points >= 0 ? Ui.Good : Ui.Alert, true);
}
