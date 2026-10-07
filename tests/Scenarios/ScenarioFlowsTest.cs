using System.Reflection;

namespace Scalpel.Tests.Scenarios;

/// <summary>
/// Every main-menu scenario played through its positive flow, like a player (see <see cref="ScenarioFlow"/>): each
/// required objective done in order, and the game itself ends the surgery with a successful report. In a run with key
/// frames the major scenarios also save the untouched site, then the site right after every objective. Review them for
/// continuity, clipping, mesh intersections, material consistency and tool contact.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("scenario"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
// CI intermittently stalls when a second operation shares the first one's engine and rendering state.
[IsolateCases]
public class ScenarioFlowsTest
{
    [TestCase]
    public void EveryScenarioHasAFlow()
    {
        var cases = typeof(ScenarioFlowsTest).GetMethods()
            .SelectMany(method => method.GetCustomAttributes<TestCaseAttribute>())
            .Where(row => row.Arguments.Length > 0)
            .Select(row => row.Arguments[0] as string)
            .ToHashSet();
        foreach (var scenario in Db.Scenarios.Concat(Db.DisabledScenarios))
        {
            AssertBool(cases.Contains(scenario.Id)).OverrideFailureMessage($"scenario {scenario.Id} has a positive flow case")
                .IsTrue();
        }
    }

    [TestCase("hand_stitch", false, "", Timeout = Limits.Slow)]
    [TestCase("hand_stitch_child", false, "", Timeout = Limits.Slow)]
    [TestCase("appendectomy", false,
        "with key frames the worst frame takes 28 ms of game work (budget 16 ms) while sewing the muscle.", Timeout = Limits.Slow)]
    [TestCase("bullet_muscle", false, "", Timeout = Limits.Slow)]
    [TestCase("sidewalk_stab", false, "", Timeout = Limits.Slow)]
    [TestCase("ambulance_bullet", false, "", Timeout = Limits.Slow)]
    [TestCase("open_fracture", false,
        "with key frames the worst frame takes 18 ms of game work (budget 16 ms) while sewing.", Timeout = Limits.Slow)]
    [TestCase("knife_back", true, "", Timeout = Limits.Slow)]
    [TestCase("lung_fluid", false, "", Timeout = Limits.Slow)]
    [TestCase("heart_attack", true, "", Timeout = Limits.Slow)]
    [TestCase("euthanasia", false, "", Timeout = Limits.Slow)]
    public async Task PlaysThrough(string scenario, bool keyFrames, string budgetBroken) => await
        ScenarioFlow.Play(scenario, keyFrames, budgetBroken);

    [TestCase("slit_throat", Timeout = Limits.Slow, Description = "BROKEN: blood bags swapped onto the IV don't bring the blood back (2.9 of 5 l, 0 ml transfused after four bags), \"Replace lost blood\" never completes.")]
    [TestCase("bullet_stomach", Timeout = Limits.Slow, Description = "BROKEN: forceps take hold as soon as they're pressed, while still coming down into the opening, so they get a vessel or the skin edge instead of the bowel and the bullet.")]
    [TestCase("broken_ribs", Timeout = Limits.Slow, Description = "BROKEN: oxygen stays at 90 and blood pressure at 76 after the bag swap, \"Oxygen back above 94\" never completes.")]
    [TestCase("gangrene_amputation", Timeout = Limits.Slow, Description = "BROKEN: the bone saw held on the bone for 60 s doesn't get through it.")]
    [TestCase("burn_graft", Timeout = Limits.Slow, Description = "BROKEN: ten graft sheets cover only 61% of the burns, 70% needed.")]
    [TestCase("nose_job", Timeout = Limits.Slow, Description = "BROKEN: the needle doesn't reach the last muscle and skin stitches on the nose (tip in the air), the patient arrests before it's closed.")]
    [TestCase("oscar_figurine", Timeout = Limits.Slow, Description = "BROKEN: forceps take hold as soon as they're pressed, while still coming down into the opening (tip 8 mm above the site, figurine 70 mm deep), so they clamp a vessel instead.")]
    [TestCase("blocked_artery", Timeout = Limits.Slow, Description = "BROKEN: forceps take hold of nothing over the clot (they close before reaching it), \"Remove the clot\" never completes.")]
    [TestCase("colon_cancer", Timeout = Limits.Slow, Description = "BROKEN: run beside another suite (--jobs 2), a 7.7 cm tear keeps bleeding 2.4 ml/s and \"Control the bleeding\" never completes; run alone it passes. Likely wall-clock driven, not yet debugged. Also, with key frames the worst frame takes 32 ms (budget 16 ms) while holding the bowel aside and sewing.")]
    [TestCase("bullet_near_heart", Timeout = Limits.Slow, Description = "BROKEN: forceps take hold as soon as they're pressed, while still coming down into the opening, so they get a vessel instead of the lung and the bullet; the patient bleeds out.")]
    [TestCase("leg_extension", Timeout = Limits.Slow, Description = "BROKEN: the needle threads both 13.5 cm cuts, but their skin stays open (\"needle cannot reach intended puncture within 4 mm\"), \"Close the leg\" never completes.")]
    [TestCase("brain_tumor", Timeout = Limits.Slow, Description = "BROKEN: a 0.5 cm tear opens while the hemostats go on, after the driver's only cautery pass, and its gauze pass presses beside it (held 0), \"Control the bleeding\" never completes. Pressed on directly, gauze and cautery stop such a tear.")]
    [TestCategory("broken")]
    public async Task PlaysThroughKnownBroken(string scenario) => await ScenarioFlow.Play(scenario);
}
