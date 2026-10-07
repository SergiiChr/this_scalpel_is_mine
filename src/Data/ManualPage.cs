using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using GodotDictionary = Godot.Collections.Dictionary;

namespace Scalpel.Data;

/// <summary>
/// One page of the in-game manual, loaded from data/manual/*.txt.
/// First line "tags: a, b" lists patient effect keys and drug ids that Divine knowledge marks the page for.
/// Second line "title: ..." is the heading. The rest is BBCode, with two additions handled by <see cref="ManualView"/>:
/// a line starting with "## " is a numbered section heading, one starting with "> " a hand written note.
/// A folder named like the page file (17_conditions.txt, 17_conditions/) holds its sub-pages, listed by title.
/// Game numbers are written as "{expression}" and worked out when the page loads, so the manual follows the game:
/// constants of any game class ("{Patient.HighPressure}"), drugs ("{drug.diazepam.Duration / 60}"), tools
/// ("{tool.heavy_saw.Delay}") and patient quirk effects ("{quirk.heart_weak.arrest_mult}", a variant joined by "_").
/// Expressions are Godot <see cref="Expression"/>s over dictionaries built from those by reflection.
/// </summary>
public sealed partial class ManualPage
{
    /// <summary>Class name -> its constants, for the classes pages have used so far.</summary>
    private static readonly Dictionary<string, GodotDictionary?> ClassConstants = [];

    public string Title { get; private init; } = "";
    public IReadOnlyList<string> Tags { get; private init; } = [];
    /// <summary>The page as written, with its "{expression}" numbers.</summary>
    public string Source { get; private init; } = "";
    public string Body { get; private init; } = "";
    public IReadOnlyList<ManualPage> Children { get; private init; } = [];

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"\b[A-Z][A-Za-z]+\b")]
    private static partial Regex ClassNamePattern();

    public static ManualPage LoadFile(string path)
    {
        var lines = FileAccess.GetFileAsString(path).Split('\n');
        var title = "";
        var tags = new List<string>();
        var start = 0;
        foreach (var line in lines.Take(2))
        {
            if (line.StartsWith("tags:", StringComparison.Ordinal))
            {
                tags = [.. line[5..].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(tag => tag.Trim())];
                start++;
            }
            else if (line.StartsWith("title:", StringComparison.Ordinal))
            {
                title = line[6..].Trim();
                start++;
            }
        }
        var source = string.Join('\n', lines.Skip(start)).Trim();
        var dir = path.GetBaseName();
        var children = DirAccess.DirExistsAbsolute(dir)
            ? DirAccess.GetFilesAt(dir).Where(file => file.GetExtension() == "txt")
                .Select(file => LoadFile(dir.PathJoin(file))).OrderBy(child => child.Title, StringComparer.Ordinal)
                .ToList()
            : [];
        return new ManualPage
        {
            Title = title,
            Tags = tags,
            Source = source,
            Body = FillNumbers(source, path),
            Children = children,
        };
    }

    /// <summary>Replaces every "{expression}" with its value. One that can't be worked out is reported and left as
    /// written.</summary>
    public static string FillNumbers(string text, string where)
    {
        var filled = text;
        foreach (Match found in NumberPattern().Matches(text))
        {
            var value = Evaluate(found.Groups[1].Value);
            if (value is null)
            {
                GD.PushError($"Manual: can't work out {{{found.Groups[1].Value}}} in {where}");
                continue;
            }
            filled = filled.Replace(found.Value, Written(value.Value));
        }
        return filled;
    }

    /// <summary>The value of one expression, or null when it can't be worked out.</summary>
    public static Variant? Evaluate(string expression)
    {
        var names = new List<string> { "drug", "tool", "quirk" };
        var inputs = new Godot.Collections.Array
        {
            ById(Db.Drugs), ById(Db.Tools), QuirkEffects(),
        };
        foreach (Match word in ClassNamePattern().Matches(expression))
        {
            if (!names.Contains(word.Value) && ConstantsOf(word.Value) is { } constants)
            {
                names.Add(word.Value);
                inputs.Add(constants);
            }
        }
        var parsed = new Expression();
        if (parsed.Parse(expression, [.. names]) != Error.Ok)
        {
            return null;
        }
        var value = parsed.Execute(inputs, null, showError: false);
        return parsed.HasExecuteFailed() ? null : value;
    }

    /// <summary>The page or one of its sub-pages is tagged with one of the keys.</summary>
    public bool Matches(IReadOnlyCollection<string> keys) =>
        Tags.Any(keys.Contains) || Children.Any(child => child.Matches(keys));

    private static string Written(Variant value)
    {
        if (value.VariantType is not (Variant.Type.Float or Variant.Type.Int))
        {
            return value.AsString();
        }
        var number = value.AsDouble();
        return Math.Abs(number - Math.Round(number)) < 0.001
            ? Math.Round(number).ToString("0", CultureInfo.InvariantCulture)
            : number.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static GodotDictionary? ConstantsOf(string typeName)
    {
        if (!ClassConstants.TryGetValue(typeName, out var constants))
        {
            var type = typeof(ManualPage).Assembly.GetTypes().FirstOrDefault(t => t.Name == typeName);
            constants = type is null ? null : StaticValues(type);
            ClassConstants[typeName] = constants;
        }
        return constants;
    }

    private static GodotDictionary StaticValues(Type type)
    {
        var values = new GodotDictionary();
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.IsLiteral || field.IsInitOnly)
            {
                values[field.Name] = ToVariant(field.GetValue(null));
            }
        }
        return values;
    }

    private static GodotDictionary ById<T>(IReadOnlyDictionary<string, T> table)
    {
        var values = new GodotDictionary();
        foreach (var (id, entry) in table)
        {
            values[id] = ToVariant(entry);
        }
        return values;
    }

    /// <summary>Numbers and text as they are, collections and objects as dictionaries of their public members.
    /// </summary>
    private static Variant ToVariant(object? value) => value switch
    {
        null => default,
        float number => number,
        double number => number,
        int number => number,
        bool flag => flag,
        string text => text,
        Color color => color,
        IDictionary dictionary => DictionaryOf(dictionary),
        IEnumerable list => new Godot.Collections.Array(list.Cast<object>().Select(ToVariant)),
        _ => MembersOf(value),
    };

    private static GodotDictionary DictionaryOf(IDictionary dictionary)
    {
        var values = new GodotDictionary();
        foreach (DictionaryEntry entry in dictionary)
        {
            values[entry.Key.ToString() ?? ""] = ToVariant(entry.Value);
        }
        return values;
    }

    private static GodotDictionary MembersOf(object value)
    {
        var values = new GodotDictionary();
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0)
            {
                values[property.Name] = ToVariant(property.GetValue(value));
            }
        }
        foreach (var field in value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            values[field.Name] = ToVariant(field.GetValue(value));
        }
        return values;
    }

    /// <summary>Patient quirk effects by quirk id, or id_variant for each variant, numbers as numbers.</summary>
    private static GodotDictionary QuirkEffects()
    {
        var values = new GodotDictionary();
        foreach (var quirk in Db.PatientQuirks.Values)
        {
            foreach (var variant in quirk.Variants.Count > 0 ? quirk.Variants : [""])
            {
                var effects = new GodotDictionary();
                foreach (var (key, text) in quirk.Effects(variant))
                {
                    effects[key] = Modifiers.Number(text) is { } number ? number : text;
                }
                values[quirk.Id + (variant.Length > 0 ? "_" + variant : "")] = effects;
            }
        }
        return values;
    }
}
