namespace Scalpel.Surgeons;

/// <summary>The surgeon's visible body: scrubs, head, legs and the walk, crouch and fall animations.</summary>
public partial class Surgeon
{
    /// <summary>Body model's leg joints (meters), matching tools/assetgen/surgeon.py:_body(). Keep these and
    /// <see cref="HeadPivot"/> in sync with that generator; the surgeon pose tests check the loaded rig.</summary>
    public const float HipHeight = 0.95f;
    public const float ThighLength = 0.46f;
    public const float ShinLength = 0.41f;
    public const float AnkleHeight = 0.08f;
    public const float HipWidth = 0.115f;
    public const float SquatSetback = 0.16f;
    public const float SquatLean = 0.48f;
    /// <summary>Visible head tilts about the top of the fixed neck, below eye level.</summary>
    public static readonly Vector3 HeadPivot = new(0f, -0.08f, 0.02f);
    /// <summary>Scrubs stain per second per fully bloody glove: hands get wiped on them without thinking.</summary>
    private const float StainRate = 0.004f;
    /// <summary>Knocked out, the surgeon tips over sideways from the feet and lies on their side, facing the table: the
    /// body model this far up off the floor (half the shoulders' width).</summary>
    private const float LyingLift = 0.2f;
    /// <summary>The hands on the floor in front of the chest (local, for a fall to the left).</summary>
    private static readonly Vector3 LyingHand = new(-0.8f, 0.05f, -0.25f);
    /// <summary>The camera's pitch lying down, enough to see the table from the floor.</summary>
    private const float LyingPitch = 0.3f;
    /// <summary>On the way down they stagger back to this far (m) from the middle of the table, walls allowing, so the
    /// table isn't right overhead.</summary>
    private const float LyingDistance = 1.4f;
    /// <summary>How much floor (m) a falling surgeon looks for beside them, to pick the side they fall to.</summary>
    private const float FallRoom = 2f;
    private static readonly string[] JointNames = ["Torso", "LegL", "LegR", "ShinL", "ShinR", "ShoeL", "ShoeR"];

    private Node3D _head = null!;
    private Node3D _body = null!;
    /// <summary>Shared by the body and both sleeves, so blood wiped off the gloves stains them all.</summary>
    private ShaderMaterial _scrubs = null!;
    private float _stains;
    private Node3D _face = null!;
    private readonly Dictionary<string, Node3D> _joints = [];
    private readonly Dictionary<string, Transform3D> _rest = [];
    private float _walkPhase;
    private float _walkDrop;
    private float _collapse;
    private Vector3 _lastPosition;
    /// <summary>How far over onto the floor a knocked out surgeon has gone (0 standing, 1 lying).</summary>
    private float _down;
    /// <summary>The side a knocked out surgeon falls to (1 their left, -1 their right, 0 standing), synced.</summary>
    private float _fallSide;

    /// <summary>Sleeve attachment on the animated torso, independent of the gameplay reach origin.</summary>
    public Vector3 VisualShoulder(int hand) => _joints["Torso"].GlobalTransform * TorsoShoulder(hand);

    /// <summary>
    /// <see cref="VisualShoulder"/> as the body stands, without the walk's hip drop and lean. The local surgeon's own
    /// body isn't drawn and its view stays steady while walking: the arms it sees stay steady too, and so does how low
    /// each glove reaches (which keeps the hand off a table).
    /// </summary>
    public Vector3 SteadyShoulder(int hand)
    {
        var torso = _joints["Torso"];
        var rest = _rest["Torso"];
        var lean = new Vector3((-_collapse * 1.3f) - (Crouch * SquatLean), 0f, 0f);
        var still = new Transform3D(rest.Basis * Basis.FromEuler(lean), rest.Origin);
        var lifted = torso.GetParent<Node3D>().GlobalTransform * still;
        return (lifted * TorsoShoulder(hand)) + (GlobalBasis.Y * _walkDrop);
    }

    /// <summary>Where a shoulder is in the torso joint's space.</summary>
    private static Vector3 TorsoShoulder(int hand) =>
        new(ShoulderOffset.X * Side(hand), ShoulderOffset.Y - HipHeight, ShoulderOffset.Z);

    /// <summary>Sets this sleeve's knee support and obstacles for a deep squat, clearing them when standing.</summary>
    private void SupportElbow(SurgeonHand hand)
    {
        hand.ElbowSupportWeight = Mathf.SmoothStep(0.55f, 1f, Crouch) * (1f - _down);
        hand.KneeObstacles.Clear();
        if (hand.ElbowSupportWeight <= 0f)
        {
            hand.ElbowSupport = null;
            return;
        }
        var knee = _joints[hand.Index == 0 ? "ShinL" : "ShinR"];
        // The IK pole sits above the contact surface: projecting it onto the elbow's bend circle lowers it again.
        hand.ElbowSupport = knee.GlobalPosition + (GlobalBasis.Y * 0.22f);
        hand.KneeObstacles.Add(_joints["ShinL"].GlobalPosition);
        hand.KneeObstacles.Add(_joints["ShinR"].GlobalPosition);
    }

    private void StainScrubs(float delta)
    {
        var stains = Mathf.Min(_stains + ((Hands[0].Blood + Hands[1].Blood) * StainRate * delta), 1f);
        // Shader parameters only change in visible steps.
        if (!Mathf.IsEqualApprox(Mathf.Snapped(stains, 0.02f), Mathf.Snapped(_stains, 0.02f)))
        {
            _scrubs.SetShaderParameter("stains", stains);
        }
        _stains = stains;
    }

    private Color ScrubsColor => Materials.Scrubs[PeerId == Net.HostId ? 0 : 1];

    private void BuildVisuals()
    {
        _scrubs = Materials.FamilyUnique("cloth", ScrubsColor, 0.9f);
        _scrubs.NextPass = Materials.OutlineFor(0.003f);
        var skin = Materials.FamilyUnique("skin", new Color(0.8f, 0.64f, 0.54f), 0.6f);
        _body = ModelSlot.Instantiate("surgeon", "body", this, new Dictionary<string, Material>
        {
            ["tint"] = _scrubs, ["skin"] = skin,
            ["rubber"] = Materials.FamilyUnique("rubber", new Color(0.1f, 0.11f, 0.12f), 0.9f),
        });
        _head = new Node3D { Name = "Head", Position = new Vector3(0f, EyeHeight, 0f) };
        AddChild(_head);
        var cap = Materials.FamilyUnique("cloth", ScrubsColor.Darkened(0.12f), 0.9f);
        cap.NextPass = Materials.OutlineFor(0.001f);
        _face = ModelSlot.Instantiate("surgeon", "head", _head, new Dictionary<string, Material>
        {
            ["tint"] = cap, ["skin"] = skin,
            ["mask"] = Materials.FamilyUnique("cloth", new Color(0.55f, 0.72f, 0.78f), 0.9f),
        });
        Camera = new Camera3D { Name = "Camera", Fov = 70f, Near = 0.03f };
        _head.AddChild(Camera);
        foreach (var joint in JointNames)
        {
            if (_body.FindChild(joint, true, false) is Node3D node)
            {
                _joints[joint] = node;
                _rest[joint] = node.Transform;
            }
        }
        var mine = PeerId == Multiplayer.GetUniqueId();
        if (mine)
        {
            _face.Visible = false;
            _body.Visible = false;
        }
        var tag = Shapes.Label(this, mine ? "" : DisplayName, new Vector3(0f, 2f, 0f), 48);
        tag.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
    }

    /// <summary>Walk cycle from actual movement speed, collapse while passed out. Works the same for local and remote
    /// surgeons.</summary>
    private void AnimateBody(float delta)
    {
        var moved = new Vector2(GlobalPosition.X - _lastPosition.X, GlobalPosition.Z - _lastPosition.Z).Length();
        var speed = moved / Mathf.Max(delta, 0.0001f);
        GroundSpeed = speed;
        _lastPosition = GlobalPosition;
        _walkPhase += delta * speed * 7f;
        var stride = Mathf.Clamp(speed / WalkSpeed, 0f, 1f) * 0.45f;
        var passedOut = IsLocal ? Status.PassedOut > 0f : _remoteOut;
        _collapse = Mathf.MoveToward(_collapse, passedOut ? 1f : 0f, delta * 2.5f);
        if (IsLocal)
        {
            _fallSide = !Status.IsKnockedOut ? 0f : _fallSide != 0f ? _fallSide : RoomierSide();
        }
        _down = Mathf.MoveToward(_down, IsDown ? 1f : 0f, delta * 1.5f);
        var sway = Mathf.Abs(Mathf.Sin(_walkPhase)) * stride * 0.05f;
        // Lower the pelvis between spread knees, then solve both fixed-length legs to grounded feet.
        Pose("Torso", new Vector3((-_collapse * 1.3f) - (Crouch * SquatLean) + sway, 0f, 0f));
        // Knocked out, the whole body tips over sideways from the feet onto the floor; the eyes go where its head lies.
        var side = _fallSide != 0f ? _fallSide : 1f;
        _body.Rotation = _body.Rotation with { Z = side * _down * Mathf.Pi / 2f };
        // With straight legs, a stride needs a little pelvis drop to leave the planted foot on the floor.
        var reach = ThighLength + ShinLength - 0.0001f;
        var travel = Mathf.Abs(Mathf.Sin(_walkPhase) * stride * (1f - (Crouch * 0.75f)));
        var span = (Mathf.Sin(travel) * reach) + (Crouch * (SquatSetback + 0.04f));
        var vertical = Mathf.Sqrt(Mathf.Max(0f, (reach * reach) - (span * span) - Mathf.Pow(Crouch * 0.075f, 2f)));
        _walkDrop = Mathf.Max(0f, HipHeight - AnkleHeight - (Crouch * CrouchDrop) - vertical);
        _body.Position = new Vector3(0f, (-Crouch * CrouchDrop) - _walkDrop + (_down * LyingLift), Crouch * SquatSetback);
        PoseLeg("L", -1f, Mathf.Sin(_walkPhase) * stride);
        PoseLeg("R", 1f, -Mathf.Sin(_walkPhase) * stride);
        PoseHead();
    }

    /// <summary>
    /// Walking motion and the squat's forward lean belong only to the visible model. Precise hand work's first-person
    /// view stays steady; crouching changes its height without shifting it across the floor. Camera and visible head
    /// have independent transforms: looking never swings the neck out through the back.
    /// </summary>
    private void PoseHead()
    {
        var torso = _joints["Torso"];
        var eyeLocal = new Vector3(0f, EyeHeight - HipHeight, 0f);
        var standingEyes = new Vector3(0f, EyeHeight - (_collapse * 1.1f) - (Crouch * CrouchDrop), 0f);
        _head.Position = standingEyes.Lerp(_body.Transform * new Vector3(0f, EyeHeight, -0.06f), _down);
        var patient = Session.Patient.GlobalPosition;
        // Lying there, the head turns to the patient on the table.
        var look = 0f;
        if (_down > 0f)
        {
            var toPatient = (patient - ToGlobal(_head.Position)) * new Vector3(1f, 0f, 1f);
            look = Mathf.Wrap(Mathf.Atan2(-toPatient.X, -toPatient.Z) - Rotation.Y, -Mathf.Pi, Mathf.Pi) * _down;
        }
        _head.Rotation = _head.Rotation with { Y = look };
        // Counter the hip lean at the neck so the face still follows the player's look, rather than staring at their
        // feet.
        var tilt = new Basis(Vector3.Right, ((Pitch * 0.5f) + (Crouch * SquatLean)) * (1f - _down));
        var anchor = torso.GlobalTransform * (eyeLocal + HeadPivot);
        var faceBasis = torso.GlobalBasis * tilt;
        if (_down > 0f)
        {
            // The visible head no longer inherits the camera's yaw. Turn it toward the patient about its fixed neck
            // anchor, using its own position so the face and camera both look at the patient from where they lie.
            var toward = patient - anchor;
            var forward = -faceBasis.Z;
            var faceYaw = Mathf.Wrap(
                Mathf.Atan2(-toward.X, -toward.Z) - Mathf.Atan2(-forward.X, -forward.Z), -Mathf.Pi, Mathf.Pi);
            faceBasis = new Basis(Vector3.Up, faceYaw * _down) * faceBasis;
            // Preserve the fallen view's upward look too; rolling the body must not turn camera pitch into a sideways
            // nod.
            var headRight = (-faceBasis.Z).Slide(Vector3.Up).Cross(Vector3.Up).Normalized();
            faceBasis = new Basis(headRight, Pitch * _down) * faceBasis;
        }
        _face.GlobalTransform = new Transform3D(faceBasis, anchor - (faceBasis * HeadPivot));
    }

    /// <summary>Two-bone legs in body space. At rest the soles are on the floor; walking lifts only the swinging foot.
    /// </summary>
    private void PoseLeg(string suffix, float side, float swing)
    {
        var hip = new Vector3(side * HipWidth, HipHeight, 0f);
        var ankle = new Vector3(
            side * (HipWidth + (Crouch * 0.075f)), AnkleHeight + (Crouch * CrouchDrop) + _walkDrop,
            -Crouch * (SquatSetback + 0.04f));
        // Fade the stride in a squat so feet don't slide far out from under the pelvis.
        var step = swing * (1f - (Crouch * 0.75f));
        ankle.Z -= Mathf.Sin(step) * (ThighLength + ShinLength);
        ankle.Y += Mathf.Max(0f, Mathf.Sin(step)) * 0.06f;
        var along = (ankle - hip).Normalized();
        var distance = Mathf.Min(hip.DistanceTo(ankle), ThighLength + ShinLength - 0.0001f);
        // Circle intersection, with a forward and outward knee pole projected perpendicular to the hip-to-ankle line.
        var bend = new Vector3(side * Crouch * 0.55f, 0f, -1f).Slide(along).Normalized();
        var advance = ((ThighLength * ThighLength) - (ShinLength * ShinLength) + (distance * distance)) / (2f * distance);
        var knee = hip + (along * advance) + (bend * Mathf.Sqrt(Mathf.Max(0f, (ThighLength * ThighLength) - (advance * advance))));
        ankle = hip + (along * distance);
        var thigh = _joints["Leg" + suffix];
        var shin = _joints["Shin" + suffix];
        var shoe = _joints["Shoe" + suffix];
        thigh.Transform = new Transform3D(LegBasis(knee - hip), hip);
        shin.Transform = thigh.Transform.AffineInverse() * new Transform3D(LegBasis(ankle - knee), knee);
        // Ankle counter-rotation keeps the sole horizontal, with toes turned out in the squat.
        shoe.Transform = (thigh.Transform * shin.Transform).AffineInverse()
            * new Transform3D(new Basis(Vector3.Up, -side * Crouch * 0.25f), ankle);
    }

    private static Basis LegBasis(Vector3 down)
    {
        var y = -down.Normalized();
        var x = y.Cross(Vector3.Back).Normalized();
        return new Basis(x, y, x.Cross(y));
    }

    /// <summary>The side (1 left, -1 right) with more clear floor beside the surgeon, to fall to.</summary>
    private float RoomierSide()
    {
        var space = GetWorld3D().DirectSpaceState;
        var from = ToGlobal(new Vector3(0f, 0.3f, 0f));
        float Room(float side)
        {
            var query = PhysicsRayQueryParameters3D.Create(
                from, ToGlobal(new Vector3(-side * FallRoom, 0.3f, 0f)), 1, [GetRid()]);
            var hit = space.IntersectRay(query);
            return hit.Count == 0 ? FallRoom : from.DistanceTo(hit["position"].AsVector3());
        }
        return Room(1f) >= Room(-1f) ? 1f : -1f;
    }

    private void Pose(string joint, Vector3 euler)
    {
        if (_joints.TryGetValue(joint, out var node))
        {
            var rest = _rest[joint];
            node.Transform = new Transform3D(rest.Basis * Basis.FromEuler(euler), rest.Origin);
        }
    }

    /// <summary>Knocked out: lying on the floor facing the table, hands limp in front, nothing the player can do.
    /// </summary>
    private void LieStill(float delta)
    {
        Crouch = Mathf.MoveToward(Crouch, 0f, delta * 4f);
        Pitch = Mathf.Lerp(Pitch, LyingPitch, Mathf.Min(delta * 3f, 1f));
        var table = (Session.Patient.GlobalPosition - GlobalPosition) * new Vector3(1f, 0f, 1f);
        Velocity = _down < 1f && table.Length() < LyingDistance ? -table.Normalized() * WalkSpeed : Vector3.Zero;
        MoveAndSlide();
        Rotation = Rotation with
        {
            Y = Mathf.LerpAngle(Rotation.Y, Mathf.Atan2(-table.X, -table.Z), Mathf.Min(delta * 3f, 1f)),
        };
        _aimWrist = null;
        for (var i = 0; i < 2; i++)
        {
            var hand = Hands[i];
            hand.Aiming = false;
            hand.Raise = 0f;
            hand.Lowered = false;
            hand.Trigger = false;
            hand.Lifted = false;
            hand.Inspecting = false;
            hand.Tremor = Vector3.Zero;
            hand.Shiver = Vector3.Zero;
            hand.Trail = 0f;
            hand.Target = ToGlobal((LyingHand + new Vector3(-0.25f * i, 0f, 0f)) * new Vector3(_fallSide, 1f, 1f));
            hand.LocalTarget = ToLocal(hand.Target);
        }
    }
}
