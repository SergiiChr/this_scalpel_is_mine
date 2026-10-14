namespace Scalpel.Tests.Scenarios;

/// <summary>Objective steps with an "or" alternative (DESIGN.md, Scenarios).</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
[GodotArgs("--fixed-fps", "60")]
public class ObjectiveAlternativeTest {
    /// <summary>
    /// A timed step that never holds ("listen" without a euthanized patient resets its timer every tick) with a timed
    /// alternative ("wait") completes once the alternative has held long enough.
    /// </summary>
    [TestCase]
    public async Task TimedAlternativeHoldsDespiteTimedStepResetting() {
        var driver = SurgeryDriver.Create();
        await driver.Start("hand_stitch");
        var step = ObjectiveStep.FromVariant(Json.ParseString(
            """{"type": "listen", "seconds": 1, "or": {"type": "wait", "seconds": 1}}""").AsGodotDictionary());
        var state = new StepState();
        var done = false;
        for (var tick = 0; tick < 70 && !done; tick++) {
            done = ObjectiveChecks.Check(step, state, driver.Surgery, 1f / 60f);
        }
        AssertBool(done).OverrideFailureMessage("the wait alternative completes the step after a second").IsTrue();
        await driver.Stop();
    }
}
