using GodotDictionary = Godot.Collections.Dictionary;

namespace Scalpel.Operation;

/// <summary>A line of the report: what happened, the points it gave and how many times.</summary>
public readonly record struct ReportLine(string Text, int Points, int Count = 1);

/// <summary>
/// The post-op report, built on the host (score lines, delayed consequences, stars) and sent to every peer as it is.
/// </summary>
public sealed record SurgeryReport(
    string Scenario,
    bool Success,
    string Reason,
    int Score,
    int Stars,
    IReadOnlyList<ReportLine> Events,
    IReadOnlyList<ReportLine> Consequences,
    IReadOnlyList<QuirkRoll> PatientQuirks,
    float Time)
{
    /// <summary>Builds the report on the host. Adds the last flags (a tool left inside, no antibiotic for an open
    /// wound) first.</summary>
    public static SurgeryReport Build(Surgery surgery, bool success, string reason)
    {
        var patient = surgery.Patient;
        var scenario = surgery.Scenario;
        var rng = new RandomNumberGenerator { Seed = Net.Instance.SessionSeed + 99 };
        var retained = surgery.Tools.RetainedCount();
        if (retained > 0)
        {
            patient.AddFlag("retained_tool", retained);
            surgery.Scoring.Add("retained_item");
        }
        var opened = patient.Wounds.Any(w => w.MadeBySurgeon && w.Depth >= 0.7f);
        if (opened && !patient.Flags.ContainsKey("antibiotic"))
        {
            patient.AddFlag("no_antibiotic_open");
        }
        var total = new ConfigReader(Db.Scoring, "stars").Int("base", 50) + surgery.Scoring.Points;
        var consequences = new List<ReportLine>();
        if (success)
        {
            foreach (var config in ConfigReader.Sections(Db.Consequences))
            {
                if (patient.Flags.GetValueOrDefault(config.String("flag")) < config.Float("min", 1f))
                {
                    continue;
                }
                if (rng.Randf() > config.Float("chance", 1f))
                {
                    continue;
                }
                var points = config.Int("points");
                total += points;
                consequences.Add(new ReportLine(config.String("text").Replace("{site}", scenario.Site.Replace('_', ' '), StringComparison.Ordinal), points));
                if (config.Bool("stop"))
                {
                    break;
                }
            }
            if (scenario.TimeLimit > 0f)
            {
                var spare = scenario.TimeLimit - surgery.Elapsed;
                var bonus = (int)(spare / 20f) * new ConfigReader(Db.Scoring, "time_bonus").Int("points", 1);
                total += bonus;
                consequences.Add(new ReportLine($"Finished with {(int)spare} s to spare.", bonus));
            }
        }
        total = (int)(total * surgery.RunMods.Mult("score_mult"));
        var events = surgery.Scoring.Entries.Values
            .Select(entry => new ReportLine(entry.Text, entry.Points, entry.Count))
            .OrderByDescending(line => Math.Abs(line.Points))
            .ToList();
        return new SurgeryReport(scenario.Id, success, reason, total, Operation.Scoring.StarsFor(total, success), events,
            consequences, patient.Rolls, surgery.Elapsed);
    }

    public GodotDictionary ToVariant() => new()
    {
        ["scenario"] = Scenario,
        ["success"] = Success,
        ["reason"] = Reason,
        ["score"] = Score,
        ["stars"] = Stars,
        ["events"] = Lines(Events),
        ["consequences"] = Lines(Consequences),
        ["patient_quirks"] = QuirkRoll.ToVariant(PatientQuirks),
        ["time"] = Time,
    };

    public static SurgeryReport FromVariant(GodotDictionary data) => new(
        data.String("scenario"),
        data.Bool("success"),
        data.String("reason"),
        data.Int("score"),
        data.Int("stars"),
        Lines(data["events"].AsGodotArray()),
        Lines(data["consequences"].AsGodotArray()),
        QuirkRoll.FromVariant(data["patient_quirks"].AsGodotArray()),
        data.Float("time"));

    private static Godot.Collections.Array Lines(IEnumerable<ReportLine> lines) =>
        [.. lines.Select(line => (Variant)new Godot.Collections.Array { line.Text, line.Points, line.Count })];

    private static List<ReportLine> Lines(Godot.Collections.Array lines) =>
        [.. lines.Select(line => line.AsGodotArray()).Select(line => new ReportLine(line[0].AsString(), line[1].AsInt32(), line[2].AsInt32()))];
}
