using GodotDictionary = Godot.Collections.Dictionary;

namespace Scalpel.Data;

/// <summary>Typed reads of the loosely typed dictionaries data files hold (scenario wounds, targets, steps).</summary>
public static class DictionaryExtensions
{
    public static float Float(this GodotDictionary data, string key, float fallback = 0f) =>
        data.TryGetValue(key, out var value) ? value.AsSingle() : fallback;

    public static int Int(this GodotDictionary data, string key, int fallback = 0) =>
        data.TryGetValue(key, out var value) ? value.AsInt32() : fallback;

    public static bool Bool(this GodotDictionary data, string key, bool fallback = false) =>
        data.TryGetValue(key, out var value) ? value.AsBool() : fallback;

    public static string String(this GodotDictionary data, string key, string fallback = "") =>
        data.TryGetValue(key, out var value) ? value.AsString() : fallback;

    /// <summary>A two number list ("uv": [0.3, 0.6]) as a vector.</summary>
    public static Vector2 Vector(this GodotDictionary data, string key, Vector2 fallback) =>
        data.TryGetValue(key, out var value) ? ToVector2(value.AsGodotArray()) : fallback;

    public static Vector2 ToVector2(Godot.Collections.Array pair) => new(pair[0].AsSingle(), pair[1].AsSingle());
}
