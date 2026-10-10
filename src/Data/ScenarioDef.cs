using GodotDictionary = Godot.Collections.Dictionary;

namespace Scalpel.Data;

/// <summary>A skin wound the patient comes in with: a polyline in site uv.</summary>
public sealed record WoundSpec(WoundKind Kind, Vector2[] Points, float Depth, float Held);

/// <summary>A burn the patient comes in with.</summary>
public sealed record BurnSpec(Vector2 Uv, float Radius);

/// <summary>Internal bleeding the patient comes in with, Depth meters under the skin.</summary>
public sealed record InternalSpec(Vector2 Uv, float Depth);

/// <summary>A scripted event, fired At seconds into the surgery.</summary>
public sealed record ScriptedEvent(float At, string Id);

/// <summary>
/// Something under the skin the scenario cares about (data/scenarios [patient] targets): a bullet, an appendix, a bone
/// to saw, fluid to drain. See <see cref="CavityTarget"/>.
/// </summary>
public sealed record TargetSpec(
    string Kind,
    string RemoveWith,
    Vector2 Uv,
    Vector2 Offset,
    float Depth,
    float Anchor,
    float Amount,
    float Refill,
    float Surge,
    bool Covered,
    float YawDegrees)
{
    public static TargetSpec FromVariant(GodotDictionary data) => new(
        data.String("kind", "bullet"),
        data.String("remove_with", "clamp"),
        data.Vector("uv", new Vector2(0.5f, 0.5f)),
        data.Vector("offset", Vector2.Zero),
        data.Float("depth", 0.05f),
        data.Float("anchor"),
        data.Float("amount", 1f),
        data.Float("refill"),
        data.Float("surge"),
        data.Bool("covered"),
        data.Float("yaw"));
}

/// <summary>
/// One objective step. Type picks the check (<see cref="ObjectiveChecks"/>), Parameters holds the step's own numbers
/// as written in the scenario ("level", "seconds", "target"...).
/// </summary>
public sealed record ObjectiveStep(string Type, string Label, bool Optional, GodotDictionary Parameters)
{
    /// <summary>Another condition that completes the step too, written as a nested step under "or".</summary>
    public ObjectiveStep? Alternative { get; init; }

    public static ObjectiveStep FromVariant(GodotDictionary data) =>
        new(data.String("type"), data.String("label"), data.Bool("optional"), data)
        {
            Alternative = data.TryGetValue("or", out var alternative)
                ? FromVariant(alternative.AsGodotDictionary())
                : null,
        };
}

/// <summary>One data/scenarios/NN_id.cfg file: metadata, the patient's starting state and the objective steps.</summary>
public sealed record ScenarioDef
{
    public required string Id { get; init; }
    public int Order { get; init; }
    public string Title { get; init; } = "";
    public int Difficulty { get; init; }
    public string Group { get; init; } = "";
    /// <summary>Teaser for the menu and lobby. Never says what to do.</summary>
    public string Description { get; init; } = "";
    /// <summary>What the patient chart says brought them in: symptoms and history only, no treatment plan.</summary>
    public string Complaint { get; init; } = "";
    public bool Hidden { get; init; }
    /// <summary>Left out of the menu and lobby while its positive flow test is broken (the cfg names the test).</summary>
    public bool Disabled { get; init; }
    public float TimeLimit { get; init; }
    public string Anesthesia { get; init; } = "";
    public string Site { get; init; } = "";
    public string Side { get; init; } = "";
    public int StartOrientation { get; init; }
    public string PatientAge { get; init; } = "adult";
    public string Environment { get; init; } = "or";
    public bool Nurse { get; init; }
    public bool DirtyStart { get; init; }
    public int PatientQuirksMin { get; init; }
    public int PatientQuirksMax { get; init; }
    public IReadOnlyList<string> PatientQuirkPool { get; init; } = [];
    public IReadOnlyList<QuirkRoll> FixedPatientQuirks { get; init; } = [];
    public IReadOnlyList<string> StartingTools { get; init; } = [];
    public IReadOnlyList<string> RandomTools { get; init; } = [];
    public int RandomToolCount { get; init; }
    public float MissingToolChance { get; init; }
    public IReadOnlyList<string> Events { get; init; } = [];
    public IReadOnlyList<ScriptedEvent> ScriptedEvents { get; init; } = [];
    /// <summary>Vitals overrides at the start (<see cref="Vitals.Apply"/>).</summary>
    public GodotDictionary StartVitals { get; init; } = [];
    /// <summary>Line already in before the surgery starts.</summary>
    public bool PreopIv { get; init; }
    /// <summary>Anesthesia already working before the surgery starts.</summary>
    public float PreopAnesthesia { get; init; }
    public IReadOnlyList<WoundSpec> Wounds { get; init; } = [];
    public IReadOnlyList<BurnSpec> Burns { get; init; } = [];
    public IReadOnlyList<InternalSpec> Internal { get; init; } = [];
    public IReadOnlyList<TargetSpec> Targets { get; init; } = [];
    public IReadOnlyList<ObjectiveStep> Steps { get; init; } = [];

    /// <summary>Reads a scenario file. The file name gives the id (03_appendectomy.cfg -> appendectomy).</summary>
    public static ScenarioDef? LoadFile(string path)
    {
        var file = new ConfigFile();
        if (file.Load(path) != Error.Ok)
        {
            GD.PushError($"Can't read scenario {path}");
            return null;
        }
        var meta = new ConfigReader(file, "scenario");
        var patient = new ConfigReader(file, "patient");
        var preop = meta.Dictionary("preop");
        return new ScenarioDef
        {
            Id = path.GetFile().GetBaseName()[3..],
            Order = meta.Int("order"),
            Title = meta.String("title"),
            Difficulty = meta.Int("difficulty"),
            Group = meta.String("group"),
            Description = meta.String("description"),
            Complaint = meta.String("complaint"),
            Hidden = meta.Bool("hidden"),
            Disabled = meta.Bool("disabled"),
            TimeLimit = meta.Float("time_limit"),
            Anesthesia = meta.String("anesthesia"),
            Site = meta.String("site"),
            Side = meta.String("side"),
            StartOrientation = meta.Int("start_orientation"),
            PatientAge = meta.String("patient_age", "adult"),
            Environment = meta.String("environment", "or"),
            Nurse = meta.Bool("nurse"),
            DirtyStart = meta.Bool("dirty_start"),
            PatientQuirksMin = meta.Int("patient_quirks_min"),
            PatientQuirksMax = meta.Int("patient_quirks_max"),
            PatientQuirkPool = meta.Strings("patient_quirk_pool"),
            FixedPatientQuirks = QuirkRoll.FromVariant(meta.Array("fixed_patient_quirks")),
            StartingTools = meta.Strings("starting_tools"),
            RandomTools = meta.Strings("random_tools"),
            RandomToolCount = meta.Int("random_tool_count"),
            MissingToolChance = meta.Float("missing_tool_chance"),
            Events = meta.Strings("events"),
            ScriptedEvents = Entries(meta.Array("scripted_events"),
                data => new ScriptedEvent(data.Float("at"), data.String("id"))),
            StartVitals = meta.Dictionary("start_vitals"),
            PreopIv = preop.Bool("iv"),
            PreopAnesthesia = preop.Float("anesthesia"),
            Wounds = Entries(patient.Array("wounds"), WoundFromVariant),
            Burns = Entries(patient.Array("burns"),
                data => new BurnSpec(data.Vector("uv", Vector2.Zero), data.Float("radius"))),
            Internal = Entries(patient.Array("internal"),
                data => new InternalSpec(data.Vector("uv", Vector2.Zero), data.Float("depth", 0.05f))),
            Targets = Entries(patient.Array("targets"), TargetSpec.FromVariant),
            Steps = Entries(new ConfigReader(file, "objectives").Array("steps"), ObjectiveStep.FromVariant),
        };
    }

    private static List<T> Entries<T>(Godot.Collections.Array list, Func<GodotDictionary, T> read) =>
        [.. list.Select(entry => read(entry.AsGodotDictionary()))];

    private static WoundSpec WoundFromVariant(GodotDictionary data) => new(
        Enum.TryParse<WoundKind>(data.String("kind"), ignoreCase: true, out var kind) ? kind : WoundKind.Cut,
        [.. data["points"].AsGodotArray().Select(point => DictionaryExtensions.ToVector2(point.AsGodotArray()))],
        data.Float("depth", 0.5f),
        data.Float("held"));

    /// <summary>
    /// Picks the tools that actually spawn on the tray for this run: the starter kit plus the scenario's own tools.
    /// With a nurse, the scenario adds only what she can't fetch; the rest has to be ordered.
    /// </summary>
    public List<string> RollTools(RandomNumberGenerator rng, float extraMissingChance = 0f)
    {
        var own = StartingTools.ToList();
        var extras = RandomTools.ToList();
        for (var i = 0; i < Math.Min(RandomToolCount, RandomTools.Count); i++)
        {
            var index = rng.RandiRange(0, extras.Count - 1);
            own.Add(extras[index]);
            extras.RemoveAt(index);
        }
        if (Nurse)
        {
            own = [.. own.Where(id => Db.Tool(id) is not { Orderable: true })];
        }
        // The starter kit already covers one of each of its tools.
        foreach (var id in Db.StarterKit)
        {
            own.Remove(id);
        }
        return [.. Db.StarterKit.Concat(own).Where(_ => rng.Randf() >= MissingToolChance + extraMissingChance)];
    }

    public string StarsText => new string('★', Difficulty) + new string('☆', 5 - Difficulty);
}
