namespace Scalpel.Core;

/// <summary>One input action the game reads, with its default key or mouse button.</summary>
public sealed record InputActionDef(string Action, string Label, Key Key = Key.None, MouseButton Mouse = MouseButton.None)
{
    /// <summary>The default binding in the settings file's encoding.</summary>
    public string DefaultCode => Key != Key.None ? $"key:{(long)Key}" : $"mouse:{(long)Mouse}";
}

/// <summary>
/// Every input action the game reads, with its default binding. <see cref="Settings"/> registers these at startup and
/// stores player overrides on top.
/// </summary>
public static class InputActions
{
    // Before Defaults, which reads it: static fields are set in the order they're declared.
    private static readonly StringName[] BeltSlots = ["belt_1", "belt_2", "belt_3", "belt_4"];

    // StringNames, not strings: input is read every frame, and a string passed to the engine becomes a new StringName
    // each time, garbage the runtime has to track and collect.
    public static readonly StringName MoveForward = "move_forward";
    public static readonly StringName MoveBack = "move_back";
    public static readonly StringName MoveLeft = "move_left";
    public static readonly StringName MoveRight = "move_right";
    public static readonly StringName MoveLeftHand = "move_left_hand";
    public static readonly StringName MoveRightHand = "move_right_hand";
    public static readonly StringName UseTool = "use_tool";
    public static readonly StringName Grab = "grab";
    public static readonly StringName LevelUp = "level_up";
    public static readonly StringName LevelDown = "level_down";
    public static readonly StringName Zoom = "zoom";
    public static readonly StringName Inspect = "inspect";
    public static readonly StringName Interact = "interact";
    public static readonly StringName Lift = "lift";
    public static readonly StringName Crouch = "crouch";
    public static readonly StringName Steady = "steady";
    public static readonly StringName AimTool = "aim_tool";
    public static readonly StringName TwistLeft = "twist_left";
    public static readonly StringName TwistRight = "twist_right";
    public static readonly StringName Drink = "drink";
    public static readonly StringName Pause = "pause";

    /// <summary>Order here is the order in the settings menu and the on-screen hint.</summary>
    public static readonly IReadOnlyList<InputActionDef> Defaults =
    [
        new(MoveForward, "Move forward", Key.W),
        new(MoveBack, "Move back", Key.S),
        new(MoveLeft, "Move left", Key.A),
        new(MoveRight, "Move right", Key.D),
        new(MoveLeftHand, "Move left hand (hold)", Key.Q),
        new(MoveRightHand, "Move right hand (hold)", Key.E),
        new(UseTool, "Use tool (hold)", Mouse: MouseButton.Left),
        new(Grab, "Pick up / put down", Mouse: MouseButton.Right),
        new(LevelUp, "More effort", Mouse: MouseButton.WheelUp),
        new(LevelDown, "Less effort", Mouse: MouseButton.WheelDown),
        new(Zoom, "Zoom (2 steps)", Key.Shift),
        new(Inspect, "Look at held tool (hold)", Key.X),
        new(Interact, "Interact", Key.F),
        new(Lift, "Lift hand over (hold)", Key.Alt),
        new(Crouch, "Crouch (hold)", Key.Ctrl),
        new(Steady, "Hold breath (hold)", Key.Space),
        new(AimTool, "Turn tool with the mouse (hold)", Mouse: MouseButton.Middle),
        new(TwistLeft, "Rotate tool left", Key.C),
        new(TwistRight, "Rotate tool right", Key.V),
        new(Drink, "Drink / wear", Key.H),
        new(BeltSlot(1), "Belt slot 1", Key.Key1),
        new(BeltSlot(2), "Belt slot 2", Key.Key2),
        new(BeltSlot(3), "Belt slot 3", Key.Key3),
        new(BeltSlot(4), "Belt slot 4", Key.Key4),
        new(Pause, "Pause", Key.Escape),
    ];

    /// <summary>The action of belt slot 1-4.</summary>
    public static StringName BeltSlot(int slot) => BeltSlots[slot - 1];

    /// <summary>"key:87" / "mouse:1" for an event. Strings keep the settings file readable.</summary>
    public static string Encode(InputEvent inputEvent) => inputEvent switch
    {
        InputEventKey key => $"key:{(long)key.PhysicalKeycode}",
        InputEventMouseButton mouse => $"mouse:{(long)mouse.ButtonIndex}",
        _ => "",
    };

    /// <summary>The event an encoded binding stands for, null for a malformed one.</summary>
    public static InputEvent? Decode(string code)
    {
        var parts = code.Split(':');
        if (parts.Length != 2 || !long.TryParse(parts[1], out var value))
        {
            return null;
        }
        return parts[0] == "key"
            ? new InputEventKey { PhysicalKeycode = (Key)value }
            : new InputEventMouseButton { ButtonIndex = (MouseButton)value };
    }

    public static string LabelFor(string action) =>
        Defaults.FirstOrDefault(entry => entry.Action == action)?.Label ?? action;

    private static readonly Dictionary<StringName, string> BindingTexts = [];

    /// <summary>Human readable binding, e.g. "E" or "Mouse 1". Kept until the bindings change
    /// (<see cref="BindingsChanged"/>): the on-screen hint asks for it every frame.</summary>
    public static string BindingText(StringName action)
    {
        if (!BindingTexts.TryGetValue(action, out var text))
        {
            text = ReadBinding(action);
            BindingTexts[action] = text;
        }
        return text;
    }

    /// <summary>The input map changed: binding texts are read afresh.</summary>
    public static void BindingsChanged() => BindingTexts.Clear();

    private static string ReadBinding(StringName action)
    {
        var events = InputMap.HasAction(action) ? InputMap.ActionGetEvents(action) : [];
        if (events.Count == 0)
        {
            return "-";
        }
        if (events[0] is InputEventKey key)
        {
            return OS.GetKeycodeString(key.PhysicalKeycode);
        }
        var button = ((InputEventMouseButton)events[0]).ButtonIndex;
        return button switch
        {
            MouseButton.Left => "LMB",
            MouseButton.Right => "RMB",
            MouseButton.Middle => "MMB",
            MouseButton.WheelUp => "Wheel up",
            MouseButton.WheelDown => "Wheel down",
            _ => $"Mouse {(long)button}",
        };
    }
}
