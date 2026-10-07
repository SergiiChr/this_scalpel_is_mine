namespace Scalpel.Tests.Support;

/// <summary>
/// A player's keys, buttons and mouse, sent through the engine's input the way the hardware's are. A test never calls
/// an input handler itself: the game then sees each press at the same point of the frame as a player's (dispatched
/// before the next frame's physics), so a test catches what a player would see. Everything sent reaches the game when
/// the engine next dispatches input: await <see cref="Delivered"/> before counting physics frames for what it did.
/// </summary>
public static class PlayerInput
{
    /// <summary>Presses (with <paramref name="pressed"/> false, lets go of) an input action, as its key or mouse button
    /// does. Actions rather than raw mouse buttons: nothing else in the window catches them.</summary>
    public static void Action(string name, bool pressed = true) =>
        Input.ParseInputEvent(new InputEventAction { Action = name, Pressed = pressed });

    /// <summary>Presses and lets go of an action, like a click.</summary>
    public static void Tap(string name)
    {
        Action(name);
        Action(name, false);
    }

    /// <summary>Presses the keyboard key bound to an action: for what listens to keys themselves (a quick-time event).
    /// </summary>
    public static void Key(string name)
    {
        var key = (InputEventKey)InputMap.ActionGetEvents(name).First(e => e is InputEventKey).Duplicate();
        key.Pressed = true;
        Input.ParseInputEvent(key);
        var up = (InputEventKey)key.Duplicate();
        up.Pressed = false;
        Input.ParseInputEvent(up);
    }

    /// <summary>Moves the mouse <paramref name="motion"/> pixels: it looks around, or with a hand's key held moves that
    /// hand, or with Aim tool held turns its tool. <paramref name="motion"/> is what the game gets: the engine scales a
    /// move on the window to the game's own layout, so it's sent scaled back by as much, whatever the window's size.
    /// </summary>
    public static void Mouse(Vector2 motion) =>
        Input.ParseInputEvent(new InputEventMouseMotion { Relative = Frames.Root.GetFinalTransform().BasisXform(motion) });

    /// <summary>The mouse move (pixels) that moves <paramref name="surgeon"/>'s active hand by <paramref name="move"/>
    /// (world, across the floor): Surgeon.SteerHand() undone, outside the needle view.</summary>
    public static Vector2 HandMotion(Surgeon surgeon, Vector3 move)
    {
        var zoomed = Mathf.Tan(Mathf.DegToRad(Surgeon.ZoomFov[surgeon.Zoom]) * 0.5f)
            / Mathf.Tan(Mathf.DegToRad(Surgeon.ZoomFov[0]) * 0.5f);
        var step = new Basis(Vector3.Up, surgeon.Rotation.Y).Inverse() * move;
        return new Vector2(step.X, step.Z)
            / (Surgeon.HandSensitivity * surgeon.Status.HandSpeed() * zoomed * Settings.MouseSensitivity);
    }

    /// <summary>The key that makes the mouse move <paramref name="surgeon"/>'s active hand.</summary>
    public static string HandKey(Surgeon surgeon) =>
        surgeon.Active == 0 ? InputActions.MoveLeftHand : InputActions.MoveRightHand;

    /// <summary>Waits until the game has acted on what was sent: the engine dispatches input at the start of a frame,
    /// before its physics, so the next physics step comes after it, and the frame's processing after that step.
    /// </summary>
    public static async Task Delivered()
    {
        await Frames.NextPhysics();
        await Frames.NextProcess();
    }
}
