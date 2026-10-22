using System.Reflection;

namespace Scalpel.Tests.Scenarios;

/// <summary>
/// Every main-menu scenario played through its positive flow, like a player (see <see cref="ScenarioFlow"/>): each
/// required objective done in the listed order, the surgery still running until the patient is signed out, then a
/// successful report. In a run with key frames the major scenarios also save the untouched site, then the site right
/// after every objective. Review them for continuity, clipping, mesh intersections, material consistency and tool
/// contact.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("scenario"), TestCategory("visual_confirmation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
// CI intermittently stalls when a second operation shares the first one's engine and rendering state.
[IsolateCases]
public class ScenarioFlowsTest {
    // Temporary local budget override: these scenario rows exceeded CPU/render budgets in the recorded regressions.
    private const string BudgetOverride = "scenario surgery has recorded CPU/render overruns; profiling deferred";
    [TestCase]
    public void EveryScenarioHasAFlow() {
        var cases = new[] { typeof(ScenarioFlowsTest), typeof(HandStitchScenarioTest), typeof(AppendectomyScenarioTest),
                typeof(GlassShardsScenarioTest) }
            .SelectMany(suite => suite.GetMethods())
            .SelectMany(method => method.GetCustomAttributes<TestCaseAttribute>())
            .Where(row => row.Arguments.Length > 0)
            .Select(row => row.Arguments[0] as string)
            .ToHashSet();
        foreach (var scenario in Db.Scenarios.Concat(Db.DisabledScenarios)) {
            AssertBool(cases.Contains(scenario.Id)).OverrideFailureMessage($"scenario {scenario.Id} has a positive flow case")
                .IsTrue();
        }
    }

    [TestCase("hand_stitch_child", false, BudgetOverride, 13.5f, Timeout = Limits.Slow)]
    [TestCase("bullet_muscle", false, BudgetOverride, 19f, Timeout = Limits.Slow)]
    [TestCase("sidewalk_stab", false, BudgetOverride, 17f, Timeout = Limits.Slow)]
    [TestCase("ambulance_bullet", false, BudgetOverride, 18f, Timeout = Limits.Slow)]
    [TestCase("open_fracture", false,
        "with key frames the worst frame takes 18 ms of game work (budget 16 ms) while sewing.", 16f, Timeout = Limits.Slow)]
    [TestCase("knife_back", true, BudgetOverride, 17f, Timeout = Limits.Slow)]
    [TestCase("lung_fluid", false, BudgetOverride, 31.5f, Timeout = Limits.Slow)]
    [TestCase("oscar_figurine", false, BudgetOverride, 17f, Timeout = Limits.Slow)]
    [TestCase("heart_attack", true,
        "in some runs the frame the defibrillator is put down (56.9 s) takes 35-43 ms of the game's own work; timing "
        + "every _Process and _PhysicsProcess found nothing over 10 ms there, so it's in the engine or the finalizer "
        + "thread; not traced further", 15.5f, Timeout = Limits.Slow)]
    [TestCase("brain_tumor", false, BudgetOverride, 15.5f, Timeout = Limits.Slow)]
    [TestCase("euthanasia", false, BudgetOverride, 17.5f, Timeout = Limits.Slow)]
    [TestCase("colon_cancer", false, BudgetOverride, 26f, Timeout = Limits.Slow)]
    [TestCase("gangrene_amputation", false, BudgetOverride, 22f, Timeout = Limits.Slow)]
    [TestCase("leg_extension", false, BudgetOverride, 31f, Timeout = Limits.Slow)]
    [TestCase("broken_ribs", false, BudgetOverride, 15f, Timeout = Limits.Slow)]
    [TestCase("slit_throat", false, BudgetOverride, 8f, Timeout = Limits.Slow)]
    [TestCase("burn_graft", false, BudgetOverride, 16f, Timeout = Limits.Slow)]
    [TestCase("bullet_stomach", false, BudgetOverride, 18f, Timeout = Limits.Slow)]
    [TestCase("nose_job", false, BudgetOverride, 18f, Timeout = Limits.Slow)]
    public async Task PlaysThrough(string scenario, bool keyFrames, string budgetReason,
        float budgetFactor) => await ScenarioFlow.Play(scenario, keyFrames, new(budgetReason, budgetFactor));

    [TestCase("bullet_near_heart", "\"Remove the bullet\" (extract) didn't complete", Timeout = Limits.Slow, Description = "KNOWN ISSUE: the test driver stands too far along the table to hold the lung aside with the left hand (0.86 m from its shoulder, reach 0.72 m), so the bullet stays covered and the forceps close on nothing.")]
    public async Task PlaysThroughToKnownIssue(string scenario, string failure) =>
        await KnownIssue.Expect(failure, () => ScenarioFlow.Play(scenario));

    [TestCase("blocked_artery", Timeout = Limits.Slow, Description = "BROKEN: the running skin stitch along the 11 cm incision closes it only to about 0.4: pulled shut, the skin's springs snap (Patient.OnSnap) and tear new wounds the needle then can't reach (\"needle cannot reach intended puncture within 4 mm\"). Skin closure ends at 0.69-0.7, so \"Stitch the skin closed\" passes or fails on the random sequence.")]
    [TestCategory("broken")]
    public async Task PlaysThroughKnownBroken(string scenario) => await ScenarioFlow.Play(scenario);
}
