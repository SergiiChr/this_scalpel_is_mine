using GodotDictionary = Godot.Collections.Dictionary;

namespace Scalpel.Data;

/// <summary>Typed reads of the loosely typed dictionaries data files hold (scenario wounds, targets, steps).</summary>
public static class DictionaryExtensions {
    public static float Float(this GodotDictionary data, string key, float fallback = 0f) =>
        Read(data, key, static value => value.AsSingle(), fallback);

    public static int Int(this GodotDictionary data, string key, int fallback = 0) =>
        Read(data, key, static value => value.AsInt32(), fallback);

    public static bool Bool(this GodotDictionary data, string key, bool fallback = false) =>
        Read(data, key, static value => value.AsBool(), fallback);

    public static string String(this GodotDictionary data, string key, string fallback = "") =>
        Read(data, key, static value => value.AsString(), fallback);

    /// <summary>A two number list ("uv": [0.3, 0.6]) as a vector.</summary>
    public static Vector2 Vector(this GodotDictionary data, string key, Vector2 fallback) =>
        Read(data, key, static value => {
            using var pair = value.AsGodotArray();
            return ToVector2(pair);
        }, fallback);

    /// <summary>The value under <paramref name="key"/> as <paramref name="read"/> takes it, or
    /// <paramref name="fallback"/>. The key, and a string or list value, go through Variants on the native heap, each
    /// held by a tracked wrapper with a finalizer; disposed here at once, since objective checks read their parameters
    /// every frame, and thousands of wrappers left to the runtime make one long collection.</summary>
    private static T Read<T>(GodotDictionary data, string key, Func<Variant, T> read, T fallback) {
        using var name = Variant.CreateFrom(key);
        if (!data.TryGetValue(name, out var value)) {
            return fallback;
        }
        using (value) {
            return read(value);
        }
    }

    public static Vector2 ToVector2(Godot.Collections.Array pair) => new(pair[0].AsSingle(), pair[1].AsSingle());
}
