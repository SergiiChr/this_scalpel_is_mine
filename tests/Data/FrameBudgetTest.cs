using GdUnit4.Core.Execution.Exceptions;

namespace Scalpel.Tests.Data;

/// <summary>A documented allowance passes only that budget call and only up to its factor; an overrun within the
/// fluctuation passes, larger ones fail, and global limits stay intact.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("frame_budget")]
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

    [TestCase]
    public void OnlyOverrunsPastFluctuationFail() {
        if (FrameBudget.Mode == BudgetMode.Off) {
            return;
        }
        // Read before sampling: the first read benchmarks this machine.
        var limit = FrameBudget.Limit;
        var budget = new FrameBudget();
        budget.Sample();
        // A little over the limit, well within the fluctuation.
        OS.DelayUsec((int)(limit * 1_050_000f));
        budget.Sample();
        AssertFloat(budget.Worst).OverrideFailureMessage("the probe goes over the limit").IsGreater(limit);
        AssertBool(Fails(() => budget.Check())).OverrideFailureMessage("an overrun under 30% passes").IsFalse();
        budget.Clear();
        budget.Sample();
        OS.DelayUsec((int)(limit * FrameBudget.Fluctuation * 1_050_000f));
        budget.Sample();
        AssertBool(Fails(() => budget.Check())).OverrideFailureMessage("an overrun past 30% fails").IsTrue();
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
