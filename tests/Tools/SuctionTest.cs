namespace Scalpel.Tests.Tools;

/// <summary>The visible inlet follows the player's aim and clears blood locally from intact skin.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("tool_suction"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class SuctionTest {
    [TestCase]
    public async Task PlayerClearsBloodAtTheSuctionInletAndLeavesDistantBlood() {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        KeyFrames? shots = null;
        try {
            await driver.Start("appendectomy");
            SurgeryState.PatientIsAsleep(driver.Patient);
            SurgeryState.SurgeonIsSteady(driver.Me);
            shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(driver.Surgery, "suction_contact");
            driver.OnKeyFrame = async (name, description) => {
                if (KeyFrames.Wanted()) {
                    await shots.Capture(name, description);
                }
            };
            var tool = (await driver.PlayerRequestsItem("suction"))!;
            var uv = new Vector2(0.5f, 0.5f);
            var distant = new Vector2(0.75f, 0.65f);
            var at = driver.SitePoint(uv);
            await driver.PlayerWalksTo(at);
            await driver.PlayerReaches(at);
            await driver.SetLevel(1);
            var radius = driver.Body.MetersToUv(0.015f);
            SurgeryState.SkinIsBloody(driver.Patient, uv, radius);
            SurgeryState.SkinIsBloody(driver.Patient, distant, radius);
            var before = driver.Body.BloodAt(uv);
            AssertFloat(before).OverrideFailureMessage("blood covers the skin beneath the inlet before suction").IsGreater(0.5f);
            await driver.Capture("before", "two small blood patches on intact skin, with the suction handpiece held over the central patch");
            SurgeryDriver.Use();
            await Frames.Seconds(2f);
            AssertFloat(driver.Body.BloodAt(uv))
                .OverrideFailureMessage("using suction clears the patch at its inlet").IsLess(before * 0.2f);
            AssertFloat(driver.Body.BloodAt(distant))
                .OverrideFailureMessage("suction leaves the distant blood patch in place").IsGreater(0.1f);
            AssertFloat(tool.TipPosition().DistanceTo(driver.SitePoint(uv)))
                .OverrideFailureMessage("the visible inlet remains at the aimed skin point").IsLess(0.015f);
            await driver.Capture("clearing", "the suction inlet touches the central patch, clearing blood around its mouth while the distant patch remains");
            if (KeyFrames.Wanted()) {
                await shots.CaptureView("clearing_view", "the player holds the suction handpiece on the cleared skin; its bent shaft and blue hose remain visible");
            }
            SurgeryDriver.Use(false);
            await driver.PlayerPutsDown();
            await Frames.Physics(3);
            AssertFloat(driver.Body.BloodAt(uv))
                .OverrideFailureMessage("the cleared patch remains clear after release").IsLess(before * 0.2f);
            await driver.Capture("released", "after putting the suction down, the central patch stays clear and the distant blood remains");
        } finally {
            SurgeryDriver.Use(false);
            shots?.End();
            await driver.Stop();
            RenderingServer.RenderLoopEnabled = true;
        }
    }
}
