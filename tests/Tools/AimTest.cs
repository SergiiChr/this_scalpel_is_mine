namespace Scalpel.Tests.Tools;

/// <summary>
/// Aim tool (MMB held): the mouse turns the held tool about the wrist. Only the wrist moves, the tip follows the mouse,
/// and let go, the tool settles back onto what it rested on. The hands start turned in, the tool pointing across beside
/// the hand, and zoomed all the way in they're see-through. With key frames also the site from above and obliquely and
/// what the surgeon sees: review them for the forearm and glove staying where they were while the scalpel swings right
/// and tips up, the glove bending at the wrist without breaking from the cuff, the scalpel showing beside the hand at
/// rest and the hands see-through zoomed in. Every kind of tool, held at any tilt, comes down where its aim shows.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("smoke"), TestCategory("tool_scalpel"), TestCategory("tool_all"),
 TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class AimTest
{
    [TestCase(Timeout = Limits.Slow)]
    public async Task AimingTurnsTheToolAboutTheWrist()
    {
        RenderingServer.RenderLoopEnabled = false;
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        SurgeryState.PatientIsAsleep(driver.Patient);
        KeyFrames? shots = null;
        if (KeyFrames.Wanted())
        {
            shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(driver.Surgery, "aim");
            driver.OnKeyFrame = async (keyFrame, description) =>
            {
                await shots.Capture(keyFrame, description);
                await shots.CaptureView(keyFrame, description);
            };
        }
        var me = driver.Me;
        var scalpel = (await driver.PlayerRequestsItem("scalpel"))!;
        var site = driver.SitePoint(new Vector2(0.5f, 0.5f));
        await driver.PlayerWalksTo(site);
        await driver.PlayerReaches(site);
        await Frames.Physics(10);
        await driver.Capture("resting", "the scalpel's tip resting on the skin in the middle of the site, the aim dot on it");
        if (shots is not null)
        {
            var camera = me.Camera;
            var offset = camera.HOffset;
            var before = driver.Surgery.Hud.BladeCenter;
            camera.HOffset = offset + 0.08f;
            AssertFloat(camera.UnprojectPosition(me.AimPoint()).DistanceTo(before))
                .OverrideFailureMessage("the changed capture camera requires a new aim projection").IsGreater(10f);
            AssertObject(Surgery.Current).OverrideFailureMessage("the active surgery is available to capture").IsSame(driver.Surgery);
            var shifted = camera.UnprojectPosition(me.AimPoint());
            await shots.CaptureView("camera_shifted", "the view shifted sideways: the aim dot still on the scalpel's tip");
            AssertFloat(driver.Surgery.Hud.BladeCenter.DistanceTo(shifted))
                .OverrideFailureMessage($"the frozen capture refreshes the aim for the changed camera: drawn {driver.Surgery.Hud.BladeCenter}, expected {shifted}, now {camera.UnprojectPosition(me.AimPoint())}").IsLess(1f);
            camera.HOffset = offset;
            var restored = camera.UnprojectPosition(me.AimPoint());
            await shots.CaptureView("camera_restored", "the view back where it was: the aim dot on the scalpel's tip again");
            AssertFloat(driver.Surgery.Hud.BladeCenter.DistanceTo(restored))
                .OverrideFailureMessage("the next capture refreshes the restored camera projection").IsLess(1f);
        }
        driver.Budget.Clear();
        var hand = me.Hands[me.Active];
        var restTip = me.ToLocal(scalpel.TipPosition());
        var wrist = me.ToLocal(Wrist(hand));
        var elbow = me.ToLocal(hand.Elbow);
        // Up first: the tip comes off the skin, so nothing under it (the belly breathing) pushes the hand up.
        await driver.PlayerAims(new Vector2(0f, -4f), 30);
        var raised = me.ToLocal(scalpel.TipPosition());
        AssertFloat(raised.Y - restTip.Y)
            .OverrideFailureMessage($"the mouse moved up lifts the tip off the skin ({(raised.Y - restTip.Y) * 100f:0.0} cm)").IsGreater(0.02f);
        CheckStill(me, hand, wrist, elbow, "tipped up");
        await driver.Capture("aimed_up", "aimed up: the tip lifted off the skin, the hand, wrist and elbow where they were");
        await driver.PlayerAims(new Vector2(4f, 0f), 30);
        var swung = me.ToLocal(scalpel.TipPosition());
        AssertFloat(swung.X - raised.X)
            .OverrideFailureMessage($"the mouse moved right swings the tip right ({(swung.X - raised.X) * 100f:0.0} cm)").IsGreater(0.03f);
        CheckStill(me, hand, wrist, elbow, "swung right");
        await driver.Capture("aimed_right", "aimed right: the tip swung right, still off the skin, the arm where it was");
        SurgeryDriver.PlayerLetsGoOfAim();
        var steps = await TipSteps(me, scalpel, 30);
        var settleStep = (SurgeonHand.SettleSpeed / Engine.PhysicsTicksPerSecond) + 0.002f;
        AssertFloat(steps.X).OverrideFailureMessage($"let go, the tip doesn't jump sideways ({steps.X * 1000f:0.0} mm at most in a frame)")
            .IsLess(0.002f);
        AssertFloat(steps.Y).OverrideFailureMessage($"it eases down, no faster than SettleSpeed ({steps.Y * 1000f:0.0} mm at most in a frame)")
            .IsLess(settleStep);
        var settled = me.ToLocal(scalpel.TipPosition());
        await driver.AssertAndCapture(hand.Raise == 0f && swung.Y - settled.Y > 0.02f,
            $"let go, the tip settles back down onto its spot ({(swung.Y - settled.Y) * 100f:0.0} cm down)", "let_go");
        AssertBool(driver.Surgery.Hud.BladeShown).OverrideFailureMessage("the blade aim shows on the patient").IsTrue();
        // Use tool pressed while aiming brings the raised tip down onto the skin just as gently.
        await driver.PlayerAims(new Vector2(0f, -4f), 20);
        SurgeryDriver.Use();
        steps = await TipSteps(me, scalpel, 30);
        SurgeryDriver.Use(false);
        SurgeryDriver.PlayerLetsGoOfAim();
        AssertFloat(steps.Y)
            .OverrideFailureMessage($"Use tool while aiming eases the tip down onto the skin ({steps.Y * 1000f:0.0} mm at most in a frame)")
            .IsLess(settleStep);
        await Frames.Seconds(0.5f);
        var other = me.Hands[1 - me.Active];
        CheckFaded(hand, other, 0f, "the hands are solid at the first zoom step");
        SurgeryDriver.Press(InputActions.Zoom);
        await Frames.Seconds(0.5f);
        CheckFaded(hand, other, Surgeon.ZoomSeeThrough, "zoomed all the way in with a scalpel, both hands are see-through");
        await driver.Capture("zoomed_in", "zoomed all the way in with a scalpel: both hands see-through, the site visible through them");
        SurgeryDriver.Press(InputActions.Zoom);
        await Frames.Seconds(0.5f);
        CheckFaded(hand, other, 0f, "zoomed back out, the hands are solid again");
        await driver.Capture("zoomed_back_out", "zoomed back out: both hands solid again, the gloves their own color");
        foreach (var mesh in hand.Glove.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>())
        {
            AssertObject(mesh.MaterialOverride).OverrideFailureMessage("zooming out restores the glove material, including its color and relief").IsNull();
        }
        var away = me.GlobalPosition + me.GlobalBasis.Z * 0.5f;
        await driver.PlayerWalksTo(away);
        await driver.PlayerReaches(away);
        await Frames.Physics(10);
        await driver.AssertAndCapture(!driver.Surgery.Hud.BladeShown, "the blade aim is hidden over the floor", "over_floor");
        driver.Budget.Check(shots is not null);
        shots?.End();
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }

    /// <summary>The largest step the tool tip takes in one frame over <paramref name="count"/> frames: across the floor
    /// (X) and up or down (Y), in the surgeon's frame.</summary>
    private static async Task<Vector2> TipSteps(Surgeon me, SurgicalTool tool, int count)
    {
        var largest = Vector2.Zero;
        var last = me.ToLocal(tool.TipPosition());
        for (var i = 0; i < count; i++)
        {
            await Frames.Physics(1);
            var now = me.ToLocal(tool.TipPosition());
            largest = largest.Max(new Vector2(new Vector2(now.X - last.X, now.Z - last.Z).Length(), Mathf.Abs(now.Y - last.Y)));
            last = now;
        }
        return largest;
    }

    /// <summary>The aim shows where a tool comes down: a syringe on the forearm vein, a blade, a stapler and a Gelpi
    /// retractor on the belly, each at the lowest, a middle and the highest tilt, land within a millimeter of the aim
    /// across the skin.</summary>
    [TestCase(Timeout = Limits.Slow)]
    public async Task EveryToolLandsWhereItsAimShows()
    {
        // Nothing to see: the aim is checked against where each tool comes down.
        RenderingServer.RenderLoopEnabled = false;
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        SurgeryState.PatientIsAsleep(driver.Patient);
        var me = driver.Me;
        foreach (var id in (string[])["syringe_3", "scalpel", "skin_stapler", "gelpi"])
        {
            if (driver.FreeTools(id).Count == 0)
            {
                SurgeryState.ToolIsOnTray(driver.Surgery, id);
            }
            var tool = (await driver.PlayerRequestsItem(id))!;
            var target = id == "syringe_3" ? driver.VeinPoint() : driver.SitePoint(new Vector2(0.5f, 0.5f));
            foreach (var tilt in (float[])[SurgeonHand.TiltRange.X, -0.7f, SurgeonHand.TiltRange.Y])
            {
                await driver.PlayerWalksTo(target);
                me.Hands[me.Active].Tilt = tilt;
                await driver.PlayerReaches(target);
                await Frames.Physics(5);
                var aim = me.AimPoint();
                SurgeryDriver.Use();
                await Frames.Physics(30);
                var miss = (tool.TipPosition() - aim).Slide(Vector3.Up).Length();
                AssertFloat(miss).OverrideFailureMessage($"{id} at tilt {tilt:0.0} comes down {miss * 1000f:0.0} mm from its aim")
                    .IsLess(0.001f);
                SurgeryDriver.Use(false);
                await Frames.Physics(10);
                if (tool.InWound)
                {
                    // Set by the press: pressed again it comes out.
                    SurgeryDriver.Use();
                    await Frames.Physics(5);
                    SurgeryDriver.Use(false);
                    await Frames.Physics(5);
                }
            }
            await driver.PlayerPutsDown();
        }
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }

    /// <summary>Only the wrist bends: it and the forearm stay where they were before aiming.</summary>
    private static void CheckStill(Surgeon me, SurgeonHand hand, Vector3 wrist, Vector3 elbow, string what)
    {
        var wristOff = me.ToLocal(Wrist(hand)).DistanceTo(wrist);
        var elbowOff = me.ToLocal(hand.Elbow).DistanceTo(elbow);
        AssertFloat(wristOff).OverrideFailureMessage($"{what}, the wrist stays where it was ({wristOff * 1000f:0.0} mm off)").IsLess(0.001f);
        AssertFloat(elbowOff).OverrideFailureMessage($"{what}, the forearm stays where it was (elbow {elbowOff * 1000f:0.0} mm off)")
            .IsLess(0.001f);
    }

    private static void CheckFaded(SurgeonHand hand, SurgeonHand other, float amount, string what)
    {
        var faded = (Fade(hand), Fade(other));
        AssertBool(Mathf.IsEqualApprox(faded.Item1, amount) && Mathf.IsEqualApprox(faded.Item2, amount))
            .OverrideFailureMessage($"{what} {faded}").IsTrue();
    }

    /// <summary>How see-through a hand is drawn (0 solid).</summary>
    private static float Fade(SurgeonHand hand)
    {
        var glove = hand.FindChildren("*", nameof(GeometryInstance3D), true, false).OfType<GeometryInstance3D>().First();
        return glove.MaterialOverride is StandardMaterial3D ghost ? 1f - ghost.AlbedoColor.A : 0f;
    }

    /// <summary>The wrist where the hand holds it: the glove's own frame, without its stress tremor and shiver.
    /// </summary>
    private static Vector3 Wrist(SurgeonHand hand) => hand.Glove.GlobalPosition - hand.Shiver - hand.Tremor;
}
