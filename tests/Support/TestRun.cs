using System.Runtime.CompilerServices;
using Scalpel.Core;

namespace Scalpel.Tests.Support;

/// <summary>What every test process does before the game starts.</summary>
internal static class TestRun
{
    /// <summary>Runs when the assembly loads, before the autoloads: the machine's settings file (debug mode on, another
    /// resolution) must not change what a test sees or ends up in its key frames.</summary>
    [ModuleInitializer]
    internal static void Start() => Settings.IgnoreFile = true;
}
