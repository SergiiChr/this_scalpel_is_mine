namespace Scalpel.Tests.Patients;

[TestSuite, RequireGodotRuntime]
[TestCategory("liquids")]
public class BodyRunoffWorkerFailureTest {
    [TestCase]
    public void TraceAndDrawFailuresBackOffWithoutLosingTheHeldDose() {
        var root = new Node3D();
        var site = new Node3D();
        var pads = new MeshInstance3D { Mesh = new BoxMesh() };
        var frame = new MeshInstance3D { Mesh = new BoxMesh() };
        var mesh = new MeshInstance3D { Mesh = new ArrayMesh() };
        root.AddChild(site);
        root.AddChild(pads);
        root.AddChild(frame);
        root.AddChild(mesh);
        Frames.Tree.Root.AddChild(root);
        try {
            var bed = new BedLiquidSurface(pads, frame);
            var traceCalls = 0;
            Task<BodyLiquidSurface.Path> Trace(Func<BodyLiquidSurface.Path> work) =>
                ++traceCalls <= 5
                    ? Task.FromException<BodyLiquidSurface.Path>(new InvalidOperationException("injected trace failure"))
                    : Task.FromResult(new BodyLiquidSurface.Path([], false));
            var drawCalls = 0;
            Task Draw(Action work) {
                if (++drawCalls == 1) {
                    return Task.FromException(new InvalidOperationException("injected draw failure"));
                }
                work();
                return Task.CompletedTask;
            }
            var runoff = new BodyRunoff(new BodyLiquidSurface([], []), site, bed, mesh,
                traceWorker: Trace, drawWorker: Draw);
            const float Dose = 0.04f;
            runoff.Add(new Vector2(0.1f, 0.1f), Vector3.Zero, Dose);

            for (var failure = 1; failure <= 5; failure++) {
                for (var tick = 0; tick < 600 && runoff.TraceFailures < failure; tick++) {
                    runoff.Tick(1f / 60f);
                }
                AssertInt(runoff.TraceFailures).OverrideFailureMessage($"trace failure {failure} arrives after its bounded retry delay")
                    .IsEqual(failure);
                AssertFloat(runoff.HeldMl).OverrideFailureMessage("failed routes retain their original dose")
                    .IsEqualApprox(Dose, 0.00001f);
                if (failure < 5) {
                    var attempts = traceCalls;
                    runoff.Tick(0.05f);
                    runoff.Tick(0.05f);
                    AssertInt(traceCalls).OverrideFailureMessage("the worker respects its increasing retry delay")
                        .IsEqual(attempts);
                }
            }
            AssertBool(runoff.TracePending).OverrideFailureMessage("the final failed trace releases worker ownership")
                .IsFalse();
            AssertBool(runoff.TraceQuarantined).OverrideFailureMessage("a permanently broken route is quarantined after bounded retries")
                .IsTrue();

            // The quarantined outlet cannot block another source from tracing and preserving its own dose.
            runoff.Add(new Vector2(0.9f, 0.9f), Vector3.Zero, 0.02f);
            runoff.Tick(1f / 60f);
            runoff.Tick(1f / 60f);
            AssertInt(traceCalls).OverrideFailureMessage("a later outlet proceeds despite a quarantined sibling")
                .IsEqual(6);
            AssertFloat(runoff.HeldMl).OverrideFailureMessage("successful work and a quarantined sibling retain all supplied ml")
                .IsEqualApprox(Dose + 0.02f, 0.00001f);

            // A pose change requests a redraw; its injected fault must not block the later retry or simulation tick.
            site.RotateY(0.1f);
            for (var tick = 0; tick < 60 && runoff.DrawFailures == 0; tick++) { runoff.Tick(1f / 60f); }
            AssertInt(runoff.DrawFailures).OverrideFailureMessage("the drawing fault is observed once")
                .IsEqual(1);
            AssertBool(runoff.DrawPending).OverrideFailureMessage("the failed draw task is detached")
                .IsFalse();
            AssertFloat(runoff.HeldMl).OverrideFailureMessage("drawing faults do not release or discard held liquid")
                .IsEqualApprox(Dose + 0.02f, 0.00001f);
            for (var tick = 0; tick < 60 && runoff.DrawFailures > 0; tick++) { runoff.Tick(1f / 60f); }
            AssertInt(drawCalls).OverrideFailureMessage("a draw worker recovers after backoff")
                .IsGreater(1);
            AssertInt(runoff.DrawFailures).OverrideFailureMessage("successful drawing resets the fault count")
                .IsEqual(0);
            AssertFloat(runoff.HeldMl).OverrideFailureMessage("the recovered worker still preserves total liquid")
                .IsEqualApprox(Dose + 0.02f, 0.00001f);
        } finally {
            root.Free();
        }
    }
}
