namespace Scalpel.Tests.Scenarios;

/// <summary>The full hand-stitch operation includes the core running-suture geometry and renderer checks.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("smoke"), TestCategory("scenario"), TestCategory("tool_needle"),
 TestCategory("tissue_modification"), TestCategory("visual_confirmation"), TestCategory("core_validation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class HandStitchScenarioTest {
    [TestCase("hand_stitch", Timeout = Limits.Slow)]
    public async Task PlaysThroughWithRunningSutureChecks(string scenario) => await ScenarioFlow.Play(scenario,
        // Temporary local budget override: preserves the running-suture allowance when moved out of NeedleTest.
        budgetOverride: new("running-suture simulation has recorded CPU/render overruns; profiling deferred", 15f),
        playStep: (driver, step) => step.Type == "close" ? SutureChecks.CloseForearm(driver) : driver.PlayerCompletes(step));
}
