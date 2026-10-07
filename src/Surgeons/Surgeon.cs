namespace Scalpel.Surgeons;

/// <summary>Where a needle went into a surgeon: "hand" (at the glove's middle under it) or "body" (at the tip).</summary>
public sealed record NeedleHit(string Part, Vector3 At);

/// <summary>
/// A player in the room. The owning peer reads input, moves the body and hands, and streams its state.
/// Everyone else (including the host) sees a puppet that follows the stream.
/// <para>
/// Controls: the mouse looks around, holding a hand's key (Q/E) moves that hand instead and makes it the active one.
/// Use tool (LMB, held) rests the active hand's tool on its spot and works it, the wheel sets its effort level (see
/// <see cref="ToolActions.LevelNames"/> and TriggerNames). A syringe has its own wheel: down pulls the plunger out, up
/// pushes it in, 1 ml a notch, with or without Use tool held. Grab (RMB) picks up and puts down. Zoom (Shift) toggles
/// between two zoom levels; the closer one fades the hands, and once a syringe's needle is in with Use tool held it
/// frames the needle and what it's in. The hands start turned in, each tool pointing across in front of the eyes.
/// Holding Aim tool (MMB) the mouse turns the active hand's tool about the wrist instead, only the wrist moving: pitch
/// and swing left and right. C/V roll it about its length. WASD moves the body.
/// </para>
/// <para>
/// Hands turn and walk with the body, unless they hold onto something (attached): then they stay put. The inactive
/// hand stays exactly where it was, still doing what it was doing. Hands have no height control: the tool tip rests
/// just above whatever is under it (skin, tray, organs, a target in an open cavity), or higher while lifted. Crouching
/// brings everything down within reach of the floor.
/// </para>
/// </summary>
public partial class Surgeon : CharacterBody3D
{
    public const float WalkSpeed = 1.6f;
    public const float Reach = 0.72f;
    /// <summary>Closest a hand comes to its shoulder (meters): an elbow folds only so far, closer the forearm would
    /// squash.</summary>
    public const float MinReach = 0.18f;
    public const float EyeHeight = 1.62f;
    public static readonly Vector3 ShoulderOffset = new(0.19f, 1.4f, -0.08f);
    internal const float HandSensitivity = 0.0009f;
    private const float LookSensitivity = 0.003f;
    /// <summary>Radians the held tool turns per pixel while the mouse aims it (Aim tool held).</summary>
    internal const float AimSensitivity = 0.004f;
    /// <summary>How fast C/V roll the held tool (radians a second).</summary>
    internal const float TwistSpeed = 2f;
    /// <summary>Gap between a resting tool tip and the surface under it.</summary>
    public const float HoverGap = 0.01f;
    /// <summary>The same for a needle (a syringe, the IV catheter): its tip sits on the aim.</summary>
    public const float NeedleHover = 0.002f;
    /// <summary>A tool tip at most this far above a surface shows the aim on it, where Use tool brings it down (see
    /// <see cref="AimPoint"/>).</summary>
    private const float AimDrop = 0.1f;
    /// <summary>How far above what's inside an opening (meters) a lowered blade stays at each effort level: at full
    /// effort it goes all the way down to it, close enough to grate on a bone (Patient.BladeReach), short of cutting an
    /// organ.</summary>
    public static readonly float[] BladeInOpening = [HoverGap, 0.008f, 0.005f, 0.002f];
    /// <summary>Holding Lift while holding onto something pulls it up this fast (m/s): slow and steady, so nothing rips.
    /// </summary>
    public const float PullSpeed = 0.05f;
    /// <summary>Room between the hand (or forearm) and the surface under it: about half a hand's thickness.</summary>
    public const float HandClearance = 0.03f;
    /// <summary>Where hands hang when nothing within reach is under them (the floor while standing): about waist height.
    /// </summary>
    public const float CarryHeight = 1.05f;
    /// <summary>Crouching lowers eyes and shoulders this much and slows walking to a careful step.</summary>
    public const float CrouchDrop = 0.75f;
    private const float CrouchSpeed = 0.35f;
    /// <summary>Zoom steps, cycled by the zoom key: camera field of view, widest first. Hand motion scales with the
    /// magnification, so the hand crosses the screen as fast at every step.</summary>
    public static readonly float[] ZoomFov = [70f, 35f];
    /// <summary>How far in front of the eyes a tool is held up to look at it (Inspect).</summary>
    private const float InspectDistance = 0.26f;
    /// <summary>Tools whose needle the last zoom step frames: a syringe and the IV catheter.</summary>
    private static readonly string[] NeedleActions = ["syringe", "iv_line"];
    /// <summary>A syringe's thumb press sits this far behind its finger grip when empty.</summary>
    public const float SyringePress = 0.017f;
    /// <summary>The share of the syringe's length its plunger pulls out when full (tools/assetgen/instruments.py builds
    /// them so).</summary>
    public const float SyringeTravel = 0.62f * 0.85f;
    private const float SyncInterval = 1f / 30f;
    private const float BumpDistance = 0.07f;
    private const float SwitchDelay = 0.25f;
    public const int MaxBelt = 4;
    private static readonly Vector2 LookPitch = new(-1.3f, 0.6f);
    private const float InteractRange = 1.8f;
    /// <summary>How close a partner's empty hand must be to hand a tool over instead of dropping it.</summary>
    public const float PassDistance = 0.18f;
    /// <summary>Hand speed (m/s) above which a handoff fumbles.</summary>
    public const float FumbleSpeed = 0.35f;
    /// <summary>A remote surgeon silent this long counts as frozen: the host pauses their tools until they're heard from
    /// again, so a lag spike doesn't leave a cautery burning or a saw running on its own.</summary>
    public const float StallSeconds = 0.75f;
    /// <summary>Close enough to a glove's middle line (m, see <see cref="SurgeonHand.GloveMiddle"/>) for a needle to be
    /// in the hand.</summary>
    public const float GloveReach = 0.05f;
    /// <summary>Close enough to the spine for a needle to be in the body.</summary>
    private const float TorsoRadius = 0.16f;
    /// <summary>Where the spine runs in the body model (local, standing): hips to neck.</summary>
    private static readonly Vector3 SpineBottom = new(0f, 0.95f, 0f);
    private static readonly Vector3 SpineTop = new(0f, 1.45f, 0f);

    public int PeerId { get; private set; } = 1;
    public string DisplayName { get; private set; } = "Doctor";
    public IReadOnlyList<QuirkRoll> QuirkRolls { get; private set; } = [];
    public Modifiers Mods { get; private set; } = new();
    public SurgeonStatus Status { get; private set; } = null!;
    public SurgeonHand[] Hands { get; } = new SurgeonHand[2];
    /// <summary>The hand the player works with now; the other stays where it was.</summary>
    public int Active { get; set; } = 1;
    /// <summary>Where the eyes look up and down (radians).</summary>
    public float Pitch { get; set; } = -0.55f;
    public bool InputLocked { get; set; }
    public bool IsLocal { get; private set; }
    /// <summary>What interact would use right now (local surgeon only).</summary>
    public Interactable? Focused { get; private set; }
    /// <summary>Tool the active hand would pick up right now (local surgeon only), shown highlighted.</summary>
    public SurgicalTool? Hovered { get; private set; }
    /// <summary>0 standing, 1 fully crouched. Synced so everyone sees you duck.</summary>
    public float Crouch { get; internal set; }
    /// <summary>The zoom step (<see cref="ZoomFov"/>).</summary>
    public int Zoom { get; private set; }
    public Camera3D Camera { get; private set; } = null!;

    /// <summary>Uid of the tool each hand held last frame: a new tool starts at effort level 0.</summary>
    private readonly int[] _heldUid = [0, 0];
    private float _switchTimer;
    private float _syncAccumulated;
    private Vector3 _jolt;
    /// <summary>Whether each hand holds onto something further than the arm reaches (the host lets it go).</summary>
    private readonly bool[] _strain = [false, false];

    private static Surgery Session => Surgery.Current!;

    public void Setup(int peer, string playerName, IReadOnlyList<QuirkRoll> rolls, Transform3D spawn)
    {
        PeerId = peer;
        Name = $"Surgeon{peer}";
        DisplayName = playerName;
        QuirkRolls = rolls;
        Mods = Modifiers.FromRolls(rolls, Db.SurgeonQuirks);
        Status = new SurgeonStatus(Mods)
        {
            ColdTremor = Surgery.Current?.RunMods.Flag("cold") == true ? 0.0015f : 0f,
        };
        IsLocal = peer == Multiplayer.GetUniqueId();
        SetMultiplayerAuthority(peer);
        GlobalTransform = spawn;
        _netPosition = spawn.Origin;
        _lastPosition = spawn.Origin;
        _netYaw = Rotation.Y;
        CollisionLayer = 16;
        CollisionMask = 1 | 16;
        AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.24f, Height = 1.75f }, Position = new Vector3(0f, 0.875f, 0f),
        });
        BuildVisuals();
        for (var i = 0; i < 2; i++)
        {
            var hand = new SurgeonHand();
            AddChild(hand);
            hand.Build(i, _scrubs);
            hand.LocalTarget = new Vector3(i == 0 ? -0.17f : 0.17f, 1.18f, -0.45f);
            hand.Puppet = !IsLocal;
            hand.Target = ToGlobal(hand.LocalTarget);
            Hands[i] = hand;
        }
        if (IsLocal)
        {
            foreach (var hand in Hands)
            {
                hand.HideUpperArm();
            }
            Camera.MakeCurrent();
            Sfx.Deaf = Mods.Flag("deaf");
        }
    }

    public SurgicalTool? HeldTool(int hand) => Session.Tools.ToolInHand(PeerId, hand);

    /// <summary>What the host needs to drive a tool held in this hand.</summary>
    public HandInput HandInput(int hand)
    {
        var h = Hands[hand];
        var awake = !Status.IsOut;
        return new HandInput(h.Lowered && awake, h.Trigger && awake, h.Level, h.Speed, PeerId, Mods);
    }

    public bool IsStalled() => !IsLocal && Net.Instance.Silence(PeerId) > StallSeconds;

    /// <summary>Empty if this surgeon can use the tool, otherwise why not.</summary>
    public string BlockedReason(ToolDef def) =>
        def.IsHeavy && Mods.Flag("heavy_tools_blocked") ? $"Your hands are too small to handle the {def.Name}." : "";

    public int BeltCapacity() => Math.Clamp(MaxBelt + (int)Mods.Num("belt_slots"), 0, MaxBelt);

    public Transform3D BeltTransform(int beltSlot)
    {
        var offset = new Vector3(-0.2f + (beltSlot * 0.13f), 0.95f, -0.12f);
        return new Transform3D(GlobalBasis * new Basis(Vector3.Right, -Mathf.Pi / 2f), ToGlobal(offset));
    }

    /// <summary>Stable gameplay reach origin: crouch lowers it, while the visual walk and squat animation never move it.
    /// </summary>
    public Vector3 Shoulder(int hand)
    {
        var local = ShoulderOffset with { X = ShoulderOffset.X * Side(hand) };
        var standing = ToGlobal(local with { Y = local.Y - (Crouch * CrouchDrop) });
        return standing.Lerp(_body.GlobalTransform * local, _down);
    }

    /// <summary>-1 for the left hand, 1 for the right.</summary>
    private static float Side(int hand) => hand == 0 ? -1f : 1f;

    /// <summary>How fast the body moves across the floor (m/s), measured the same way on every peer.</summary>
    public float GroundSpeed { get; private set; }

    /// <summary>Whether this surgeon is knocked out, the same on every peer.</summary>
    public bool IsDown => _fallSide != 0f;

    /// <summary>
    /// Where a needle at <paramref name="tip"/> would go into this surgeon, null if it's in neither hand nor body.
    /// <paramref name="holding"/> is the hand with the needle: one of this surgeon's own leaves only the other hand to
    /// go into.
    /// </summary>
    public NeedleHit? NeedlePart(Vector3 tip, SurgeonHand? holding)
    {
        foreach (var hand in Hands)
        {
            var middle = hand.GloveMiddle(tip);
            if (hand != holding && middle.DistanceTo(tip) < GloveReach)
            {
                return new NeedleHit("hand", middle);
            }
        }
        if (holding is not null && Hands.Contains(holding))
        {
            return null;
        }
        var hips = _body.GlobalTransform * SpineBottom;
        var neck = _body.GlobalTransform * SpineTop;
        return Geometry3D.GetClosestPointToSegment(tip, hips, neck).DistanceTo(tip) < TorsoRadius
            ? new NeedleHit("body", tip)
            : null;
    }

    /// <summary>A partner's empty hand close enough to take what this hand holds, null if none. A partner knocked out on
    /// the floor takes nothing.</summary>
    public (Surgeon Surgeon, int Hand)? PassTarget(int hand)
    {
        var from = Hands[hand].GlobalPosition;
        foreach (var other in Session.Surgeons.Values)
        {
            if (other == this || other.IsDown)
            {
                continue;
            }
            for (var i = 0; i < 2; i++)
            {
                if (other.HeldTool(i) is null && other.Hands[i].GlobalPosition.DistanceTo(from) < PassDistance)
                {
                    return (other, i);
                }
            }
        }
        return null;
    }

    /// <summary>Something shook this surgeon (seizure, pothole, a bump, a shove). May drop what's in hand.</summary>
    public void Jolt(float strength)
    {
        if (!IsLocal)
        {
            return;
        }
        _jolt += new Vector3(
            (float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-0.5, 0.5), (float)GD.RandRange(-1.0, 1.0))
            * strength * 0.03f;
        Status.AddStress(strength * 0.05f);
        var dropChance = strength * 0.35f * (1f - Mathf.Clamp(Mods.Num("bump_resist"), 0f, 1f));
        if (GD.Randf() < dropChance && HeldTool(Active) is not null && !Hands[Active].Attached)
        {
            Hands[Active].Lowered = false;
            Session.Tools.RequestRelease(Active, _jolt * 10f);
            Session.Hud.Toast("It slips out of your hand!");
        }
    }

    public void DropEverything()
    {
        for (var i = 0; i < 2; i++)
        {
            Hands[i].Lowered = false;
            Hands[i].Trigger = false;
            if (HeldTool(i) is not null)
            {
                Session.Tools.RequestRelease(i, Vector3.Zero);
            }
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        var dt = (float)delta;
        if (IsLocal)
        {
            LocalUpdate(dt);
            _syncAccumulated += dt;
            if (_syncAccumulated >= SyncInterval)
            {
                _syncAccumulated = 0f;
                Rpc(MethodName.SyncState, PackState());
            }
        }
        else
        {
            var follow = Mathf.Min(dt * 15f, 1f);
            GlobalPosition = GlobalPosition.Lerp(_netPosition, follow);
            Rotation = Rotation with { Y = Mathf.LerpAngle(Rotation.Y, _netYaw, follow) };
        }
        _head.Rotation = _head.Rotation with { X = Pitch };
        if (IsLocal)
        {
            Camera.Fov = Mathf.Lerp(Camera.Fov, ZoomFov[Zoom], Mathf.Min(dt * 12f, 1f));
            FrameNeedle(dt);
        }
        AnimateBody(dt);
        for (var i = 0; i < 2; i++)
        {
            UpdateHand(i, dt);
        }
        Session.Tools.Follow(this);
        StainScrubs(dt);
    }

    /// <summary>Keeps a hand's grip, angles, thumb and blood in step with the tool it holds, then poses it.</summary>
    private void UpdateHand(int i, float dt)
    {
        var hand = Hands[i];
        var tool = HeldTool(i);
        hand.Holding = tool is not null;
        var uid = tool?.Uid ?? 0;
        if (uid != _heldUid[i])
        {
            _heldUid[i] = uid;
            hand.Level = 0;
            hand.Grip = tool?.Def.Grip ?? "pencil";
            hand.Spreads = tool?.Def.Action == "spread";
            hand.Fit = tool is null ? GripFit.None : Db.GripFit(tool.Def, i);
            if (tool?.Def.Grip == "needle")
            {
                hand.Tilt = hand.DefaultTilt();
                hand.Turn = hand.DefaultTurn();
            }
            if (IsLocal)
            {
                FaceSyringe(i, tool);
            }
        }
        if (IsLocal && _unfaced.ContainsKey(i) && _rolledHand != i)
        {
            // Moved about, it keeps turning its scale to the eyes.
            var facing = hand.TwistFacing(Camera.GlobalPosition - hand.GlobalPosition);
            hand.Twist = Mathf.LerpAngle(hand.Twist, facing, Mathf.Min(dt * 8f, 1f));
        }
        // The thumb rides the plunger's press: behind the finger grip by the press's own offset plus the pull.
        hand.Press = tool?.Def.Action == "syringe"
            ? SyringePress + (tool.Def.Length * SyringeTravel * (tool.Ml + tool.Air) / tool.Def.Volume)
            : float.NaN;
        hand.Soak(tool?.Blood ?? 0f, dt);
        SupportElbow(hand);
        hand.UpdatePose(IsLocal ? SteadyShoulder(i) : VisualShoulder(i), dt);
    }
}
