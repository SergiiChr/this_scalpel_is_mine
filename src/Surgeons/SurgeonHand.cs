namespace Scalpel.Surgeons;

/// <summary>A point inside the glove (world space) and how thick the glove is around it.</summary>
public readonly record struct BonePoint(Vector3 Position, float Radius);

/// <summary>
/// One hand and its arm. The arm is solved with analytic two-bone IK from shoulder to hand every frame.
/// Position is owned by the surgeon's peer and synced to everyone; the host uses it to drive tools.
/// </summary>
public partial class SurgeonHand : Node3D
{
    public const float UpperArm = 0.34f;
    public const float Forearm = 0.34f;
    private const float KneeSleeveClearance = 0.14f;
    public static readonly Vector2 TiltRange = new(-1.5f, -0.2f);
    /// <summary>A spreader is held tipped this far toward the skin, so the hand stays clear of the body, and set lying
    /// flatter along it (<see cref="TiltRange"/>.Y, see ToolActions).</summary>
    public const float SpreaderTilt = -0.6f;
    /// <summary>How far the wrist turns the tool left and right of straight ahead (radians).</summary>
    public const float TurnRange = 0.9f;
    /// <summary>How far in toward the middle each hand starts turned, so the tool points across in front of the eyes,
    /// beside the hand, not hidden under it.</summary>
    public const float RestTurn = 0.4f;
    /// <summary>The tilt a glove is fitted onto its tool at (see GloveFrame()). Tilted or turned from there, both turn
    /// together.</summary>
    public const float RestTilt = -1.1f;
    public const float LiftHeight = 0.12f;
    /// <summary>How high (meters) and for how long (seconds) the hand hops when its tool bounces off what it was pressed
    /// onto.</summary>
    private const float BounceHeight = 0.02f;
    private const float BounceTime = 0.35f;
    /// <summary>How fast (m/s) a lifted or raised hand comes back down.</summary>
    public const float SettleSpeed = 0.8f;
    /// <summary>Seconds of game time the hand's speed is measured over.</summary>
    private const float SpeedWindow = 0.25f;
    public static readonly IReadOnlyList<string> Fingers = ["Index", "Middle", "Ring", "Pinky", "Thumb"];
    /// <summary>Radians each finger joint bends at full curl, knuckle first.</summary>
    private static readonly float[] JointBend = [0.9f, 1.2f, 0.8f];
    /// <summary>Most the thumb bends in all (radians) reaching for a syringe's plunger.</summary>
    private const float MaxThumbBend = 3.2f;
    /// <summary>How far the thumb's tip reaches past its last joint (meters).</summary>
    private const float ThumbTip = 0.02f;
    /// <summary>Furthest the thumb's root slides toward a plunger out of its reach (meters).</summary>
    private const float ThumbSlide = 0.012f;
    /// <summary>How far behind the thumb press its pad's middle is (meters).</summary>
    private const float PressPad = 0.008f;
    /// <summary>The toon shader's coat runs back from a tip at -Z, the glove's fingers point along +X.</summary>
    private static readonly Transform3D CoatFrame = new(new Basis(Vector3.Forward, Vector3.Up, Vector3.Right), Vector3.Zero);
    /// <summary>Wrist to fingertips along the glove's X.</summary>
    public const float GloveLength = 0.19f;
    /// <summary>About how thick a finger is around its bones.</summary>
    private const float FingerRadius = 0.009f;
    /// <summary>The palm's thickness above and below its bone.</summary>
    public const float PalmHalfThickness = 0.014f;
    /// <summary>Glove blood gained per second while the held tool is bloodier than the glove.</summary>
    private const float SoakRate = 0.15f;
    /// <summary>How far (meters) the forearm reaches past the wrist into the glove's cuff, so the cuff never shows as an
    /// open end.</summary>
    private const float CuffDepth = 0.06f;
    /// <summary>Where the glove's cuff bone points at rest (glove model space): back from the wrist
    /// (tools/blender/hand.py).</summary>
    private static readonly Vector3 CuffRest = new(-1f, 0f, 0f);
    /// <summary>Most a held tool's angle in the fingers gives way to keep the wrist straight (radians). More and the
    /// fingers swing into the tool.</summary>
    private const float MaxToolTip = 0.3f;
    /// <summary>Most the hand turns about the tool to face the elbow (radians), from the grip's own pose with the back of
    /// the hand up.</summary>
    private const float MaxRoll = 0.45f;
    /// <summary>How much a held tool's direction decides where the elbow goes, against the arm hanging down and out.
    /// </summary>
    private const float ForearmPull = 0.7f;
    /// <summary>How far under the shoulder a held tool can raise the elbow (meters).</summary>
    private const float ElbowBelowShoulder = 0.1f;
    /// <summary>Empty hand: the glove point (glove model space) at the hand's position, the hollow of the fingers.
    /// </summary>
    private static readonly Vector3 GripPoint = new(0.07f, -0.028f, 0f);
    /// <summary>Seconds the elbow takes to ease back after aiming.</summary>
    private const float ElbowEase = 0.25f;
    /// <summary>Stands in for the hand's materials while it's see-through (see SetSeeThrough()).</summary>
    private static readonly Color GhostColor = new(0.75f, 0.82f, 0.9f);
    private const int TrailCopies = 4;
    /// <summary>How far apart in time (s) the afterimages are.</summary>
    private const float TrailStep = 0.06f;
    private const float TrailAlpha = 0.45f;
    private static readonly Color TrailColor = new(0.5f, 0.65f, 0.95f);
    /// <summary>How far (m) from the glove an afterimage has to be to show.</summary>
    private const float TrailGap = 0.01f;

    public int Index { get; private set; }
    /// <summary>Where the hand wants to be, world space, before tremor and lift.</summary>
    public Vector3 Target { get; set; }
    /// <summary>Target relative to the surgeon, used while not attached so the hand moves with the body.</summary>
    public Vector3 LocalTarget { get; set; }
    /// <summary>The held tool's pitch, tip down (<see cref="TiltRange"/>); the hand turns with it.</summary>
    public float Tilt { get; set; } = RestTilt;
    /// <summary>How far the held tool swings left and right of the body's facing (<see cref="TurnRange"/>).</summary>
    public float Turn { get; set; }
    /// <summary>How far the held tool rolls about its own length.</summary>
    public float Twist { get; set; }
    /// <summary>Holding a spreader, which lies flat: twist swings it about the upright instead of rolling it, so its jaws
    /// turn across a cut and stay flat.</summary>
    public bool Spreads { get; set; }
    /// <summary>Use tool held: the tool rests on its spot instead of hovering over it.</summary>
    public bool Lowered { get; set; }
    /// <summary>Use tool held, for tools with a single action (ToolActions.TriggerNames): on press, while held, on
    /// release.</summary>
    public bool Trigger { get; set; }
    /// <summary>Effort level 0..3 from the wheel (cut depth, stitch tension, heat, plunger...), see
    /// ToolActions.LevelNames.</summary>
    public int Level { get; set; }
    public bool Lifted { get; set; }
    /// <summary>Set by the surgeon while the wrist aims the tool: the elbow stays put.</summary>
    public bool Aiming { get; set; }
    /// <summary>How far above its resting spot (meters) the hand is held while aiming.</summary>
    public float Raise { get; set; }
    public bool Attached { get; set; }
    /// <summary>Held up in front of the eyes, turned across the view with its markings toward them (reading a syringe).
    /// </summary>
    public bool Inspecting { get; set; }
    public Vector3 Tremor { get; set; }
    /// <summary>A twitch of the glove alone (the tool and the hand's position stay put), set by the surgeon while stress
    /// is low.</summary>
    public Vector3 Shiver { get; set; }
    /// <summary>Afterimages following the glove (0 none, 1 strongest), set on the local view while a sedative works.
    /// </summary>
    public float Trail { get; set; }
    /// <summary>Set by the surgeon while the held tool rests on something hard (a tray, the table): the tremor can't push
    /// it in.</summary>
    public bool OnHard { get; set; }
    public float Speed { get; private set; }
    /// <summary>Remote copies receive the final position already including lift and tremor.</summary>
    public bool Puppet { get; set; }
    /// <summary>How bloody the glove is (0..1). Every peer soaks it from the held tool's synced blood, so it matches
    /// everywhere.</summary>
    public float Blood { get; private set; }
    /// <summary>Deep squat: aim the elbow toward the knee's upper surface instead of bending through the thigh.</summary>
    public Vector3? ElbowSupport { get; set; }
    public float ElbowSupportWeight { get; set; }
    public List<Vector3> KneeObstacles { get; } = [];
    /// <summary>Set by the surgeon each frame; drives how far the fingers curl.</summary>
    public bool Holding { get; set; }
    /// <summary><see cref="ToolDef.Grip"/> of the tool in this hand, set with <see cref="Holding"/>. Empty hands follow
    /// the forearm, open.</summary>
    public string Grip { get; set; } = "pencil";
    /// <summary>A held syringe's thumb press: how far behind the grip it is along the syringe (meters, it moves with the
    /// plunger), set by the surgeon. NaN for any other tool.</summary>
    public float Press { get; set; } = float.NaN;
    /// <summary>How this hand's grip is fitted to the tool it holds (see <see cref="GripFit"/>).</summary>
    public GripFit Fit { get; set; } = GripFit.None;
    /// <summary>How far below the hand's position the glove reaches as it's posed now (negative: below). The surgeon
    /// keeps that clear of tables and trays, not only the hand's middle.</summary>
    public float GloveDrop { get; private set; } = -0.03f;

    private GripStyle Style => GripStyle.For(Grip);
    private Node3D Body => GetParent<Node3D>();

    private float _lift;
    /// <summary>How far through a bounce (<see cref="Bounce"/>) the hand is, 1 for none.</summary>
    private float _bounce = 1f;
    /// <summary>Recent positions in game time. Speed over a short window ignores tremor and network jitter. Game time,
    /// not the wall clock: physics frames run back to back after a stall would read as a burst of speed.</summary>
    private readonly Queue<(float Time, Vector3 Position)> _history = new();
    private float _clock;
    private ThumbRest? _thumb;
    private float _curl = 0.2f;
    /// <summary>What the fingers were last posed for.</summary>
    private PoseKey? _posed;
    private Node3D _glove = null!;
    private BoneRig? _gloveRig;
    private List<ShaderMaterial> _gloveMaterials = [];
    private Node3D _upper = null!;
    /// <summary>The upper arm's sleeve model.</summary>
    internal Node3D UpperSleeve => _upper;
    private Node3D _fore = null!;
    /// <summary>Where the elbow was put last.</summary>
    private Vector3 _elbow;
    /// <summary>Where the elbow is held (owner's space) while aiming.</summary>
    private Vector3 _heldElbow;
    /// <summary>1 held, 0 free, eased back after aiming.</summary>
    private float _elbowHold;
    private AnimatableBody3D _pusher = null!;
    /// <summary>Fraction of the forearm hidden at the elbow end (local player only, keeps the view clear).</summary>
    private float _forearmStart;
    private StandardMaterial3D? _ghost;
    private Node3D? _trailRoot;
    private readonly List<TrailCopy> _trailCopies = [];
    /// <summary>The glove frames and bone poses the afterimages show, newest first.</summary>
    private readonly List<TrailFrame> _trailFrames = [];
    private float _trailAccumulated;

    private readonly record struct PoseKey(float Curl, string Grip, bool Holding, GripFit Fit, float Press);

    /// <summary>The thumb at rest, glove space: where its root is, each bone (to the next joint, the last to its tip) and
    /// the axis each joint bends about.</summary>
    private sealed record ThumbRest(Vector3 Root, Vector3[] Bones, Vector3[] Axes);

    private readonly record struct TrailFrame(Transform3D Glove, Transform3D[] Poses);

    private sealed record TrailCopy(Node3D Root, Skeleton3D? Skeleton, StandardMaterial3D Material);

    public void Build(int handIndex, ShaderMaterial scrubs)
    {
        Index = handIndex;
        Name = Index == 0 ? "LeftHand" : "RightHand";
        Turn = Index == 1 ? RestTurn : -RestTurn;
        var sleeve = new Dictionary<string, Material> { ["tint"] = scrubs };
        _glove = ModelSlot.Instantiate("surgeon", "glove", this);
        _gloveMaterials = ModelSlot.OwnMaterials(_glove);
        foreach (var material in _gloveMaterials)
        {
            material.SetShaderParameter("coat_length", GloveLength);
        }
        // The glove is modeled wrist at the origin, fingers along +X, palm facing -Y, thumb toward -Z.
        // Empty, it follows the forearm (see PlaceGlove()). Holding a tool, it sits on the tool by its grip (GripStyle).
        _glove.TopLevel = true;
        _gloveRig = BoneRig.Find(_glove);
        _upper = ModelSlot.Instantiate("surgeon", "upper_arm", this, sleeve);
        _fore = ModelSlot.Instantiate("surgeon", "forearm", this, sleeve);
        _upper.TopLevel = true;
        _fore.TopLevel = true;
        _pusher = new AnimatableBody3D { CollisionLayer = PatientBody.PusherLayer, CollisionMask = 0, TopLevel = true };
        _pusher.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.025f } });
        AddChild(_pusher);
    }

    /// <summary>The local player sees forearms and gloves only; the upper arm would sit right under the camera.</summary>
    public void HideUpperArm()
    {
        _upper.Visible = false;
        _forearmStart = 0.2f;
        if (_fore.FindChild("Cuff", true, false) is Node3D cuff)
        {
            cuff.Visible = false;
        }
    }

    /// <summary>
    /// Fades the glove and sleeve (0 solid, 1 gone) so the hand doesn't hide what it works on. Set on the local view
    /// only. A plain see-through material stands in for the hand's own while it's faded: the compatibility renderer
    /// ignores GeometryInstance3D.Transparency, and the ink outline would draw the hand's inside.
    /// </summary>
    public void SetSeeThrough(float amount)
    {
        _ghost ??= new StandardMaterial3D
        {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, AlbedoColor = GhostColor,
        };
        _ghost.AlbedoColor = _ghost.AlbedoColor with { A = 1f - amount };
        foreach (var node in FindChildren("*", nameof(GeometryInstance3D), true, false))
        {
            if (_trailRoot is null || !_trailRoot.IsAncestorOf(node))
            {
                ((GeometryInstance3D)node).MaterialOverride = amount > 0f ? _ghost : null;
            }
        }
    }

    /// <summary>The tool bounced off what it was pressed onto: the hand hops up off it and comes back down.</summary>
    public void Bounce() => _bounce = 0f;

    /// <summary>Final world position: target plus lift, bounce and tremor.</summary>
    public Vector3 EffectivePosition()
    {
        if (Puppet)
        {
            return Target;
        }
        var hop = BounceHeight * Mathf.Sin(Mathf.Pi * _bounce);
        var tremor = OnHard ? Tremor with { Y = Mathf.Max(Tremor.Y, 0f) } : Tremor;
        return Target + new Vector3(0f, _lift + Raise + hop, 0f) + tremor;
    }

    public Transform3D GripTransform() =>
        new(Inspecting ? InspectBasis() : ToolBasis(Lowered), GlobalPosition);

    /// <summary>Contact position before the wrist returns to its carry angle on button release.</summary>
    public Vector3 WorkingTipPosition(float toolLength) => GlobalPosition - (ToolBasis(true).Z * toolLength);

    private Basis ToolBasis(bool working)
    {
        var yaw = Body.GlobalRotation.Y + Turn;
        var pitch = Tilt;
        if (working && Style.WorkTilt is { } workTilt)
        {
            pitch = Mathf.Min(pitch, workTilt);
        }
        return Spreads
            ? new Basis(Vector3.Up, yaw + Twist) * new Basis(Vector3.Right, pitch)
            : new Basis(Vector3.Up, yaw) * new Basis(Vector3.Right, pitch) * new Basis(Vector3.Forward, Twist);
    }

    /// <summary>The neutral tilt for this grip.</summary>
    public float DefaultTilt() => Style.Tilt;

    /// <summary>The neutral turn for this grip. Asymmetric turns point a tool from either hand in toward the work.
    /// </summary>
    public float DefaultTurn() => Style.Turn * (Index == 1 ? 1f : -1f);

    /// <summary>The twist that turns the held tool's underside (-Y, the side a syringe's scale is printed on) toward
    /// <paramref name="direction"/>. Twist rolls the tool about its own length, so its tip stays where it is.</summary>
    public float TwistFacing(Vector3 direction)
    {
        var yaw = Body.GlobalRotation.Y + Turn;
        var local = (new Basis(Vector3.Up, yaw) * new Basis(Vector3.Right, Tilt)).Inverse() * direction;
        return Mathf.Atan2(-local.X, -local.Y);
    }

    /// <summary>A tool held up to look at: its tip across the view toward the other hand's side, and its top (+Y, where
    /// the back of the hand is in every grip) turned away from the eyes, so the hand is behind the tool and doesn't hide
    /// it.</summary>
    public Basis InspectBasis()
    {
        var eyes = GetParent<Surgeon>().Camera.GlobalBasis.Orthonormalized();
        var along = eyes.X * (Index == 1 ? 1f : -1f);
        var top = -eyes.Z;
        return new Basis(top.Cross(along), top, along);
    }

    public Vector3 TipOffset(float toolLength) => GripTransform().Basis * new Vector3(0f, 0f, -toolLength);

    /// <summary>Where the tip of a tool this long would be from the hand if it held the tool at these angles (twist
    /// doesn't move it).</summary>
    public Vector3 TipOffsetAt(float toolLength, float atTilt, float atTurn)
    {
        var yaw = Body.GlobalRotation.Y + atTurn;
        return new Basis(Vector3.Up, yaw) * new Basis(Vector3.Right, atTilt) * new Vector3(0f, 0f, -toolLength);
    }

    /// <summary>Where the wrist is from the hand's position (world space), the way the hand holds its tool now.</summary>
    public Vector3 WristOffset() => GloveFrame(_elbow, Body.GlobalBasis).Origin - GlobalPosition;

    public void UpdatePose(Vector3 shoulder, float delta)
    {
        _lift = Mathf.MoveToward(_lift, Lifted ? LiftHeight : 0f, delta * SettleSpeed);
        _bounce = Mathf.Min(_bounce + (delta / BounceTime), 1f);
        GlobalPosition = EffectivePosition();
        TrackSpeed(delta);
        GlobalBasis = GripTransform().Basis;
        _pusher.GlobalPosition = GlobalPosition;
        // Held from where it is when aiming starts, also while still easing back from the last time.
        if (Aiming && _elbowHold < 1f)
        {
            _heldElbow = Body.ToLocal(_elbow);
        }
        _elbowHold = Aiming ? 1f : Mathf.MoveToward(_elbowHold, 0f, delta / ElbowEase);
        SolveArm(shoulder);
        AnimateFingers(delta);
        GloveDrop = GloveLowest() - GlobalPosition.Y;
        UpdateTrail(delta);
        // The blood coat is drawn in the glove's own frame, so it has to follow the glove around.
        if (Blood > 0f)
        {
            var coat = new Projection(CoatFrame * _glove.GlobalTransform.AffineInverse());
            foreach (var material in _gloveMaterials)
            {
                material.SetShaderParameter("coat_inverse", coat);
            }
        }
    }

    /// <summary>The point of the glove's middle line, wrist to fingertips, nearest <paramref name="point"/> (world
    /// space). The back of the hand is <see cref="PalmHalfThickness"/> above it.</summary>
    public Vector3 GloveMiddle(Vector3 point)
    {
        var wrist = _glove.GlobalPosition;
        return Geometry3D.GetClosestPointToSegment(point, wrist, wrist + (_glove.GlobalBasis.X.Normalized() * GloveLength));
    }

    /// <summary>Poses the hand at once, fingers already closed as far as they go (no easing in). For fitting and
    /// checking grips.</summary>
    public void SnapPose(Vector3 shoulder)
    {
        _curl = TargetCurl();
        UpdatePose(shoulder, 0f);
        PoseFingers();
    }

    private float GloveLowest()
    {
        var lowest = GlobalPosition.Y - 0.03f;
        foreach (var point in BonePoints())
        {
            lowest = Mathf.Min(lowest, point.Position.Y - point.Radius);
        }
        return lowest;
    }

    /// <summary>
    /// Points inside the glove as it's posed now (world space): along the finger bones and through the palm, so nothing
    /// solid should be at them. <paramref name="part"/>: "Palm" or a finger (<see cref="Fingers"/>) for just that part,
    /// empty for all of it.
    /// </summary>
    public List<BonePoint> BonePoints(string part = "")
    {
        var points = new List<BonePoint>();
        if (_gloveRig is null)
        {
            return points;
        }
        var skeleton = _gloveRig.Skeleton;
        var toWorld = skeleton.GlobalTransform;
        for (var i = 0; i < skeleton.GetBoneCount(); i++)
        {
            var bone = skeleton.GetBoneName(i);
            if (bone is "Hand" or "Cuff" || (part.Length > 0 && !bone.StartsWith(part, StringComparison.Ordinal)))
            {
                continue;
            }
            var at = toWorld * skeleton.GetBoneGlobalPose(i).Origin;
            var children = skeleton.GetBoneChildren(i);
            var to = children.Length > 0 ? toWorld * skeleton.GetBoneGlobalPose(children[0]).Origin : at;
            foreach (var t in (float[])[0f, 0.5f, 1f])
            {
                points.Add(new(at.Lerp(to, t), FingerRadius));
            }
        }
        if (part.Length > 0 && part != "Palm")
        {
            return points;
        }
        // The palm is wide and flat: a grid through it, glove model space (fingers +X, back of the hand +Y).
        for (var i = 0; i < 7; i++)
        {
            for (var j = 0; j < 5; j++)
            {
                foreach (var y in (float[])[-PalmHalfThickness * 0.5f, 0f, PalmHalfThickness * 0.5f])
                {
                    var at = new Vector3(0.085f * i / 6f, y, -0.024f + (0.012f * j));
                    points.Add(new(_glove.GlobalTransform * at, PalmHalfThickness * 0.5f));
                }
            }
        }
        return points;
    }

    /// <summary>Blood works its way from a bloody tool onto the fingers, then the palm. It never drips off on its own.
    /// </summary>
    public void Soak(float toolBlood, float delta)
    {
        if (toolBlood * 0.8f > Blood)
        {
            SetBlood(Mathf.Min(Blood + (SoakRate * delta), toolBlood * 0.8f));
        }
    }

    public void SetBlood(float amount)
    {
        Blood = amount;
        foreach (var material in _gloveMaterials)
        {
            material.SetShaderParameter("coat", amount);
            material.SetShaderParameter("coat_reach", GloveLength * (0.3f + amount));
        }
    }

    private void TrackSpeed(float delta)
    {
        _clock += delta;
        _history.Enqueue((_clock, GlobalPosition));
        while (_history.Count > 2 && _clock - _history.Peek().Time > SpeedWindow)
        {
            _history.Dequeue();
        }
        var oldest = _history.Peek();
        Speed = GlobalPosition.DistanceTo(oldest.Position) / Mathf.Max(_clock - oldest.Time, 0.016f);
    }

    /// <summary>Relaxed when empty, closed around a held tool as its grip says, squeezed a little tighter while using it.
    /// </summary>
    private float TargetCurl() => !Holding ? 0.15f : Lowered || Trigger ? 1.1f : 1f;

    private void AnimateFingers(float delta)
    {
        _curl = Mathf.MoveToward(_curl, TargetCurl(), delta * 4f);
        PoseFingers();
    }

    /// <summary>Bends the finger bones for the current curl. Posing 15 bones only matters while it changes, a fraction of
    /// the time.</summary>
    private void PoseFingers()
    {
        if (_gloveRig is null)
        {
            return;
        }
        // Keyed on the plunger's own position, not where it is in the glove (which shifts with every shiver of the
        // hand): otherwise every bone would be posed again every frame.
        var pose = new PoseKey(_curl, Grip, Holding, Fit, Holding ? Mathf.Snapped(Press, 0.001f) : float.NaN);
        if (pose == _posed)
        {
            return;
        }
        _posed = pose;
        var plunger = PressInGlove();
        var style = Style;
        var amounts = Holding ? Fit.Curl ?? style.Curl : [1f, 1f, 1f, 1f, 1f];
        // The thumb swings across under the index from its root, as far as the fingers are closed.
        var oppose = Holding ? style.Oppose * Mathf.Min(_curl, 1f) : 0f;
        for (var f = 0; f < Fingers.Count; f++)
        {
            var finger = Fingers[f];
            for (var joint = 0; joint < 3; joint++)
            {
                var bone = $"{finger}{joint + 1}";
                // Each joint bends its bone toward the palm (the glove's -Y); the thumb folds in less.
                var bend = Mathf.Min(_curl * amounts[f], 1f) * JointBend[joint] * (finger == "Thumb" ? 0.55f : 1f);
                var axis = _gloveRig.Direction(bone).Cross(Vector3.Down).Normalized();
                var turn = new Basis(axis, bend);
                if (bone == "Thumb1")
                {
                    turn = new Basis(Vector3.Down, oppose) * turn;
                }
                _gloveRig.Rotate(bone, turn);
            }
        }
        if (plunger is { } press)
        {
            ReachPlunger(press);
        }
        else if (_gloveRig.Has("Thumb1"))
        {
            _gloveRig.Shift("Thumb1", Vector3.Zero);
        }
    }

    /// <summary>Where the held syringe's thumb press is in the glove's skeleton space, null when there's none.</summary>
    private Vector3? PressInGlove()
    {
        if (!Holding || float.IsNaN(Press))
        {
            return null;
        }
        return _gloveRig!.Skeleton.GlobalTransform.AffineInverse()
            * (GripTransform() * new Vector3(0f, 0f, Press + PressPad));
    }

    /// <summary>
    /// Bends and swings the thumb so its pad rests on the syringe's thumb press at <paramref name="target"/> (glove
    /// space), following the plunger in and out. The thumb's three bones bend together (as far as it takes to reach
    /// that far from its root), then the whole thumb turns at its root to point there. Out of reach straight (a plunger
    /// pushed right in, by the fingers), its root slides toward it a little, as a thumb's base swings across the palm.
    /// </summary>
    private void ReachPlunger(Vector3 target)
    {
        var rest = ThumbAtRest();
        var straightReach = ThumbTipAt(0f, Basis.Identity).DistanceTo(rest.Root);
        var slide = Mathf.Min(Mathf.Max(rest.Root.DistanceTo(target) - straightReach, 0f), ThumbSlide);
        var offset = (target - rest.Root).Normalized() * slide;
        _gloveRig!.Shift("Thumb1", offset);
        var root = rest.Root + offset;
        var want = root.DistanceTo(target);
        // Bisects the bend: the more it bends, the closer the tip comes to the root.
        var low = 0f;
        var high = MaxThumbBend;
        for (var i = 0; i < 12; i++)
        {
            var mid = (low + high) * 0.5f;
            if (ThumbTipAt(mid, Basis.Identity).DistanceTo(rest.Root) > want)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }
        var bend = (low + high) * 0.5f;
        var from = (ThumbTipAt(bend, Basis.Identity) - rest.Root).Normalized();
        var to = (target - root).Normalized();
        var aim = from.Cross(to).Length() > 0.0001f ? new Basis(new Quaternion(from, to)) : Basis.Identity;
        for (var joint = 0; joint < 3; joint++)
        {
            _gloveRig.Rotate($"Thumb{joint + 1}", ThumbTurn(joint, bend, aim));
        }
    }

    /// <summary>The turn of thumb joint <paramref name="joint"/> (model space, from rest) bent <paramref name="bend"/>
    /// radians in all, the whole thumb turned by <paramref name="aim"/>.</summary>
    private Basis ThumbTurn(int joint, float bend, Basis aim)
    {
        var turn = new Basis(ThumbAtRest().Axes[joint], bend * JointBend[joint] / JointBend.Sum());
        return joint == 0 ? aim * turn : turn;
    }

    /// <summary>Where the thumb's tip ends up (glove space) bent and turned like that: each joint carries the ones past
    /// it.</summary>
    private Vector3 ThumbTipAt(float bend, Basis aim)
    {
        var rest = ThumbAtRest();
        var at = rest.Root;
        var turned = Basis.Identity;
        for (var joint = 0; joint < 3; joint++)
        {
            turned *= ThumbTurn(joint, bend, aim);
            at += turned * rest.Bones[joint];
        }
        return at;
    }

    private ThumbRest ThumbAtRest()
    {
        if (_thumb is not null)
        {
            return _thumb;
        }
        var rig = _gloveRig!;
        var joints = Enumerable.Range(1, 3)
            .Select(joint => rig.Skeleton.GetBoneGlobalRest(rig.Skeleton.FindBone($"Thumb{joint}")).Origin)
            .ToArray();
        var bones = new Vector3[3];
        var axes = new Vector3[3];
        for (var joint = 0; joint < 3; joint++)
        {
            var direction = rig.Direction($"Thumb{joint + 1}");
            bones[joint] = joint < 2 ? joints[joint + 1] - joints[joint] : direction * ThumbTip;
            axes[joint] = direction.Cross(Vector3.Down).Normalized();
        }
        return _thumb = new ThumbRest(joints[0], bones, axes);
    }

    private void SolveArm(Vector3 shoulder)
    {
        var toHand = GlobalPosition - shoulder;
        var distance = Mathf.Clamp(toHand.Length(), 0.05f, UpperArm + Forearm - 0.001f);
        var direction = toHand.Normalized();
        var along = ((UpperArm * UpperArm) - (Forearm * Forearm) + (distance * distance)) / (2f * distance);
        var height = Mathf.Sqrt(Mathf.Max((UpperArm * UpperArm) - (along * along), 0f));
        var bendCenter = shoulder + (direction * along);
        var side = Index == 0 ? -1f : 1f;
        var ownerBasis = Body.GlobalBasis;
        var pole = ((ownerBasis.X * side * 0.6f) + Vector3.Down).Normalized();
        // Reaching straight along the pole (down and out) leaves no bend direction: bend the elbow back instead.
        if (Mathf.Abs(pole.Dot(direction)) > 0.98f)
        {
            pole = ownerBasis.Z;
        }
        pole = (pole - (direction * pole.Dot(direction))).Normalized();
        var elbow = bendCenter + (pole * height);
        if (Holding)
        {
            // The held tool sets which way the wrist points: the elbow goes toward the forearm's line from there, so the
            // wrist doesn't bend over backwards when the hand comes close. Only as far as it stays under the shoulder.
            // The tool as it's held before the wrist aims it: aiming bends the wrist, the elbow stays.
            var held = GloveFrame(elbow, ownerBasis, false);
            var line = held.Origin - (held.Basis.X.Normalized() * Forearm) - shoulder;
            var toward = line - (direction * line.Dot(direction));
            if (toward.Length() > 0.001f)
            {
                foreach (var pull in (float[])[ForearmPull, ForearmPull * 0.75f, ForearmPull * 0.5f, ForearmPull * 0.25f])
                {
                    var bent = bendCenter + (((toward.Normalized() * pull) + (pole * (1f - pull))).Normalized() * height);
                    if (bent.Y < shoulder.Y - ElbowBelowShoulder)
                    {
                        elbow = bent;
                        break;
                    }
                }
            }
        }
        if (ElbowSupport is { } support && ElbowSupportWeight > 0f)
        {
            elbow = SupportedElbow(shoulder, elbow, support, direction, along, height);
        }
        if (_elbowHold > 0f)
        {
            elbow = elbow.Lerp(Body.ToGlobal(_heldElbow), _elbowHold);
        }
        _elbow = elbow;
        PlaceSegment(_upper, shoulder, elbow);
        var wrist = PlaceGlove(elbow, ownerBasis);
        var cuffEnd = wrist + (AimCuff(elbow, wrist) * CuffDepth);
        PlaceSegment(_fore, elbow.Lerp(cuffEnd, _forearmStart), cuffEnd);
    }

    /// <summary>The elbow turned toward the knee it rests on (deep squat). Low working hands can leave the preferred
    /// bend through a knee: then it searches the same IK circle for clearance. The hand and both segment lengths stay
    /// fixed, so reaching for something never moves the tool's contact.</summary>
    private Vector3 SupportedElbow(
        Vector3 shoulder, Vector3 elbow, Vector3 support, Vector3 direction, float along, float height)
    {
        var bendCenter = shoulder + (direction * along);
        var supportPole = (support - bendCenter).Slide(direction);
        if (supportPole.LengthSquared() <= 0.000001f)
        {
            return elbow;
        }
        var currentPole = (elbow - bendCenter).Normalized();
        var supported = currentPole.Slerp(supportPole.Normalized(), ElbowSupportWeight).Normalized();
        elbow = bendCenter + (supported * height);
        if (KneeClearance(shoulder, elbow) >= KneeSleeveClearance)
        {
            return elbow;
        }
        var best = elbow;
        var bestCost = float.PositiveInfinity;
        for (var step = 0; step < 32; step++)
        {
            var candidate = bendCenter + (supported.Rotated(direction, step * Mathf.Tau / 32f) * height);
            var gap = KneeClearance(shoulder, candidate);
            var cost = candidate.DistanceSquaredTo(elbow) + (Mathf.Max(0f, KneeSleeveClearance - gap) * 100f);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = candidate;
            }
        }
        return best;
    }

    private float KneeClearance(Vector3 shoulder, Vector3 elbow)
    {
        var clearance = float.PositiveInfinity;
        foreach (var knee in KneeObstacles)
        {
            clearance = Mathf.Min(clearance, knee.DistanceTo(Geometry3D.GetClosestPointToSegment(knee, shoulder, elbow)));
            clearance = Mathf.Min(clearance,
                knee.DistanceTo(Geometry3D.GetClosestPointToSegment(knee, elbow, GlobalPosition)));
        }
        return clearance;
    }

    /// <summary>Aims the glove's cuff bone down the forearm and returns that direction (world space). However the wrist
    /// bends, the cuff stays on the sleeve and the glove stretches from the hand over it.</summary>
    private Vector3 AimCuff(Vector3 elbow, Vector3 wrist)
    {
        var back = (elbow - wrist).Normalized();
        if (_gloveRig is null || !_gloveRig.Has("Cuff"))
        {
            return back;
        }
        // The cuff bone starts at the wrist, where its parent does, so BoneRig.Direction() can't tell where it points.
        var aim = (_glove.GlobalBasis.Inverse() * back).Normalized();
        var axis = CuffRest.Cross(aim);
        _gloveRig.Rotate("Cuff",
            axis.Length() > 0.0001f ? new Basis(axis.Normalized(), CuffRest.AngleTo(aim)) : Basis.Identity);
        return back;
    }

    /// <summary>Places the glove, shivering, and returns the wrist (the glove's origin).</summary>
    private Vector3 PlaceGlove(Vector3 elbow, Basis ownerBasis)
    {
        var frame = GloveFrame(elbow, ownerBasis);
        frame.Origin += Shiver;
        _glove.GlobalTransform = frame;
        return frame.Origin;
    }

    /// <summary>
    /// Where the glove goes, its origin at the wrist. The left glove is the right one mirrored.
    /// Holding a tool, the glove sits on it by its grip, fitted as if the tool weren't tilted or turned (the grip's rest
    /// tilt, no turn), then turned along with it (unless not <paramref name="turned"/>): the wrist aims the tool, the
    /// tool doesn't move in the hand. Empty, it points along the forearm, palm down.
    /// </summary>
    private Transform3D GloveFrame(Vector3 elbow, Basis ownerBasis, bool turned = true)
    {
        if (!Holding)
        {
            var forward = (GlobalPosition - elbow).Normalized();
            var up = (Vector3.Up - (forward * Vector3.Up.Dot(forward))).Normalized();
            if (up.LengthSquared() < 0.5f)
            {
                up = ownerBasis.Z;
            }
            var side = forward.Cross(up);
            var empty = new Basis(forward, up, Index == 1 ? side : -side);
            return new Transform3D(empty, GlobalPosition - (empty * GripPoint));
        }
        var style = Style;
        var aimed = GripTransform();
        var yaw = Body.GlobalRotation.Y;
        var toolBasis = new Basis(Vector3.Up, yaw) * new Basis(Vector3.Right, style.Tilt) * new Basis(Vector3.Forward, Twist);
        var tool = new Transform3D(toolBasis, aimed.Origin);
        var aim = turned ? aimed.Basis * toolBasis.Inverse() : Basis.Identity;
        var mirror = Index == 0 ? new Vector3(-1f, 1f, 1f) : Vector3.One;
        var contact = tool * (style.On * mirror);
        var frame = toolBasis * Basis.FromScale(mirror) * style.Frame;
        frame = TurnToForearm(frame, toolBasis * Vector3.Forward, contact, elbow, Grip != "fist");
        var wrist = contact - (frame * style.At) + (frame.Y.Normalized() * Fit.Lift) + (frame.Z.Normalized() * Fit.Shift);
        return new Transform3D(aim * frame, aimed.Origin + (aim * (wrist - aimed.Origin)));
    }

    /// <summary>
    /// Turns a held glove about the tool so the wrist faces the elbow, without letting go: first it rolls around the
    /// tool's own axis (the hand wraps the same way, just from another side), then, for grips that allow it, it tips the
    /// tool up to <see cref="MaxToolTip"/> against the fingers, like changing a pen angle.
    /// </summary>
    private static Basis TurnToForearm(Basis frame, Vector3 axis, Vector3 contact, Vector3 elbow, bool canTip)
    {
        var toElbow = (elbow - contact).Normalized();
        var wristDirection = -frame.X.Normalized();
        // Only as far as a forearm turns: turned further, the hand ends up palm up and twisted outward.
        var roll = Mathf.Clamp(
            SignedAngle(wristDirection - (axis * wristDirection.Dot(axis)), toElbow - (axis * toElbow.Dot(axis)), axis),
            -MaxRoll, MaxRoll);
        frame = new Basis(axis, roll) * frame;
        if (!canTip)
        {
            return frame;
        }
        wristDirection = -frame.X.Normalized();
        var pivot = wristDirection.Cross(toElbow);
        return pivot.Length() > 0.001f
            ? new Basis(pivot.Normalized(), Mathf.Min(wristDirection.AngleTo(toElbow), MaxToolTip)) * frame
            : frame;
    }

    private static float SignedAngle(Vector3 from, Vector3 to, Vector3 axis) =>
        from.Length() < 0.0001f || to.Length() < 0.0001f ? 0f : from.SignedAngleTo(to, axis);

    /// <summary>Stretches a unit-long segment model (along +Y) from <paramref name="a"/> to <paramref name="b"/>.
    /// </summary>
    private static void PlaceSegment(Node3D mesh, Vector3 a, Vector3 b)
    {
        var y = b - a;
        var length = y.Length();
        if (length < 0.001f)
        {
            return;
        }
        y /= length;
        var x = y.Cross(Mathf.Abs(y.Dot(Vector3.Forward)) < 0.9f ? Vector3.Forward : Vector3.Right).Normalized();
        var z = x.Cross(y);
        mesh.GlobalTransform = new Transform3D(new Basis(x, y * length, z), (a + b) * 0.5f);
    }

    /// <summary>Copies of the glove trail behind it, each showing where the glove was a step earlier, fainter the older
    /// it is.</summary>
    private void UpdateTrail(float delta)
    {
        if (Trail <= 0f)
        {
            _trailRoot?.QueueFree();
            _trailRoot = null;
            _trailCopies.Clear();
            _trailFrames.Clear();
            return;
        }
        if (_trailRoot is null)
        {
            BuildTrail();
        }
        _trailAccumulated += delta;
        if (_trailAccumulated >= TrailStep || _trailFrames.Count == 0)
        {
            _trailAccumulated = 0f;
            var skeleton = _gloveRig?.Skeleton;
            var poses = new Transform3D[skeleton?.GetBoneCount() ?? 0];
            for (var i = 0; i < poses.Length; i++)
            {
                poses[i] = skeleton!.GetBonePose(i);
            }
            _trailFrames.Insert(0, new TrailFrame(_glove.GlobalTransform, poses));
            if (_trailFrames.Count > TrailCopies + 1)
            {
                _trailFrames.RemoveAt(_trailFrames.Count - 1);
            }
        }
        // The newest frame is about where the glove is now: the copies show the ones before it. A copy right on the
        // glove (a hand held still) would only speckle it.
        for (var i = 0; i < _trailCopies.Count; i++)
        {
            var copy = _trailCopies[i];
            var shown = i + 1 < _trailFrames.Count
                && _trailFrames[i + 1].Glove.Origin.DistanceTo(_glove.GlobalPosition) > TrailGap;
            copy.Root.Visible = shown;
            if (!shown)
            {
                continue;
            }
            var frame = _trailFrames[i + 1];
            copy.Root.GlobalTransform = frame.Glove;
            if (copy.Skeleton is { } copySkeleton)
            {
                for (var bone = 0; bone < frame.Poses.Length; bone++)
                {
                    copySkeleton.SetBonePose(bone, frame.Poses[bone]);
                }
            }
            copy.Material.AlbedoColor = copy.Material.AlbedoColor with
            {
                A = Trail * TrailAlpha * (1f - ((float)i / TrailCopies)),
            };
        }
    }

    private void BuildTrail()
    {
        _trailRoot = new Node3D { Name = "Trail" };
        AddChild(_trailRoot);
        for (var i = 0; i < TrailCopies; i++)
        {
            var copy = (Node3D)_glove.Duplicate();
            copy.TopLevel = true;
            _trailRoot.AddChild(copy);
            var material = new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = TrailColor,
            };
            foreach (var node in copy.FindChildren("*", nameof(GeometryInstance3D), true, false))
            {
                ((GeometryInstance3D)node).MaterialOverride = material;
            }
            var skeleton = copy.FindChildren("*", nameof(Skeleton3D), true, false).FirstOrDefault() as Skeleton3D;
            _trailCopies.Add(new TrailCopy(copy, skeleton, material));
        }
    }
}
