using GdUnit4.Core.Execution.Exceptions;

namespace Scalpel.Tests.Support;

/// <summary>
/// A case held back by a known issue (CLAUDE.md, Known issues). It runs with the rest and passes only when it fails
/// exactly where the issue says: everything before still works, and the regression still catches anything new.
/// </summary>
public static class KnownIssue
{
    /// <summary>Runs <paramref name="test"/>, which passes only by failing with a message that contains
    /// <paramref name="failure"/>. Any other failure fails the case as it is. When it passes, the issue is fixed, and
    /// the case fails until it's made an ordinary one again.</summary>
    public static async Task Expect(string failure, Func<Task> test)
    {
        try
        {
            await test();
        }
        catch (TestFailedException e) when (e.Message.Contains(failure, StringComparison.Ordinal))
        {
            GD.Print($"Known issue reproduced: {failure}");
            return;
        }
        AssertBool(false)
            .OverrideFailureMessage($"Known issue no longer reproduces, it's fixed: make the case an ordinary one again ({failure})")
            .IsTrue();
    }
}
