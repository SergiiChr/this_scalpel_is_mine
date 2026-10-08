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
        AssertInt(Run("sync")).OverrideFailureMessage("host/client wounds, painted map, topology and handoff agree").IsEqual(0);

    [TestCase]
    public void TenSecondClientStallKeepsSessionAndPausesInput() =>
        AssertInt(Run("stall")).OverrideFailureMessage("a stalled client stays connected and its held input is paused")
            .IsEqual(0);

    private static int Run(string mode)
    {
        var output = new Godot.Collections.Array();
        var code = OS.Execute("/bin/bash",
            [ProjectSettings.GlobalizePath(Runner), OS.GetExecutablePath(), ProjectSettings.GlobalizePath("res://"), mode],
            output, true);
        if (code != 0)
        {
            GD.Print($"network {mode} driver failed:\n{string.Join("\n", output)}");
        }
        return code;
    }
}
