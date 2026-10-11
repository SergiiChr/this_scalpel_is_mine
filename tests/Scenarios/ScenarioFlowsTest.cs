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
    [TestCase("oscar_figurine", false, "", Timeout = Limits.Slow)]
    [TestCase("heart_attack", true,
        "in some runs the frame the defibrillator is put down (56.9 s) takes 35-43 ms of the game's own work; timing "
        + "every _Process and _PhysicsProcess found nothing over 10 ms there, so it's in the engine or the finalizer "
        + "thread; not traced further", Timeout = Limits.Slow)]
    [TestCase("brain_tumor", false, "", Timeout = Limits.Slow)]
    [TestCase("euthanasia", false, "", Timeout = Limits.Slow)]
    public async Task PlaysThrough(string scenario, bool keyFrames, string budgetBroken) => await
        ScenarioFlow.Play(scenario, keyFrames, budgetBroken);

    [TestCase("slit_throat", "\"Close the throat\" (close) didn't complete", Timeout = Limits.Slow, Description = "KNOWN ISSUE: pulled shut, the running stitches tear the skin (Patient.OnSnap) and the needle can't reach the new tears (\"needle cannot reach intended puncture within 4 mm\"): skin closure ends at 0.64, \"Close the throat\" never completes.")]
    [TestCase("bullet_stomach", "\"Find and remove the bullet\" (extract) didn't complete", Timeout = Limits.Slow, Description = "KNOWN ISSUE: the test driver finds no spot on the bowel in the opening clear of the bleeder to hold it aside, so the bullet stays covered and the forceps clamp the bleeder beside it instead.")]
    [TestCase("broken_ribs", "\"Close the chest\" (close) didn't complete", Timeout = Limits.Slow, Description = "KNOWN ISSUE: pulled shut, the running stitches tear the skin (Patient.OnSnap) and the needle can't reach the new tears (\"needle cannot reach intended puncture within 4 mm\"): skin closure stays at 0, \"Close the chest\" never completes.")]
    [TestCase("burn_graft", "\"Graft skin over the burns\" (graft) didn't complete", Timeout = Limits.Slow, Description = "KNOWN ISSUE: ten graft sheets cover only 61% of the burns, 70% needed.")]
    [TestCase("nose_job", "\"Close up\" (close) didn't complete", Timeout = Limits.Slow, Description = "KNOWN ISSUE: the needle doesn't reach the last muscle and skin stitches on the nose (tip in the air), the patient arrests before it's closed.")]
    [TestCase("bullet_near_heart", "\"Remove the bullet\" (extract) didn't complete", Timeout = Limits.Slow, Description = "KNOWN ISSUE: the test driver stands too far along the table to hold the lung aside with the left hand (0.86 m from its shoulder, reach 0.72 m), so the bullet stays covered and the forceps close on nothing.")]
    [TestCase("leg_extension", "\"Close the leg\" (close) didn't complete", Timeout = Limits.Slow, Description = "KNOWN ISSUE: the needle threads both 13.5 cm cuts, but their skin stays open (\"needle cannot reach intended puncture within 4 mm\"), \"Close the leg\" never completes.")]
    public async Task PlaysThroughToKnownIssue(string scenario, string failure) =>
        await KnownIssue.Expect(failure, () => ScenarioFlow.Play(scenario));

    [TestCase("gangrene_amputation", Timeout = Limits.Slow, Description = "BROKEN: the bone saw held on the bone for 60 s doesn't get through it.")]
    [TestCase("colon_cancer", Timeout = Limits.Slow, Description = "BROKEN: run beside another suite (--jobs 2), a 7.7 cm tear keeps bleeding 2.4 ml/s and \"Control the bleeding\" never completes; run alone it passes. Likely wall-clock driven, not yet debugged. Also, with key frames the worst frame takes 32 ms (budget 16 ms) while holding the bowel aside and sewing.")]
    [TestCase("blocked_artery", Timeout = Limits.Slow, Description = "BROKEN: the running skin stitch along the 11 cm incision closes it only to about 0.4: pulled shut, the skin's springs snap (Patient.OnSnap) and tear new wounds the needle then can't reach (\"needle cannot reach intended puncture within 4 mm\"). Skin closure ends at 0.69-0.7, so \"Close the skin\" passes or fails on the random sequence.")]
    [TestCategory("broken")]
    public async Task PlaysThroughKnownBroken(string scenario) => await ScenarioFlow.Play(scenario);
}
