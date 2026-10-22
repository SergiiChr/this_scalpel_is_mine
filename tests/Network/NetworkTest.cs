namespace Scalpel.Tests.Network;

/// <summary>Real two-process ENet coverage. tests/Support/network_runner.sh only orchestrates the processes; the cases
/// and the verdict are here.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("network")]
public class NetworkTest {
    private const string Runner = "res://tests/Support/network_runner.sh";

    [TestCase]
    public void MultiplayerCutTopologyAndToolHandoffStayInSync() =>
        Run("sync", "host/client wounds, painted map, topology and handoff agree");

    [TestCase]
    public void ClientForcepsTakeTheBulletOnTheHost() =>
        Run("pinch", "the host sees a client's forceps, pressed over the bullet, take it");

    [TestCase]
    public void GauzeTheHostHoldsStaysTapedWhereTheClientTapedIt() =>
        Run("tape", "the client's strip tapes the gauze the host holds down in the host's hand, and it stays there once let go of");

    [TestCase]
    public void ClientWaitsInTheLobbyForTheHostThenBothPlayAnotherScenario() =>
        Run("lobby", "a client off the report first waits in an empty lobby; then both are back in the lobby, "
            + "connected, and play the host's next pick");

    [TestCase]
    public void TenSecondClientStallKeepsSessionAndPausesInput() =>
        Run("stall", "a stalled client stays connected and its held input is paused");

    /// <summary>The bleed sources and cut pools cross the network packed as floats: what the host packs, a client reads
    /// back as it was. The sync run above checks the sources that reach a real client; its cut holds no pool.</summary>
    [TestCase]
    public void PackedBleedSourcesAndCutPoolsReadBackAsSent() {
        List<BleedSource> sources = [new(new Vector2(0.25f, 0.75f), 1.5f), new(new Vector2(0.5f, 0.5f), 0.2f, 0.004f), new(new Vector2(0.1f, 0.9f), 0.7f, Point: true)];
        List<CutPool> pools = [new(new Vector2(0.4f, 0.6f), 0.006f, 0.3f), new(new Vector2(0.45f, 0.65f), 0.009f, 1f)];
        List<BleedSource> readSources = [new(Vector2.Zero, 9f)];
        List<CutPool> readPools = [new(Vector2.Zero, 9f, 9f)];
        BleedSource.Unpack(BleedSource.Pack(sources), readSources);
        CutPool.Unpack(CutPool.Pack(pools), readPools);
        AssertBool(readSources.SequenceEqual(sources)).OverrideFailureMessage($"sources read back as sent: {string.Join(" ", readSources)}").IsTrue();
        AssertBool(readPools.SequenceEqual(pools)).OverrideFailureMessage($"pools read back as sent: {string.Join(" ", readPools)}").IsTrue();
        BleedSource.Unpack([], readSources);
        AssertInt(readSources.Count).OverrideFailureMessage("no sources packed reads back as none").IsEqual(0);
    }

    /// <summary>Runs the network runner in <paramref name="mode"/>; on failure the message carries its output, which
    /// names the problem and the driver log to look at.</summary>
    private static void Run(string mode, string claim) {
        var output = new Godot.Collections.Array();
        var code = OS.Execute("/bin/bash",
            [ProjectSettings.GlobalizePath(Runner), OS.GetExecutablePath(), ProjectSettings.GlobalizePath("res://"), mode,
                $"{Limits.Default / 1000}"],
            output, true);
        AssertInt(code).OverrideFailureMessage($"{claim}: network {mode} runner failed\n{string.Join("\n", output)}")
            .IsEqual(0);
    }
}
