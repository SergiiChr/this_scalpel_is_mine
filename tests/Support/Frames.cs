namespace Scalpel.Tests.Support;

/// <summary>Waiting for the game: physics steps, drawn frames and game time.</summary>
public static class Frames
{
    public static SceneTree Tree => (SceneTree)Engine.GetMainLoop();

    /// <summary>The scene tree's root, where tests add what they run.</summary>
    public static Window Root => Tree.Root;

    public static SignalAwaiter NextPhysics() => Tree.ToSignal(Tree, SceneTree.SignalName.PhysicsFrame);

    public static SignalAwaiter NextProcess() => Tree.ToSignal(Tree, SceneTree.SignalName.ProcessFrame);

    public static async Task Physics(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await NextPhysics();
        }
    }

    public static async Task Process(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await NextProcess();
        }
    }

    /// <summary>Waits <paramref name="seconds"/> of game time.</summary>
    public static Task Seconds(float seconds) => Physics((int)(seconds * Engine.PhysicsTicksPerSecond));

    /// <summary>Waits until <paramref name="done"/> returns true or <paramref name="limit"/> seconds of game time pass.
    /// Returns whether it came true.</summary>
    public static async Task<bool> Until(Func<bool> done, float limit)
    {
        var steps = (int)(limit * Engine.PhysicsTicksPerSecond);
        for (var i = 0; i < steps; i++)
        {
            if (done())
            {
                return true;
            }
            await NextPhysics();
        }
        return done();
    }
}
