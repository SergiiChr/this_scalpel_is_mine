namespace Scalpel.Tests.Support;

/// <summary>
/// Managed allocations of code that runs every frame or tick: every byte it allocates is garbage the runtime collects
/// later, in a pause that stalls a frame (<see cref="FrameBudget.RuntimeStalls"/>).
/// Counts the bytes the calling thread allocates, so it holds the same on any machine and needs no
/// <see cref="FrameBudget.Mode"/>: work the code hands to other threads isn't counted, and neither is native memory
/// Godot allocates.
/// </summary>
public static class AllocationBudget {
    /// <summary>
    /// Fails when <paramref name="work"/>, after <paramref name="warmup"/> runs (first-use setup, buffers growing to
    /// size), allocates more than <paramref name="maxBytesPerRun"/> bytes per run over <paramref name="runs"/> more.
    /// The total over all runs counts, so an allocation every few runs fails a budget of 0 too.
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
        var report = $"{label}: {bytes} B over {runs} runs, {(double)bytes / runs:0.#} B per run (budget {maxBytesPerRun} B)";
        GD.Print(report);
        AssertBool(bytes <= maxBytesPerRun * runs).OverrideFailureMessage($"allocation budget exceeded: {report}").IsTrue();
    }
}
