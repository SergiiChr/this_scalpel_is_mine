using System.Globalization;
using GodotDictionary = Godot.Collections.Dictionary;

namespace Scalpel.Core;

/// <summary>
/// Player settings: display, audio, mouse and key bindings, saved to user://settings.cfg. An autoload, so the audio
/// buses, saved settings and key bindings are in place before the first scene; the settings themselves are static.
/// </summary>
public partial class Settings : Node
{
    private const string Path = "user://settings.cfg";

    public static readonly IReadOnlyList<Vector2I> Resolutions =
    [
        new(1280, 720), new(1600, 900), new(1920, 1080), new(2560, 1440), new(3840, 2160),
    ];
    public static readonly IReadOnlyList<string> Buses = ["Master", "SFX", "Voice", "Music"];

    public static Vector2I Resolution { get; set; } = new(1920, 1080);
    public static bool Fullscreen { get; set; }
    public static float MouseSensitivity { get; set; } = 1f;
    public static Dictionary<string, float> Volumes { get; } = new()
    {
        ["Master"] = 0.8f,
        ["SFX"] = 1f,
        ["Voice"] = 1f,
        ["Music"] = 0.6f,
    };
    /// <summary>Debug mode: shows what the game tracks behind the scenes (objectives, scored actions).
    /// Off in normal play, where finding out what to do is the game.</summary>
    public static bool Debug { get; set; }

    /// <summary>Action -> encoded binding (<see cref="InputActions.Encode"/>), only for actions the player changed.
    /// </summary>
    private static readonly Dictionary<string, string> Bindings = [];

    public override void _EnterTree()
    {
        // Game text writes numbers one way (0.5, not 0,5), whatever the player's locale.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    /// <summary>The game is quitting. Wrappers of Godot objects nothing uses any more are let go now: freed by the
    /// garbage collector later, after the C# runtime has shut down, they abort the engine on its way out.</summary>
    public override void _ExitTree()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    public override void _Ready()
    {
        CreateBuses();
        Load();
        Apply();
    }

    public static void Load()
    {
        var file = new ConfigFile();
        if (file.Load(Path) != Error.Ok)
        {
            return;
        }
        Resolution = file.GetValue("display", "resolution", Resolution).AsVector2I();
        Fullscreen = file.GetValue("display", "fullscreen", Fullscreen).AsBool();
        MouseSensitivity = file.GetValue("input", "mouse_sensitivity", MouseSensitivity).AsSingle();
        Debug = file.GetValue("debug", "enabled", Debug).AsBool();
        Bindings.Clear();
        foreach (var (action, code) in file.GetValue("input", "bindings", new GodotDictionary()).AsGodotDictionary())
        {
            Bindings[action.AsString()] = code.AsString();
        }
        foreach (var bus in Buses)
        {
            Volumes[bus] = file.GetValue("audio", bus, Volumes[bus]).AsSingle();
        }
    }

    public static void Save()
    {
        var file = new ConfigFile();
        file.SetValue("display", "resolution", Resolution);
        file.SetValue("display", "fullscreen", Fullscreen);
        file.SetValue("input", "mouse_sensitivity", MouseSensitivity);
        var bindings = new GodotDictionary();
        foreach (var (action, code) in Bindings)
        {
            bindings[action] = code;
        }
        file.SetValue("input", "bindings", bindings);
        file.SetValue("debug", "enabled", Debug);
        foreach (var bus in Buses)
        {
            file.SetValue("audio", bus, Volumes[bus]);
        }
        file.Save(Path);
    }

    public static void Apply()
    {
        ApplyBindings();
        foreach (var bus in Buses)
        {
            AudioServer.SetBusVolumeDb(AudioServer.GetBusIndex(bus), Mathf.LinearToDb(Mathf.Max(Volumes[bus], 0.0001f)));
        }
        if (DisplayServer.GetName() == "headless")
        {
            return;
        }
        DisplayServer.WindowSetMode(Fullscreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);
        if (!Fullscreen)
        {
            DisplayServer.WindowSetSize(Resolution);
        }
    }

    public static void Rebind(string action, InputEvent inputEvent)
    {
        Bindings[action] = InputActions.Encode(inputEvent);
        ApplyBindings();
    }

    public static void ResetBindings()
    {
        Bindings.Clear();
        ApplyBindings();
    }

    private static void ApplyBindings()
    {
        foreach (var entry in InputActions.Defaults)
        {
            if (!InputMap.HasAction(entry.Action))
            {
                InputMap.AddAction(entry.Action);
            }
            InputMap.ActionEraseEvents(entry.Action);
            if (InputActions.Decode(Bindings.GetValueOrDefault(entry.Action, entry.DefaultCode)) is { } inputEvent)
            {
                InputMap.ActionAddEvent(entry.Action, inputEvent);
            }
        }
        InputActions.BindingsChanged();
    }

    private static void CreateBuses()
    {
        foreach (var bus in Buses)
        {
            if (AudioServer.GetBusIndex(bus) == -1)
            {
                AudioServer.AddBus();
                var index = AudioServer.BusCount - 1;
                AudioServer.SetBusName(index, bus);
                AudioServer.SetBusSend(index, "Master");
            }
        }
    }
}
