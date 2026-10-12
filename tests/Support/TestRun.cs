using System.Runtime.CompilerServices;
using Scalpel.Core;

namespace Scalpel.Tests.Support;

/// <summary>What every test process does before the game starts.</summary>
internal static class TestRun
{
    /// <summary>Set by build.py and .runsettings for test processes only.</summary>
    private const string Marker = "TEST_RUN";

    /// <summary>Runs when the assembly loads, before the autoloads: the machine's settings file (debug mode on, another
    /// resolution) must not change what a test sees or ends up in its key frames.
    /// Debug builds of the game load this assembly too, so it acts only when the test runner set the marker.
    /// It reads the variable through .NET, not Godot: the test host loads the assembly without an engine.</summary>
    [ModuleInitializer]
    internal static void Start() => Settings.IgnoreFile = System.Environment.GetEnvironmentVariable(Marker) == "1";
}
