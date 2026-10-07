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
    public const string MoveForward = "move_forward";
    public const string MoveBack = "move_back";
    public const string MoveLeft = "move_left";
    public const string MoveRight = "move_right";
    public const string MoveLeftHand = "move_left_hand";
    public const string MoveRightHand = "move_right_hand";
    public const string UseTool = "use_tool";
    public const string Grab = "grab";
    public const string LevelUp = "level_up";
    public const string LevelDown = "level_down";
    public const string Zoom = "zoom";
    public const string Inspect = "inspect";
    public const string Interact = "interact";
    public const string Lift = "lift";
    public const string Crouch = "crouch";
    public const string Steady = "steady";
    public const string AimTool = "aim_tool";
    public const string TwistLeft = "twist_left";
    public const string TwistRight = "twist_right";
    public const string Drink = "drink";
    public const string Pause = "pause";

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
    public static string BeltSlot(int slot) => $"belt_{slot}";

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

    /// <summary>Human readable binding, e.g. "E" or "Mouse 1".</summary>
    public static string BindingText(string action)
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
