namespace Scalpel.Tests.Support;

/// <summary>
/// Managed allocations of code that runs every frame or tick: every byte it allocates is garbage the runtime collects
/// later, in a pause that stalls a frame (<see cref="FrameBudget.RuntimeStalls"/>).
/// Counts bytes, so it holds the same on any machine and needs no <see cref="FrameBudget.Mode"/>. Native memory Godot
/// allocates isn't counted.
/// </summary>
public static class AllocationBudget {
    /// <summary>
    /// Fails when <paramref name="work"/>, after <paramref name="warmup"/> runs (first-use setup, buffers growing to
    /// size), allocates more than <paramref name="maxBytesPerRun"/> bytes per run over <paramref name="runs"/> more.
    /// The total over all runs counts, so an allocation every few runs fails a budget of 0 too. Only the calling
    /// thread's bytes count: work the code hands to other threads isn't measured.
    /// </summary>
    public static void Check(string label, Action work, long maxBytesPerRun = 0, int warmup = 20, int runs = 200) {
        for (var i = 0; i < warmup; i++) {
            work();
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < runs; i++) {
            work();
        }
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var report = $"{label}: {bytes} B over {runs} runs, {(double)bytes / runs:0.#} B per run "
            + $"(budget {maxBytesPerRun} B)";
        GD.Print(report);
        AssertBool(bytes <= maxBytesPerRun * runs)
            .OverrideFailureMessage($"allocation budget exceeded: {report}").IsTrue();
    }

    /// <summary>
    /// Fails when the frames <paramref name="play"/> takes allocate more than <paramref name="maxBytesPerFrame"/> per
    /// frame on average, on every thread: a whole game's frames, which the test's own awaiting adds a little to.
    /// For a scene allocation can't be kept out of entirely (meshes rebuilt at a new size); the budget is what it
    /// measures with some room, so garbage that grows by a third fails it.
    /// </summary>
    public static async Task CheckFrames(string label, Func<Task> play, long maxBytesPerFrame) {
        var (frames, before) = (Engine.GetProcessFrames(), GC.GetTotalAllocatedBytes(precise: true));
        await play();
        var played = Math.Max(Engine.GetProcessFrames() - frames, 1);
        var perFrame = (GC.GetTotalAllocatedBytes(precise: true) - before) / (double)played;
        var report = $"{label}: {perFrame / 1024:0.0} KB per frame over {played} frames (budget "
            + $"{maxBytesPerFrame / 1024.0:0.#} KB)";
        GD.Print(report);
        AssertBool(perFrame <= maxBytesPerFrame)
            .OverrideFailureMessage($"allocation budget exceeded: {report}").IsTrue();
    }
}
