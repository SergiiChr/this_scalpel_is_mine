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
    /// <summary>Seconds after midnight the surgery started at, by the system clock.</summary>
    private static double _startOfDay;

    /// <summary>When set, surgeries start at this time of day (seconds after midnight) whatever the system clock says:
    /// tests set it so the operating room's clock reads the same in every run.</summary>
    internal static double? FixedStartOfDay { get; set; }

    public static double Seconds => Ticks / (double)Engine.PhysicsTicksPerSecond;
    public static ulong Msec => Ticks * 1000 / (ulong)Engine.PhysicsTicksPerSecond;
    private static ulong Ticks => Engine.GetPhysicsFrames() - _start;

    /// <summary>The operating room clock, hh:mm: the time of day the surgery started, moved on by game time.</summary>
    public static string TimeOfDay()
    {
        var seconds = (int)(_startOfDay + Seconds) % 86400;
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}";
    }

    /// <summary>Starts counting again from now, as a surgery starts.</summary>
    public static void Restart()
    {
        _start = Engine.GetPhysicsFrames();
        var now = Time.GetTimeDictFromSystem();
        _startOfDay = FixedStartOfDay ?? ((now["hour"].AsInt32() * 3600) + (now["minute"].AsInt32() * 60) + now["second"].AsInt32());
    }
}
