namespace Scalpel.Tests.Liquids;

/// <summary>Hanging a bag on the IV stand ("Swap IV bag") as a player does it.
/// A working line is covered by SyringeTest.SwapIvBag.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("liquids"), TestCategory("tool_saline_bag")]
[GodotArgs("--fixed-fps", "60")]
public class IvBagSwapTest {
    /// <summary>Without a working line the held bag stays full in hand and the hung one keeps what it had: on the
    /// sidewalk and in the ambulance there's no nurse to bring another.</summary>
    [TestCase("no line")]
    [TestCase("missed vein")]
    public async Task BagIsKeptWithoutAWorkingLine(string line) {
        var driver = SurgeryDriver.Create();
        await driver.Start("hand_stitch");
        if (line == "missed vein") {
            SurgeryState.IvLineMissedTheVein(driver.Patient);
        }
        AssertBool(driver.Patient.IvWorking).OverrideFailureMessage($"{line}: nothing runs through a line")
            .IsFalse();
        var bag = (await driver.PlayerRequestsItem("saline_bag"))!;
        // Cefazolin pushed into the hung bag waits there for a working line.
        var hung = driver.Surgery.Tools.DripBag()!;
        driver.Surgery.Tools.AddLiquid(hung, 5f, new Dictionary<string, float> { ["cefazolin"] = 0.5f });
        hung.Bolus = 5f;
        hung.DrippedTotal = 1f;
        var hungMl = hung.Ml;
        var hungContents = new Dictionary<string, float>(hung.Contents);
        await driver.PlayerInteracts("Swap IV bag");
        await Frames.Physics(10);
        AssertThat(bag.State).OverrideFailureMessage($"{line}: the bag is still there to hang once a line works")
            .IsNotEqual(ToolState.Consumed);
        AssertInt(bag.Charges).OverrideFailureMessage($"{line}: the bag is still full").IsEqual(1);
        AssertBool(Mathf.IsEqualApprox(hung.Ml, hungMl) && hung.Contents.Count == hungContents.Count
                && hungContents.All(liquid => Mathf.IsEqualApprox(hung.Contents.GetValueOrDefault(liquid.Key), liquid.Value))
                && Mathf.IsEqualApprox(hung.Bolus, 5f) && Mathf.IsEqualApprox(hung.DrippedTotal, 1f))
            .OverrideFailureMessage($"{line}: the hung bag keeps its fluid and the cefazolin pushed into it, still waiting "
                + "to drip")
            .IsTrue();
        await driver.Stop();
    }
}
