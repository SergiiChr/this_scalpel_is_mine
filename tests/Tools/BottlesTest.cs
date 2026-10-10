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
public class BottlesTest
{
    private const string Vial = "vial_propofol";
    /// <summary>How far from a bottle the key frames look at it (meters).</summary>
    private const float View = 0.3f;

    [TestCase]
    public async Task BottlesComeStandingAndStandWhenGrabIsHeld()
    {
        RenderingServer.RenderLoopEnabled = false;
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        SurgeryState.PatientIsAsleep(driver.Patient);
        SurgicalTool vial = null!;
        KeyFrames? shots = null;
        if (KeyFrames.Wanted())
        {
            shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(driver.Surgery, "bottles");
            driver.OnKeyFrame = (keyFrame, description) => shots.CaptureAt(keyFrame, description, vial.Middle(), View);
        }
        driver.Budget.Clear();
        var onTray = driver.FreeTools(Vial);
        await driver.PlayerOrders([Vial]);
        await Frames.Until(() => driver.FreeTools(Vial).Count > onTray.Count, 60f);
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

        driver.Budget.Check(shots is not null, broken: "the frame the ordered vial arrives takes 17-19 ms of the game's own work");
        shots?.End();
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }

    private static async Task PickUp(SurgeryDriver driver, SurgicalTool vial)
    {
        await driver.PlayerWalksTo(vial.GlobalPosition);
        await driver.PlayerReaches(vial.GlobalPosition);
        SurgeryDriver.Tap(InputActions.Grab);
        await Frames.Physics(5);
        AssertObject(driver.Me.HeldTool(driver.Me.Active)).OverrideFailureMessage("the vial is picked up").IsEqual(vial);
    }

    /// <summary>Grab held a little over a second, then let go: the bottle in the hand stands upright where it is.
    /// </summary>
    private static async Task HoldGrab()
    {
        SurgeryDriver.Press(InputActions.Grab);
        await Frames.Seconds(Surgeon.StandHold + 0.2f);
        SurgeryDriver.Release(InputActions.Grab);
        await Frames.Seconds(1f);
    }

    /// <summary>How far up a tool's tip end points (1: straight up).</summary>
    private static float TipUp(SurgicalTool tool) => (-tool.GlobalBasis.Z.Normalized()).Y;

    private static bool Stands(SurgicalTool tool) => TipUp(tool) > 0.95f;
}
