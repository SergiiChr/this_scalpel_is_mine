using GdUnit4.Core.Execution.Exceptions;

namespace Scalpel.Tests.Data;

/// <summary>A documented allowance passes only that budget call; ordinary overruns and global limits stay intact.</summary>
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
        budget.Check(localOverride: "explicit allowance in the budget API regression check");
        AssertFloat(FrameBudget.Limit).OverrideFailureMessage("the local override leaves the global limit unchanged")
            .IsEqual(limit);
        var ordinaryFailed = false;
        try {
            budget.Check();
        } catch (TestFailedException) {
            ordinaryFailed = true;
        }
        AssertBool(ordinaryFailed).OverrideFailureMessage("the same overrun still fails without a local override")
            .IsTrue();
    }
}
