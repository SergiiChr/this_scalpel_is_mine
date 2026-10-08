namespace Scalpel.Data;

/// <summary>Typed reads of one section of a Godot <see cref="ConfigFile"/>, falling back to a default.</summary>
public readonly struct ConfigReader(ConfigFile file, string section)
{
    public string Section => section;

    public bool Has(string key) => file.HasSectionKey(section, key);

    public Variant Value(string key) => file.GetValue(section, key);

    public float Float(string key, float fallback = 0f) => file.GetValue(section, key, fallback).AsSingle();

    public int Int(string key, int fallback = 0) => file.GetValue(section, key, fallback).AsInt32();

    public bool Bool(string key, bool fallback = false) => file.GetValue(section, key, fallback).AsBool();

    public string String(string key, string fallback = "") => file.GetValue(section, key, fallback).AsString();

    public Color Color(string key, Color fallback) => file.GetValue(section, key, fallback).AsColor();

    public string[] Strings(string key) =>
        file.HasSectionKey(section, key) ? file.GetValue(section, key).AsStringArray() : [];

    public Godot.Collections.Array Array(string key) =>
        file.HasSectionKey(section, key) ? file.GetValue(section, key).AsGodotArray() : [];

    public Godot.Collections.Dictionary Dictionary(string key) =>
        file.HasSectionKey(section, key) ? file.GetValue(section, key).AsGodotDictionary() : [];

    /// <summary>Every section of a file, read through a reader each.</summary>
    public static IEnumerable<ConfigReader> Sections(ConfigFile file) =>
        file.GetSections().Select(section => new ConfigReader(file, section));
}
