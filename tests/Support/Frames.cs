namespace Scalpel.Tests.Support;

/// <summary>Waiting for the game: physics steps, drawn frames and game time.</summary>
public static class Frames {
    /// <summary>A key frame run's window on a desktop takes no focus and lets the pointer through: losing focus to
    /// another test's window would release the keys a test holds down, and the pointer passing over it would move the
    /// hand. Tests send their input as events, which need neither.</summary>
    static Frames() {
        if (KeyFrames.Wanted() && DisplayServer.GetName() != "headless") {
            Root.SetFlag(Window.Flags.NoFocus, true);
            Root.SetFlag(Window.Flags.MousePassthrough, true);
        }
    }

    public static SceneTree Tree => (SceneTree)Engine.GetMainLoop();

    /// <summary>The scene tree's root, where tests add what they run.</summary>
    public static Window Root => Tree.Root;

    public static SignalAwaiter NextPhysics() => Tree.ToSignal(Tree, SceneTree.SignalName.PhysicsFrame);

    public static SignalAwaiter NextProcess() => Tree.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);

    public static async Task Physics(int count) {
        for (var i = 0; i < count; i++) {
            await NextPhysics();
        }
    }

    public static async Task Process(int count) {
        for (var i = 0; i < count; i++) {
            await NextProcess();
        }
    }

    /// <summary>Waits <paramref name="seconds"/> of game time.</summary>
    public static Task Seconds(float seconds) => Physics((int)(seconds * Engine.PhysicsTicksPerSecond));

    /// <summary>Waits until <paramref name="done"/> returns true or <paramref name="limit"/> seconds of game time pass.
    /// Returns whether it came true.</summary>
    public static async Task<bool> Until(Func<bool> done, float limit) {
        var steps = (int)(limit * Engine.PhysicsTicksPerSecond);
        for (var i = 0; i < steps; i++) {
            if (done()) {
                return true;
            }
            await NextPhysics();
        }
        return done();
    }
}
