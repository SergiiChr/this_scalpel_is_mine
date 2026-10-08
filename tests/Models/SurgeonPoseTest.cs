namespace Scalpel.Tests.Models;

/// <summary>Grounded deep squat and torso-anchored neck. The puppet receives the owner's packed state; hard geometry
/// checks cover the transition and rendered front/side/back views expose intersections.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class SurgeonPoseTest
{
    private Room _room = null!;
    private Surgeon _owner = null!;
    private Surgeon _puppet = null!;
    private Camera3D _camera = null!;
    /// <summary>Key frames taken so far by every case of the suite: they number one folder between them.</summary>
    private static int _shots;

    /// <summary>The owner and the puppet that mirrors them, in an operating room shell with a camera for key frames.
    /// Rendering stays off except while a key frame is saved.</summary>
    private void Begin()
    {
        RenderingServer.RenderLoopEnabled = false;
        _room = new Room();
        Frames.Root.AddChild(_room);
        SurgeryState.RoomHasShell(_room, "or");
        _owner = SurgeryState.SurgeonIsPoseFixture(_room, 2, false);
        _puppet = SurgeryState.SurgeonIsPoseFixture(_room, 3);
        _camera = new Camera3D { Fov = 55f };
        _room.AddChild(_camera);
        _camera.MakeCurrent();
    }

    private async Task End()
    {
        _room.QueueFree();
        await Frames.NextProcess();
        RenderingServer.RenderLoopEnabled = true;
    }

    private void ReceivePose(float crouch, float pitch, float speed = 0f) =>
        SurgeryState.SurgeonPoseIsReceived(_owner, _puppet, crouch, pitch, speed);

    [TestCase]
    public async Task GeneratedLegPivotsMatchRuntimeDimensions()
    {
        Begin();
        foreach (var suffix in (string[])["L", "R"])
        {
            var side = suffix == "L" ? -1f : 1f;
            var thigh = _puppet.Joint("Leg" + suffix);
            var shin = _puppet.Joint("Shin" + suffix);
            var shoe = _puppet.Joint("Shoe" + suffix);
            AssertFloat(thigh.Position.Y).OverrideFailureMessage("generated hip height matches the solver")
                .IsEqualApprox(Surgeon.HipHeight, 0.0001f);
            AssertFloat(thigh.Position.X).OverrideFailureMessage("generated hip spacing matches the solver")
                .IsEqualApprox(side * Surgeon.HipWidth, 0.0001f);
            AssertFloat(thigh.GlobalPosition.DistanceTo(shin.GlobalPosition))
                .OverrideFailureMessage("generated thigh matches the solver")
                .IsEqualApprox(Surgeon.ThighLength, 0.0001f);
            AssertFloat(shin.GlobalPosition.DistanceTo(shoe.GlobalPosition))
                .OverrideFailureMessage("generated shin matches the solver").IsEqualApprox(Surgeon.ShinLength, 0.0001f);
            AssertFloat(shoe.GlobalPosition.Y).OverrideFailureMessage("generated ankle matches the solver")
                .IsEqualApprox(Surgeon.AnkleHeight, 0.0001f);
        }
        await End();
    }

    [TestCase]
    public async Task DeepSquatKeepsHeelsDownSleevesClearAndRecovers()
    {
        Begin();
        ReceivePose(0f, 0f);
        CheckNeckInsideCollar();
        await Capture("standing", new Vector3(1.4f, 1.25f, -1.85f), new Vector3(0f, 0.85f, 0f));
        for (var step = 0; step <= 60; step++)
        {
            ReceivePose(step / 60f, -0.55f);
            CheckLegGeometry($"lowering {step}");
            if (_puppet.Crouch > 0.55f)
            {
                CheckSleevesClearKnees();
            }
            await Frames.NextProcess();
            if (step == 30)
            {
                await Capture("squat_transition", new Vector3(1.5f, 1.15f, -1.6f), new Vector3(0f, 0.7f, 0f));
            }
        }
        await Capture("squat_front", new Vector3(0.8f, 0.85f, -1.75f), new Vector3(0f, 0.48f, 0f));
        await Capture("squat_side", new Vector3(2.2f, 0.75f, 0.1f), new Vector3(0f, 0.48f, 0f));
        var hip = _puppet.Joint("LegL").GlobalPosition;
        var knee = _puppet.Joint("ShinL").GlobalPosition;
        AssertFloat(hip.Y).OverrideFailureMessage("hips drop below knee height in a deep squat").IsLess(0.24f);
        AssertFloat(knee.Y).OverrideFailureMessage("deeply flexed knee sits above the hips").IsGreater(hip.Y + 0.08f);
        AssertFloat(knee.Z).OverrideFailureMessage("knees point forward, rather than rigid legs pointing behind")
            .IsLess(hip.Z - 0.2f);
        AssertFloat(Mathf.Abs(knee.X)).OverrideFailureMessage("knees open out to make room for the torso")
            .IsGreater(0.2f);
        AssertFloat(_puppet.Joint("Torso").GlobalBasis.Y.Dot(Vector3.Forward))
            .OverrideFailureMessage("torso leans forward at the hips in the deep squat").IsGreater(0.4f);
        AssertFloat(_puppet.FaceModel.GlobalPosition.Z)
            .OverrideFailureMessage("head stays forward of the hips rather than leaning back").IsLess(hip.Z - 0.2f);
        CheckSleevesClearKnees();
        // The floor-reaching hand pose must also clear the knee, with the same controlled hand position.
        foreach (var height in (float[])[0.04f, 0.2f, 0.3f, 0.55f])
        {
            SurgeryState.SurgeonHandsAreAtHeight(_puppet, height);
            CheckSleevesClearKnees();
        }
        AssertFloat(_puppet.Camera.GlobalPosition.Y)
            .OverrideFailureMessage("view height follows crouch gameplay height")
            .IsEqualApprox(Surgeon.EyeHeight - Surgeon.CrouchDrop, 0.001f);
        for (var step = 0; step <= 60; step++)
        {
            ReceivePose(1f - (step / 60f), -0.55f);
            CheckLegGeometry($"rising {step}");
            if (_puppet.Crouch > 0.55f)
            {
                CheckSleevesClearKnees();
            }
            await Frames.NextProcess();
        }
        await Capture("recovered", new Vector3(1.4f, 1.25f, -1.85f), new Vector3(0f, 0.85f, 0f));
        await End();
    }

    [TestCase]
    public async Task NeckAnchorAndReceivedPoseAgreeAtEveryCrouchAndLookAngle()
    {
        Begin();
        // Neck/collar dimensions correspond to tools/assetgen/surgeon.py:_body(); keep these samples in sync.
        foreach (var crouch in (float[])[0f, 0.5f, 1f])
        {
            foreach (var pitch in (float[])[Surgeon.LookPitch.X, 0f, Surgeon.LookPitch.Y])
            {
                ReceivePose(crouch, pitch);
                var torso = _puppet.Joint("Torso");
                var neck = (Node3D)_puppet.BodyModel.FindChild("Neck", true, false);
                var eyes = new Vector3(0f, Surgeon.EyeHeight - Surgeon.HipHeight, 0f);
                var anchor = torso.GlobalTransform * (eyes + Surgeon.HeadPivot);
                AssertFloat((_puppet.FaceModel.GlobalTransform * Surgeon.HeadPivot).DistanceTo(anchor))
                    .OverrideFailureMessage("head rotates about the top of the neck at every crouch/look angle")
                    .IsLess(0.001f);
                AssertFloat(torso.ToLocal(neck.GlobalPosition).Z)
                    .OverrideFailureMessage("neck stays anchored inside the collar")
                    .IsEqualApprox(0.015f, 0.001f);
                AssertBool(_puppet.FaceModel.GlobalTransform.IsEqualApprox(_owner.FaceModel.GlobalTransform))
                    .OverrideFailureMessage("owner and puppet head transforms agree").IsTrue();
                foreach (var joint in Surgeon.JointNames)
                {
                    AssertBool(_owner.Joint(joint).GlobalTransform.IsEqualApprox(_puppet.Joint(joint).GlobalTransform))
                        .OverrideFailureMessage($"{joint} agrees after pose synchronization").IsTrue();
                }
            }
        }
        ReceivePose(0f, Surgeon.LookPitch.X);
        await Capture("looking_down_back", new Vector3(1.25f, 1.7f, 1.6f), new Vector3(0f, 1.46f, 0f));
        ReceivePose(0f, Surgeon.LookPitch.Y);
        await Capture("looking_up_side", new Vector3(1.6f, 1.7f, -0.3f), new Vector3(0f, 1.48f, 0f));
        await End();
    }

    [TestCase]
    public async Task WalkingRigPreservesLegLengthsAndAPlantedFoot()
    {
        Begin();
        for (var step = 0; step < 60; step++)
        {
            ReceivePose(0f, 0f, Surgeon.WalkSpeed);
            CheckLegGeometry($"walking {step}", true);
            await Frames.NextProcess();
            if (step == 8)
            {
                await Capture("walking_stride", new Vector3(1.5f, 1.15f, -1.6f), new Vector3(0f, 0.8f, 0f));
            }
        }
        await End();
    }

    private void CheckNeckInsideCollar()
    {
        var torso = (MeshInstance3D)_puppet.Joint("Torso");
        var neck = (MeshInstance3D)_puppet.BodyModel.FindChild("Neck", true, false);
        var shell = torso.Mesh.GetFaces();
        var vertices = neck.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        // The collar cover sample at 1.48 m matches the neck/collar profile in tools/assetgen/surgeon.py:_body().
        var exposed = 0;
        foreach (var vertex in vertices)
        {
            var at = torso.ToLocal(neck.ToGlobal(vertex));
            if (at.Y > 1.48f - Surgeon.HipHeight)
            {
                continue;
            }
            var axis = new Vector3(0f, at.Y, 0f);
            var outward = (at - axis).Normalized();
            var covered = false;
            for (var t = 0; t < shell.Length; t += 3)
            {
                var hit = Geometry3D.RayIntersectsTriangle(axis, outward, shell[t], shell[t + 1], shell[t + 2]);
                if (hit.VariantType != Variant.Type.Nil
                    && axis.DistanceTo(hit.AsVector3()) >= axis.DistanceTo(at) - 0.001f)
                {
                    covered = true;
                    break;
                }
            }
            if (!covered)
            {
                exposed++;
            }
        }
        AssertInt(exposed).OverrideFailureMessage("every neck vertex below the collar is inside the actual torso mesh")
            .IsEqual(0);
    }

    private void CheckLegGeometry(string what, bool walking = false)
    {
        var lowest = float.PositiveInfinity;
        foreach (var suffix in (string[])["L", "R"])
        {
            var thigh = _puppet.Joint("Leg" + suffix);
            var shin = _puppet.Joint("Shin" + suffix);
            var shoe = _puppet.Joint("Shoe" + suffix);
            AssertFloat(thigh.GlobalPosition.DistanceTo(shin.GlobalPosition))
                .OverrideFailureMessage($"{what}: thigh length")
                .IsEqualApprox(Surgeon.ThighLength, 0.0001f);
            AssertFloat(shin.GlobalPosition.DistanceTo(shoe.GlobalPosition))
                .OverrideFailureMessage($"{what}: shin length")
                .IsEqualApprox(Surgeon.ShinLength, 0.0001f);
            AssertFloat(shoe.GlobalBasis.Y.Dot(Vector3.Up)).OverrideFailureMessage($"{what}: sole stays horizontal")
                .IsEqualApprox(1f, 0.0001f);
            var sole = (MeshInstance3D)_puppet.BodyModel.FindChild("Sole" + suffix, true, false);
            var bottom = sole.Mesh.GetFaces().Min(vertex => (sole.GlobalTransform * vertex).Y);
            AssertFloat(bottom).OverrideFailureMessage($"{what}: sole clears the floor")
                .IsBetween(-0.001f, walking ? 0.065f : 0.005f);
            lowest = Mathf.Min(lowest, bottom);
        }
        AssertFloat(lowest).OverrideFailureMessage($"{what}: at least one foot stays planted").IsLess(0.005f);
    }

    private void CheckSleevesClearKnees()
    {
        foreach (var hand in _puppet.Hands)
        {
            var elbow = hand.UpperSleeve.GlobalTransform * new Vector3(0f, 0.5f, 0f);
            var shoulder = _puppet.VisualShoulder(hand.Index);
            AssertFloat(shoulder.DistanceTo(elbow)).OverrideFailureMessage("supported elbow preserves upper arm length")
                .IsEqualApprox(SurgeonHand.UpperArm, 0.001f);
            foreach (var joint in (string[])["ShinL", "ShinR"])
            {
                var knee = _puppet.Joint(joint).GlobalPosition;
                (Node3D Sleeve, float Radius)[] sleeves = [(hand.UpperSleeve, 0.064f), (hand.ForeSleeve, 0.05f)];
                foreach (var (sleeve, radius) in sleeves)
                {
                    var start = sleeve.GlobalTransform * new Vector3(0f, -0.5f, 0f);
                    var end = sleeve.GlobalTransform * new Vector3(0f, 0.5f, 0f);
                    var nearest = Geometry3D.GetClosestPointToSegment(knee, start, end);
                    AssertFloat(nearest.DistanceTo(knee))
                        .OverrideFailureMessage(
                            "actual sleeve segment and knee volumes clear each other for the controlled hand pose")
                        .IsGreaterEqual(0.065f + radius);
                }
            }
        }
    }

    /// <summary>In a run with key frames, saves the view from <paramref name="from"/> looking at <paramref name="at"/>
    /// as NN_label.png.</summary>
    private async Task Capture(string label, Vector3 from, Vector3 at)
    {
        if (!KeyFrames.Wanted())
        {
            return;
        }
        var folder = KeyFrames.Folder("surgeon_pose");
        if (_shots == 0)
        {
            // An earlier run's frames would read as this run's.
            var old = DirAccess.GetFilesAt(folder).Where(file => file.EndsWith(".png", StringComparison.Ordinal));
            foreach (var file in old)
            {
                DirAccess.RemoveAbsolute(folder.PathJoin(file));
            }
        }
        _camera.Position = from;
        _camera.LookAt(at);
        var path = folder.PathJoin($"{_shots++:00}_{label}.png");
        AssertBool(await KeyFrames.SaveViewport(path)).OverrideFailureMessage("saved " + path).IsTrue();
    }
}
