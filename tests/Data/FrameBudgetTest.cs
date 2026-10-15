using GdUnit4.Core.Execution.Exceptions;

namespace Scalpel.Tests.Data;

/// <summary>A documented allowance passes only that budget call and only up to its factor; ordinary overruns and global
/// limits stay intact.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
public class FrameBudgetTest {
    [TestCase]
    public void LocalOverrideDoesNotDisableOrdinaryBudgetFailures() {
        if (FrameBudget.Mode == BudgetMode.Off) {
            return;
        }
        var limit = FrameBudget.Limit;
        var budget = new FrameBudget();
        budget.Sample();
        // Artificial measured overrun verifies the budget API, without simulating or changing any game scene.
        OS.DelayUsec((int)(limit * 2_000_000f));
        budget.Sample();
        AssertFloat(budget.WorstOwn).OverrideFailureMessage("the probe has a real overrun, not only a runtime stall")
            .IsGreater(limit);
        // The probe's frame takes a little over twice the limit, so 4x holds it and 1.5x doesn't.
        budget.Check(localOverride: new("explicit allowance in the budget API regression check", 4f));
        AssertFloat(FrameBudget.Limit).OverrideFailureMessage("the local override leaves the global limit unchanged")
            .IsEqual(limit);
        AssertBool(Fails(() => budget.Check(localOverride: new("an allowance smaller than the overrun", 1.5f))))
            .OverrideFailureMessage("an overrun past the local override's factor still fails").IsTrue();
        AssertBool(Fails(() => budget.Check()))
            .OverrideFailureMessage("the same overrun still fails without a local override").IsTrue();
    }

    private static bool Fails(Action check) {
        try {
            check();
        } catch (TestFailedException) {
            return true;
        }
        return false;
    }
}
