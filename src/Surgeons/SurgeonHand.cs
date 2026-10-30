using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace Scalpel.Surgeons;

/// <summary>A point inside the glove (world space) and how thick the glove is around it.</summary>
public readonly record struct BonePoint(Vector3 Position, float Radius);

/// <summary>
/// One hand and its arm. The arm is solved with analytic two-bone IK from shoulder to hand every frame.
/// Position is owned by the surgeon's peer and synced to everyone; the host uses it to drive tools.
/// </summary>
public partial class SurgeonHand : Node3D {
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
    /// <summary>How high (meters) and for how long (seconds) the hand hops when its tool bounces off what it was pressed
    /// onto.</summary>
    public const float BounceHeight = 0.02f;
    private const float BounceTime = 0.35f;
    /// <summary>How fast (m/s) a raised hand comes back down.</summary>
    public const float SettleSpeed = 0.8f;
    /// <summary>Seconds of game time the hand's speed is measured over.</summary>
    private const float SpeedWindow = 0.25f;
    public static readonly IReadOnlyList<string> Fingers = ["Index", "Middle", "Ring", "Pinky", "Thumb"];
    /// <summary>Each finger's joint bones in <see cref="Fingers"/>' order, root first: the poses name them every frame.
    /// </summary>
    private static readonly string[][] JointBones = [.. Fingers.Select(finger => (string[])[finger + "1", finger + "2", finger + "3"])];
    private static readonly string[] ThumbBones = JointBones[^1];
    /// <summary>Radians each finger joint bends at full curl, knuckle first.</summary>
    private static readonly float[] JointBend = [0.9f, 1.2f, 0.8f];
    /// <summary>Most the thumb bends in all (radians) reaching for a syringe's plunger.</summary>
    private const float MaxThumbBend = 3.2f;
    /// <summary>How far a finger's tip reaches past its last joint (meters).</summary>
    private const float FingerTip = 0.02f;
    /// <summary>How many turns of the palm round a limb <see cref="GraspFit"/> tries from its top to
    /// <see cref="MaxGraspTurn"/> toward the near side, and how many past the top toward the far side (where the lower
    /// palm lies with the palm's middle over the top).</summary>
    private const int GraspSteps = 16;
    private const int GraspStepsPastTop = 8;
    /// <summary>The palm's length along its middle, heel to knuckles (meters), and how many spots along it
    /// <see cref="GraspFit"/> tries touching a limb's top with.</summary>
    private const float PalmLength = 0.085f;
    private const int TouchSteps = 10;
    /// <summary>Furthest (radians) a hand grasping a limb turns its palm from the limb's top toward the near side.
    /// </summary>
    private const float MaxGraspTurn = 1.3f;
    /// <summary>Furthest out from a limb's middle line (meters) <see cref="GraspFit"/> looks for its skin.</summary>
    private const float GraspClear = 0.15f;
    /// <summary>How far round toward the near side of a limb (radians) from straight into its palm a hand grasping it
    /// presses.</summary>
    private const float GraspPressSlant = Mathf.Pi / 4f;
    /// <summary>How many directions round a limb <see cref="GraspFit"/> finds its skin in.</summary>
    private const int GraspAngles = 48;
    /// <summary>How far past the first surface out from a limb's middle (meters) <see cref="GraspFit"/> looks for its
    /// skin's outside.</summary>
    private const float UnderSkin = 0.02f;
    /// <summary>How many times <see cref="GraspFit"/> reads the skin's distance where a spot of the palm comes to.
    /// </summary>
    private const int ClearingReads = 3;
    /// <summary>How far apart along a limb (meters) <see cref="GraspFit"/> finds its skin round it, and how many times
    /// either side of the grasp: past the palm's sides, so a limb widening under it (a thigh toward the hip) counts.
    /// </summary>
    private const float GraspSliceStep = 0.02f;
    private const int GraspSlicesEachSide = 2;
    /// <summary>The cells under the palm, heel to knuckles and thumb side to little finger side (glove space x, z): the
    /// lowest point of the glove's skin in each is where the palm's underside is there (see
    /// <see cref="PalmUnderside"/>).</summary>
    private static readonly Vector2[] PalmCells =
    [
        .. Enumerable.Range(0, 9).SelectMany(i => Enumerable.Range(0, 5)
            .Select(j => new Vector2(0.011f * i, -0.032f + (0.016f * j)))),
    ];
    private static readonly Vector2 PalmCellSize = new(0.011f, 0.016f);
    /// <summary>The middle of the lower half of the palm's underside, glove space: what a hand grasping a limb lays on
    /// it.</summary>
    private static readonly Vector3 LowerPalm = new(0.0213f, -PalmHalfThickness, 0f);
    /// <summary>Where the palm's knuckle half turns to cup it, glove space, and how far either side of it the cup bends
    /// the palm, heel to knuckles (the Palm bone and its skin weights, tools/blender/hand.py).</summary>
    private static readonly Vector3 PalmHinge = new(0.045f, 0f, 0f);
    private const float PalmBlend = 0.04f;
    /// <summary>The axis the palm cups about straight across it, glove space: its knuckle half bends toward the palm
    /// (-Y). It may slant as the palm's crease does, cupping the index side more or the little finger side.</summary>
    private static readonly Vector3 PalmCupAxis = Vector3.Forward;
    /// <summary>Furthest (radians) the palm's cup slants from straight across it, and how many slants
    /// <see cref="GraspFit"/> tries each way.</summary>
    private const float MaxCupSlant = 0.6f;
    private const int SlantSteps = 4;
    /// <summary>Most a hand grasping a limb cups its palm round it (radians). A palm bends only at the wrist and the
    /// knuckles: between them it just gives a little, its soft flesh pressed against the limb.</summary>
    private const float MaxPalmCup = 0.12f;
    /// <summary>How many cups of the palm <see cref="GraspFit"/> tries at each turn, from flat to
    /// <see cref="MaxPalmCup"/>.</summary>
    private const int CupSteps = 4;
    /// <summary>Furthest (radians) a hand grasping a limb leans its palm along it, and how many leans
    /// <see cref="GraspFit"/> tries each way.</summary>
    private const float MaxGraspLean = 0.3f;
    private const int LeanSteps = 6;
    /// <summary>Most a finger curls closing round a limb it grasps (times <see cref="JointBend"/>).</summary>
    private const float MaxGraspCurl = 1.7f;
    /// <summary>Room (meters) a grasping finger keeps off the skin: its pose follows the hand only once it has moved a
    /// millimeter (see PoseFingers()).</summary>
    private const float FingerClearance = 0.0015f;
    /// <summary>Furthest the thumb's root slides toward a plunger out of its reach (meters).</summary>
    private const float ThumbSlide = 0.012f;
    /// <summary>How far behind the thumb press its pad's middle is (meters).</summary>
    internal const float PressPad = 0.008f;
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
    /// <summary>Which way the elbow bends (owner's space, the right arm's; the left one is mirrored): out to the side,
    /// a little down and back. The player's own arms then come into the view from its bottom corners rather than up its
    /// middle.</summary>
    internal static readonly Vector3 ElbowPole = new(1f, -0.4f, 0.2f);
    /// <summary>How far out past its shoulder (meters, across the body) the elbow stays when a held tool draws it in
    /// toward the forearm's line.</summary>
    private const float ElbowOut = 0.15f;
    /// <summary>Room between the elbow and the table, patient or drapes under it (meters): about a sleeve's thickness.
    /// </summary>
    private const float ElbowClearance = 0.07f;
    /// <summary>How many places round its IK circle the elbow tries to clear what's under it.</summary>
    private const int GroundSteps = 32;
    /// <summary>How fast (radians a second) the elbow turns round its IK circle to clear what's under it and back.
    /// </summary>
    private const float ElbowTurnSpeed = 4f;
    /// <summary>How fast (radians a second) a hand that let go of its tool rolls back from the tool's twist to palm
    /// down.</summary>
    internal const float UntwistSpeed = 3f;
    /// <summary>Empty hand: the glove point (glove model space) at the hand's position, the hollow of the fingers.
    /// </summary>
    private static readonly Vector3 GripPoint = new(0.07f, -0.028f, 0f);
    /// <summary>Seconds the elbow takes to ease back after aiming.</summary>
    private const float ElbowEase = 0.25f;
    /// <summary>Stands in for the hand's materials while it's see-through (see SetSeeThrough()).</summary>
    private static readonly Color GhostColor = new(0.75f, 0.82f, 0.9f);
    internal const int TrailCopies = 4;
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
    /// <summary>How deep under an opening's lip (meters) the wheel holds this hand's forceps tip, null while it rests on
    /// what's inside (see Surgeon.HoldsDepth()). Only the owner places its hands, so it isn't streamed.</summary>
    public float? HeldDepth { get; set; }
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

    /// <summary>Motion across the operating surface, excluding the hand settling into an opening.</summary>
    internal float LateralSpeed { get; private set; }
    /// <summary>Remote copies receive the final position already including lift and tremor.</summary>
    public bool Puppet { get; set; }
    /// <summary>How bloody the glove is (0..1). Every peer soaks it from the held tool's synced blood, so it matches
    /// everywhere.</summary>
    public float Blood { get; private set; }
    /// <summary>Deep squat: aim the elbow toward the knee's upper surface instead of bending through the thigh.</summary>
    public Vector3? ElbowSupport { get; set; }
    public float ElbowSupportWeight { get; set; }
    public List<Vector3> KneeObstacles { get; } = [];
    /// <summary>How high whatever lies under a point reaches (world), set by the surgeon: the elbow stays above it, so
    /// a surgeon bent over the table doesn't put an elbow through the patient. Null for a hand on its own.</summary>
    public Func<Vector3, float>? GroundAt { get; set; }
    /// <summary>Set by the surgeon each frame; drives how far the fingers curl.</summary>
    public bool Holding { get; set; }
    /// <summary><see cref="ToolDef.Grip"/> of the tool in this hand, set with <see cref="Holding"/>. Empty hands follow
    /// the forearm, open.</summary>
    public string Grip { get; set; } = "pencil";
    /// <summary>A held syringe's thumb press: how far behind the grip it is along the syringe (meters, it moves with the
    /// plunger), set by the surgeon. NaN for any other tool.</summary>
    public float Press { get; set; } = float.NaN;
    /// <summary>Empty and pressed flat on the patient (Surgeon.HoldPatient()): the skin's normal there, its palm laid on
    /// the skin across it. Null while it isn't.</summary>
    public Vector3? PressNormal { get; set; }
    /// <summary>The patient's rig bone a hand pressed on the patient holds, which keeps that part still ("" while it
    /// isn't).</summary>
    public string PressBone { get; set; } = "";
    /// <summary>How far the fingers of a hand pressed on the patient curl round what they hold: 0 flat on a trunk, more
    /// round a limb, negative straightened out over an opening.</summary>
    public float PressWrap { get; set; }
    /// <summary>Which way the fingers of a hand grasping a limb point, across it and round it (zero off a limb): they
    /// close round it until they meet its skin (see GraspWith()).</summary>
    public Vector3 PressAcross { get; set; }
    /// <summary>How a hand grasping a limb cups its palm round it, its knuckle half turned toward the palm (see
    /// GraspFit()): the axis it turns about (glove space) times how far (radians); zero flat.</summary>
    public Vector3 PressCup { get; set; }
    /// <summary>How this hand's grip is fitted to the tool it holds (see <see cref="GripFit"/>).</summary>
    public GripFit Fit { get; set; } = GripFit.None;
    /// <summary>How far below the hand's position the glove reaches as it's posed now (negative: below). The surgeon
    /// keeps that clear of tables and trays, not only the hand's middle.</summary>
    public float GloveDrop { get; private set; } = -0.03f;

    private GripStyle Style => GripStyle.For(PressNormal is null ? Grip : "press");
    /// <summary>The fingers close as a grip says: around a held tool or pressed on the patient.</summary>
    private bool Gripping => Holding || PressNormal is not null;
    private Node3D Body => GetParent<Node3D>();

    /// <summary>How far through a bounce (<see cref="Bounce"/>) the hand is, 1 for none.</summary>
    private float _bounce = 1f;
    /// <summary>Recent positions in game time. Speed over a short window ignores tremor and network jitter. Game time,
    /// not the wall clock: physics frames run back to back after a stall would read as a burst of speed.</summary>
    private readonly Queue<(float Time, Vector3 Position)> _history = new();
    private float _clock;
    private readonly Dictionary<string, FingerRest> _fingerRests = [];
    private Vector3[]? _palmUnderside;
    private float _curl = 0.2f;
    /// <summary>What the fingers were last posed for.</summary>
    private PoseKey? _posed;
    private Node3D _glove = null!;
    /// <summary>The glove model, its origin at the wrist.</summary>
    internal Node3D Glove => _glove;
    private BoneRig? _gloveRig;
    /// <summary>Where along each finger bone, and how high through the palm, the glove's points are taken.</summary>
    private static readonly float[] BoneSamples = [0f, 0.5f, 1f];
    private static readonly float[] PalmLayers = [-PalmHalfThickness * 0.5f, 0f, PalmHalfThickness * 0.5f];
    /// <summary>Reused for the glove's points every frame.</summary>
    private readonly List<BonePoint> _gloveScratch = [];
    /// <summary>The glove model's skeleton, null before <see cref="Build"/>.</summary>
    internal Skeleton3D? GloveSkeleton => _gloveRig?.Skeleton;
    private List<ShaderMaterial> _gloveMaterials = [];
    private Node3D _upper = null!;
    /// <summary>The upper arm's sleeve model.</summary>
    internal Node3D UpperSleeve => _upper;
    private Node3D _upperArm = null!;

    /// <summary>Whether the upper arm is drawn above the elbow; the rounded elbow that ends the forearm always is.</summary>
    internal bool UpperArmShown {
        get => _upperArm.Visible;
        set => _upperArm.Visible = value;
    }

    private Node3D _fore = null!;
    /// <summary>The forearm's sleeve model.</summary>
    internal Node3D ForeSleeve => _fore;
    /// <summary>Where the elbow was put last.</summary>
    private Vector3 _elbow;
    /// <summary>Where the elbow was put last (world).</summary>
    internal Vector3 Elbow => _elbow;
    /// <summary>Where the elbow is held (owner's space) while aiming.</summary>
    private Vector3 _heldElbow;
    /// <summary>1 held, 0 free, eased back after aiming.</summary>
    private float _elbowHold;
    /// <summary>How far round its IK circle (radians) the elbow is turned to stay above what's under it.</summary>
    private float _groundTurn;
    private AnimatableBody3D _pusher = null!;
    private StandardMaterial3D? _ghost;
    private bool _seeThrough;
    private Node3D? _trailRoot;
    private readonly List<TrailCopy> _trailCopies = [];
    /// <summary>The glove frames and bone poses the afterimages show, newest first.</summary>
    private readonly List<TrailFrame> _trailFrames = [];
    private float _trailAccumulated;

    private readonly record struct PoseKey(float Curl, string Grip, bool Holding, GripFit Fit, float Press, float Wrap,
        Vector3 Grasping, Vector3 Cup);

    /// <summary>A finger at rest, glove space: where its root is, each bone (to the next joint, the last to its tip) and
    /// the axis each joint bends about.</summary>
    internal sealed record FingerRest(Vector3 Root, Vector3[] Bones, Vector3[] Axes);

    private readonly record struct TrailFrame(Transform3D Glove, Transform3D[] Poses);

    private sealed record TrailCopy(Node3D Root, Skeleton3D? Skeleton, StandardMaterial3D Material);

    public void Build(int handIndex, ShaderMaterial scrubs) {
        Index = handIndex;
        Name = Index == 0 ? "LeftHand" : "RightHand";
        Turn = Index == 1 ? RestTurn : -RestTurn;
        var sleeve = new Dictionary<string, Material> { ["tint"] = scrubs };
        _glove = ModelSlot.Instantiate("surgeon", "glove", this);
        _gloveMaterials = ModelSlot.OwnMaterials(_glove);
        foreach (var material in _gloveMaterials) {
            material.SetShaderParameter(ShaderParam.CoatLength, GloveLength);
        }
        // The glove is modeled wrist at the origin, fingers along +X, palm facing -Y, thumb toward -Z.
        // Empty, it follows the forearm (see PlaceGlove()). Holding a tool, it sits on the tool by its grip (GripStyle).
        _glove.TopLevel = true;
        _gloveRig = BoneRig.Find(_glove);
        _upper = ModelSlot.Instantiate("surgeon", "upper_arm", this, sleeve);
        _fore = ModelSlot.Instantiate("surgeon", "forearm", this, sleeve);
        _upperArm = ModelSlot.Parts(_upper, "Sleeve")["Sleeve"];
        _upper.TopLevel = true;
        _fore.TopLevel = true;
        _pusher = new AnimatableBody3D { CollisionLayer = PatientBody.PusherLayer, CollisionMask = 0, TopLevel = true };
        _pusher.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.025f } });
        AddChild(_pusher);
        // Read now, not on the frame the hand first grasps a limb.
        _palmUnderside = PalmUnderside();
    }

    /// <summary>
    /// Fades the glove and sleeve (0 solid, 1 gone) so the hand doesn't hide what it works on. Set on the local view
    /// only. A plain see-through material stands in for the hand's own while it's faded: the compatibility renderer
    /// ignores GeometryInstance3D.Transparency, and the ink outline would draw the hand's inside.
    /// </summary>
    public void SetSeeThrough(float amount) {
        _ghost ??= new StandardMaterial3D {
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = GhostColor,
        };
        _ghost.AlbedoColor = _ghost.AlbedoColor with { A = 1f - amount };
        // The shared material carries the fade; switching overrides only at its boundaries avoids
        // rebuilding material state and walking the scene tree on every animation step.
        var seeThrough = amount > 0f;
        if (seeThrough == _seeThrough) {
            return;
        }
        _seeThrough = seeThrough;
        foreach (var node in FindChildren("*", nameof(GeometryInstance3D), true, false)) {
            if (_trailRoot is null || !_trailRoot.IsAncestorOf(node)) {
                ((GeometryInstance3D)node).MaterialOverride = amount > 0f ? _ghost : null;
            }
        }
    }

    /// <summary>The tool bounced off what it was pressed onto: the hand hops up off it and comes back down.</summary>
    public void Bounce() => _bounce = 0f;

    /// <summary>Final world position: target plus raise, bounce and tremor.</summary>
    public Vector3 EffectivePosition() {
        if (Puppet) {
            return Target;
        }
        var hop = BounceHeight * Mathf.Sin(Mathf.Pi * _bounce);
        var tremor = OnHard ? Tremor with { Y = Mathf.Max(Tremor.Y, 0f) } : Tremor;
        return Target + new Vector3(0f, Raise + hop, 0f) + tremor;
    }

    public Transform3D GripTransform() =>
        new(Inspecting ? InspectBasis() : ToolBasis(Lowered), GlobalPosition);

    /// <summary>Contact position before the wrist returns to its carry angle on button release.</summary>
    public Vector3 WorkingTipPosition(float toolLength) => GlobalPosition - (ToolBasis(true).Z * toolLength);

    private Basis ToolBasis(bool working) {
        var yaw = Body.GlobalRotation.Y + Turn;
        var pitch = Tilt;
        if (working && Style.WorkTilt is { } workTilt) {
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
    public float TwistFacing(Vector3 direction) {
        var yaw = Body.GlobalRotation.Y + Turn;
        var local = (new Basis(Vector3.Up, yaw) * new Basis(Vector3.Right, Tilt)).Inverse() * direction;
        return Mathf.Atan2(-local.X, -local.Y);
    }

    /// <summary>A tool held up to look at: its tip across the view toward the other hand's side, and its top (+Y, where
    /// the back of the hand is in every grip) turned away from the eyes, so the hand is behind the tool and doesn't hide
    /// it.</summary>
    public Basis InspectBasis() {
        var eyes = GetParent<Surgeon>().Camera.GlobalBasis.Orthonormalized();
        var along = eyes.X * (Index == 1 ? 1f : -1f);
        var top = -eyes.Z;
        return new Basis(top.Cross(along), top, along);
    }

    public Vector3 TipOffset(float toolLength) => GripTransform().Basis * new Vector3(0f, 0f, -toolLength);

    /// <summary>Where the tip of a tool this long would be from the hand if it held the tool at these angles (twist
    /// doesn't move it).</summary>
    public Vector3 TipOffsetAt(float toolLength, float atTilt, float atTurn) {
        var yaw = Body.GlobalRotation.Y + atTurn;
        return new Basis(Vector3.Up, yaw) * new Basis(Vector3.Right, atTilt) * new Vector3(0f, 0f, -toolLength);
    }

    /// <summary>Where the wrist is from the hand's position (world space), the way the hand holds its tool now.</summary>
    public Vector3 WristOffset() => GloveFrame(_elbow, Body.GlobalBasis).Origin - GlobalPosition;

    public void UpdatePose(Vector3 shoulder, float delta) {
        _bounce = Mathf.Min(_bounce + (delta / BounceTime), 1f);
        if (!Holding) {
            // Twist wraps to -pi..pi, so this rolls back the short way.
            Twist = Mathf.MoveToward(Twist, 0f, delta * UntwistSpeed);
        }
        GlobalPosition = EffectivePosition();
        TrackSpeed(delta);
        GlobalBasis = GripTransform().Basis;
        _pusher.GlobalPosition = GlobalPosition;
        // Held from where it is when aiming starts, also while still easing back from the last time.
        if (Aiming && _elbowHold < 1f) {
            _heldElbow = Body.ToLocal(_elbow);
        }
        _elbowHold = Aiming ? 1f : Mathf.MoveToward(_elbowHold, 0f, delta / ElbowEase);
        SolveArm(shoulder, delta);
        AnimateFingers(delta);
        GloveDrop = GloveLowest() - GlobalPosition.Y;
        UpdateTrail(delta);
        // The blood coat is drawn in the glove's own frame, so it has to follow the glove around.
        if (Blood > 0f) {
            var coat = new Projection(CoatFrame * _glove.GlobalTransform.AffineInverse());
            foreach (var material in _gloveMaterials) {
                material.SetMatrix(ShaderParam.CoatInverse, coat);
            }
        }
    }

    /// <summary>The point of the glove's middle line, wrist to fingertips, nearest <paramref name="point"/> (world
    /// space). The back of the hand is <see cref="PalmHalfThickness"/> above it.</summary>
    public Vector3 GloveMiddle(Vector3 point) {
        var wrist = _glove.GlobalPosition;
        return Geometry3D.GetClosestPointToSegment(point, wrist, wrist + (_glove.GlobalBasis.X.Normalized() * GloveLength));
    }

    /// <summary>Poses the hand at once, fingers already closed as far as they go (no easing in). For fitting and
    /// checking grips.</summary>
    public void SnapPose(Vector3 shoulder) {
        _curl = TargetCurl();
        UpdatePose(shoulder, 0f);
        PoseFingers();
    }

    /// <summary>Points inside the glove as it was last posed (world space, see <see cref="BonePoints"/>).</summary>
    internal IReadOnlyList<BonePoint> PosedPoints => _gloveScratch;

    private float GloveLowest() {
        _gloveScratch.Clear();
        AddBonePoints(_gloveScratch, "");
        var lowest = GlobalPosition.Y - 0.03f;
        foreach (var point in _gloveScratch) {
            lowest = Mathf.Min(lowest, point.Position.Y - point.Radius);
        }
        return lowest;
    }

    /// <summary>
    /// Points inside the glove as it's posed now (world space): along the finger bones and through the palm, so nothing
    /// solid should be at them. <paramref name="part"/>: "Palm" or a finger (<see cref="Fingers"/>) for just that part,
    /// empty for all of it.
    /// </summary>
    public List<BonePoint> BonePoints(string part = "") {
        var points = new List<BonePoint>();
        AddBonePoints(points, part);
        return points;
    }

    private void AddBonePoints(List<BonePoint> points, string part) {
        if (_gloveRig is null) {
            return;
        }
        var skeleton = _gloveRig.Skeleton;
        var toWorld = skeleton.GlobalTransform;
        var bones = _gloveRig.Bones;
        for (var i = 0; i < bones.Count; i++) {
            var (bone, child) = bones[i];
            if (bone is "Hand" or "Palm" or "Cuff" || (part.Length > 0 && !bone.StartsWith(part, StringComparison.Ordinal))) {
                continue;
            }
            var at = toWorld * skeleton.GetBoneGlobalPose(i).Origin;
            var to = child >= 0 ? toWorld * skeleton.GetBoneGlobalPose(child).Origin : at;
            foreach (var t in BoneSamples) {
                points.Add(new(at.Lerp(to, t), FingerRadius));
            }
        }
        if (part.Length > 0 && part != "Palm") {
            return;
        }
        // The palm is wide and flat: a grid through it, glove model space (fingers +X, back of the hand +Y), cupped as
        // it is.
        var glove = _glove.GlobalTransform;
        var knuckles = KnuckleHalf(PressNormal is null ? Vector3.Zero : PressCup);
        for (var i = 0; i < 7; i++) {
            for (var j = 0; j < 5; j++) {
                foreach (var y in PalmLayers) {
                    var at = new Vector3(0.085f * i / 6f, y, -0.024f + (0.012f * j));
                    points.Add(new(glove * Cupped(at, knuckles), PalmHalfThickness * 0.5f));
                }
            }
        }
    }

    /// <summary>Blood works its way from a bloody tool onto the fingers, then the palm. It never drips off on its own.
    /// </summary>
    public void Soak(float toolBlood, float delta) {
        if (toolBlood * 0.8f > Blood) {
            SetBlood(Mathf.Min(Blood + (SoakRate * delta), toolBlood * 0.8f));
        }
    }

    public void SetBlood(float amount) {
        Blood = amount;
        foreach (var material in _gloveMaterials) {
            material.SetShaderParameter(ShaderParam.Coat, amount);
            material.SetShaderParameter(ShaderParam.CoatReach, GloveLength * (0.3f + amount));
        }
    }

    private void TrackSpeed(float delta) {
        _clock += delta;
        _history.Enqueue((_clock, GlobalPosition));
        while (_history.Count > 2 && _clock - _history.Peek().Time > SpeedWindow) {
            _history.Dequeue();
        }
        var oldest = _history.Peek();
        var motion = GlobalPosition - oldest.Position;
        var elapsed = Mathf.Max(_clock - oldest.Time, 0.016f);
        Speed = motion.Length() / elapsed;
        LateralSpeed = motion.Slide(Vector3.Up).Length() / elapsed;
    }

    /// <summary>Relaxed when empty, closed around a held tool as its grip says, squeezed a little tighter while using it.
    /// </summary>
    private float TargetCurl() => PressNormal is not null ? 1f : !Holding ? 0.15f : Lowered || Trigger ? 1.1f : 1f;

    private void AnimateFingers(float delta) {
        _curl = Mathf.MoveToward(_curl, TargetCurl(), delta * 4f);
        PoseFingers();
    }

    /// <summary>Bends the finger bones for the current curl. Posing 15 bones only matters while it changes, a fraction of
    /// the time.</summary>
    private void PoseFingers() {
        if (_gloveRig is null) {
            return;
        }
        // Keyed on the plunger's own position, not where it is in the glove (which shifts with every shiver of the
        // hand): otherwise every bone would be posed again every frame.
        var wrap = PressNormal is null ? 0f : PressWrap;
        // Grasping a limb, the fingers close round its skin from where the glove is: posed again once it has moved.
        var grasping = PressNormal is not null && PressAcross != Vector3.Zero ? _glove.GlobalPosition.Snapped(0.001f)
            : Vector3.Zero;
        var cup = PressNormal is null ? Vector3.Zero : PressCup;
        var pose = new PoseKey(_curl, Grip, Gripping, Fit, Holding ? Mathf.Snapped(Press, 0.001f) : float.NaN, wrap,
            grasping, cup);
        if (pose == _posed) {
            return;
        }
        _posed = pose;
        _gloveRig.Rotate("Palm", CupTurn(cup));
        var plunger = PressInGlove();
        var style = Style;
        float[] amounts = PressNormal is not null ? [.. style.Curl.Select(curl => Mathf.Max(curl + wrap, 0f))]
            : Holding ? Fit.Curl ?? style.Curl
            : [1f, 1f, 1f, 1f, 1f];
        // The thumb swings across under the index from its root, as far as the fingers are closed.
        var oppose = Gripping ? style.Oppose * Mathf.Min(_curl, 1f) : 0f;
        for (var f = 0; f < Fingers.Count; f++) {
            var finger = Fingers[f];
            if (grasping != Vector3.Zero && finger != "Thumb") {
                GraspWith(finger);
                continue;
            }
            for (var joint = 0; joint < 3; joint++) {
                var bone = JointBones[f][joint];
                // Each joint bends its bone toward the palm (the glove's -Y); the thumb folds in less.
                var bend = Mathf.Min(_curl * amounts[f], 1f) * JointBend[joint] * (finger == "Thumb" ? 0.55f : 1f);
                var axis = _gloveRig.Direction(bone).Cross(Vector3.Down).Normalized();
                var turn = new Basis(axis, bend);
                if (bone == "Thumb1") {
                    turn = new Basis(Vector3.Down, oppose) * turn;
                }
                _gloveRig.Rotate(bone, turn);
            }
        }
        if (plunger is { } press) {
            ReachPlunger(press);
        } else if (_gloveRig.Has("Thumb1")) {
            _gloveRig.Shift("Thumb1", Vector3.Zero);
        }
    }

    /// <summary>Where the held syringe's thumb press is in the glove's skeleton space, null when there's none.</summary>
    private Vector3? PressInGlove() {
        if (!Holding || float.IsNaN(Press)) {
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
    private void ReachPlunger(Vector3 target) {
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
        for (var i = 0; i < 12; i++) {
            var mid = (low + high) * 0.5f;
            if (ThumbTipAt(mid, Basis.Identity).DistanceTo(rest.Root) > want) {
                low = mid;
            } else {
                high = mid;
            }
        }
        var bend = (low + high) * 0.5f;
        var from = (ThumbTipAt(bend, Basis.Identity) - rest.Root).Normalized();
        var to = (target - root).Normalized();
        var aim = from.Cross(to).Length() > 0.0001f ? new Basis(new Quaternion(from, to)) : Basis.Identity;
        for (var joint = 0; joint < 3; joint++) {
            _gloveRig.Rotate(ThumbBones[joint], ThumbTurn(joint, bend, aim));
        }
    }

    /// <summary>The turn of thumb joint <paramref name="joint"/> (model space, from rest) bent <paramref name="bend"/>
    /// radians in all, the whole thumb turned by <paramref name="aim"/>.</summary>
    private Basis ThumbTurn(int joint, float bend, Basis aim) {
        var turn = new Basis(ThumbAtRest().Axes[joint], bend * JointBend[joint] / JointBend.Sum());
        return joint == 0 ? aim * turn : turn;
    }

    /// <summary>Where the thumb's tip ends up (glove space) bent and turned like that: each joint carries the ones past
    /// it.</summary>
    private Vector3 ThumbTipAt(float bend, Basis aim) {
        var rest = ThumbAtRest();
        var at = rest.Root;
        var turned = Basis.Identity;
        for (var joint = 0; joint < 3; joint++) {
            turned *= ThumbTurn(joint, bend, aim);
            at += turned * rest.Bones[joint];
        }
        return at;
    }

    internal FingerRest ThumbAtRest() => FingerAtRest("Thumb");

    private FingerRest FingerAtRest(string finger) {
        if (_fingerRests.TryGetValue(finger, out var known)) {
            return known;
        }
        var rig = _gloveRig!;
        var joints = Enumerable.Range(1, 3)
            .Select(joint => rig.Skeleton.GetBoneGlobalRest(rig.Skeleton.FindBone($"{finger}{joint}")).Origin)
            .ToArray();
        var bones = new Vector3[3];
        var axes = new Vector3[3];
        for (var joint = 0; joint < 3; joint++) {
            var direction = rig.Direction($"{finger}{joint + 1}");
            bones[joint] = joint < 2 ? joints[joint + 1] - joints[joint] : direction * FingerTip;
            axes[joint] = direction.Cross(Vector3.Down).Normalized();
        }
        return _fingerRests[finger] = new FingerRest(joints[0], bones, axes);
    }

    /// <summary>Closes a finger round the limb the hand grasps the way a finger wraps round something: from the knuckle
    /// out, each joint bends as far as it goes short of its bone's palm side going into the skin, so each bone in turn
    /// comes to rest against the skin whatever the limb's shape.</summary>
    private void GraspWith(string finger) {
        var rest = FingerAtRest(finger);
        var bends = new float[3];
        var most = Mathf.Min(_curl, 1f) * MaxGraspCurl;
        for (var joint = 0; joint < 3; joint++) {
            // Bisects this joint's bend: bent further, its bone reaches the skin.
            var low = 0f;
            var high = most * JointBend[joint];
            for (var i = 0; i < 7; i++) {
                bends[joint] = (low + high) * 0.5f;
                if (FingerInSkin(rest, bends, joint)) {
                    high = bends[joint];
                } else {
                    low = bends[joint];
                }
            }
            bends[joint] = low;
        }
        for (var joint = 0; joint < 3; joint++) {
            _gloveRig!.Rotate($"{finger}{joint + 1}", new Basis(rest.Axes[joint], bends[joint]));
        }
    }

    /// <summary>Whether bone <paramref name="last"/> of a finger bent by <paramref name="bends"/> goes into the
    /// patient's skin: a line from its joint to the palm side of its end crosses it. The bones before it are already
    /// clear of the skin (GraspWith() settles them first).</summary>
    private bool FingerInSkin(FingerRest rest, float[] bends, int last) {
        // The fingers ride the palm's knuckle half, cupped as it is.
        var knuckles = KnuckleHalf(PressCup);
        var at = knuckles * rest.Root;
        var turned = knuckles.Basis;
        for (var joint = 0; joint < last; joint++) {
            turned *= new Basis(rest.Axes[joint], bends[joint]);
            at += turned * rest.Bones[joint];
        }
        turned *= new Basis(rest.Axes[last], bends[last]);
        var end = at + (turned * rest.Bones[last]);
        // The palm side: the way the joint bends, square to the bone.
        var palm = turned * rest.Axes[last].Cross(rest.Bones[last]).Normalized();
        var toWorld = _gloveRig!.Skeleton.GlobalTransform;
        return Rays.Cast(this, toWorld * at, toWorld * (end + (palm * (FingerRadius + FingerClearance))),
            PatientBody.SurfaceLayer) is not null;
    }

    private void SolveArm(Vector3 shoulder, float delta) {
        var toHand = GlobalPosition - shoulder;
        var distance = Mathf.Clamp(toHand.Length(), 0.05f, UpperArm + Forearm - 0.001f);
        var direction = toHand.Normalized();
        var along = ((UpperArm * UpperArm) - (Forearm * Forearm) + (distance * distance)) / (2f * distance);
        var height = Mathf.Sqrt(Mathf.Max((UpperArm * UpperArm) - (along * along), 0f));
        var bendCenter = shoulder + (direction * along);
        var side = Index == 0 ? -1f : 1f;
        var ownerBasis = Body.GlobalBasis;
        var pole = (ownerBasis * (ElbowPole * new Vector3(side, 1f, 1f))).Normalized();
        // Reaching straight along the pole (down and out) leaves no bend direction: bend the elbow back instead.
        if (Mathf.Abs(pole.Dot(direction)) > 0.98f) {
            pole = ownerBasis.Z;
        }
        pole = (pole - (direction * pole.Dot(direction))).Normalized();
        var elbow = bendCenter + (pole * height);
        if (Holding) {
            // The held tool sets which way the wrist points: the elbow goes toward the forearm's line from there, so the
            // wrist doesn't bend over backwards when the hand comes close. Only as far as it stays under the shoulder
            // and out to its side.
            // The tool as it's held before the wrist aims it: aiming bends the wrist, the elbow stays.
            var held = GloveFrame(elbow, ownerBasis, false);
            var line = held.Origin - (held.Basis.X.Normalized() * Forearm) - shoulder;
            var toward = line - (direction * line.Dot(direction));
            if (toward.Length() > 0.001f) {
                foreach (var pull in (float[])[ForearmPull, ForearmPull * 0.75f, ForearmPull * 0.5f, ForearmPull * 0.25f]) {
                    var bent = bendCenter + (((toward.Normalized() * pull) + (pole * (1f - pull))).Normalized() * height);
                    if (bent.Y < shoulder.Y - ElbowBelowShoulder
                        && side * ownerBasis.X.Normalized().Dot(bent - shoulder) >= ElbowOut) {
                        elbow = bent;
                        break;
                    }
                }
            }
        }
        if (ElbowSupport is { } support && ElbowSupportWeight > 0f) {
            elbow = SupportedElbow(shoulder, elbow, support, direction, along, height);
        }
        if (_elbowHold > 0f) {
            elbow = elbow.Lerp(Body.ToGlobal(_heldElbow), _elbowHold);
        }
        elbow = AboveGround(elbow, shoulder + (direction * along), direction, delta);
        _elbow = elbow;
        PlaceSegment(_upper, shoulder, elbow);
        var wrist = PlaceGlove(elbow, ownerBasis);
        var cuffEnd = wrist + (AimCuff(elbow, wrist) * CuffDepth);
        PlaceSegment(_fore, elbow, cuffEnd);
    }

    /// <summary>The elbow turned toward the knee it rests on (deep squat). Low working hands can leave the preferred
    /// bend through a knee: then it searches the same IK circle for clearance. The hand and both segment lengths stay
    /// fixed, so reaching for something never moves the tool's contact.</summary>
    private Vector3 SupportedElbow(
        Vector3 shoulder, Vector3 elbow, Vector3 support, Vector3 direction, float along, float height) {
        var bendCenter = shoulder + (direction * along);
        var supportPole = (support - bendCenter).Slide(direction);
        if (supportPole.LengthSquared() <= 0.000001f) {
            return elbow;
        }
        var currentPole = (elbow - bendCenter).Normalized();
        var supported = currentPole.Slerp(supportPole.Normalized(), ElbowSupportWeight).Normalized();
        elbow = bendCenter + (supported * height);
        if (KneeClearance(shoulder, elbow) >= KneeSleeveClearance) {
            return elbow;
        }
        var best = elbow;
        var bestCost = float.PositiveInfinity;
        for (var step = 0; step < 32; step++) {
            var candidate = bendCenter + (supported.Rotated(direction, step * Mathf.Tau / 32f) * height);
            var gap = KneeClearance(shoulder, candidate);
            var cost = candidate.DistanceSquaredTo(elbow) + (Mathf.Max(0f, KneeSleeveClearance - gap) * 100f);
            if (cost < bestCost) {
                bestCost = cost;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>The elbow turned round the same IK circle (about <paramref name="bendCenter"/>) as little as keeps it
    /// <see cref="ElbowClearance"/> above what's under it, easing there and back so the arm doesn't pop. Searched only
    /// while it's in something. A squat's knee support places the elbow itself.</summary>
    private Vector3 AboveGround(Vector3 elbow, Vector3 bendCenter, Vector3 direction, float delta) {
        var spoke = elbow - bendCenter;
        var needed = 0f;
        // Turned already, it comes back only once the elbow clears with room to spare, so it doesn't hover at the line.
        var margin = _groundTurn != 0f ? ElbowClearance + 0.02f : ElbowClearance;
        if (GroundAt is { } groundAt && ElbowSupportWeight <= 0f && elbow.Y < groundAt(elbow) + margin) {
            // Nowhere clear, it stays turned as it is. Found, the search stops even at the turn it already has: going on
            // to the next one, it would swing between the two every frame.
            needed = _groundTurn;
            var found = false;
            for (var step = 1; step <= GroundSteps / 2 && !found; step++) {
                foreach (var sign in (float[])[1f, -1f]) {
                    var angle = sign * step * Mathf.Tau / GroundSteps;
                    var candidate = bendCenter + spoke.Rotated(direction, angle);
                    if (candidate.Y >= groundAt(candidate) + ElbowClearance) {
                        needed = angle;
                        found = true;
                        break;
                    }
                }
            }
        }
        _groundTurn = delta > 0f ? Mathf.MoveToward(_groundTurn, needed, delta * ElbowTurnSpeed) : needed;
        return _groundTurn == 0f ? elbow : bendCenter + spoke.Rotated(direction, _groundTurn);
    }

    private float KneeClearance(Vector3 shoulder, Vector3 elbow) {
        var clearance = float.PositiveInfinity;
        foreach (var knee in KneeObstacles) {
            clearance = Mathf.Min(clearance, knee.DistanceTo(Geometry3D.GetClosestPointToSegment(knee, shoulder, elbow)));
            clearance = Mathf.Min(clearance,
                knee.DistanceTo(Geometry3D.GetClosestPointToSegment(knee, elbow, GlobalPosition)));
        }
        return clearance;
    }

    /// <summary>Aims the glove's cuff bone down the forearm and returns that direction (world space). However the wrist
    /// bends, the cuff stays on the sleeve and the glove stretches from the hand over it.</summary>
    private Vector3 AimCuff(Vector3 elbow, Vector3 wrist) {
        var back = (elbow - wrist).Normalized();
        if (_gloveRig is null || !_gloveRig.Has("Cuff")) {
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
    private Vector3 PlaceGlove(Vector3 elbow, Basis ownerBasis) {
        var frame = GloveFrame(elbow, ownerBasis);
        frame.Origin += Shiver;
        _glove.GlobalTransform = frame;
        return frame.Origin;
    }

    /// <summary>
    /// Where the glove goes, its origin at the wrist. The left glove is the right one mirrored.
    /// Holding a tool, the glove sits on it by its grip, fitted as if the tool weren't tilted or turned (the grip's rest
    /// tilt, no turn), then turned along with it (unless not <paramref name="turned"/>): the wrist aims the tool, the
    /// tool doesn't move in the hand. Empty, it points along the forearm, palm down once it has untwisted.
    /// </summary>
    private Transform3D GloveFrame(Vector3 elbow, Basis ownerBasis, bool turned = true) {
        if (PressNormal is { } normal) {
            return PressFrame(elbow, normal);
        }
        if (!Holding) {
            var forward = (GlobalPosition - elbow).Normalized();
            var up = (Vector3.Up - (forward * Vector3.Up.Dot(forward))).Normalized();
            if (up.LengthSquared() < 0.5f) {
                up = ownerBasis.Z;
            }
            var side = forward.Cross(up);
            // Still rolled by the twist of the tool it let go of, until it rolls back (see UpdatePose()).
            var empty = new Basis(forward, Twist) * new Basis(forward, up, Index == 1 ? side : -side);
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
        // Pressing flat, it stays flat on what it presses, whichever way the forearm comes in.
        if (Grip != "press") {
            frame = TurnToForearm(frame, toolBasis * Vector3.Forward, contact, elbow, !GripStyle.AcrossPalm(Grip));
        }
        var wrist = contact - (frame * style.At) + (frame.Y.Normalized() * Fit.Lift) + (frame.Z.Normalized() * Fit.Shift);
        return new Transform3D(aim * frame, aimed.Origin + (aim * (wrist - aimed.Origin)));
    }

    /// <summary>Pressed flat on the patient: the palm (the press grip's glove point) on the hand's position, laid on the
    /// skin across <paramref name="normal"/>, the fingers pointing on the way the forearm comes in.</summary>
    private Transform3D PressFrame(Vector3 elbow, Vector3 normal) {
        var fingers = (PressAcross == Vector3.Zero ? GlobalPosition - elbow : PressAcross).Slide(normal);
        if (fingers.LengthSquared() < 0.000001f) {
            fingers = Body.GlobalBasis.Z.Slide(normal);
        }
        var frame = PressBasis(normal, fingers);
        // Grasping a limb, the hand's position is where its lower palm lies (GraspFit()).
        return new Transform3D(frame, GlobalPosition - (frame * (PressAcross == Vector3.Zero ? PressStyle.At : LowerPalm)));
    }

    private static GripStyle PressStyle => GripStyle.All["press"];

    /// <summary>The glove's axes pressed flat across <paramref name="normal"/>, the fingers along
    /// <paramref name="fingers"/>.</summary>
    private Basis PressBasis(Vector3 normal, Vector3 fingers) {
        // The press grip's tool frame: its -Z (where the fingers point) along the fingers, +Y out of the skin.
        var tool = Basis.LookingAt(fingers.Normalized(), normal);
        var mirror = Index == 0 ? new Vector3(-1f, 1f, 1f) : Vector3.One;
        return tool * Basis.FromScale(mirror) * PressStyle.Frame;
    }

    /// <summary>
    /// Where a hand grasping the limb round <paramref name="ring"/> at <paramref name="at"/> lays its palm, seen from
    /// <paramref name="from"/>: the spot the lower palm lies on, the palm's normal, which way the fingers point (from the
    /// near side over the top) and how the palm gives. A palm bends only at the wrist and the knuckles: it is turned
    /// round the limb, touches it at the spot along its length, leans along it and gives as little as it takes to lay
    /// its underside closest to the skin all over, with no part of it in it. On a limb about as wide as the palm is long
    /// it lies across the round like a chord, its ends lifting. Held firmly, it then presses down and toward the near
    /// side as far as the palm gives. The fingers close round the rest from the knuckles (<see cref="GraspWith"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal (Vector3 At, Vector3 Normal, Vector3 Fingers, Vector3 Cup) GraspFit(LimbRing ring, Vector3 at, Vector3 from) {
        var axis = ring.Axis;
        var center = ring.Center + (axis * axis.Dot(at - ring.Center));
        var top = Vector3.Up.Slide(axis).Normalized();
        var side = axis.Cross(top);
        // The limb's skin round its middle line, where the palm's sides and middle lie along it.
        var slices = Enumerable.Range(-GraspSlicesEachSide, (GraspSlicesEachSide * 2) + 1)
            .Select(step => SkinRound(center + (axis * GraspSliceStep * step), top, axis)).ToArray();
        // Where a spot is round the limb: how far along it, counted in slices, and how far it is from the middle line
        // toward the top and toward the side.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        (float Slice, Vector2 Off) Round(Vector3 spot) {
            var slice = Mathf.Clamp((axis.Dot(spot - center) / GraspSliceStep) + GraspSlicesEachSide, 0f, slices.Length - 1f);
            return (slice, new Vector2(top.Dot(spot - center), side.Dot(spot - center)));
        }
        // How far out from the middle line the skin is in the direction of a spot there, read between the slices
        // either side.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        float SkinAt(float slice, Vector2 off) {
            var count = slices[0].Length;
            var round = Mathf.PosMod(Mathf.Atan2(off.Y, off.X), Mathf.Tau) / Mathf.Tau * count;
            var (below, next, past) = ((int)round % count, ((int)round + 1) % count, round - Mathf.Floor(round));
            var before = Mathf.Min((int)slice, slices.Length - 2);
            var (near, far) = (slices[before], slices[before + 1]);
            return Mathf.Lerp(Mathf.Lerp(near[below], near[next], past), Mathf.Lerp(far[below], far[next], past),
                slice - before);
        }
        // How far a spot there is out from the skin (negative: in it).
        float GapAt(float slice, Vector2 off) => off.Length() - SkinAt(slice, off);
        float Gap(Vector3 spot) {
            var (slice, off) = Round(spot);
            return GapAt(slice, off);
        }
        // Which way round the axis turns the top toward the near side.
        var toNear = side.Dot(from - center) >= 0f ? 1f : -1f;
        var underside = PalmUnderside();
        // The thumb lies along the palm's near edge as the press grip leaves it, at rest: it mustn't go in either.
        var thumbRest = ThumbAtRest();
        var thumbJoints = new Vector3[4];
        thumbJoints[0] = thumbRest.Root;
        for (var joint = 0; joint < 3; joint++) {
            thumbJoints[joint + 1] = thumbJoints[joint] + thumbRest.Bones[joint];
        }
        Vector3[] thumbRestPoints = [.. thumbJoints, .. thumbJoints.Skip(1).Select((end, joint) => end.Lerp(thumbJoints[joint], 0.5f))];
        // The palm turned round the limb, touching it at a spot along its length, leaning along it and giving a little:
        // where its lower palm rests and how far the part of it furthest off the skin is.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        (float Spread, Vector3 At, Vector3 Normal, Vector3 Fingers, Vector3 Cup) Fit(int step, int touch, int lean,
            int cupStep, int slant) {
            var turned = new Basis(axis, toNear * MaxGraspTurn * step / GraspSteps) * top;
            // Round the limb away from the near side, the way the palm turns back to its top.
            var fingers = -toNear * axis.Cross(turned);
            // Leaning along the limb, the palm follows it narrowing toward the wrist.
            var normal = new Basis(fingers, MaxGraspLean * lean / LeanSteps) * turned;
            var frame = PressBasis(normal, fingers);
            var cup = new Basis(Vector3.Up, MaxCupSlant * slant / SlantSteps) * PalmCupAxis * (MaxPalmCup * cupStep / CupSteps);
            // That spot of the palm over the limb's middle line: a stiff palm lies on a limb's round like a chord, its
            // ends lifting off where it touches nearest its middle.
            var touching = new Vector3(PalmLength * touch / TouchSteps, LowerPalm.Y, 0f);
            // The palm only moves out across the limb from here on: each spot stays as far along it.
            var knuckles = KnuckleHalf(cup);
            var spots = new (float Slice, Vector2 Off)[underside.Length];
            for (var k = 0; k < spots.Length; k++) {
                spots[k] = Round(center + (frame * (Cupped(underside[k], knuckles) - touching)));
            }
            var thumb = new (float Slice, Vector2 Off)[thumbRestPoints.Length];
            for (var k = 0; k < thumb.Length; k++) {
                thumb[k] = Round(center + (frame * (thumbRestPoints[k] - touching)));
            }
            var outward = new Vector2(top.Dot(turned), side.Dot(turned));
            // How far out the palm goes for a spot to stand off the skin by clear: where its line out meets the skin,
            // the skin read again where it comes to, as it's nearer or further round the limb. Negative infinity
            // where the line passes it by.
            [MethodImpl(MethodImplOptions.AggressiveOptimization)]
            float Clearing(float slice, Vector2 off, float clear) {
                var along = off.Dot(outward);
                var at = off;
                var mid = float.NegativeInfinity;
                for (var i = 0; i < ClearingReads; i++) {
                    var radius = SkinAt(slice, at) + clear;
                    var square = (along * along) - off.LengthSquared() + (radius * radius);
                    if (square < 0f) {
                        return float.NegativeInfinity;
                    }
                    mid = Mathf.Sqrt(square) - along;
                    at = off + (outward * mid);
                }
                return mid;
            }
            // As close to the middle line as leaves the palm out of the skin.
            var high = 0f;
            foreach (var (slice, off) in spots) {
                high = Mathf.Max(high, Clearing(slice, off, 0f));
            }
            foreach (var (slice, off) in thumb) {
                high = Mathf.Max(high, Clearing(slice, off, FingerRadius));
            }
            high = Mathf.Min(high, GraspClear);
            var resting = center + (turned * high);
            var spread = float.NegativeInfinity;
            foreach (var (slice, off) in spots) {
                spread = Mathf.Max(spread, GapAt(slice, off + (outward * high)));
            }
            return (spread, resting + (frame * (LowerPalm - touching)), normal, fingers, cup);
        }
        // Where the palm touches and its turn first, then how it gives and the slant of that, the lean, and the turn,
        // the touch and the give again near where they were: each matters far more than the next, and all of them
        // together would take a great many tries. On a round limb every turn fits alike: the first tried, its top, is
        // kept.
        var (bestStep, bestTouch, bestLean, bestCup, bestSlant) = (0, TouchSteps / 2, 0, 0, 0);
        var best = Fit(bestStep, bestTouch, 0, 0, 0);
        // Each round's fits are worked out at once, then taken in order as if tried one by one.
        void Try(List<(int Step, int Touch, int Lean, int Cup, int Slant)> tries) {
            var fits = new (float Spread, Vector3 At, Vector3 Normal, Vector3 Fingers, Vector3 Cup)[tries.Count];
            Parallel.For(0, tries.Count, i => fits[i] = Fit(tries[i].Step, tries[i].Touch, tries[i].Lean, tries[i].Cup,
                tries[i].Slant));
            for (var i = 0; i < fits.Length; i++) {
                // A change that lays the palm barely closer isn't worth it.
                if (fits[i].Spread < best.Spread - 0.0002f) {
                    best = fits[i];
                    (bestStep, bestTouch, bestLean, bestCup, bestSlant) = tries[i];
                }
            }
        }
        var tries = new List<(int Step, int Touch, int Lean, int Cup, int Slant)>();
        for (var step = -GraspStepsPastTop; step <= GraspSteps; step++) {
            // Every other touch: the last round tries those either side of the one found.
            for (var touch = 0; touch <= TouchSteps; touch += 2) {
                tries.Add((step, touch, 0, 0, 0));
            }
        }
        Try(tries);
        var (turnedTo, touchedAt) = (bestStep, bestTouch);
        tries.Clear();
        for (var slant = -SlantSteps; slant <= SlantSteps; slant++) {
            for (var cupStep = 0; cupStep <= CupSteps; cupStep++) {
                tries.Add((turnedTo, touchedAt, 0, cupStep, slant));
            }
        }
        Try(tries);
        var (cuppedTo, slantedTo) = (bestCup, bestSlant);
        tries.Clear();
        for (var lean = -LeanSteps; lean <= LeanSteps; lean++) {
            tries.Add((turnedTo, touchedAt, lean, cuppedTo, slantedTo));
        }
        Try(tries);
        var leanedTo = bestLean;
        tries.Clear();
        for (var step = Mathf.Max(turnedTo - 2, -GraspStepsPastTop); step <= Mathf.Min(turnedTo + 2, GraspSteps); step++) {
            for (var touch = Mathf.Max(touchedAt - 1, 0); touch <= Mathf.Min(touchedAt + 1, TouchSteps); touch++) {
                for (var cupStep = 0; cupStep <= CupSteps; cupStep++) {
                    tries.Add((step, touch, leanedTo, cupStep, slantedTo));
                }
            }
        }
        Try(tries);
        // Held firmly, the hand presses into the limb and toward the near side, round under the knuckles: the palm gives
        // as much as a hand pressed flat on the skin (Surgeon.PressSink).
        var pressFrame = PressBasis(best.Normal, best.Fingers);
        Vector3[] pressed = [.. underside.Select(spot => pressFrame * (Cupped(spot, KnuckleHalf(best.Cup)) - LowerPalm))];
        Vector3[] thumbPressed = [.. thumbRestPoints.Select(point => pressFrame * (point - LowerPalm))];
        // Into the palm and round toward the near side, whichever way round the limb it lies.
        var press = new Basis(axis, -toNear * GraspPressSlant) * (-best.Normal).Slide(axis).Normalized();
        var (shallow, deep) = (0f, GraspClear);
        for (var i = 0; i < 10; i++) {
            var mid = (shallow + deep) * 0.5f;
            var place = best.At + (press * mid);
            if (pressed.Any(spot => Gap(place + spot) < -Surgeon.PressSink)
                || thumbPressed.Any(point => Gap(place + point) < FingerRadius - Surgeon.PressSink)) {
                deep = mid;
            } else {
                shallow = mid;
            }
        }
        return (best.At + (press * shallow), best.Normal, best.Fingers, best.Cup);
    }

    /// <summary>How the palm's knuckle half (and the fingers on it) is moved cupped by <paramref name="cup"/>, glove
    /// space.</summary>
    private static Transform3D KnuckleHalf(Vector3 cup) {
        var turn = CupTurn(cup);
        return new Transform3D(turn, PalmHinge - (turn * PalmHinge));
    }

    /// <summary>The palm's underside at rest, glove space: the lowest point of the glove's skin in each of
    /// <see cref="PalmCells"/> (none where the glove has no skin under it). Read off the model once.</summary>
    private Vector3[] PalmUnderside() {
        if (_palmUnderside is not null) {
            return _palmUnderside;
        }
        var skin = _glove.FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>()
            .First(mesh => mesh.Skin is not null);
        // Built before the glove is in the scene: its transform to the glove, parent by parent.
        var toGlove = Transform3D.Identity;
        for (Node node = skin; node != _glove; node = node.GetParent()) {
            toGlove = ((Node3D)node).Transform * toGlove;
        }
        using var arrays = skin.Mesh.SurfaceGetArrays(0);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Select(vertex => toGlove * vertex).ToArray();
        return _palmUnderside =
        [
            .. PalmCells.Select(cell => vertices
                    .Where(v => Mathf.Abs(v.X - cell.X) <= PalmCellSize.X * 0.5f && Mathf.Abs(v.Z - cell.Y) <= PalmCellSize.Y * 0.5f)
                    .Select(v => (Vector3?)new Vector3(cell.X, v.Y, cell.Y)).MinBy(v => v!.Value.Y))
                .OfType<Vector3>(),
        ];
    }

    /// <summary>The palm's underside as the hand is posed now (world space): where it should lie on what it holds.
    /// </summary>
    internal IEnumerable<Vector3> PalmUndersidePosed() {
        var knuckles = KnuckleHalf(PressNormal is null ? Vector3.Zero : PressCup);
        return PalmUnderside().Select(spot => _glove.GlobalTransform * Cupped(spot, knuckles));
    }

    /// <summary>The turn of the palm's knuckle half cupped by <paramref name="cup"/> (see <see cref="PressCup"/>).
    /// </summary>
    private static Basis CupTurn(Vector3 cup) => cup.IsZeroApprox() ? Basis.Identity : new Basis(cup.Normalized(), cup.Length());

    /// <summary>Where a point of the palm (glove space) goes with its knuckle half moved by <paramref name="knuckles"/>
    /// (<see cref="KnuckleHalf"/>): as its skin does, blended across the hinge.</summary>
    private static Vector3 Cupped(Vector3 point, Transform3D knuckles) {
        var share = Mathf.SmoothStep(PalmHinge.X - PalmBlend, PalmHinge.X + PalmBlend, point.X);
        return point.Lerp(knuckles * point, share);
    }

    /// <summary>How far out from <paramref name="middle"/> the skin is all round it (meters), from
    /// <paramref name="top"/> on round <paramref name="axis"/>. Its outside: a surface under the skin (the gown over a
    /// hip) is looked past, though not as far as another limb.</summary>
    private float[] SkinRound(Vector3 middle, Vector3 top, Vector3 axis) =>
    [
        .. Enumerable.Range(0, GraspAngles).Select(k => {
            var outward = new Basis(axis, Mathf.Tau * k / GraspAngles) * top;
            if (Rays.Cast(this, middle, middle + (outward * GraspClear), PatientBody.SurfaceLayer, inside: true) is not { } inner) {
                return GraspClear;
            }
            var near = inner.Position.DistanceTo(middle);
            return Rays.Cast(this, middle + (outward * (near + UnderSkin)), middle, PatientBody.SurfaceLayer) is { } outer
                ? outer.Position.DistanceTo(middle) : near;
        }),
    ];

    /// <summary>
    /// Turns a held glove about the tool so the wrist faces the elbow, without letting go: first it rolls around the
    /// tool's own axis (the hand wraps the same way, just from another side), then, for grips that allow it, it tips the
    /// tool up to <see cref="MaxToolTip"/> against the fingers, like changing a pen angle.
    /// </summary>
    private static Basis TurnToForearm(Basis frame, Vector3 axis, Vector3 contact, Vector3 elbow, bool canTip) {
        var toElbow = (elbow - contact).Normalized();
        var wristDirection = -frame.X.Normalized();
        // Only as far as a forearm turns: turned further, the hand ends up palm up and twisted outward.
        var roll = Mathf.Clamp(
            SignedAngle(wristDirection - (axis * wristDirection.Dot(axis)), toElbow - (axis * toElbow.Dot(axis)), axis),
            -MaxRoll, MaxRoll);
        frame = new Basis(axis, roll) * frame;
        if (!canTip) {
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
    private static void PlaceSegment(Node3D mesh, Vector3 a, Vector3 b) {
        var y = b - a;
        var length = y.Length();
        if (length < 0.001f) {
            return;
        }
        y /= length;
        var x = y.Cross(Mathf.Abs(y.Dot(Vector3.Forward)) < 0.9f ? Vector3.Forward : Vector3.Right).Normalized();
        var z = x.Cross(y);
        mesh.GlobalTransform = new Transform3D(new Basis(x, y * length, z), (a + b) * 0.5f);
    }

    /// <summary>Copies of the glove trail behind it, each showing where the glove was a step earlier, fainter the older
    /// it is.</summary>
    private void UpdateTrail(float delta) {
        if (Trail <= 0f) {
            _trailRoot?.QueueFree();
            _trailRoot = null;
            _trailCopies.Clear();
            _trailFrames.Clear();
            return;
        }
        if (_trailRoot is null) {
            BuildTrail();
        }
        _trailAccumulated += delta;
        if (_trailAccumulated >= TrailStep || _trailFrames.Count == 0) {
            _trailAccumulated = 0f;
            var skeleton = _gloveRig?.Skeleton;
            var poses = new Transform3D[skeleton?.GetBoneCount() ?? 0];
            for (var i = 0; i < poses.Length; i++) {
                poses[i] = skeleton!.GetBonePose(i);
            }
            _trailFrames.Insert(0, new TrailFrame(_glove.GlobalTransform, poses));
            if (_trailFrames.Count > TrailCopies + 1) {
                _trailFrames.RemoveAt(_trailFrames.Count - 1);
            }
        }
        // The newest frame is about where the glove is now: the copies show the ones before it. A copy right on the
        // glove (a hand held still) would only speckle it.
        for (var i = 0; i < _trailCopies.Count; i++) {
            var copy = _trailCopies[i];
            var shown = i + 1 < _trailFrames.Count
                && _trailFrames[i + 1].Glove.Origin.DistanceTo(_glove.GlobalPosition) > TrailGap;
            copy.Root.Visible = shown;
            if (!shown) {
                continue;
            }
            var frame = _trailFrames[i + 1];
            copy.Root.GlobalTransform = frame.Glove;
            if (copy.Skeleton is { } copySkeleton) {
                for (var bone = 0; bone < frame.Poses.Length; bone++) {
                    copySkeleton.SetBonePose(bone, frame.Poses[bone]);
                }
            }
            copy.Material.AlbedoColor = copy.Material.AlbedoColor with {
                A = Trail * TrailAlpha * (1f - ((float)i / TrailCopies)),
            };
        }
    }

    private void BuildTrail() {
        _trailRoot = new Node3D { Name = "Trail" };
        AddChild(_trailRoot);
        for (var i = 0; i < TrailCopies; i++) {
            var copy = (Node3D)_glove.Duplicate();
            copy.TopLevel = true;
            _trailRoot.AddChild(copy);
            var material = new StandardMaterial3D {
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = TrailColor,
            };
            foreach (var node in copy.FindChildren("*", nameof(GeometryInstance3D), true, false)) {
                ((GeometryInstance3D)node).MaterialOverride = material;
            }
            var skeleton = copy.FindChildren("*", nameof(Skeleton3D), true, false).FirstOrDefault() as Skeleton3D;
            _trailCopies.Add(new TrailCopy(copy, skeleton, material));
        }
    }
}
