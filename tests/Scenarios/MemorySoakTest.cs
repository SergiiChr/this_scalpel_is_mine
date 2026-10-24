namespace Scalpel.Tests.Scenarios;

/// <summary>
/// Surgeries played one after another in one game session leave nothing behind: once the first few have grown caches,
/// pools and buffers to size, the managed heap, Godot's own memory and its object, node and resource counts stop
/// growing.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("scenario"), TestCategory("tissue_modification")]
[GodotArgs("--fixed-fps", "60")]
public class MemorySoakTest {
    /// <summary>Surgeries before the first measured one: the first loads the room's models and warms caches up.
    /// </summary>
    private const int Warmup = 4;
    /// <summary>Surgeries in each window compared. A value that only shows for a moment (a node freed a frame later)
    /// raises a window's readings, never its lowest one, so windows are compared by their lowest reading.</summary>
    private const int Window = 4;
    private const int Measured = 3 * Window;
    /// <summary>
    /// Memory growth per surgery that doesn't count as a leak.
    /// Godot's memory grows 10-30 KiB per surgery whose HUD is built (most of it the nurse shop's controls), in steps
    /// of a few hundred KiB as its tables double, with no object, signal connection or engine handle left over: inside
    /// the engine, a few MB over a hundred surgeries.
    /// The managed heap grows a few KiB per surgery until the history holds its last <see cref="Progress.HistorySize"/>
    /// games. A surgery left behind is tens of MB.
    /// </summary>
    private const long AllowedBytesPerSurgery = 128 * 1024;

    private readonly record struct Usage(long Managed, long Godot, long Objects, long Nodes, long Resources) {
        public override string ToString() => $"managed {Managed / 1024} KiB, Godot {Godot / 1024} KiB, "
            + $"{Objects} objects, {Nodes} nodes, {Resources} resources";
    }

    [TestCase(Timeout = Limits.Slow)]
    public async Task RepeatedSurgeriesLeaveNothingBehind() {
        FrameBudget.RenderingOff();
        try {
            var readings = new List<Usage>();
            for (var round = 0; round < Warmup + Measured; round++) {
                await PlayerOperatesAndDischarges();
                readings.Add(await Settled());
                GD.Print($"surgery {round + 1}: {readings[^1]}");
            }
            var early = Lowest(readings.GetRange(Warmup, Window));
            var late = Lowest(readings.GetRange(readings.Count - Window, Window));
            var allowed = (Measured - Window) * AllowedBytesPerSurgery;
            var report = $"from {early}\nto {late}";
            AssertBool(late.Managed - early.Managed <= allowed && late.Godot - early.Godot <= allowed)
                .OverrideFailureMessage($"memory grows more than {AllowedBytesPerSurgery / 1024} KiB per surgery, {report}")
                .IsTrue();
            AssertBool(late.Objects <= early.Objects && late.Nodes <= early.Nodes && late.Resources <= early.Resources)
                .OverrideFailureMessage($"surgeries leave objects behind, {report}").IsTrue();
        } finally {
            RenderingServer.RenderLoopEnabled = true;
        }
    }

    /// <summary>One short surgery through what makes and frees the most: the room and patient built, a 5 cm incision
    /// cut into the tissue and bleeding, the patient signed out with the job undone and the surgery freed.</summary>
    private static async Task PlayerOperatesAndDischarges() {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        SurgeryState.PatientIsAsleep(driver.Patient);
        await driver.PlayerIncises(0.05f);
        await Frames.Seconds(2f);
        await driver.PlayerDischarges();
        AssertBool(driver.Surgery.Finished).OverrideFailureMessage("the surgery ends on the signature").IsTrue();
        await driver.Stop();
    }

    /// <summary>Memory in use once what the surgery left has been freed: Godot's queued frees, then the managed
    /// garbage and the Godot objects its finalizers release.</summary>
    private static async Task<Usage> Settled() {
        await Frames.Process(5);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        await Frames.Process(5);
        static long Monitor(Performance.Monitor monitor) => (long)Performance.GetMonitor(monitor);
        return new Usage(GC.GetTotalMemory(true), Monitor(Performance.Monitor.MemoryStatic),
            Monitor(Performance.Monitor.ObjectCount), Monitor(Performance.Monitor.ObjectNodeCount),
            Monitor(Performance.Monitor.ObjectResourceCount));
    }

    private static Usage Lowest(List<Usage> window) => new(window.Min(usage => usage.Managed),
        window.Min(usage => usage.Godot), window.Min(usage => usage.Objects), window.Min(usage => usage.Nodes),
        window.Min(usage => usage.Resources));
}
