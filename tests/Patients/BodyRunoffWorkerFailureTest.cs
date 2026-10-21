using System.Threading;

namespace Scalpel.Tests.Patients;

[TestSuite, RequireGodotRuntime]
[TestCategory("liquids"), TestCategory("liquid_contours"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class BodyRunoffWorkerFailureTest {
    [TestCase]
    public async Task TraceAndDrawFailuresBackOffWithoutLosingTheHeldDose() {
        var session = await ToolSession.Start("appendectomy", "body_runoff_worker_failure", captureUntouched: false);
        var driver = session.Driver;
        driver.Patient.SetPhysicsProcess(false);
        driver.Patient.SetProcess(false);
        driver.Body.SetProcess(false);
        var site = driver.Body.Site;
        var mesh = new MeshInstance3D { Mesh = new ArrayMesh() };
        site.AddChild(mesh);
        var traceCalls = 0;
        Task<BodyLiquidSurface.Path> Trace(Func<BodyLiquidSurface.Path> work) =>
            Interlocked.Increment(ref traceCalls) <= 5
                ? Task.FromException<BodyLiquidSurface.Path>(new InvalidOperationException("injected trace failure"))
                : Task.FromResult(new BodyLiquidSurface.Path([], false));
        var drawCalls = 0;
        Task Draw(Action work) {
            if (Interlocked.Increment(ref drawCalls) == 1) {
                return Task.FromException(new InvalidOperationException("injected draw failure"));
            }
            work();
            return Task.CompletedTask;
        }
        var runoff = new BodyRunoff(new BodyLiquidSurface([], []), site, driver.Surgery.Room.BedSurface, mesh,
            traceWorker: Trace, drawWorker: Draw);
        const float dose = 0.04f;
        runoff.Add(new Vector2(0.1f, 0.1f), Vector3.Zero, dose);

        for (var failure = 1; failure <= 5; failure++) {
            var observed = false;
            for (var frame = 0; frame < 600 && !observed; frame++) {
                runoff.Tick(1f / 60f);
                await Frames.NextProcess();
                observed = runoff.TraceFailures >= failure;
            }
            AssertBool(observed).OverrideFailureMessage($"trace failure {failure} arrives after its bounded retry delay")
                .IsTrue();
            AssertInt(runoff.TraceFailures).OverrideFailureMessage("each failed trace is observed only once")
                .IsEqual(failure);
            AssertFloat(runoff.HeldMl).OverrideFailureMessage("failed routes retain their original dose")
                .IsEqualApprox(dose, 0.00001f);
        }
        AssertBool(runoff.TracePending).OverrideFailureMessage("the final failed trace releases worker ownership")
            .IsFalse();
        AssertBool(runoff.TraceQuarantined).OverrideFailureMessage("a permanently broken route is quarantined after bounded retries")
            .IsTrue();

        // The quarantined outlet cannot block another source from tracing and preserving its own dose.
        runoff.Add(new Vector2(0.9f, 0.9f), Vector3.Zero, 0.02f);
        runoff.Tick(1f / 60f);
        await Frames.Process(1);
        runoff.Tick(1f / 60f);
        AssertInt(traceCalls).OverrideFailureMessage("a later outlet proceeds despite a quarantined sibling")
            .IsEqual(6);
        AssertFloat(runoff.HeldMl).OverrideFailureMessage("successful work and a quarantined sibling retain all supplied ml")
            .IsEqualApprox(dose + 0.02f, 0.00001f);

        // A pose change requests a redraw; its injected fault must not block the later retry or simulation tick.
        site.RotateY(0.1f);
        for (var frame = 0; frame < 60 && runoff.DrawFailures == 0; frame++) {
            runoff.Tick(1f / 60f);
            await Frames.NextProcess();
        }
        AssertInt(runoff.DrawFailures).OverrideFailureMessage("the drawing fault is observed once")
            .IsEqual(1);
        AssertBool(runoff.DrawPending).OverrideFailureMessage("the failed draw task is detached")
            .IsFalse();
        AssertFloat(runoff.HeldMl).OverrideFailureMessage("drawing faults do not release or discard held liquid")
            .IsEqualApprox(dose + 0.02f, 0.00001f);
        for (var frame = 0; frame < 60 && runoff.DrawPending; frame++) {
            runoff.Tick(1f / 60f);
            await Frames.NextProcess();
        }
        for (var frame = 0; frame < 60 && runoff.DrawFailures > 0; frame++) {
            runoff.Tick(1f / 60f);
            await Frames.NextProcess();
        }
        AssertInt(drawCalls).OverrideFailureMessage("a draw worker recovers after backoff")
            .IsGreater(1);
        AssertInt(runoff.DrawFailures).OverrideFailureMessage("successful drawing resets the fault count")
            .IsEqual(0);
        AssertFloat(runoff.HeldMl).OverrideFailureMessage("the recovered worker still preserves total liquid")
            .IsEqualApprox(dose + 0.02f, 0.00001f);
        await session.Finish();
    }
}
