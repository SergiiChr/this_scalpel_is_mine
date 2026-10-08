namespace Scalpel.Data;

/// <summary>Whose quirk it is: the patient's or a surgeon's.</summary>
public enum QuirkKind { Patient, Surgeon }

/// <summary>A rolled quirk. Plain id strings, so rolls travel over RPC as they are.</summary>
public readonly record struct QuirkRoll(string Id, string Variant)
{
    public Godot.Collections.Dictionary ToVariant() => new() { ["id"] = Id, ["variant"] = Variant };

    public static QuirkRoll FromVariant(Variant data)
    {
        var dictionary = data.AsGodotDictionary();
        return new QuirkRoll(dictionary["id"].AsString(), dictionary["variant"].AsString());
    }

    public static Godot.Collections.Array ToVariant(IEnumerable<QuirkRoll> rolls) =>
        [.. rolls.Select(roll => (Variant)roll.ToVariant())];

    public static List<QuirkRoll> FromVariant(Godot.Collections.Array rolls) => [.. rolls.Select(FromVariant)];
}

/// <summary>
/// One quirk parsed from the markdown sheets in data/quirks: "## id" starts an entry, "- key: value" lines fill it,
/// everything else is ignored. Any field can be overridden per variant with a "field.variant" key, see
/// <see cref="Text"/>.
/// </summary>
public sealed class QuirkDef(string id, QuirkKind kind)
{
    private readonly Dictionary<string, string> _fields = [];

    public string Id { get; } = id;
    public QuirkKind Kind { get; } = kind;
    public string IconPath { get; private set; } = "";
    public IReadOnlyList<string> Variants { get; private set; } = [];

    /// <summary>Reads a quirk sheet into id -> quirk.</summary>
    public static Dictionary<string, QuirkDef> ParseSheet(string path, QuirkKind kind)
    {
        var quirks = new Dictionary<string, QuirkDef>();
        QuirkDef? current = null;
        foreach (var raw in FileAccess.GetFileAsString(path).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                current = new QuirkDef(line[3..].Trim(), kind);
                quirks[current.Id] = current;
            }
            else if (current is not null && line.StartsWith("- ", StringComparison.Ordinal) && line.Contains(':'))
            {
                var separator = line.IndexOf(':');
                current.SetField(line[2..separator].Trim(), line[(separator + 1)..].Trim());
            }
        }
        foreach (var quirk in quirks.Values)
        {
            quirk.ResolveIcon(path.GetBaseDir());
        }
        return quirks;
    }

    private void SetField(string key, string value)
    {
        if (key == "variants")
        {
            Variants = [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim())];
        }
        else
        {
            _fields[key] = value;
        }
    }

    /// <summary>Field value for a variant, falling back to the shared value.</summary>
    public string Text(string field, string variant = "") =>
        _fields.GetValueOrDefault($"{field}.{variant}") ?? _fields.GetValueOrDefault(field) ?? "";

    public string DisplayName(string variant = "")
    {
        var name = Text("name", variant);
        return variant.Length == 0 ? name : $"{name} ({variant.Capitalize()})";
    }

    public string Polarity(string variant = "") => Text("polarity", variant);

    public bool IsExclusive => Text("exclusive") == "true";

    public bool IsRedHerring => Text("red_herring") == "true";

    /// <summary>Quirks with a sites list only roll where the surgery happens on one of them.</summary>
    public bool FitsSite(string site)
    {
        var sites = Text("sites");
        return sites.Length == 0 || sites.Split(',').Select(s => s.Trim()).Contains(site);
    }

    public Dictionary<string, string> Effects(string variant = "") => Modifiers.ParseEffects(Text("effects", variant));

    public Texture2D? Icon() => ResourceLoader.Exists(IconPath) ? GD.Load<Texture2D>(IconPath) : null;

    /// <summary>Codex/save key, unique across patient and surgeon quirks.</summary>
    public string UnlockKey => $"{(Kind == QuirkKind.Patient ? "patient" : "surgeon")}:{Id}";

    /// <summary>Turns the markdown link "[x.svg](../../assets/x.svg)" into a res:// path relative to the sheet.</summary>
    private void ResolveIcon(string sheetDir)
    {
        var link = Text("icon");
        var start = link.IndexOf('(');
        var end = link.LastIndexOf(')');
        if (start != -1 && end > start)
        {
            IconPath = sheetDir.PathJoin(link.Substring(start + 1, end - start - 1)).SimplifyPath();
        }
    }
}
