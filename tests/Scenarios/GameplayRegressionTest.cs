using System.Reflection;

namespace Scalpel.Tests.Scenarios;

/// <summary>
/// Every scenario, one case each (<see cref="GameplaySweep"/>): loads it, uses every tool on the patient, fires every
/// event and drug, turns the patient and builds the report. Any script error shows up in the output. Checks that
/// depend on the room, the site or the patient run in every scenario; the rest (controls, effects, iodine, syringe,
/// nurse, anesthesia, smoking) only in the first one.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("scenario")]
public class GameplayRegressionTest
{
    [TestCase]
    public void EveryScenarioIsSwept()
    {
        var cases = typeof(GameplayRegressionTest).GetMethod(nameof(Sweeps))!.GetCustomAttributes<TestCaseAttribute>()
            .Select(row => row.Arguments[0] as string)
            .ToHashSet();
        foreach (var scenario in Db.Scenarios.Concat(Db.DisabledScenarios))
        {
            AssertBool(cases.Contains(scenario.Id)).OverrideFailureMessage($"scenario {scenario.Id} has a sweep case").IsTrue();
        }
    }

    [TestCase("hand_stitch", Timeout = Limits.Slow)]
    [TestCase("hand_stitch_child", Timeout = Limits.Slow)]
    [TestCase("appendectomy", Timeout = Limits.Slow)]
    [TestCase("bullet_muscle", Timeout = Limits.Slow)]
    [TestCase("sidewalk_stab", Timeout = Limits.Slow)]
    [TestCase("ambulance_bullet", Timeout = Limits.Slow)]
    [TestCase("open_fracture", Timeout = Limits.Slow)]
    [TestCase("knife_back", Timeout = Limits.Slow)]
    [TestCase("lung_fluid", Timeout = Limits.Slow)]
    [TestCase("heart_attack", Timeout = Limits.Slow)]
    [TestCase("euthanasia", Timeout = Limits.Slow)]
    [TestCase("slit_throat", Timeout = Limits.Slow)]
    [TestCase("bullet_stomach", Timeout = Limits.Slow)]
    [TestCase("broken_ribs", Timeout = Limits.Slow)]
    [TestCase("gangrene_amputation", Timeout = Limits.Slow)]
    [TestCase("burn_graft", Timeout = Limits.Slow)]
    [TestCase("nose_job", Timeout = Limits.Slow)]
    [TestCase("oscar_figurine", Timeout = Limits.Slow)]
    [TestCase("blocked_artery", Timeout = Limits.Slow)]
    [TestCase("colon_cancer", Timeout = Limits.Slow)]
    [TestCase("bullet_near_heart", Timeout = Limits.Slow)]
    [TestCase("leg_extension", Timeout = Limits.Slow)]
    [TestCase("brain_tumor", Timeout = Limits.Slow)]
    public async Task Sweeps(string scenario)
    {
        // The first scenario also runs the checks that don't depend on the scenario.
        var failures = await GameplaySweep.Run(Db.Scenario(scenario)!, scenario == Db.Scenarios[0].Id);
        AssertBool(failures.Count == 0).OverrideFailureMessage(string.Join("\n", failures)).IsTrue();
    }
}
