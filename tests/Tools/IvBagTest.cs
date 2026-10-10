namespace Scalpel.Tests.Tools;

/// <summary>
/// The IV bag as a player meets it: hung on the stand, ports down, its liquid inside the film up to the level its ml
/// says. A line in and the bag run dry, a saline bag taken off the tray and hung with "Swap IV bag" fills the stand's
/// bag again. With key frames also the hung bag up close at each step, from its front and side, and the new one held in
/// the hand. Review them for the film, print, ports and hanger flange reading as an IV bag, the liquid inside the film
/// (none when it's empty), the bag held in the fist without going through the glove and hanging under its hook.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("liquids"), TestCategory("tool_saline_bag"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class IvBagTest
{
    /// <summary>How far from the hung bag the key frames look at it (meters).</summary>
    private const float View = 0.4f;
    /// <summary>Close enough to a bag in hand that the camera stays in front of the surgeon.</summary>
    private const float HeldView = 0.3f;
    /// <summary>Slack for the liquid's edges against the film's (meters).</summary>
    private const float Tolerance = 0.0005f;

    [TestCase]
    public async Task HangsOnTheStandAndIsSwappedForANewBag()
    {
        RenderingServer.RenderLoopEnabled = false;
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        SurgeryState.PatientIsAsleep(driver.Patient);
        var surgery = driver.Surgery;
        var drip = surgery.Tools.DripBag()!;
        SurgicalTool? bag = null;
        KeyFrames? shots = null;
        if (KeyFrames.Wanted())
        {
            shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(surgery, "iv_bag");
            driver.OnKeyFrame = async keyFrame => AssertBool(bag is { State: ToolState.Held } held
                    ? await shots.CaptureFacing(keyFrame, held.Middle(), TowardEyes(held), HeldView, hands: true)
                    : await shots.CaptureFacing(keyFrame, drip.Middle(), drip.GlobalBasis.Y, View))
                .OverrideFailureMessage($"saved key frame {keyFrame}").IsTrue();
        }
        driver.Budget.Clear();

        AssertBool((-drip.GlobalBasis.Z.Normalized()).Y < -0.99f)
            .OverrideFailureMessage("the bag hangs on the stand with its ports straight down").IsTrue();
        AssertLiquidAtItsLevel(drip, "hung");
        await driver.Capture("hung");

        await driver.PlayerSetsIv();
        AssertBool(driver.Patient.IvWorking).OverrideFailureMessage("a working line is in").IsTrue();
        SurgeryState.IvBagIsEmpty(surgery);
        AssertBool(drip.LiquidPart("Level")!.Visible).OverrideFailureMessage("an empty bag shows no liquid").IsFalse();
        await driver.Capture("emptied");

        bag = (await driver.PlayerRequestsItem("saline_bag"))!;
        AssertObject(driver.Me.HeldTool(driver.Me.Active)).OverrideFailureMessage("a saline bag is in hand")
            .IsEqual(bag);
        await driver.Capture("bag_in_hand");

        AssertBool(await driver.PlayerInteracts("Swap IV bag")).OverrideFailureMessage("the stand offers Swap IV bag")
            .IsTrue();
        AssertBool(bag.State == ToolState.Consumed && Mathf.IsEqualApprox(drip.Ml, SurgicalTool.DripFluid))
            .OverrideFailureMessage($"the held bag is hung in place of the empty one, full ({drip.Ml:0} ml)").IsTrue();
        AssertLiquidAtItsLevel(drip, "swapped");
        await driver.Capture("swapped");

        driver.Budget.Check(shots is not null);
        shots?.End();
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }

    /// <summary>The liquid lies inside the film, at its bottom, as high as its share of what the bag holds.</summary>
    private static void AssertLiquidAtItsLevel(SurgicalTool bag, string when)
    {
        var film = InBag(bag, (MeshInstance3D)bag.FindChild("Bag", true, false)!);
        var level = bag.LiquidPart("Level")!;
        var liquid = InBag(bag, level);
        var full = level.GetAabb().Size.Z;
        var inside = film.Grow(Tolerance).Encloses(liquid);
        AssertBool(level.Visible && inside && Mathf.Abs(liquid.Size.Z - (full * bag.Ml / bag.Def.Volume)) < Tolerance)
            .OverrideFailureMessage($"{when}: the liquid fills the film from the bottom to {bag.Ml:0} of "
                + $"{bag.Def.Volume:0} ml ({liquid.Size.Z * 100f:0.0} of {full * 100f:0.0} cm high, inside: {inside})")
            .IsTrue();
    }

    /// <summary>The face of a bag in hand that's turned toward the surgeon's eyes.</summary>
    private static Vector3 TowardEyes(SurgicalTool bag)
    {
        var face = bag.GlobalBasis.Y;
        return face * Mathf.Sign(face.Dot(Surgery.Current!.LocalSurgeon!.Camera.GlobalPosition - bag.Middle()));
    }

    /// <summary>A part's box in the bag's own space.</summary>
    private static Aabb InBag(SurgicalTool bag, MeshInstance3D part) =>
        bag.GlobalTransform.AffineInverse() * part.GlobalTransform * part.GetAabb();
}
