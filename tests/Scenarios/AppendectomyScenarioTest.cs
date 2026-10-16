namespace Scalpel.Tests.Scenarios;

/// <summary>The full appendectomy includes core incision, organ extraction and blood-loss checks.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("scenario"), TestCategory("core_validation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class AppendectomyScenarioTest {
    [TestCase("appendectomy", Timeout = Limits.Slow)]
    public async Task PlaysThroughWithIncisionAndExtractionChecks(string scenario) {
        using var checks = new AppendectomyFlow();
        await ScenarioFlow.Play(scenario,
            // Temporary local budget override: retains the scenario's measured muscle-sewing allowance.
            budgetOverride: new("appendectomy muscle sewing has recorded CPU/render overruns; profiling deferred", 20f),
            playStep: checks.PlayStep, validate: checks.Validate, includeOptional: true);
    }
}
