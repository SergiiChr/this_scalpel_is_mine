using System.Runtime;

namespace Scalpel.Tests.Support;

/// <summary>How a run checks frame times: build.py sets it in FRAME_BUDGET (focused runs and performance_test check
/// them), and a run without it (an IDE's) checks <see cref="Cpu"/>.</summary>
public enum BudgetMode {
    /// <summary>Frame times aren't checked: on CI, in a full regression or smoke run.</summary>
    Off,
    /// <summary>The game's own work per frame, rendering off, scaled to this machine's speed: a check that holds on
    /// any machine.</summary>
    Cpu,
    /// <summary>Whole frames as drawn, on a real display and GPU (./build.py performance_test --render-budget).</summary>
    Render,
}

/// <summary>A case's approved overrun: why it goes over, and how many times this run's limit its worst frame may take.
/// The factor scales with the limit, so it holds on machines of any speed, and an overrun that grows past it still
/// fails.</summary>
public readonly record struct BudgetAllowance(string Reason, float Factor);

/// <summary>
/// Frame times: the wall time from one frame to the next, sampled once per frame while the dynamic part runs. Run with
/// --fixed-fps, so frames don't wait for the clock.
/// <para>In a <see cref="BudgetMode.Cpu"/> run rendering is off (<see cref="RenderingOff"/>), so a frame is the game's
/// own work, held to <see cref="CpuBudget"/> scaled by how much slower this machine is than the reference one
/// (<see cref="Slowness"/>). In a <see cref="BudgetMode.Render"/> run rendering stays on, so a frame is the whole
/// frame, held to <see cref="RenderBudget"/>.</para>
/// <para>Godot's Performance monitors can't stand in: they hold the worst frame of the last whole second. After a pause
/// in sampling (loading, a screenshot), <see cref="Resume"/> starts again without counting the gap.</para>
/// <para>Also measured: how much of each frame the .NET runtime took for itself, collecting garbage and compiling code
/// on first use. A frame over the budget only because of that is the known issue <see cref="RuntimeStalls"/>, not a
/// failure.</para>
/// </summary>
public sealed class FrameBudget {
    /// <summary>A whole frame, drawn, must stay within this so 60 fps remains possible.</summary>
    public const float RenderBudget = 0.016f;

    /// <summary>The game's own work per frame on the reference machine: half of <see cref="RenderBudget"/>, the other
    /// half left for drawing.</summary>
    public const float CpuBudget = 0.008f;

    /// <summary>The longest runtime stall in one frame on the reference machine still taken as the known issue
    /// <see cref="RuntimeStalls"/>: a longer one fails, so pauses that grow don't go unnoticed.</summary>
    public const float StallLimit = 0.040f;

    /// <summary>How far over its limit a frame may go before a check fails: frame times vary from run to run, so only
    /// an overrun of 30% or more is an issue.</summary>
    public const float Fluctuation = 1.3f;

    /// <summary>Known issue: frames over the budget only for the runtime's own stalls.</summary>
    public const string RuntimeStalls = "the .NET runtime stalls a frame now and then: a garbage collection (up to "
        + "about 40 ms here, about once a minute) or compiling code that runs for the first time (a few ms; the "
        + "loading screen compiles most ahead)";

    /// <summary>Measured by the requested headless Scalpel calibration on the i9-14900HX (2026-10-13), in seconds.
    /// The previous 5.3 ms was an estimate; this unchanged benchmark actually printed 28.49 ms on that machine.</summary>
    private const double ReferenceBenchmark = 0.02849;

    public static BudgetMode Mode { get; } = OS.GetEnvironment("FRAME_BUDGET") switch {
        "off" => BudgetMode.Off,
        "render" => BudgetMode.Render,
        _ => BudgetMode.Cpu,
    };

    /// <summary>How many times slower than the reference machine this one runs the game's own work: what CPU limits are
    /// scaled by. Measured once per process, the first time it's needed.</summary>
    public static double Slowness => BenchmarkHere.Value / ReferenceBenchmark;

    /// <summary>How long <see cref="Benchmark"/> takes here, in seconds.</summary>
    private static readonly Lazy<double> BenchmarkHere = new(Benchmarked);

    private int _frames;
    private ulong _lastUsec;
    private double _runtimeWas;
    /// <summary>What was going on during the slowest frame, to find where to look.</summary>
    private string _worstDuring = "";

    /// <summary>The slowest frame recorded, in seconds.</summary>
    public float Worst { get; private set; }

    /// <summary>The slowest frame not counting the runtime's own stalls, in seconds.</summary>
    public float WorstOwn { get; private set; }

    /// <summary>The longest the runtime stalled within one frame, in seconds.</summary>
    public float WorstStall { get; private set; }

    /// <summary>This run's limit for one frame, in seconds.</summary>
    public static float Limit => Mode == BudgetMode.Render ? RenderBudget : (float)(CpuBudget * Slowness);

    private static float StallLimitHere => Mode == BudgetMode.Render ? StallLimit : (float)(StallLimit * Slowness);

    /// <summary>Turns rendering off so a frame costs only the game's own work, and tests run faster. A
    /// <see cref="BudgetMode.Render"/> run keeps it on: its frames are timed as drawn.</summary>
    public static void RenderingOff() => RenderingServer.RenderLoopEnabled = Mode == BudgetMode.Render;

    public void Resume() {
        _lastUsec = 0;
        _runtimeWas = RuntimeMs();
    }

    /// <summary>Records the time since the last sample, <paramref name="during"/> what.</summary>
    public void Sample(string during = "", double gameTime = -1) {
        var now = Time.GetTicksUsec();
        var runtime = RuntimeMs();
        if (_lastUsec > 0) {
            var time = (now - _lastUsec) / 1_000_000f;
            _frames++;
            if (time > Worst) {
                Worst = time;
                _worstDuring = during + (gameTime >= 0 ? $" (frame at {gameTime:0.000} s)" : "");
            }
            var stall = (float)((runtime - _runtimeWas) / 1000.0);
            WorstOwn = Mathf.Max(WorstOwn, time - stall);
            WorstStall = Mathf.Max(WorstStall, stall);
        }
        _lastUsec = now;
        _runtimeWas = runtime;
    }

    /// <summary>Runs <paramref name="work"/>, the test's own work, without counting its time (runtime stalls included)
    /// in this frame. Not nested: the outer call would take the inner one's time off again.</summary>
    public T Uncounted<T>(Func<T> work) {
        var (started, runtime) = (Time.GetTicksUsec(), RuntimeMs());
        try {
            return work();
        } finally {
            if (_lastUsec > 0) {
                _lastUsec += Time.GetTicksUsec() - started;
            }
            _runtimeWas += RuntimeMs() - runtime;
        }
    }

    public void Clear() {
        _frames = 0;
        Worst = 0f;
        WorstOwn = 0f;
        WorstStall = 0f;
        _worstDuring = "";
        Resume();
    }

    /// <summary>
    /// The one frame budget check for every test: fails when a frame went over <see cref="Limit"/> by
    /// <see cref="Fluctuation"/> or more, and does nothing in a run that doesn't check frame times
    /// (<see cref="BudgetMode.Off"/>). A <paramref name="localOverride"/>, a known issue, raises the limit for this case
    /// only, by its factor, as a hard ceiling on the whole frame, runtime stalls included: the factor already holds
    /// the case's own fluctuation.
    /// Functional assertions still run.
    /// Without an override, frames over the limit only for the runtime's own stalls pass within their bounded
    /// allowance.
    /// </summary>
    public void Check(string label = "", BudgetAllowance? localOverride = null) {
        if (Mode == BudgetMode.Off) {
            return;
        }
        var limit = Limit * (localOverride?.Factor ?? Fluctuation);
        var report = (label.Length > 0 ? label + ": " : "") + Summary()
            + (localOverride is { } allowed ? $"; local budget override up to {allowed.Factor:0.0}x, "
                + $"{limit * 1000f:0.0} ms: {allowed.Reason}" : $"; fails past {limit * 1000f:0.0} ms");
        if (Worst <= limit) {
            GD.Print(report);
        } else if (localOverride is null && WorstOwn <= limit && WorstStall <= StallLimitHere) {
            GD.Print($"PASS: {RuntimeStalls}; {report}");
        } else {
            var issue = localOverride is null ? "new frame budget issue" : "known frame budget issue grew past its override";
            AssertBool(false).OverrideFailureMessage($"{issue}: {report}").IsTrue();
        }
    }

    public string Summary() =>
        $"worst frame {Worst * 1000f:0.0} ms over {_frames} frames ({Mode} budget {Limit * 1000f:0.0} ms"
        + (Mode == BudgetMode.Cpu ? $", {CpuBudget * 1000f:0} ms on the reference machine; benchmark {BenchmarkHere.Value * 1000:0.00} ms here, "
            + $"{ReferenceBenchmark * 1000:0.00} ms there" : "")
        + ")" + (_worstDuring.Length > 0 ? ", during: " + _worstDuring : "")
        + $"; {WorstOwn * 1000f:0.0} ms without the runtime's own stalls, the longest of which took "
        + $"{WorstStall * 1000f:0.0} ms (limit {StallLimitHere * 1000f:0} ms)";

    /// <summary>The fastest of a few runs of <see cref="Benchmark"/>, in seconds, the first one compiling it.</summary>
    private static double Benchmarked() {
        var fastest = double.MaxValue;
        for (var run = 0; run < 6; run++) {
            var started = Time.GetTicksUsec();
            Benchmark();
            fastest = Math.Min(fastest, run == 0 ? double.MaxValue : (Time.GetTicksUsec() - started) / 1_000_000.0);
        }
        return fastest;
    }

    /// <summary>A fixed piece of work like the game's own, on one thread: vector math over arrays, as the tissue and
    /// physics code does. Only its time matters, never its result.</summary>
    private static float Benchmark() {
        var points = new Vector3[4096];
        for (var i = 0; i < points.Length; i++) {
            points[i] = new Vector3(i, i * 0.5f, i * 0.25f) * 0.001f;
        }
        var sum = 0f;
        for (var step = 0; step < 15; step++) {
            for (var i = 0; i < points.Length; i++) {
                var moved = points[i].Rotated(Vector3.Up, 0.01f) + (points[(i + 1) % points.Length] * 0.001f);
                points[i] = moved.Normalized() * Mathf.Sin(moved.Length());
                sum += points[i].Dot(Vector3.Right);
            }
        }
        return sum;
    }

    /// <summary>Time the runtime has spent so far on garbage collection pauses and on compiling on the main thread.
    /// </summary>
    private static double RuntimeMs() =>
        GC.GetTotalPauseDuration().TotalMilliseconds + JitInfo.GetCompilationTime(currentThread: true).TotalMilliseconds;
}
