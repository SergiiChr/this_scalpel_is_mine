namespace Scalpel.Tests.TissueModification;

/// <summary>Mesh rebuilds must not change how much solver time elapses at lower rendering rates.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("tissue_modification")]
public class TissueTimingTest {
    [TestCase(false)]
    [TestCase(true)]
    public async Task RebuildsPreserveSolverTimeAtThirtyFps(bool folded) {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var body = driver.Body;
        body.SetProcess(false);
        var sim = body.Tissue;
        sim.Tearing = false;
        var at = new Vector2(0.5f, 0.55f);
        var k = sim.Nearest(at);
        var origin = sim.Pos[k];
        sim.Grip(1, at);
        var pull = folded ? new Vector3(0.045f, 0.018f, 0) : Vector3.Up * 0.002f;
        var first = sim.StepsDone;
        for (var frame = 0; frame < 60; frame++) {
            // A moving grip keeps the solver awake while the production mesh scheduler alternates its work.
            sim.MoveGrip(1, origin + pull + Vector3.Up * (frame % 2 == 0 ? 0.0001f : 0f));
            body._Process(TissueSim.Step);
        }
        AssertInt(sim.StepsDone - first).OverrideFailureMessage("two seconds at 30 fps advance 60 solver steps, allowing one frame of rebuild debt")
            .IsGreaterEqual(58);
        AssertBool(sim.HasFoldFootprint).IsEqual(folded);
        await driver.Stop();
    }
}
