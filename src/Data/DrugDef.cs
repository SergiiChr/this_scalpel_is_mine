namespace Scalpel.Data;

/// <summary>One entry of data/drugs.cfg. Field meaning is documented at the top of that file.</summary>
public sealed class DrugDef
{
    /// <summary>Leeway around the right dose (as a share of it): anything in between works like the right dose.</summary>
    public const float DoseLow = 0.7f;
    public const float DoseHigh = 1.4f;
    /// <summary>Below this share of the right dose a drug only has a faint effect and doesn't do its job (restart,
    /// antibiotic...).</summary>
    public const float DoseEffective = 0.5f;
    /// <summary>From this share on the chart calls it an overdose.</summary>
    public const float DoseOverdose = 2.5f;
    /// <summary>A direct injection (into tissue, not a vein) works this much sooner.</summary>
    public const float DirectOnset = 0.4f;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public float Onset { get; init; }
    public float Duration { get; init; }
    public IReadOnlySet<string> Flags { get; init; } = new HashSet<string>();
    public IReadOnlySet<string> DangerWith { get; init; } = new HashSet<string>();
    public string BloodType { get; init; } = "";
    /// <summary>The right dose per kg of body weight in <see cref="Unit"/>, 0 when it isn't dosed (bags, masks,
    /// drinks).</summary>
    public float Dose { get; init; }
    public string Unit { get; init; } = "mg";
    public DrugEffects Peak { get; init; } = new();

    public bool IsBlood => BloodType.Length > 0;

    public static DrugDef FromConfig(ConfigReader config) => new()
    {
        Id = config.Section,
        Name = config.String("name", config.Section.Capitalize()),
        Onset = config.Float("onset", 5f),
        Duration = config.Float("duration", 60f),
        Flags = config.Strings("flags").ToHashSet(),
        DangerWith = config.Strings("danger_with").ToHashSet(),
        BloodType = config.String("blood_type"),
        Dose = config.Float("dose"),
        Unit = config.String("unit", "mg"),
        Peak = DrugEffects.FromConfig(config),
    };

    public bool HasFlag(string flag) => Flags.Contains(flag);

    /// <summary>How strongly a dose works, from its share of the right dose. Roughly right counts as right.</summary>
    public static float DoseStrength(float share)
    {
        if (share < DoseLow)
        {
            return share / DoseLow;
        }
        return share > DoseHigh ? share / DoseHigh : 1f;
    }
}
