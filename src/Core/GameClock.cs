namespace Scalpel.Core;

/// <summary>
/// Game time: the physics ticks run since the surgery started, in seconds. In play it keeps pace with the wall clock;
/// unlike it, it moves by the same steps in every run, so a game run with --fixed-fps (the tests) plays out and looks
/// the same each time. It counts from the surgery's start, not the engine's: loading takes a different number of frames
/// each run. Network timeouts and sound throttling stay on the wall clock: they're about real time.
/// </summary>
public static class GameClock
{
    private static ulong _start;

    public static double Seconds => Ticks / (double)Engine.PhysicsTicksPerSecond;
    public static ulong Msec => Ticks * 1000 / (ulong)Engine.PhysicsTicksPerSecond;
    private static ulong Ticks => Engine.GetPhysicsFrames() - _start;

    /// <summary>Starts counting again from now, as a surgery starts.</summary>
    public static void Restart() => _start = Engine.GetPhysicsFrames();
}
