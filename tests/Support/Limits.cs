namespace Scalpel.Tests.Support;

/// <summary>How long a test may take (msec, for [TestCase(Timeout = ...)]); build.py's per-test limits match.</summary>
public static class Limits
{
    /// <summary>A test in a suite tagged slow: one surgery played through.</summary>
    public const int Slow = 360_000;
}
