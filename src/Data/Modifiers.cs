using System.Globalization;

namespace Scalpel.Data;

/// <summary>
/// Effect values stacked from quirks and run modifiers ("key=value, key=value" in the data files).
/// Keys ending in "_mult" multiply, other numbers add up, text values collect into lists ("a|b").
/// </summary>
public sealed class Modifiers
{
    private readonly Dictionary<string, float> _numbers = [];
    private readonly Dictionary<string, List<string>> _lists = [];

    /// <summary>"key=value, key=value" into key -> value as written. Malformed pairs are skipped.</summary>
    public static Dictionary<string, string> ParseEffects(string text)
    {
        var effects = new Dictionary<string, string>();
        foreach (var pair in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=');
            if (parts.Length == 2)
            {
                effects[parts[0].Trim()] = parts[1].Trim();
            }
        }
        return effects;
    }

    /// <summary>A written effect value as a number, or null for text.</summary>
    public static float? Number(string value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    /// <summary>Stacks the effects of quirk rolls against a quirk table.</summary>
    public static Modifiers FromRolls(IEnumerable<QuirkRoll> rolls, IReadOnlyDictionary<string, QuirkDef> table)
    {
        var modifiers = new Modifiers();
        foreach (var roll in rolls)
        {
            if (table.TryGetValue(roll.Id, out var quirk))
            {
                modifiers.Add(quirk.Effects(roll.Variant));
            }
        }
        return modifiers;
    }

    public void Add(IReadOnlyDictionary<string, string> effects)
    {
        foreach (var (key, value) in effects)
        {
            if (Number(value) is { } number)
            {
                _numbers[key] = key.EndsWith("_mult", StringComparison.Ordinal) ? Mult(key) * number : Num(key) + number;
            }
            else
            {
                if (!_lists.TryGetValue(key, out var list))
                {
                    list = [];
                    _lists[key] = list;
                }
                list.AddRange(value.Split('|', StringSplitOptions.RemoveEmptyEntries));
            }
        }
    }

    /// <summary>Every effect set, numbers and lists.</summary>
    public IEnumerable<string> Keys => _numbers.Keys.Concat(_lists.Keys);

    public float Num(string key, float fallback = 0f) => _numbers.GetValueOrDefault(key, fallback);

    public float Mult(string key) => Num(key, 1f);

    public bool Flag(string key) => Num(key) > 0f;

    public IReadOnlyList<string> List(string key) => _lists.TryGetValue(key, out var list) ? list : [];
}
