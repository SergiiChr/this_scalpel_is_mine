namespace Scalpel.Tests.Liquids;

/// <summary>Hanging a bag on the IV stand ("Swap IV bag") as a player does it.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("liquids"), TestCategory("tool_saline_bag")]
[GodotArgs("--fixed-fps", "60")]
public class IvBagSwapTest
{
    /// <summary>With no line in a vein, Surgery's bag request still uses up the held bag after Patient.Administer()
    /// refused to give it (only the toast "Nothing happens. There's no IV line in." says so), and refills the hung bag
    /// with its fluid. On the sidewalk and in the ambulance there's no nurse, so a lost bag can't be replaced. To fix:
    /// check Patient.IvWorking first and keep the bag (and the hung one) as they were.</summary>
    [TestCase(Description = "BROKEN: \"Swap IV bag\" uses up the held bag although no line is in and nothing ran.")]
    [TestCategory("broken")]
    public async Task BagHungWithoutALineIsKept()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("hand_stitch");
        var bag = (await driver.PlayerRequestsItem("saline_bag"))!;
        await driver.PlayerInteracts("Swap IV bag");
        await Frames.Physics(10);
        AssertBool(driver.Patient.IvSet).OverrideFailureMessage("no line is in").IsFalse();
        AssertThat(bag.State).OverrideFailureMessage("the bag is still there to hang once a line is in")
            .IsNotEqual(ToolState.Consumed);
        AssertInt(bag.Charges).OverrideFailureMessage("the bag is still full").IsEqual(1);
        await driver.Stop();
    }
}
