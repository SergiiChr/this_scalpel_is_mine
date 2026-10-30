namespace Scalpel.Tests.Scenarios;

/// <summary>A real two-peer positive flow: two players complete Ambulance Run from their assigned sides.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("scenario"), TestCategory("network"), TestCategory("core_validation")]
public class AmbulanceCoopTest {
    [TestCase(Timeout = Limits.Slow, Description = "BROKEN: about one run in three the client's IV catheter misses the vein and the ketamine it then gives into the vein falls short or misses, so \"Put the patient under general anesthesia\" doesn't complete.")]
    [TestCategory("broken")]
    public void TwoSurgeonsCooperateToCompleteAmbulanceRun() {
        var output = new Godot.Collections.Array();
        var code = OS.Execute("/bin/bash",
            [ProjectSettings.GlobalizePath("res://tests/Support/network_runner.sh"), OS.GetExecutablePath(),
                ProjectSettings.GlobalizePath("res://"), "ambulance", $"{Limits.Slow / 1000}"], output, true);
        AssertInt(code).OverrideFailureMessage("the two surgeons complete Ambulance Run from their assigned sides "
            + $"and both receive success: network runner failed\n{string.Join("\n", output)}").IsEqual(0);
    }
}
