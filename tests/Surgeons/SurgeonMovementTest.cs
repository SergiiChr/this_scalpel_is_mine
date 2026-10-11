namespace Scalpel.Tests.Surgeons;

/// <summary>Real local input and normal surgery physics frames: the walk animation must not bob the camera, the reach
/// origin or the arms and hands the player sees. Crouching changes only their height, while the visible rig leans
/// forward. Zoomed in over an open belly the surgeon leans in to look down into it. A fallen surgeon's face tracks the
/// patient.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class SurgeonMovementTest
{
    private SurgeryDriver? _driver;
    private KeyFrames? _frames;

    /// <summary>A surgery with a steady surgeon, its key frames (when wanted) going to surgeon_movement.</summary>
    private async Task<SurgeryDriver> Begin(string scenario = "appendectomy")
    {
        RenderingServer.RenderLoopEnabled = false;
        var driver = _driver = SurgeryDriver.Create();
        await driver.Start(scenario);
        var frames = _frames = new KeyFrames();
        driver.AddChild(frames);
        frames.Begin(driver.Surgery, "surgeon_movement");
        driver.OnKeyFrame = async (name, description) =>
        {
            if (KeyFrames.Wanted())
            {
                await frames.CaptureView(name, description);
            }
        };
        SurgeryState.SurgeonIsSteady(driver.Me);
        return driver;
    }

    [AfterTest]
    public async Task End()
    {
        try
        {
            foreach (var action in (string[])[InputActions.MoveLeft, InputActions.MoveRight, InputActions.MoveBack, InputActions.Crouch])
            {
                PlayerInput.Action(action, false);
            }
            await PlayerInput.Delivered();
        }
        finally
        {
            try
            {
                _frames?.End();
                if (_driver is { } driver)
                {
                    await driver.Stop();
                }
            }
            finally
            {
                _frames = null;
                _driver = null;
                RenderingServer.RenderLoopEnabled = true;
            }
        }
    }

    [TestCase]
    public async Task WalkingAndCrouchingKeepCameraAndGameplayReachSteady()
    {
        var driver = await Begin();
        var me = driver.Me;
        await Frames.Physics(10);
        var initial = me.GlobalPosition;
        SurgeryState.SurgeonHandIsAttached(me, 0, me.Shoulder(0) + (Vector3.Down * (Surgeon.Reach - 0.001f)));
        await driver.Capture("standing", "standing still, one hand reaching down at the edge of its reach");
        var maxDrop = 0f;
        driver.Budget.Clear();
        PlayerInput.Action(InputActions.MoveRight);
        driver.Note("walking sideways with a hand near the reach boundary");
        for (var frame = 0; frame < 24; frame++)
        {
            // PhysicsFrame is emitted before node physics callbacks; inspect the completed gameplay frame.
            await PlayerInput.Delivered();
            CheckStableOrigins(me, 0f);
            AssertBool(me.Strained(0)).OverrideFailureMessage("a target inside reach is not strained by a walking step").IsFalse();
            maxDrop = Mathf.Max(maxDrop, me.WalkDrop);
            if (frame == 8)
            {
                await driver.Capture("walking", "a walking step: the view steady, the reaching hand where it was");
            }
        }
        PlayerInput.Action(InputActions.MoveRight, false);
        AssertFloat(me.GlobalPosition.DistanceTo(initial)).OverrideFailureMessage("normal movement input actually walked the surgeon")
            .IsGreater(0.1f);
        AssertFloat(maxDrop).OverrideFailureMessage("the visible leg animation moved the pelvis while gameplay origins stayed steady")
            .IsGreater(0.01f);
        PlayerInput.Action(InputActions.Crouch);
        driver.Note("crouching through normal input");
        for (var frame = 0; frame < 18; frame++)
        {
            // PhysicsFrame is emitted before node physics callbacks; inspect the completed gameplay frame.
            await PlayerInput.Delivered();
            CheckStableOrigins(me, me.Crouch);
        }
        AssertFloat(me.Crouch).OverrideFailureMessage("normal input reaches the deep squat").IsEqualApprox(1f, 0.001f);
        await driver.Capture("crouched", "in the deep squat: the view lowered, the hands where they were");
        PlayerInput.Action(InputActions.Crouch, false);
        driver.Note("standing back up through normal input");
        for (var frame = 0; frame < 18; frame++)
        {
            // PhysicsFrame is emitted before node physics callbacks; inspect the completed gameplay frame.
            await PlayerInput.Delivered();
            CheckStableOrigins(me, me.Crouch);
        }
        AssertFloat(me.Crouch).OverrideFailureMessage("releasing crouch restores standing height").IsEqualApprox(0f, 0.001f);
        await driver.Capture("recovered", "back up to standing height");
        driver.Budget.Check(KeyFrames.Wanted());
    }

    /// <summary>Walking along the table with a free hand over it, past the patient's arm (hand stitch): over the flat
    /// table top the hand hovers at one height. How low its glove reaches keeps it off the table, and that mustn't bob
    /// with the stride.</summary>
    [TestCase]
    public async Task WalkingKeepsAFreeHandLevelOverTheTable()
    {
        var driver = await Begin("hand_stitch");
        var me = driver.Me;
        await driver.PlayerWalksTo(driver.SitePoint(new Vector2(0.5f, 0.5f)));
        await Frames.Physics(30);
        var heights = new List<float>();
        var maxDrop = 0f;
        PlayerInput.Action(InputActions.MoveLeft);
        for (var frame = 0; frame < 60; frame++)
        {
            // PhysicsFrame is emitted before node physics callbacks; inspect the completed gameplay frame.
            await PlayerInput.Delivered();
            var hand = me.Hands[me.Active];
            var under = me.SurfaceBelow(hand.Target);
            if (!under.Soft && under.Y > 0.5f)
            {
                heights.Add(hand.GlobalPosition.Y - under.Y);
            }
            maxDrop = Mathf.Max(maxDrop, me.WalkDrop);
        }
        PlayerInput.Action(InputActions.MoveLeft, false);
        AssertFloat(maxDrop).OverrideFailureMessage($"the legs strode (pelvis dropped {maxDrop * 1000f:0} mm)").IsGreater(0.01f);
        AssertInt(heights.Count).OverrideFailureMessage("the hand went over the table top").IsGreater(16);
        // Its first few frames there it's still coming down off the patient's arm.
        var level = heights.Skip(6).ToList();
        var spread = level.DefaultIfEmpty(0f).Max() - level.DefaultIfEmpty(0f).Min();
        AssertFloat(spread).OverrideFailureMessage($"and hovered at one height over it ({spread * 1000f:0.0} mm up and down)")
            .IsLess(0.001f);
    }

    /// <summary>
    /// Standing at an open belly with the hand in the opening, zooming in bends the surgeon over it: the eyes come over
    /// the opening and look down into it from above, gradually, without turning sideways, so a mouse move to the right
    /// still moves the hand right on screen. Their partner sees them bent over the table. Walking away zooms back out
    /// and straightens them up, just as gradually. Looking up while leaning in, the look is the player's; the hand moved
    /// off the opening onto the skin, they straighten up but stay zoomed in.
    /// </summary>
    [TestCase]
    public async Task ZoomingInOverAnOpeningLeansInWhileTheHandIsInIt()
    {
        var driver = await Begin();
        var me = driver.Me;
        var patient = driver.Patient;
        var at = new Vector2(0.5f, 0.62f);
        SurgeryState.SkinIsCut(patient, at - new Vector2(0.15f, 0f), at + new Vector2(0.15f, 0f), 1f);
        await Frames.Seconds(1f);
        AssertBool(driver.Body.IsOpen(at)).OverrideFailureMessage("the belly stands open").IsTrue();
        var opening = driver.SitePoint(at);
        await driver.PlayerWalksTo(opening);
        await driver.PlayerReaches(opening);
        await Frames.Physics(10);
        var puppet = SurgeryState.SurgeonHasRemoteCopy(me);
        // It stands where the surgeon does: shown only from beside, it would fill the surgeon's own view.
        puppet.Visible = false;
        var standingEyes = me.ToLocal(me.Camera.GlobalPosition);
        var standingPitch = me.Pitch;
        await driver.Capture("at_the_opening", "standing at the open belly, the hand over the opening, the view level");
        driver.Budget.Clear();

        SurgeryDriver.Tap(InputActions.Zoom);
        driver.Note("zooming in over the opening");
        await FollowView(me, puppet, 60);
        var spot = me.LeanSpot;
        AssertObject(spot).OverrideFailureMessage("the view leans in on the opening").IsNotNull();
        AssertFloat(me.Lean).OverrideFailureMessage($"the torso bends forward ({me.Lean:0.00} rad)").IsGreater(0.2f);
        var eyes = me.Camera.GlobalPosition;
        var ahead = standingEyes.Z - me.ToLocal(eyes).Z;
        AssertFloat(ahead).OverrideFailureMessage($"the eyes come forward over the table ({ahead * 100f:0} cm)").IsGreater(0.1f);
        AssertFloat(eyes.Y - spot!.Value.Y).OverrideFailureMessage($"and stay clear above the opening ({(eyes.Y - spot.Value.Y) * 100f:0} cm)")
            .IsGreater(Surgeon.LeanClearance - 0.01f);
        var forward = -me.Camera.GlobalBasis.Z;
        AssertFloat(forward.Dot((spot.Value - eyes).Normalized())).OverrideFailureMessage("looking down into the opening")
            .IsGreater(0.99f);
        AssertFloat(me.Camera.GlobalBasis.X.Dot(me.GlobalBasis.X)).OverrideFailureMessage("without turning sideways")
            .IsGreater(0.9999f);
        var hand = me.Hands[me.Active];
        var tip = hand.GlobalPosition + hand.TipOffset(0.05f);
        AssertBool(me.Camera.IsPositionInFrustum(tip) && me.Camera.IsPositionInFrustum(hand.GlobalPosition) && hand.Visible)
            .OverrideFailureMessage("the hand in the opening stays in view").IsTrue();
        await driver.Capture("leaning_in", "zoomed in and leaning in: looking down into the open belly from above, the "
            + "see-through hand in the opening, nothing clipped by drapes or organs");

        var onScreen = me.Camera.UnprojectPosition(tip);
        await driver.PlayerReaches(opening + (me.GlobalBasis.X * 0.03f));
        await Frames.Physics(10);
        var moved = me.Camera.UnprojectPosition(hand.GlobalPosition + hand.TipOffset(0.05f)) - onScreen;
        AssertFloat(moved.X).OverrideFailureMessage($"moving the hand right moves it right on screen ({moved})").IsGreater(20f);
        AssertFloat(Mathf.Abs(moved.Y)).OverrideFailureMessage($"and not up or down ({moved})").IsLess(Mathf.Abs(moved.X) * 0.2f);
        AssertObject(me.LeanSpot).OverrideFailureMessage("still leaning in over the opening").IsNotNull();

        await FollowView(me, puppet, 1);
        AssertFloat(puppet.Lean).OverrideFailureMessage("the partner gets how far they lean").IsEqualApprox(me.Lean, 0.001f);
        var neck = puppet.FaceModel.GlobalTransform * Surgeon.HeadPivot;
        var bent = puppet.ToLocal(neck).Z;
        AssertFloat(bent).OverrideFailureMessage($"and sees their head bent forward over the table ({bent * 100f:0} cm ahead)")
            .IsLess(-0.15f);
        if (KeyFrames.Wanted())
        {
            var camera = new Camera3D { Fov = 55f };
            driver.Surgery.AddChild(camera);
            camera.GlobalPosition = neck + (puppet.GlobalBasis * new Vector3(1.1f, 0.2f, 0.3f));
            camera.LookAt(neck + (Vector3.Down * 0.4f));
            camera.MakeCurrent();
            puppet.Visible = true;
            await driver.Capture("leaning_seen", "seen from beside: the surgeon bent forward from the hips over the open "
                + "belly, head down over it, feet where they stood, nothing passing through the table");
            puppet.Visible = false;
            me.Camera.MakeCurrent();
            camera.QueueFree();
        }

        PlayerInput.Action(InputActions.MoveBack);
        driver.Note("walking away from the table");
        await FollowView(me, puppet, 30);
        PlayerInput.Action(InputActions.MoveBack, false);
        AssertInt(me.Zoom).OverrideFailureMessage("walking away zooms back out").IsEqual(0);
        await FollowView(me, puppet, 30);
        AssertFloat(me.Lean).OverrideFailureMessage($"and straightens back up ({me.Lean:0.000} rad)").IsLess(0.01f);
        AssertFloat(me.Pitch).OverrideFailureMessage("looking where they did before leaning in").IsEqualApprox(standingPitch, 0.02f);
        await driver.Capture("walked_away", "walked back from the table: zoomed out, standing straight, looking at the table as before, the hands solid");

        await driver.PlayerWalksTo(opening);
        await driver.PlayerReaches(opening);
        SurgeryDriver.Tap(InputActions.Zoom);
        driver.Note("leaning in again, then looking up");
        await FollowView(me, puppet, 60);
        PlayerInput.Mouse(new Vector2(0f, -40f));
        await PlayerInput.Delivered();
        var lookedAt = me.Pitch;
        await FollowView(me, puppet, 20);
        AssertFloat(me.Pitch).OverrideFailureMessage("looking up while leaning in, the look stays where the player put it")
            .IsEqualApprox(lookedAt, 0.001f);
        await driver.PlayerReaches(opening + (me.GlobalBasis.Z * 0.05f));
        driver.Note("moving the hand off the opening onto the skin");
        await FollowView(me, puppet, 60);
        AssertObject(me.LeanSpot).OverrideFailureMessage("the hand on the skin beside the opening, the lean lets go").IsNull();
        AssertFloat(me.Lean).OverrideFailureMessage($"and the surgeon straightens up ({me.Lean:0.000} rad)").IsLess(0.01f);
        AssertInt(me.Zoom).OverrideFailureMessage("still zoomed in").IsEqual(Surgeon.ZoomFov.Length - 1);
        driver.Budget.Check(KeyFrames.Wanted());
    }

    /// <summary>Plays <paramref name="frames"/> physics frames, the partner's copy getting each one, and checks the view
    /// moves gradually: no snap of the eyes within the body or of where they look.</summary>
    private static async Task FollowView(Surgeon me, Surgeon puppet, int frames)
    {
        var eyes = me.ToLocal(me.Camera.GlobalPosition);
        var look = me.GlobalBasis.Inverse() * -me.Camera.GlobalBasis.Z;
        for (var frame = 0; frame < frames; frame++)
        {
            SurgeryState.SurgeonCopyHasOwnerState(me, puppet);
            await Frames.Physics(1);
            var nextEyes = me.ToLocal(me.Camera.GlobalPosition);
            var nextLook = me.GlobalBasis.Inverse() * -me.Camera.GlobalBasis.Z;
            AssertFloat(nextEyes.DistanceTo(eyes)).OverrideFailureMessage($"the eyes move gradually ({nextEyes.DistanceTo(eyes) * 1000f:0} mm in a frame)")
                .IsLess(0.04f);
            AssertFloat(nextLook.AngleTo(look)).OverrideFailureMessage($"and so does the look ({nextLook.AngleTo(look):0.000} rad in a frame)")
                .IsLess(0.08f);
            eyes = nextEyes;
            look = nextLook;
        }
    }

    private static void CheckStableOrigins(Surgeon me, float crouch)
    {
        AssertFloat(me.ToLocal(me.Camera.GlobalPosition).DistanceTo(new Vector3(0f, Surgeon.EyeHeight - (crouch * Surgeon.CrouchDrop), 0f)))
            .OverrideFailureMessage("camera only lowers with crouch; no animation bob or sway").IsLess(0.0001f);
        for (var index = 0; index < 2; index++)
        {
            var side = index == 0 ? -1f : 1f;
            var expected = new Vector3(side * Surgeon.ShoulderOffset.X, Surgeon.ShoulderOffset.Y - (crouch * Surgeon.CrouchDrop),
                Surgeon.ShoulderOffset.Z);
            AssertFloat(me.ToLocal(me.Shoulder(index)).DistanceTo(expected))
                .OverrideFailureMessage("reach origin is independent of torso and leg animation").IsLess(0.0001f);
            var start = me.Hands[index].UpperSleeve.GlobalTransform * new Vector3(0f, -0.5f, 0f);
            AssertFloat(start.DistanceTo(me.SteadyShoulder(index)))
                .OverrideFailureMessage($"the player's own sleeve hangs from the torso without the walk's bob ({start.DistanceTo(me.SteadyShoulder(index)) * 1000f:0.000} mm, hand {index})").IsLess(0.0001f);
        }
    }

    [TestCase]
    public async Task DownedVisibleHeadTurnsToPatientOnBothFallSides()
    {
        var driver = await Begin();
        var me = driver.Me;
        var puppet = SurgeryState.SurgeonHasRemoteCopy(me);
        var camera = new Camera3D { Fov = 55f };
        driver.Surgery.AddChild(camera);
        driver.Budget.Clear();
        foreach (var side in (float[])[-1f, 1f])
        {
            SurgeryState.SurgeonIsKnockedOut(me, side);
            driver.Note("falling onto the floor and looking at the patient");
            for (var frame = 0; frame < 50; frame++)
            {
                SurgeryState.SurgeonCopyHasOwnerState(me, puppet);
                await Frames.Physics(1);
            }
            AssertFloat(me.Down).OverrideFailureMessage("normal physics reaches the lying pose").IsEqualApprox(1f, 0.001f);
            var patient = driver.Patient.GlobalPosition;
            var cameraToward = (patient - me.Camera.GlobalPosition).Slide(Vector3.Up).Normalized();
            AssertFloat((-me.Camera.GlobalBasis.Z).Slide(Vector3.Up).Normalized().Dot(cameraToward))
                .OverrideFailureMessage("the fallen first-person camera still turns toward the patient").IsGreater(0.999f);
            var face = puppet.FaceModel;
            AssertFloat((-face.GlobalBasis.Z).Dot(Vector3.Up)).OverrideFailureMessage("visible fallen face keeps the owner's upward look")
                .IsEqualApprox(Mathf.Sin(puppet.Pitch), 0.001f);
            var pivot = face.GlobalTransform * Surgeon.HeadPivot;
            var toPatient = (patient - pivot).Slide(Vector3.Up).Normalized();
            AssertFloat((-face.GlobalBasis.Z).Slide(Vector3.Up).Normalized().Dot(toPatient))
                .OverrideFailureMessage("visible face looks toward the patient from either fall side").IsGreater(0.999f);
            var anchor = puppet.Joint("Torso").GlobalTransform
                * (new Vector3(0f, Surgeon.EyeHeight - Surgeon.HipHeight, 0f) + Surgeon.HeadPivot);
            AssertFloat(pivot.DistanceTo(anchor)).OverrideFailureMessage("patient-facing yaw preserves the neck anchor").IsLess(0.0001f);
            if (KeyFrames.Wanted())
            {
                camera.GlobalPosition = pivot + new Vector3(side * 0.8f, 0.6f, -0.7f);
                camera.LookAt(pivot);
                camera.MakeCurrent();
                await driver.Capture(side > 0f ? "fallen_left" : "fallen_right",
                    "the fallen surgeon lying on the floor, face turned up toward the patient, the head on the neck");
                me.Camera.MakeCurrent();
            }
        }
        driver.Budget.Check(KeyFrames.Wanted());
    }
}
