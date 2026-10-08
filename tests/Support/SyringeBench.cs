namespace Scalpel.Tests.Support;

/// <summary>
/// One syringe case: what the needle is in, the syringe, ml of the drug in it at the start, and the wheel notches to
/// work (&gt; 0 pulls the plunger out). A vial starts with what the syringe didn't take from it, the dish with
/// <paramref name="Dish"/> ml, the IV drip on its stand full, with a working line in the arm. The syringe holds the drug
/// of <paramref name="Vial"/>. Surgeon targets: the surgeon's own other hand, the partner's hand, the partner's body,
/// and the hand of the partner knocked out on the floor (the surgeon crouches beside them).
/// </summary>
public sealed record SyringeCase(
    string Name, string Target, string Syringe, float Ml, int Notches, float Dish = 0f, string Vial = SyringeBench.Vial)
{
    public override string ToString() => Name;
}

/// <summary>
/// Stages the syringe cases for checks and deliberate visual captures: the local surgeon holds a syringe with its needle
/// in the case's target and works the plunger with wheel notches. Over a vial, the dish or the IV drip the needle just
/// rests there; on the patient or a surgeon Use tool presses it in and stays held. The wheel works the plunger either
/// way. <see cref="StageCatheter"/> puts an IV catheter on the forearm vein, or beside it. A partner (a puppet surgeon,
/// peer 2) stands out of the way until a case needs them.
/// </summary>
public partial class SyringeBench : Node
{
    public const string Vial = "vial_cefazolin";
    public const string Drug = "cefazolin";

    public static readonly IReadOnlyList<SyringeCase> Cases =
    [
        new("vial_pull", "vial", "syringe_10", 0f, 6),
        new("vial_push", "vial", "syringe_10", 6f, -6),
        new("dish_push", "dish", "syringe_50", 40f, -40),
        new("dish_pull", "dish", "syringe_50", 0f, 40, Dish: 60f),
        new("vein_push", "vein", "syringe_10", 6f, -6),
        new("vein_pull", "vein", "syringe_10", 3f, 6),
        new("skin_push", "skin", "syringe_10", 6f, -6),
        new("fat_push", "fat", "syringe_10", 6f, -6),
        new("muscle_push", "muscle", "syringe_10", 6f, -6),
        new("skin_pull", "skin", "syringe_10", 3f, 6),
        new("fat_pull", "fat", "syringe_10", 3f, 6),
        new("muscle_pull", "muscle", "syringe_10", 3f, 6),
        new("air_pull", "air", "syringe_10", 3f, 6),
        new("drip_push", "drip", "syringe_10", 6f, -6),
        new("drip_pull", "drip", "syringe_10", 0f, 6),
        new("doctor_hand_push", "doctor_hand", "syringe_10", 3f, -3, Vial: "vial_diazepam"),
        new("doctor_body_push", "doctor_body", "syringe_10", 3f, -3, Vial: "vial_diazepam"),
        new("doctor_down_push", "doctor_down", "syringe_3", 2f, -2, Vial: "vial_flumazenil"),
        new("own_hand_pull", "own_hand", "syringe_10", 3f, 3, Vial: "vial_diazepam"),
        new("own_hand_push", "own_hand", "syringe_10", 3f, -3, Vial: "vial_diazepam"),
    ];

    public static readonly IReadOnlySet<string> SurgeonTargets =
        new HashSet<string> { "own_hand", "doctor_hand", "doctor_body", "doctor_down" };
    /// <summary>Where the partner waits while no case needs them: a corner, hands down.</summary>
    internal static readonly Vector3 PartnerPark = new(2f, 0f, -1.7f);
    /// <summary>IV catheter cases: on the vein, and 2.5 cm across the forearm from it (on the arm, off the vein).
    /// </summary>
    public static readonly IReadOnlyList<(string Name, float Miss)> CatheterCases = [("catheter_vein", 0f), ("catheter_miss", 0.025f)];
    /// <summary>Site uv of a cut through the skin (fat shows), one through the fat (muscle shows), and whole skin.
    /// </summary>
    private static readonly Vector2 FatUv = new(0.3f, 0.3f);
    private static readonly Vector2 MuscleUv = new(0.5f, 0.62f);
    private static readonly Vector2 SkinUv = new(0.75f, 0.4f);
    /// <summary>How far from the target (meters, across the floor) the surgeon stands to work on it.</summary>
    private const float StandOff = 0.45f;

    public Surgery Surgery { get; private set; } = null!;
    public SurgicalTool? Syringe { get; private set; }
    /// <summary>The case's vial, kidney dish or the IV drip, null for the others.</summary>
    public SurgicalTool? Container { get; private set; }
    public SurgicalTool? Catheter { get; private set; }
    public Surgeon? Partner { get; private set; }

    private Surgeon Me => Surgery.LocalSurgeon!;

    /// <summary>A bench added to the scene tree's root, ready to <see cref="Start"/>.</summary>
    public static SyringeBench Create()
    {
        var bench = new SyringeBench();
        Frames.Root.AddChild(bench);
        return bench;
    }

    /// <summary>
    /// A solo appendectomy with nothing rolled, no random events, the patient asleep, both cuts made and held open,
    /// ready for <see cref="Stage"/>. <paramref name="openLayers"/> false leaves the skin intact,
    /// <paramref name="withPartner"/> false stages a solo surgery. Awake (or woken by an event), a patient in pain
    /// thrashes and can knock the syringe out of the hand.
    /// </summary>
    public async Task Start(bool openLayers = true, bool withPartner = true)
    {
        var ordinary = new List<QuirkRoll> { new("normal_dude", "") };
        Net.Instance.StartLocalSession(Db.Scenario("appendectomy")!, 42, new LobbyPlayer("Tester", ordinary, Ready: true), []);
        Surgery = GD.Load<PackedScene>("res://scenes/surgery.tscn").Instantiate<Surgery>();
        AddChild(Surgery);
        await Frames.Physics(10);
        if (withPartner)
        {
            Net.Instance.Roster[2] = new LobbyPlayer("Partner", ordinary, Ready: true);
            Partner = Surgery.SpawnSurgeon(2, 1);
            PlacePartner(PartnerPark, 0f);
        }
        var patient = Surgery.Patient;
        Surgery.Director.StopRandomEvents();
        SurgeryState.PatientIsAsleep(patient);
        var tissue = patient.Body.Tissue;
        if (openLayers)
        {
            foreach (var (middle, depth) in ((Vector2, float)[])[(FatUv, 0.3f), (MuscleUv, 0.6f)])
            {
                patient.Cut(10 + tissue.Grips().Count, middle - new Vector2(0.1f, 0f), middle + new Vector2(0.1f, 0f), depth,
                    1f, false, 0.1f);
                // Held open with two pins, like forceps on either edge.
                foreach (var pull in (float[])[-0.025f, 0.025f])
                {
                    var key = 900 + tissue.Grips().Count;
                    var edge = middle + new Vector2(0f, Mathf.Sign(pull) * 0.03f);
                    tissue.Grip(key, edge);
                    tissue.MoveGrip(key, tissue.Pos[tissue.Nearest(edge)] + new Vector3(0f, 0.004f, pull));
                }
            }
        }
        await Frames.Physics(60);
    }

    /// <summary>Ends the surgery and frees the bench with it.</summary>
    public async Task Stop()
    {
        QueueFree();
        await Frames.Process(2);
        Net.Instance.Leave();
    }

    /// <summary>Puts a fresh syringe holding the case's start in the active hand; <paramref name="insert"/> false leaves
    /// it ready over the target.</summary>
    public async Task Stage(SyringeCase @case, bool insert = true)
    {
        var tools = Surgery.Tools;
        ConsumeStaged();
        PlacePartner(PartnerPark, 0f);
        PlayerInput.Action(InputActions.Crouch, false);
        var syringe = Spawn(@case.Syringe, Me.GlobalPosition + Vector3.Up);
        Syringe = syringe;
        // Straight into the hand: left to fall, it can reach the floor and shatter first.
        tools.RequestGrab(syringe, Me.Active);
        switch (@case.Target)
        {
            case "vial":
                // Delivered vials stand, cap up.
                tools.SpawnStanding(Vial, ClearSpot());
                Container = tools.Tools.Values.Last();
                tools.Transfer(Container, syringe, @case.Ml);
                break;
            case "dish":
                Container = Spawn("kidney_dish", ClearSpot());
                Fill(Container, @case.Dish);
                Fill(syringe, @case.Ml);
                break;
            case "drip":
                Container = tools.DripBag();
                Fill(syringe, @case.Ml);
                break;
            default:
                Fill(syringe, @case.Ml, @case.Vial);
                break;
        }
        await Frames.Physics(30);
        if (SurgeonTargets.Contains(@case.Target))
        {
            await FacePartner(@case.Target);
        }
        var aim = AimPoint(@case.Target);
        if (!SurgeonTargets.Contains(@case.Target))
        {
            StandBy(aim);
        }
        if (@case.Target == "drip" && !Surgery.Patient.IvWorking)
        {
            // Only once the surgeon is in place: stepping over to the stand would count as walking through the tubing.
            await Frames.Physics(5);
            Surgery.Patient.IvRemoved();
            Surgery.Patient.SetIv(VeinPoint(), true);
        }
        await Frames.Physics(2);
        var hand = Me.Hands[Me.Active];
        // The hand rests the needle on whatever is under the aim; a few rounds let the arm settle on it. Only then Use
        // tool presses it in: a needle in the patient sticks, and moved on from there it would tear out.
        for (var i = 0; i < 40; i++)
        {
            hand.LocalTarget = Me.ToLocal(aim - Me.OwnTipOffset(Me.Active) + (Vector3.Up * 0.04f));
            await Frames.NextPhysics();
        }
        PlayerInput.Action(InputActions.UseTool, insert && @case.Target is not ("vial" or "dish" or "air"));
        await PlayerInput.Delivered();
        await Frames.Physics(10);
    }

    /// <summary>Takes away the last case's syringe and container, unless the container is part of the room.</summary>
    private void ConsumeStaged()
    {
        foreach (var old in (SurgicalTool?[])[Syringe, Container])
        {
            if (old is not null && IsInstanceValid(old) && !old.Def.Fixed)
            {
                Surgery.Tools.Consume(old);
            }
        }
        Syringe = null;
        Container = null;
    }

    /// <summary>Puts the partner standing at <paramref name="at"/> facing <paramref name="yaw"/>, hands hanging at
    /// their sides. A puppet goes where it's told.</summary>
    public void PlacePartner(Vector3 at, float yaw)
    {
        if (Partner is null)
        {
            return;
        }
        Partner.FallSide = 0f;
        Partner.Down = 0f;
        Partner.PlaceAt(at, yaw);
        for (var i = 0; i < 2; i++)
        {
            Partner.Hands[i].Target = Partner.ToGlobal(new Vector3(i == 1 ? 0.3f : -0.3f, 0.95f, -0.05f));
        }
    }

    /// <summary>Surgeon targets: away from the table, the surgeon with their back to it. Their own other hand held out
    /// in front, or the partner facing them, a hand held out between them; or the partner knocked out on the floor
    /// beside the table, the surgeon crouched by their hand.</summary>
    private async Task FacePartner(string target)
    {
        var partner = Partner!;
        var patient = Surgery.Patient.GlobalPosition with { Y = 0f };
        var floor = Me.GlobalPosition.Y;
        if (target == "doctor_down")
        {
            PlacePartner(patient + new Vector3(0f, floor, 1.2f), 0f);
            partner.FallSide = 1f;
            await Frames.Physics(60);
            for (var i = 0; i < 2; i++)
            {
                partner.Hands[i].Target = partner.ToGlobal(
                    (Surgeon.LyingHand + new Vector3(-0.25f * i, 0f, 0f)) * new Vector3(partner.FallSide, 1f, 1f));
            }
            var glove = partner.Hands[0].Target;
            Me.GlobalPosition = new Vector3(glove.X, floor, glove.Z - 0.3f);
            Me.Rotation = Me.Rotation with { Y = Mathf.Pi };
            Me.Hands[1 - Me.Active].LocalTarget = new Vector3(-0.3f, 1f, -0.1f);
            PlayerInput.Action(InputActions.Crouch);
            await Frames.Physics(30);
            return;
        }
        Me.GlobalPosition = patient + new Vector3(0f, floor, 1.1f);
        Me.Rotation = Me.Rotation with { Y = Mathf.Pi };
        Me.Hands[1 - Me.Active].LocalTarget = target == "own_hand" ? new Vector3(-0.08f, 1.05f, -0.4f) : new Vector3(-0.3f, 1f, -0.1f);
        if (target != "own_hand")
        {
            PlacePartner(Me.ToGlobal(new Vector3(0f, 0f, -0.75f)), 0f);
        }
        if (target == "doctor_hand")
        {
            partner.Hands[1].Target = partner.ToGlobal(new Vector3(0.05f, 1.05f, -0.35f));
        }
        await Frames.Physics(10);
    }

    /// <summary>One wheel notch, as the mouse sends it: down pulls the plunger out, up pushes it in.</summary>
    public static async Task Notch(bool pull)
    {
        PlayerInput.Tap(pull ? InputActions.LevelDown : InputActions.LevelUp);
        await PlayerInput.Delivered();
        await Frames.Physics(2);
    }

    /// <summary>Takes the needle out: the hand goes up and away over the floor.</summary>
    public async Task Withdraw()
    {
        await Release();
        Me.Hands[Me.Active].LocalTarget = new Vector3(0.15f, 1.1f, -0.2f);
        await Frames.Physics(20);
    }

    /// <summary>Puts an IV catheter in the active hand over the forearm vein, <paramref name="miss"/> meters across the
    /// arm from it, with no line in yet. <see cref="Press"/> then pushes it in.</summary>
    public async Task StageCatheter(float miss)
    {
        ConsumeStaged();
        Surgery.Patient.IvRemoved();
        // Use tool let go, or the new catheter would go in wherever the hand passes over the arm.
        PlayerInput.Action(InputActions.UseTool, false);
        await PlayerInput.Delivered();
        var catheter = Spawn("iv_catheter", Me.GlobalPosition + Vector3.Up);
        Catheter = catheter;
        await Frames.Physics(30);
        var aim = VeinPoint() + (VeinAcross() * miss);
        StandBy(aim);
        Surgery.Tools.RequestGrab(catheter, Me.Active);
        await Frames.Physics(2);
        var hand = Me.Hands[Me.Active];
        for (var i = 0; i < 40; i++)
        {
            hand.LocalTarget = Me.ToLocal(aim - hand.TipOffset(catheter.Def.Length) + (Vector3.Up * 0.04f));
            await Frames.NextPhysics();
        }
    }

    /// <summary>Use tool: the held needle goes in where it rests.</summary>
    public static async Task Press()
    {
        PlayerInput.Action(InputActions.UseTool);
        await PlayerInput.Delivered();
        await Frames.Physics(5);
    }

    /// <summary>Use tool let go: the needle comes out without moving the hand away.</summary>
    public static async Task Release(int waitFrames = 5)
    {
        PlayerInput.Action(InputActions.UseTool, false);
        await PlayerInput.Delivered();
        await Frames.Physics(waitFrames);
    }

    /// <summary>Moves the mouse <paramref name="motion"/> pixels with the active hand's key held, as a player moves
    /// that hand.</summary>
    public async Task Steer(Vector2 motion)
    {
        PlayerInput.Action(PlayerInput.HandKey(Me));
        PlayerInput.Mouse(motion / Settings.MouseSensitivity);
        PlayerInput.Action(PlayerInput.HandKey(Me), false);
        await PlayerInput.Delivered();
    }

    /// <summary>Turns the active hand's tool with Aim tool held and the mouse moved <paramref name="motion"/> pixels,
    /// then lets go of Aim tool.</summary>
    public static async Task Aim(Vector2 motion)
    {
        PlayerInput.Action(InputActions.AimTool);
        PlayerInput.Mouse(motion / Settings.MouseSensitivity);
        PlayerInput.Action(InputActions.AimTool, false);
        await PlayerInput.Delivered();
    }

    /// <summary>The middle of the forearm vein the cases use (world space).</summary>
    public Vector3 VeinPoint()
    {
        var vein = Surgery.Patient.Body.Veins[0];
        return vein.ToGlobal(vein.Line[vein.Line.Length / 2]);
    }

    /// <summary>Across the forearm at the middle of the vein (world, unit length).</summary>
    private Vector3 VeinAcross()
    {
        var vein = Surgery.Patient.Body.Veins[0];
        var middle = vein.Line.Length / 2;
        return (vein.GlobalBasis * (vein.Line[middle + 1] - vein.Line[middle - 1])).Cross(Vector3.Up).Normalized();
    }

    public NeedleTarget NeedleTarget() => Scalpel.Tools.Syringe.NeedleTarget(Syringe!, Surgery.Patient);

    private SurgicalTool Spawn(string id, Vector3 at) => Surgery.Tools.ByUid(Surgery.Tools.Spawn(id, at))!;

    private void Fill(SurgicalTool tool, float ml, string vial = Vial)
    {
        if (ml > 0f)
        {
            var def = Db.Tool(vial)!;
            Surgery.Tools.AddLiquid(tool, ml, new Dictionary<string, float> { [def.Drug] = ml * def.Concentration });
        }
    }

    /// <summary>The clear strip down the middle of the instrument tray.</summary>
    private Vector3 ClearSpot()
    {
        var rest = Surgery.Room.TrayZone("");
        return new Vector3(rest.End.X + 0.07f, rest.Position.Y + 0.05f, rest.GetCenter().Z);
    }

    private Vector3 AimPoint(string target)
    {
        var body = Surgery.Patient.Body;
        return target switch
        {
            // A needle goes into a vial through its cap.
            "vial" => Container!.TipPosition(),
            "dish" or "drip" => Container!.Middle(),
            "vein" => VeinPoint(),
            "fat" => body.UvToWorld(FatUv),
            "muscle" => body.UvToWorld(MuscleUv),
            "skin" => body.UvToWorld(SkinUv),
            "own_hand" => Me.Hands[1 - Me.Active].GlobalPosition,
            "doctor_hand" => Partner!.Hands[1].GlobalPosition,
            "doctor_body" => Partner!.ToGlobal(new Vector3(0f, 1f, -0.1f)),
            "doctor_down" => Partner!.Hands[0].GlobalPosition,
            // Out over the floor beside the table, nothing under it within reach.
            _ => Me.ToGlobal(new Vector3(0.2f, 1f, -0.15f)),
        };
    }

    /// <summary>Walks the surgeon to stand facing the aim from outside the table, close enough to reach it, and looking
    /// at it.</summary>
    private void StandBy(Vector3 aim)
    {
        // Stepping over counts as walking through the tubing, which would rip a line out and jolt the hand: take it out.
        Surgery.Patient.IvRemoved();
        var patient = Surgery.Patient.GlobalPosition;
        // Off the table's long side, on the aim's side of it.
        var away = Mathf.Abs(aim.Z - patient.Z) < 0.15f ? 1f : Mathf.Sign(aim.Z - patient.Z);
        var spot = new Vector3(aim.X, 0f, patient.Z + (away * (Mathf.Abs(aim.Z - patient.Z) + StandOff)));
        if (aim.DistanceTo(patient) > 1.2f)
        {
            // The tray or the IV stand: from the side that faces the room's middle.
            spot = (aim with { Y = 0f }) + (-aim.Slide(Vector3.Up).Normalized() * StandOff);
        }
        spot.Y = Me.GlobalPosition.Y;
        Me.GlobalPosition = spot;
        var facing = (aim - spot) with { Y = 0f };
        Me.Rotation = Me.Rotation with { Y = Mathf.Atan2(-facing.X, -facing.Z) };
        // Looking at it, as a player zooming in on it would.
        Me.Pitch = Mathf.Clamp(Mathf.Atan2(aim.Y - spot.Y - Surgeon.EyeHeight, facing.Length()), Surgeon.LookPitch.X, Surgeon.LookPitch.Y);
    }
}
