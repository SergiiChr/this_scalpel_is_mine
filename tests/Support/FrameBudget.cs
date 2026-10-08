using System.Runtime;

namespace Scalpel.Tests.Support;

/// <summary>
/// The game's own work per frame: the wall time from one frame to the next, sampled once per frame while the dynamic
/// part runs. Run with --fixed-fps (frames don't wait for the clock) and with rendering off or headless (no waiting for
/// the screen), so what's measured is the game, not VSync or the test machine's GPU. Godot's Performance monitors can't
/// stand in: they hold the worst frame of the last whole second. After a pause in sampling (loading, a screenshot),
/// <see cref="Resume"/> starts again without counting the gap.
/// <para>Also measured: how much of each frame the .NET runtime took for itself, collecting garbage and compiling code
/// on first use. A frame over the budget only because of that is the known issue <see cref="RuntimeStalls"/>, not a
/// failure.</para>
/// </summary>
public sealed class FrameBudget
{
    /// <summary>Interactive gameplay must stay within this per frame so 60 fps remains possible.</summary>
    public const float Budget = 0.016f;

    /// <summary>Known issue: frames over the budget only for the runtime's own stalls.</summary>
    public const string RuntimeStalls = "the .NET runtime stalls a frame now and then: a garbage collection (up to "
        + "about 40 ms here, about once a minute) or compiling code that runs for the first time (a few ms; the "
        + "loading screen compiles most ahead)";

    private int _frames;
    private ulong _lastUsec;
    private double _runtimeWas;
    /// <summary>What was going on during the slowest frame, to find where to look.</summary>
    private string _worstDuring = "";

    /// <summary>The slowest frame recorded, in seconds.</summary>
    public float Worst { get; private set; }

    /// <summary>The slowest frame not counting the runtime's own stalls, in seconds.</summary>
    public float WorstOwn { get; private set; }

    public bool Within => Worst <= Budget;

    /// <summary>Whether this run fails on frames over the budget: everywhere but on CI (build.py test --ci-run), whose
    /// shared machines are slower than the ones the budget is for.</summary>
    public static bool Enforced => OS.GetEnvironment("CI_RUN") != "1";

    public void Resume()
    {
        _lastUsec = 0;
        _runtimeWas = RuntimeMs();
    }

    /// <summary>Records the time since the last sample, <paramref name="during"/> what.</summary>
    public void Sample(string during = "")
    {
        var now = Time.GetTicksUsec();
        var runtime = RuntimeMs();
        if (_lastUsec > 0)
        {
            var time = (now - _lastUsec) / 1_000_000f;
            _frames++;
            if (time > Worst)
            {
                Worst = time;
                _worstDuring = during;
            }
            WorstOwn = Mathf.Max(WorstOwn, time - (float)((runtime - _runtimeWas) / 1000.0));
        }
        _lastUsec = now;
        _runtimeWas = runtime;
    }

    public void Clear()
    {
        _frames = 0;
        Worst = 0f;
        WorstOwn = 0f;
        _worstDuring = "";
        Resume();
    }

    /// <summary>
    /// The one frame budget check for every test: fails when a frame went over the budget, in a run that renders key
    /// frames and isn't on CI, and otherwise only reports the worst frame. Headless frame times depend on the machine
    /// and on suites running alongside, so they're never checked. <paramref name="broken"/> says why a case is known to
    /// go over: over the budget it then prints a BROKEN line instead of failing (failing in a run with RUN_BROKEN=1),
    /// so a run tells a known slow case apart from a new one. A frame over only for <see cref="RuntimeStalls"/> is
    /// such a known case too.
    /// </summary>
    public void Check(bool withKeyFrames, string label = "", string broken = "")
    {
        var report = (label.Length > 0 ? label + ": " : "") + Summary();
        if (!withKeyFrames || !Enforced || Within)
        {
            GD.Print(report);
            return;
        }
        if (broken.Length == 0 && WorstOwn <= Budget)
        {
            broken = RuntimeStalls;
        }
        if (broken.Length > 0 && OS.GetEnvironment("RUN_BROKEN") != "1")
        {
            GD.Print($"BROKEN: {broken}; {report}");
            return;
        }
        AssertBool(Within).OverrideFailureMessage(report).IsTrue();
    }

    public string Summary() =>
        $"worst frame {Worst * 1000f:0.0} ms of game work over {_frames} frames (budget {Budget * 1000f:0} ms)"
        + (_worstDuring.Length > 0 ? ", during: " + _worstDuring : "")
        + $"; {WorstOwn * 1000f:0.0} ms without the runtime's own stalls";

    /// <summary>Time the runtime has spent so far on garbage collection pauses and on compiling on the main thread.
    /// </summary>
    private static double RuntimeMs() =>
        GC.GetTotalPauseDuration().TotalMilliseconds + JitInfo.GetCompilationTime(currentThread: true).TotalMilliseconds;
}
