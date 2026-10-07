using GodotDictionary = Godot.Collections.Dictionary;

namespace Scalpel.Core;

/// <summary>
/// Every file in res://data, loaded once on first use. Game code reads definitions from here and never parses files
/// itself.
/// </summary>
public static class Db
{
    private const string ScenarioDir = "res://data/scenarios";
    private const string ManualDir = "res://data/manual";

    public static readonly IReadOnlyDictionary<string, QuirkDef> PatientQuirks =
        QuirkDef.ParseSheet("res://data/quirks/patient_quirks.md", QuirkKind.Patient);
    public static readonly IReadOnlyDictionary<string, QuirkDef> SurgeonQuirks =
        QuirkDef.ParseSheet("res://data/quirks/surgeon_quirks.md", QuirkKind.Surgeon);
    public static readonly IReadOnlyDictionary<string, ToolDef> Tools =
        ConfigReader.Sections(LoadConfig("res://data/tools.cfg")).Select(ToolDef.FromConfig).ToDictionary(t => t.Id);
    public static readonly IReadOnlyDictionary<string, DrugDef> Drugs =
        ConfigReader.Sections(LoadConfig("res://data/drugs.cfg")).Select(DrugDef.FromConfig).ToDictionary(d => d.Id);
    public static readonly ConfigFile Dialogue = LoadConfig("res://data/dialogue/patient_lines.cfg");
    public static readonly ConfigFile Events = LoadConfig("res://data/events.cfg");
    public static readonly ConfigFile Scoring = LoadConfig("res://data/scoring.cfg");
    public static readonly ConfigFile Consequences = LoadConfig("res://data/consequences.cfg");
    public static readonly ConfigFile Audio = LoadConfig("res://data/audio.cfg");
    public static readonly ConfigFile RunModifiers = LoadConfig("res://data/run_modifiers.cfg");
    /// <summary>Tool ids every surgery starts with, see data/starter_kit.cfg.</summary>
    public static readonly IReadOnlyList<string> StarterKit =
        new ConfigReader(LoadConfig("res://data/starter_kit.cfg"), "starter_kit").Strings("tools");
    /// <summary>Surgical sites on the patient body, see data/patient_sites.json.</summary>
    public static readonly GodotDictionary PatientSites = LoadJson("res://data/patient_sites.json");
    /// <summary>How each tool model's grip is fitted to the glove, per hand, see <see cref="SurgeonHand"/> and the
    /// grip fitting tool in tests/Support.</summary>
    public static readonly GodotDictionary GripFits = LoadJson("res://data/grips.json");

    private static readonly List<ScenarioDef> AllScenarios = LoadScenarios();

    /// <summary>Scenarios offered to play, in menu order.</summary>
    public static readonly IReadOnlyList<ScenarioDef> Scenarios = [.. AllScenarios.Where(s => !s.Disabled)];
    /// <summary>Scenarios with disabled=true: not offered to play, still found by <see cref="Scenario"/> for tests.
    /// </summary>
    public static readonly IReadOnlyList<ScenarioDef> DisabledScenarios = [.. AllScenarios.Where(s => s.Disabled)];

    public static readonly IReadOnlyList<ManualPage> Manual =
        [.. ListFiles(ManualDir, "txt").Select(file => ManualPage.LoadFile(ManualDir.PathJoin(file)))];

    public static ScenarioDef? Scenario(string id) => AllScenarios.Find(s => s.Id == id);

    public static ToolDef? Tool(string id) => Tools.GetValueOrDefault(id);

    public static DrugDef? Drug(string id) => Drugs.GetValueOrDefault(id);

    public static QuirkDef? Quirk(QuirkKind kind, string id) =>
        (kind == QuirkKind.Patient ? PatientQuirks : SurgeonQuirks).GetValueOrDefault(id);

    /// <summary>The site's entry in data/patient_sites.json, empty for an unknown site.</summary>
    public static GodotDictionary Site(string site) =>
        PatientSites.TryGetValue(site, out var entry) ? entry.AsGodotDictionary() : [];

    /// <summary>How a hand's grip is fitted to this tool's model.</summary>
    public static Surgeons.GripFit GripFit(ToolDef def, int hand) =>
        GripFits.TryGetValue(def.ModelName, out var fits)
        && fits.AsGodotDictionary().TryGetValue(hand == 0 ? "left" : "right", out var fit)
            ? Surgeons.GripFit.FromDictionary(fit.AsGodotDictionary())
            : Surgeons.GripFit.None;

    /// <summary>Combined effects of the given run modifier ids.</summary>
    public static Modifiers RunModifierEffects(IEnumerable<string> ids)
    {
        var modifiers = new Modifiers();
        foreach (var id in ids)
        {
            modifiers.Add(Modifiers.ParseEffects(new ConfigReader(RunModifiers, id).String("effects")));
        }
        return modifiers;
    }

    private static List<ScenarioDef> LoadScenarios() =>
        [.. ListFiles(ScenarioDir, "cfg")
            .Select(file => ScenarioDef.LoadFile(ScenarioDir.PathJoin(file)))
            .OfType<ScenarioDef>()
            .OrderBy(s => s.Order)];

    private static ConfigFile LoadConfig(string path)
    {
        var file = new ConfigFile();
        var error = file.Load(path);
        if (error != Error.Ok)
        {
            GD.PushError($"Can't read {path} ({error})");
        }
        return file;
    }

    private static GodotDictionary LoadJson(string path)
    {
        if (!FileAccess.FileExists(path))
        {
            return [];
        }
        var parsed = Json.ParseString(FileAccess.GetFileAsString(path));
        return parsed.VariantType == Variant.Type.Dictionary ? parsed.AsGodotDictionary() : [];
    }

    private static IEnumerable<string> ListFiles(string dir, string extension) =>
        DirAccess.GetFilesAt(dir).Where(file => file.GetExtension() == extension).Order(StringComparer.Ordinal);
}
