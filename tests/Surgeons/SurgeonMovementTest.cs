namespace Scalpel.Tests.Surgeons;

/// <summary>Real local input and normal surgery physics frames: the walk animation must not bob the camera, the reach
/// origin or the arms and hands the player sees. Crouching changes only their height, while the visible rig leans
/// forward. A fallen surgeon's face tracks the patient.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class SurgeonMovementTest
{
    /// <summary>A surgery with a steady surgeon, its key frames (when wanted) going to surgeon_movement.</summary>
    private static async Task<(SurgeryDriver Driver, KeyFrames Frames)> Begin(string scenario = "appendectomy")
    {
        RenderingServer.RenderLoopEnabled = false;
        var driver = SurgeryDriver.Create();
        await driver.Start(scenario);
        var frames = new KeyFrames();
        driver.AddChild(frames);
        frames.Begin(driver.Surgery, "surgeon_movement");
        driver.OnKeyFrame = async name =>
        {
            if (KeyFrames.Wanted())
            {
                AssertBool(await frames.CaptureView(name)).OverrideFailureMessage("saved " + name).IsTrue();
            }
        };
        SurgeryState.SurgeonIsSteady(driver.Me);
        return (driver, frames);
    }

    private static async Task End(SurgeryDriver driver, KeyFrames frames)
    {
        foreach (var action in (string[])[InputActions.MoveLeft, InputActions.MoveRight, InputActions.Crouch])
        {
            PlayerInput.Action(action, false);
        }
        frames.End();
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }

    [TestCase]
    public async Task WalkingAndCrouchingKeepCameraAndGameplayReachSteady()
    {
        var (driver, frames) = await Begin();
        var me = driver.Me;
        await Frames.Physics(10);
        var initial = me.GlobalPosition;
        SurgeryState.SurgeonHandIsAttached(me, 0, me.Shoulder(0) + (Vector3.Down * (Surgeon.Reach - 0.001f)));
        await driver.Capture("standing");
        var maxDrop = 0f;
        driver.Budget.Clear();
        PlayerInput.Action(InputActions.MoveRight);
        driver.Note("walking sideways with a hand near the reach boundary");
        for (var frame = 0; frame < 24; frame++)
        {
            await Frames.Physics(1);
            CheckStableOrigins(me, 0f);
            AssertBool(me.Strained(0)).OverrideFailureMessage("a target inside reach is not strained by a walking step").IsFalse();
            maxDrop = Mathf.Max(maxDrop, me.WalkDrop);
            if (frame == 8)
            {
                await driver.Capture("walking");
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
            await Frames.Physics(1);
            CheckStableOrigins(me, me.Crouch);
        }
        AssertFloat(me.Crouch).OverrideFailureMessage("normal input reaches the deep squat").IsEqualApprox(1f, 0.001f);
        await driver.Capture("crouched");
        PlayerInput.Action(InputActions.Crouch, false);
        driver.Note("standing back up through normal input");
        for (var frame = 0; frame < 18; frame++)
        {
            await Frames.Physics(1);
            CheckStableOrigins(me, me.Crouch);
        }
        AssertFloat(me.Crouch).OverrideFailureMessage("releasing crouch restores standing height").IsEqualApprox(0f, 0.001f);
        await driver.Capture("recovered");
        driver.Budget.Check(KeyFrames.Wanted());
        await End(driver, frames);
    }

    /// <summary>Walking along the table with a free hand over it, past the patient's arm (hand stitch): over the flat
    /// table top the hand hovers at one height. How low its glove reaches keeps it off the table, and that mustn't bob
    /// with the stride.</summary>
    [TestCase]
    public async Task WalkingKeepsAFreeHandLevelOverTheTable()
    {
        var (driver, frames) = await Begin("hand_stitch");
        var me = driver.Me;
        await driver.PlayerWalksTo(driver.SitePoint(new Vector2(0.5f, 0.5f)));
        await Frames.Physics(30);
        var heights = new List<float>();
        var maxDrop = 0f;
        PlayerInput.Action(InputActions.MoveLeft);
        for (var frame = 0; frame < 60; frame++)
        {
            await Frames.Physics(1);
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
        await End(driver, frames);
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
                .OverrideFailureMessage("the player's own sleeve hangs from the torso without the walk's bob").IsLess(0.0001f);
        }
    }

    [TestCase]
    public async Task DownedVisibleHeadTurnsToPatientOnBothFallSides()
    {
        var (driver, frames) = await Begin();
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
                await driver.Capture(side > 0f ? "fallen_left" : "fallen_right");
                me.Camera.MakeCurrent();
            }
        }
        driver.Budget.Check(KeyFrames.Wanted());
        await End(driver, frames);
    }
}
