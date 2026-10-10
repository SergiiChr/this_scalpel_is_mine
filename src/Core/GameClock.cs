namespace Scalpel.Core;

/// <summary>
/// Game time: the physics ticks run so far, in seconds. In play it keeps pace with the wall clock; unlike it, it moves by
/// the same steps in every run, so a game run with --fixed-fps (the tests) plays out and looks the same each time.
/// Network timeouts and sound throttling stay on the wall clock: they're about real time.
/// </summary>
public static class GameClock
{
    public static double Seconds => Engine.GetPhysicsFrames() / (double)Engine.PhysicsTicksPerSecond;
    public static ulong Msec => Engine.GetPhysicsFrames() * 1000 / (ulong)Engine.PhysicsTicksPerSecond;
}
