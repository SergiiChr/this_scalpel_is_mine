using System.Runtime.CompilerServices;

namespace Scalpel.Tests.Support;

/// <summary>
/// Direct changes to a running surgery, for setting up what a test isn't about: each one writes the state an action
/// would leave, without anyone doing the action, and is named for that state (SkinIsCut(), PatientIsAsleep()). What a
/// player does goes through the Player* steps of <see cref="SurgeryDriver"/> instead, so the game itself does the
/// work. Keep the two apart: a test about cutting cuts with PlayerIncises(), a test about stitching starts from
/// SkinIsCut().
/// </summary>
public static class SurgeryState
{
    /// <summary>Enable debug displays for a test and restore the previous preference when its scope ends.</summary>
    public static IDisposable DebugHudIsEnabled() => new DebugHudScope();

    private sealed class DebugHudScope : IDisposable
    {
        private readonly bool _was = Settings.Debug;

        public DebugHudScope() => Settings.Debug = true;

        public void Dispose() => Settings.Debug = _was;
    }

    /// <summary>A second, remote surgeon (peer 2) standing at the second spawn, as if they'd joined and put their card
    /// back: nothing streams their state, so they stay as placed.</summary>
    public static Surgeon PartnerIsAtTheTable(Surgery surgery)
    {
        Net.Instance.Roster[2] = new LobbyPlayer("Partner", [new QuirkRoll("normal_dude", "")], Ready: true);
        var partner = surgery.SpawnSurgeon(2, 1);
        partner.ReadingCard = false;
        return partner;
    }

    /// <summary>Tray spots something slid away from, per surgery: they aren't offered again.</summary>
    private static readonly ConditionalWeakTable<Surgery, List<Vector3>> BadTraySpots = [];

    /// <summary>As if a right dose of propofol had gone in: asleep within seconds, for the rest of the surgery.
    /// </summary>
    public static void PatientIsAsleep(Patient patient) =>
        patient.Administer("propofol", DrugRoute.Direct, Db.Drug("propofol")!.Dose * patient.WeightKg);

    /// <summary>As if lidocaine had gone into the site: numb within seconds.</summary>
    public static void PatientIsNumb(Patient patient) =>
        patient.Administer("lidocaine", DrugRoute.Direct, Db.Drug("lidocaine")!.Dose * patient.WeightKg);

    /// <summary>A surgeon's cut from <paramref name="from"/> to <paramref name="to"/> (site uv),
    /// <paramref name="depth"/> deep (0..1, see <see cref="Wound.MuscleDepth"/>), as one clean stroke.</summary>
    public static Wound SkinIsCut(Patient patient, Vector2 from, Vector2 to, float depth)
    {
        patient.Cut(800_000 + patient.Wounds.Count, from, to, depth, 1f, false, 0f);
        return patient.Wounds[^1];
    }

    /// <summary>Overstretched skin torn from <paramref name="from"/> along <paramref name="direction"/> (site uv) for
    /// <paramref name="lengthUv"/>, down to the fat. Returns the tear.</summary>
    public static Wound SkinIsTorn(Patient patient, Vector2 from, Vector2 direction, float lengthUv)
    {
        patient.Tear(from, direction, lengthUv);
        return patient.Wounds[^1];
    }

    /// <summary>As if the whole length of <paramref name="wound"/> had been sewn shut.</summary>
    public static void WoundIsClosed(Patient patient, Wound wound)
    {
        patient.Body.Tissue.StitchPath(wound.Points, Wound.BinLengthUv * 1.2f, 1f,
            Patient.ThreadStrength[(int)TissueDepth.Skin]);
        Array.Fill(wound.Bins, 1f);
    }

    /// <summary>As if <paramref name="wound"/> had been cauterized as far as a cautery seals.</summary>
    public static void WoundIsCauterized(Wound wound) => wound.Cauterized = 0.95f;

    /// <summary>Tied running threads on stationary skin, for renderer and cache load checks without moving the
    /// player's needle.</summary>
    public static void SkinHasFinishedThreads(Patient patient, int count, int firstId)
    {
        var tissue = patient.Body.Tissue;
        for (var id = firstId; id < firstId + count; id++)
        {
            var n = id - firstId;
            var uv = new Vector2(0.3f + (n % 4 * 0.1f), 0.3f + (n / 4 * 0.1f));
            foreach (var hole in (Vector2[])[uv, uv + new Vector2(0.035f, 0.035f)])
            {
                tissue.ThreadAnchor(id, hole, TissueDepth.Skin, TissueSim.ThreadLoose[(int)TissueDepth.Skin], 4f, 1f);
            }
            tissue.FinishThread(id);
        }
    }

    /// <summary>Every session of <paramref name="scenarioId"/> in this process gets the whole starter kit on the tray.
    /// </summary>
    public static void ScenarioHasAllStarterTools(string scenarioId) =>
        Db.ReplaceScenario(Db.Scenario(scenarioId)! with { MissingToolChance = 0f });

    /// <summary>Host: both network surgeons have ordinary reach and steadiness, no run modifiers, and the session rolls
    /// come out the same every run.</summary>
    public static void NetworkSessionHasOrdinarySurgeons(ulong seed = 1)
    {
        var net = Net.Instance;
        foreach (var (peer, player) in net.Roster.ToList())
        {
            net.Roster[peer] = player with { Quirks = [new QuirkRoll("normal_dude", "")] };
        }
        net.RunModifiers.Clear();
        net.SeedSessionRolls(seed);
    }

    /// <summary>A tool lying in a free place on the instrument tray (stocked there, as the nurse would have).</summary>
    public static SurgicalTool ToolIsOnTray(Surgery surgery, string id) => ToolLiesAt(surgery, id, FreeTraySpot(surgery));

    /// <summary>This one improvised tool tears out (<paramref name="tear"/>) or catches a vessel
    /// (<paramref name="bleed"/>) with these chances, the same tool on the tray otherwise.</summary>
    public static void ToolGoesWrong(SurgicalTool tool, float tear, float bleed) =>
        tool.Def = tool.Def with { TearChance = tear, BleedChance = bleed };

    /// <summary>A tool dropped at <paramref name="at"/> (world), lying wherever it falls (left on the patient, say).
    /// </summary>
    public static SurgicalTool ToolLiesAt(Surgery surgery, string id, Vector3 at) =>
        surgery.Tools.ByUid(surgery.Tools.Spawn(id, at))!;

    /// <summary>
    /// A free place on the instrument tray, nothing else lying within 8 cm: the clear strip down its middle first. A
    /// spot something slid away from (<see cref="TraySpotIsBad"/>) isn't offered again.
    /// </summary>
    public static Vector3 FreeTraySpot(Surgery surgery)
    {
        var rest = surgery.Room.TrayZone("");
        var strip = rest.End.X + 0.07f;
        var spots = new List<Vector3>();
        foreach (var x in (float[])[strip, strip + 0.1f, rest.End.X - 0.05f, rest.Position.X + 0.05f])
        {
            for (var i = 0; i < 9; i++)
            {
                var along = i == 8 ? 0f : ((i / 2) + 1) * 0.06f * (i % 2 == 0 ? 1 : -1);
                spots.Add(new Vector3(x, rest.Position.Y + 0.05f, rest.GetCenter().Z + along));
            }
        }
        var bad = BadTraySpots.GetOrCreateValue(surgery);
        bool Crowded(Vector3 at) => surgery.Tools.Tools.Values.Any(tool =>
            tool.State == ToolState.Free && (tool.Middle() - at).Slide(Vector3.Up).Length() < 0.08f);
        return spots.FirstOrDefault(at => !bad.Contains(at) && !Crowded(at), spots[0]);
    }

    /// <summary>A tool set down at <paramref name="spot"/> (from <see cref="FreeTraySpot"/>) landed against something
    /// there and slid away.</summary>
    public static void TraySpotIsBad(Surgery surgery, Vector3 spot) => BadTraySpots.GetOrCreateValue(surgery).Add(spot);

    /// <summary>An isolated room shell for geometry and visual checks, with the same builders as Room.Build().</summary>
    public static void RoomHasShell(Room room, string environment) => room.BuildShell(environment);

    /// <summary>A stationary visual fixture. Real input and physics movement are covered by the movement tests.
    /// </summary>
    public static Surgeon SurgeonIsPoseFixture(Node3D parent, int peer, bool visible = true)
    {
        var surgeon = new Surgeon();
        parent.AddChild(surgeon);
        surgeon.Setup(peer, "", [], Transform3D.Identity);
        surgeon.SetPhysicsProcess(false);
        surgeon.BodyModel.Visible = visible;
        surgeon.FaceModel.Visible = visible;
        foreach (var hand in surgeon.Hands)
        {
            hand.Visible = visible;
        }
        return surgeon;
    }

    /// <summary>A received pose packet plus one animation tick on each peer, for exact model geometry and replication
    /// checks.</summary>
    public static void SurgeonPoseIsReceived(Surgeon owner, Surgeon puppet, float crouch, float pitch, float speed = 0f)
    {
        owner.Crouch = crouch;
        owner.Pitch = pitch;
        owner.LastPosition = owner.GlobalPosition - new Vector3(speed / 60f, 0f, 0f);
        puppet.LastPosition = puppet.GlobalPosition - new Vector3(speed / 60f, 0f, 0f);
        owner.AnimateBody(1f / 60f);
        puppet.SyncState(owner.PackState());
        puppet.AnimateBody(1f / 60f);
        SurgeonHandsAreAtHeight(puppet, 1.05f - (crouch * Surgeon.CrouchDrop));
    }

    /// <summary>Empty hands at a known floor-reaching height, to test sleeve clearance over the knees.</summary>
    public static void SurgeonHandsAreAtHeight(Surgeon surgeon, float height)
    {
        foreach (var hand in surgeon.Hands)
        {
            hand.Target = surgeon.ToGlobal(new Vector3(hand.Index == 0 ? -0.17f : 0.17f, height, -0.38f));
            surgeon.SupportElbow(hand);
            hand.SnapPose(surgeon.VisualShoulder(hand.Index));
        }
    }

    /// <summary>A surgeon lies on the chosen side long enough for the normal physics animation to settle.</summary>
    public static void SurgeonIsKnockedOut(Surgeon surgeon, float side)
    {
        surgeon.Status.KnockedOut = 60f;
        surgeon.FallSide = side;
    }

    /// <summary>No stress-induced hand jitter during a camera or reach measurement.</summary>
    public static void SurgeonIsSteady(Surgeon surgeon) => surgeon.Status.Stress = 0f;

    /// <summary>Stressed enough that the held tool plainly shakes (SurgeonStatus.TremorAmount()).</summary>
    public static void SurgeonIsStressed(Surgeon surgeon) => surgeon.Status.Stress = 0.9f;

    /// <summary>A hand attached just inside its reach boundary; the walk cycle must not pull it around.</summary>
    public static void SurgeonHandIsAttached(Surgeon surgeon, int hand, Vector3 target)
    {
        surgeon.Hands[hand].Attached = true;
        surgeon.Hands[hand].Target = target;
    }

    /// <summary>A visible, non-colliding remote copy of an owner, for checking how other players see its poses.
    /// </summary>
    public static Surgeon SurgeonHasRemoteCopy(Surgeon owner)
    {
        var puppet = new Surgeon();
        owner.GetParent().AddChild(puppet);
        puppet.Setup(2, "", [], owner.GlobalTransform);
        puppet.CollisionLayer = 0;
        puppet.CollisionMask = 0;
        return puppet;
    }

    /// <summary>A normal owner packet delivered to the remote copy before its next physics tick.</summary>
    public static void SurgeonCopyHasOwnerState(Surgeon owner, Surgeon puppet)
    {
        puppet.GlobalTransform = owner.GlobalTransform;
        puppet.SyncState(owner.PackState());
    }

    /// <summary>
    /// Opens the site as wide as it goes, like a clamshell: an H-shaped incision through the muscle, both flaps folded
    /// back over the sides they're still attached to, slowly, by forceps all along both edges that keep holding them.
    /// Returns how many springs tore on the way.
    /// </summary>
    public static int SiteIsOpenedWide(Patient patient)
    {
        const float Middle = 0.51f;
        (Vector2 From, Vector2 To)[] cuts =
        [
            (new(0.03f, Middle), new(0.97f, Middle)), (new(0.03f, 0.02f), new(0.03f, 0.98f)),
            (new(0.97f, 0.02f), new(0.97f, 0.98f)),
        ];
        foreach (var (from, to) in cuts)
        {
            patient.Cut(777000 + Mathf.RoundToInt(from.Y * 100f), from, to, 1f, 1f, false, 0.1f);
        }
        var tissue = patient.Body.Tissue;
        var snapped = tissue.Snapped.Count;
        // Thin skin (a patient quirk) is meant to tear on a fold like this; this is about ordinary skin.
        var breakMult = tissue.BreakMult;
        tissue.BreakMult = Mathf.Max(breakMult, 1f);
        // The flap turns up and over about the side of the site it's still attached to: one straight hinge along that
        // side, where it lies on the body on average (the body's flank drops away more under some of it than the
        // rest).
        var hinges = new[] { 0, tissue.ResY }.Select(row =>
        {
            var on = Enumerable.Range(0, tissue.ResX + 1).Select(i => tissue.Index(i, row)).Where(k => !tissue.Off[k]).ToList();
            return on.Aggregate(Vector3.Zero, (sum, k) => sum + tissue.Rest[k]) / Math.Max(on.Count, 1);
        }).ToArray();
        // Forceps about every 25 mm along each edge.
        var spacing = Math.Max(1, Mathf.RoundToInt(0.025f / (patient.Body.SiteSize.X / tissue.ResX)));
        var grips = new List<(int Key, Vector3 Hinge, Vector3 Arm, float Side)>();
        for (var i = 1; i < tissue.ResX; i += spacing)
        {
            foreach (var edge in (int[])[Mathf.FloorToInt(Middle * tissue.ResY), Mathf.CeilToInt(Middle * tissue.ResY)])
            {
                var k = tissue.Index(i, edge);
                var key = 88000 + grips.Count;
                tissue.Grip(key, tissue.UvOf(k));
                var side = edge < Middle * tissue.ResY ? -1f : 1f;
                var line = hinges[side < 0f ? 0 : 1];
                var hinge = new Vector3(tissue.Rest[k].X, line.Y, line.Z);
                grips.Add((key, hinge, tissue.Rest[k] - hinge, side));
            }
        }
        const int Steps = 240;
        for (var step = 0; step < Steps; step++)
        {
            var angle = Mathf.Pi * 0.85f * (step + 1) / Steps;
            foreach (var (key, hinge, arm, side) in grips)
            {
                tissue.MoveGrip(key, hinge + arm.Rotated(Vector3.Right, side * angle));
            }
            tissue.Substep();
        }
        for (var i = 0; i < 60; i++)
        {
            tissue.Substep();
        }
        tissue.BreakMult = breakMult;
        return tissue.Snapped.Count - snapped;
    }
}
