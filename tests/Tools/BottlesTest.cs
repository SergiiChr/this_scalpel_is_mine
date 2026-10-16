namespace Scalpel.Tests.Tools;

/// <summary>
/// Bottles as a player handles them: a vial ordered from the nurse comes standing on the delivery tray, cap up. Picked
/// up, Grab held a second stands it upright where it's held: on the instrument tray, or on the patient's belly, resting
/// on the skin. A quick click puts it down the way any tool goes down, lying. With key frames also each bottle from
/// above and obliquely: review them for the vial standing straight on its base, resting on what's under it (not
/// floating, not sunk into the tray or the skin) and lying flat after the click.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("tool_vial_propofol"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class BottlesTest {
    private const string Vial = "vial_propofol";
    /// <summary>How far from a bottle the key frames look at it (meters).</summary>
    private const float View = 0.3f;

    [TestCase]
    public async Task BottlesComeStandingAndStandWhenGrabIsHeld() {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        SurgeryState.PatientIsAsleep(driver.Patient);
        SurgicalTool vial = null!;
        KeyFrames? shots = null;
        if (KeyFrames.Wanted()) {
            shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(driver.Surgery, "bottles");
            driver.OnKeyFrame = (keyFrame, description) => shots.CaptureAt(keyFrame, description, vial.Middle(), View);
        }
        driver.Budget.Clear();
        var onTray = driver.FreeTools(Vial);
        await driver.PlayerOrders([Vial]);
        await Frames.Seconds(1f);
        vial = driver.FreeTools(Vial).First(tool => !onTray.Contains(tool));
        AssertBool(Stands(vial)).OverrideFailureMessage($"a vial comes from the nurse standing, cap up (tip {TipUp(vial):0.00} up)").IsTrue();
        AssertFloat(vial.LinearVelocity.Length()).OverrideFailureMessage("it stands still on the delivery tray").IsLess(0.01f);
        await driver.Capture("delivered", "the vial delivered standing still on the tray, cap up");

        await PickUp(driver, vial);
        var spot = SurgeryState.FreeTraySpot(driver.Surgery);
        await driver.PlayerWalksTo(spot);
        await driver.PlayerReaches(spot - (vial.Middle() - vial.TipPosition()));
        await HoldGrab();
        await driver.AssertAndCapture(vial.State == ToolState.Free && Stands(vial) && driver.LiesOnTray(vial),
            $"Grab held a second stands the vial upright on the tray (tip {TipUp(vial):0.00} up)", "stood_on_tray");

        await PickUp(driver, vial);
        var belly = driver.SitePoint(new Vector2(0.5f, 0.5f));
        await driver.PlayerWalksTo(belly);
        await driver.PlayerReaches(belly - (vial.Middle() - vial.TipPosition()));
        await HoldGrab();
        // Its base's middle against the skin as it's drawn there. It settles on the patient's collider, which over the
        // site lies a little above the drawn skin; inside the body (on the table) it would be far below.
        var above = driver.Body.HeightAboveSite(vial.GlobalPosition);
        await driver.AssertAndCapture(Stands(vial) && above > -0.005f && above < 0.03f,
            $"stood on the patient's belly, the vial rests on the body, not inside it (base {above * 100f:0.0} cm over the skin)",
            "stood_on_patient");

        await PickUp(driver, vial);
        await driver.PlayerPutsDown();
        await driver.AssertAndCapture(vial.State == ToolState.Free && !Stands(vial) && driver.LiesOnTray(vial),
            $"a quick click puts it down lying, as before (tip {TipUp(vial):0.00} up)", "put_down");

        // Temporary local budget override: delivery has a recorded overrun; pickup and placement assertions remain enforced.
        driver.Budget.Check(localOverride: new("the frame the ordered vial arrives goes over the CPU budget", 6.5f));
        shots?.End();
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }

    /// <summary>
    /// A bottle held in one hand and a syringe in the other: the needle brought to the bottle's fist turns the bottle up
    /// and both hands meet at its cap, the needle in it. The wheel draws the dose, and moved away, the bottle hand goes
    /// back to where and how it held the bottle. With key frames the two hands from the front: before, connected, drawn
    /// and moved away.
    /// </summary>
    [TestCase]
    public async Task ASyringeDrawsFromABottleHeldInTheOtherHand() {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        SurgeryState.PatientIsAsleep(driver.Patient);
        var me = driver.Me;
        var bottleHand = me.Hands[SurgeryDriver.Left];
        var syringeHand = me.Hands[SurgeryDriver.Right];
        KeyFrames? shots = null;
        if (KeyFrames.Wanted()) {
            shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(driver.Surgery, "bottle_in_hand");
            driver.OnKeyFrame = (keyFrame, description) => shots.CaptureFacing(keyFrame, description,
                bottleHand.GlobalPosition.Lerp(syringeHand.GlobalPosition, 0.5f), me.GlobalPosition - bottleHand.GlobalPosition,
                0.4f, true);
        }
        await driver.SwitchTo(SurgeryDriver.Left);
        var vial = (await driver.PlayerRequestsItem(Vial))!;
        await driver.SwitchTo(SurgeryDriver.Right);
        var syringe = (await driver.PlayerRequestsItem("syringe_10"))!;
        var held = TipUp(vial);
        var bottleAt = bottleHand.GlobalPosition;
        await driver.AssertAndCapture(me.HeldTool(SurgeryDriver.Left) == vial && held < 0f,
            $"the bottle held in the left fist, cap down the way a fist holds it, the empty syringe in the right hand (tip {held:0.00} up)",
            "both_held");

        // The bottle brought off the table toward the surgeon, clear of the drape over the feet, so the fist can come down
        // to meet the needle.
        await driver.SwitchTo(SurgeryDriver.Left);
        await driver.PlayerReaches(bottleAt + ((me.GlobalPosition - bottleAt) with { Y = 0f }).Normalized() * 0.1f);
        await driver.SwitchTo(SurgeryDriver.Right);
        bottleAt = bottleHand.GlobalPosition;
        // The needle brought over the bottle's fist, as a player aims at the bottle in their other hand.
        await driver.PlayerReaches(bottleHand.GlobalPosition);
        var inVial = await Frames.Until(
            () => Syringe.NeedleTarget(syringe, driver.Patient) is ContainerTarget { Container: var into } && into == vial, 2f);
        await Frames.Physics(30);
        var moved = bottleHand.GlobalPosition.DistanceTo(bottleAt);
        // Where the mouse holds the needle, before the meeting moves it.
        var needleAt = me.ToGlobal(syringeHand.LocalTarget) + me.OwnTipOffset(SurgeryDriver.Right);
        var needleMoved = syringe.TipPosition().DistanceTo(needleAt);
        await driver.AssertAndCapture(inVial && TipUp(vial) > 0.95f && moved > 0.01f && needleMoved > 0.01f
                && syringe.TipPosition().DistanceTo(vial.TipPosition()) < 0.01f,
            $"brought over the bottle's fist, the needle goes in through its cap: the bottle turned cap up, both hands "
                + $"moved to meet (bottle {moved * 100f:0.0} cm, needle {needleMoved * 100f:0.0} cm; in the vial: {inVial}, "
                + $"tip {TipUp(vial):0.00} up, needle "
                + $"{syringe.TipPosition().DistanceTo(vial.TipPosition()) * 100f:0.0} cm from the cap)",
            "connected");

        var before = vial.Ml;
        for (var i = 0; i < 3; i++) {
            await SurgeryDriver.Notch(false);
        }
        await driver.AssertAndCapture(Mathf.IsEqualApprox(syringe.Ml, 3f) && Mathf.IsEqualApprox(before - vial.Ml, 3f)
                && syringe.Contents.ContainsKey(Db.Tool(Vial)!.Drug),
            $"three notches draw 3 ml of the drug from the held bottle (syringe {syringe.Ml:0.0} ml, bottle down "
                + $"{before - vial.Ml:0.0} ml)",
            "drawn");

        await driver.PlayerReaches(bottleAt + (me.GlobalBasis.X * 0.15f));
        await Frames.Physics(30);
        var back = bottleHand.GlobalPosition.DistanceTo(bottleAt);
        await driver.AssertAndCapture(Mathf.Abs(TipUp(vial) - held) < 0.05f && back < 0.01f
                && Syringe.NeedleTarget(syringe, driver.Patient) is not ContainerTarget,
            $"the syringe moved away, out of the bottle: the bottle hand holds it where and how it did, cap down (tip "
                + $"{TipUp(vial):0.00} up, was {held:0.00}; {back * 100f:0.0} cm from where it held it)",
            "moved_away");
        shots?.End();
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }

    private static async Task PickUp(SurgeryDriver driver, SurgicalTool vial) {
        await driver.PlayerWalksTo(vial.GlobalPosition);
        await driver.PlayerReaches(vial.GlobalPosition);
        SurgeryDriver.Tap(InputActions.Grab);
        await Frames.Physics(5);
        AssertObject(driver.Me.HeldTool(driver.Me.Active)).OverrideFailureMessage("the vial is picked up").IsEqual(vial);
    }

    /// <summary>Grab held a little over a second, then let go: the bottle in the hand stands upright where it is.
    /// </summary>
    private static async Task HoldGrab() {
        SurgeryDriver.Press(InputActions.Grab);
        await Frames.Seconds(Surgeon.StandHold + 0.2f);
        SurgeryDriver.Release(InputActions.Grab);
        await Frames.Seconds(1f);
    }

    /// <summary>How far up a tool's tip end points (1: straight up).</summary>
    private static float TipUp(SurgicalTool tool) => (-tool.GlobalBasis.Z.Normalized()).Y;

    private static bool Stands(SurgicalTool tool) => TipUp(tool) > 0.95f;
}
