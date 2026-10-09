namespace Scalpel.Tests.Network;

/// <summary>Real two-process ENet coverage. tests/Support/network_runner.sh only orchestrates the processes; the cases
/// and the verdict are here.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("network")]
public class NetworkTest
{
    private const string Runner = "res://tests/Support/network_runner.sh";

    [TestCase]
    public void MultiplayerCutTopologyAndToolHandoffStayInSync() =>
        Run("sync", "host/client wounds, painted map, topology and handoff agree");

    [TestCase]
    public void TenSecondClientStallKeepsSessionAndPausesInput() =>
        Run("stall", "a stalled client stays connected and its held input is paused");

    /// <summary>Runs the network runner in <paramref name="mode"/>; on failure the message carries its output, which
    /// names the problem and the driver log to look at.</summary>
    private static void Run(string mode, string claim)
    {
        var output = new Godot.Collections.Array();
        var code = OS.Execute("/bin/bash",
            [ProjectSettings.GlobalizePath(Runner), OS.GetExecutablePath(), ProjectSettings.GlobalizePath("res://"), mode],
            output, true);
        AssertInt(code).OverrideFailureMessage($"{claim}: network {mode} runner failed\n{string.Join("\n", output)}")
            .IsEqual(0);
    }
}
