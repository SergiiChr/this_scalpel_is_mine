using System.Runtime.CompilerServices;

namespace Scalpel.Tests.Support;

/// <summary>
/// Direct changes to a running surgery, for setting up what a test isn't about: each one writes the state an action
/// would leave, without anyone doing the action, and is named for that state (SkinIsCut(), PatientIsAsleep()). What a
/// player does goes through the Player* steps of <see cref="SurgeryDriver"/> instead, so the game itself does the
/// work. Keep the two apart: a test about cutting cuts with PlayerIncises(), a test about stitching starts from
/// SkinIsCut().
/// </summary>
public static class SurgeryState {
    /// <summary>The nurse has supplied the corrected patient chart.</summary>
    public static void PatientChartIsCorrected(Surgery surgery) => surgery.CorrectChart();

    /// <summary>Enable debug displays for a test and restore the previous preference when its scope ends.</summary>
    public static IDisposable DebugHudIsEnabled() {
        var was = Settings.Debug;
        Settings.Debug = true;
        return new Scope(() => Settings.Debug = was);
    }

    /// <summary>The player has picked <paramref name="difficulty"/> (null: none yet) until the scope ends.</summary>
    public static IDisposable DifficultyIs(Difficulty? difficulty) {
        var was = Settings.Difficulty;
        Settings.Difficulty = difficulty;
        return new Scope(() => Settings.Difficulty = was);
    }

    private sealed class Scope(Action restore) : IDisposable {
        public void Dispose() => restore();
    }

    /// <summary>A second, remote surgeon (peer 2) standing at the second spawn, as if they'd joined and put their card
    /// back: nothing streams their state, so they stay as placed.</summary>
    public static Surgeon PartnerIsAtTheTable(Surgery surgery) {
        Net.Instance.Roster[2] = new LobbyPlayer("Partner", [new QuirkRoll("normal_dude", "")], Ready: true);
        var partner = surgery.SpawnSurgeon(2, 1);
        partner.ReadingCard = false;
        return partner;
    }

    /// <summary>Tray spots something slid away from, per surgery: they aren't offered again.</summary>
    private static readonly ConditionalWeakTable<Surgery, List<Vector3>> BadTraySpots = [];

    /// <summary>As if <paramref name="surgeon"/> had worked a while in blood and sweated: both gloves bloody, sweat high.
    /// </summary>
    public static void GlovesAreSoiled(Surgeon surgeon) {
        foreach (var hand in surgeon.Hands) {
            hand.SetBlood(0.8f);
        }
        surgeon.Status.Sweat = 0.8f;
    }

    /// <summary>An awake patient in full panic, flailing; it calms down by itself over the next minute.</summary>
    public static void PatientPanics(Patient patient) => patient.Vitals.Panic = 1f;

    /// <summary>As if a right dose of propofol had gone in: asleep within seconds, for the rest of the surgery.
    /// </summary>
    public static void PatientIsAsleep(Patient patient) =>
        patient.Administer("propofol", DrugRoute.Direct, Db.Drug("propofol")!.Dose * patient.WeightKg);

    /// <summary>No remaining dose or effectiveness flag for this drug, between independent plunger checks.
    /// Other drugs (the shared patient's anesthesia) and its prepared tissue stay intact.</summary>
    public static void PatientHasNoDrug(Patient patient, string id) {
        if (patient.Drugs.Find(id) is { } entry) {
            entry.Depot = 0f;
            entry.Level = 0f;
            entry.Working = -1f;
            entry.Overdosed = false;
        }
        patient.Flags.Remove("drug_" + id);
    }

    /// <summary>As if lidocaine had gone into the site: numb within seconds.</summary>
    public static void PatientIsNumb(Patient patient) =>
        patient.Administer("lidocaine", DrugRoute.Direct, Db.Drug("lidocaine")!.Dose * patient.WeightKg);

    /// <summary>As if a catheter had gone into the arm's vein with the drip line on it.</summary>
    public static void IvLineIsIn(Patient patient) => patient.SetIv(patient.Body.Root.ToGlobal(Patient.PreopIvPoint), true);

    /// <summary>As if a catheter had gone into the arm and missed the vein: the line is in, but nothing runs through it.
    /// </summary>
    public static void IvLineMissedTheVein(Patient patient) =>
        patient.SetIv(patient.Body.Root.ToGlobal(Patient.PreopIvPoint), false);

    /// <summary>As if <paramref name="by"/> had caught the IV tubing and walked off with it, ripping the catheter out.
    /// </summary>
    public static void IvLineIsRippedOut(Patient patient, Surgeon by) => patient.PullIv(by);

    /// <summary>As if the IV tubing had been connected to <paramref name="catheter"/> at the stand.</summary>
    public static void IvLineIsOnCatheter(Patient patient, SurgicalTool catheter) =>
        patient.Rpc(Patient.MethodName.IvLineOn, catheter.Uid);

    /// <summary>A surgeon's cut from <paramref name="from"/> to <paramref name="to"/> (site uv),
    /// <paramref name="depth"/> deep (0..1, see <see cref="Wound.MuscleDepth"/>), as one clean stroke, or a hasty
    /// jagged one that bleeds far more (<paramref name="jagged"/>).</summary>
    public static Wound SkinIsCut(Patient patient, Vector2 from, Vector2 to, float depth, bool jagged = false) =>
        SkinIsCutAlong(patient, [from, to], depth, jagged);

    /// <summary>A clean cut in one stroke through <paramref name="points"/> (site uv), or a jagged one made too fast.
    /// Returns the stroke's wound, or the cut already there it ran along.</summary>
    public static Wound SkinIsCutAlong(Patient patient, IReadOnlyList<Vector2> points, float depth, bool jagged = false) {
        var stroke = 800_000L + patient.Wounds.Count;
        for (var i = 1; i < points.Count; i++) {
            patient.Cut(stroke, points[i - 1], points[i], depth, 1f, false, jagged ? 1f : 0f);
        }
        return patient.StrokeWound(stroke) ?? patient.Wounds.MinBy(wound => wound.DistanceTo(points[0]))!;
    }

    /// <summary>As if gauze had been pressed on <paramref name="wound"/> long enough to stop it for good.</summary>
    public static void WoundIsClotted(Wound wound) => wound.Clotted = true;

    /// <summary>Overstretched skin torn from <paramref name="from"/> along <paramref name="direction"/> (site uv) for
    /// <paramref name="lengthUv"/>, down to the fat. Returns the tear.</summary>
    public static Wound SkinIsTorn(Patient patient, Vector2 from, Vector2 direction, float lengthUv) {
        patient.Tear(from, direction, lengthUv);
        return patient.Wounds[^1];
    }

    /// <summary>As if the whole length of <paramref name="wound"/> had been sewn shut.</summary>
    public static void WoundIsClosed(Patient patient, Wound wound) {
        patient.Body.Tissue.StitchPath(wound.Points, Wound.BinLengthUv * 1.2f, 1f,
            Patient.ThreadStrength[(int)TissueDepth.Skin]);
        Array.Fill(wound.Bins, 1f);
    }

    /// <summary><paramref name="wound"/> torn on along its line by <paramref name="lengthUv"/> (site uv).</summary>
    public static void WoundIsTornFurther(Wound wound, float lengthUv) {
        var end = wound.Points[^1];
        var along = (end - wound.Points[0]).Normalized();
        wound.Extend(end + (along * lengthUv));
    }

    /// <summary>A vessel <paramref name="depthM"/> under the skin at <paramref name="uv"/> has given way and bleeds.
    /// </summary>
    public static Wound VesselBleeds(Patient patient, Vector2 uv, float depthM, float severity = 0.7f) {
        var vessel = patient.NewWound(WoundKind.Internal, uv, severity);
        vessel.DepthM = depthM;
        return vessel;
    }

    /// <summary><paramref name="ml"/> of blood has pooled in the opening.</summary>
    public static void CavityHoldsBlood(Patient patient, float ml) => patient.CavityBloodMl = ml;

    /// <summary>A film of blood covers the skin within <paramref name="radius"/> (uv) of <paramref name="uv"/>.</summary>
    public static void SkinIsBloody(Patient patient, Vector2 uv, float radius) =>
        patient.Paint(WoundMap.Layer.Fluids, WoundMap.Blood, uv, uv, radius, 1f, WoundMap.Mode.Max);

    /// <summary>No blood on the skin within <paramref name="radius"/> (uv) of <paramref name="uv"/>.</summary>
    public static void SkinIsDry(Patient patient, Vector2 uv, float radius) =>
        patient.Paint(WoundMap.Layer.Fluids, WoundMap.Blood, uv, uv, radius, 1f, WoundMap.Mode.Sub);

    /// <summary>The whole site just swabbed: no blood on its skin, in its cuts or in the opening. For key frames of what
    /// retained blood would hide; bleeding goes on and fills it again. Returns once the film has redrawn.</summary>
    public static async Task SiteIsSwabbed(Patient patient) {
        SkinIsDry(patient, new Vector2(0.5f, 0.5f), 0.75f);
        foreach (var wound in patient.Wounds) { wound.PooledMl = 0f; }
        CavityHoldsBlood(patient, 0f);
        await Frames.Process(3);
    }

    /// <summary>The skin at <paramref name="uv"/> is punctured (a stab of <paramref name="severity"/>, 0..1) and bleeds
    /// from the hole.</summary>
    public static Wound SkinIsPunctured(Patient patient, Vector2 uv, float severity) =>
        patient.NewWound(WoundKind.Puncture, uv, severity);

    /// <summary>A tool grips the tissue at <paramref name="uv"/> and holds it pulled <paramref name="by"/> (site-local
    /// meters) from where it lies. Returns the gripped grid point.</summary>
    public static int TissueIsPulled(Patient patient, Vector2 uv, Vector3 by) {
        const int Key = 99000;
        var tissue = patient.Body.Tissue;
        tissue.Grip(Key, uv);
        var k = tissue.GrippedParticle(Key);
        tissue.MoveGrip(Key, tissue.Pos[k] + by);
        return k;
    }

    /// <summary>As if <paramref name="wound"/> had been clamped shut: it doesn't bleed at all.</summary>
    public static void WoundIsClamped(Wound wound) => wound.Clamped = 1f;

    /// <summary>As if <paramref name="wound"/> had been cauterized as far as a cautery seals.</summary>
    public static void WoundIsCauterized(Wound wound) => wound.Cauterized = 0.95f;

    /// <summary>Tied running threads on stationary skin, for renderer and cache load checks without moving the
    /// player's needle.</summary>
    public static void SkinHasFinishedThreads(Patient patient, int count, int firstId) {
        var tissue = patient.Body.Tissue;
        for (var id = firstId; id < firstId + count; id++) {
            var n = id - firstId;
            var uv = new Vector2(0.3f + (n % 4 * 0.1f), 0.3f + (n / 4 * 0.1f));
            foreach (var hole in (Vector2[])[uv, uv + new Vector2(0.035f, 0.035f)]) {
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
    public static void NetworkSessionHasOrdinarySurgeons(ulong seed = 1) {
        var net = Net.Instance;
        foreach (var (peer, player) in net.Roster.ToList()) {
            net.Roster[peer] = player with { Quirks = [new QuirkRoll("normal_dude", "")] };
        }
        net.RunModifiers.Clear();
        net.SeedSessionRolls(seed);
    }

    /// <summary>Host: the run's modifiers are <paramref name="ids"/> instead of the lobby's random roll.</summary>
    public static void RunHasModifiers(params string[] ids) {
        Net.Instance.RunModifiers.Clear();
        Net.Instance.RunModifiers.AddRange(ids);
    }

    /// <summary>Surgeries start at 08:00 on the operating room's clock, whatever the system clock says, so it reads the
    /// same in every run's key frames.</summary>
    public static void ClockStartsAtEight() => GameClock.FixedStartOfDay = 8 * 3600;

    /// <summary><paramref name="tool"/> (a dish, or gauze or a pad soaked in it) holds <paramref name="ml"/> more of
    /// <paramref name="liquid"/>.</summary>
    public static void ToolHolds(Surgery surgery, SurgicalTool tool, string liquid, float ml) =>
        surgery.Tools.AddLiquid(tool, ml, new Dictionary<string, float> { [liquid] = ml });

    /// <summary>A tool lying in a free place on the instrument tray (stocked there, as the nurse would have).</summary>
    public static SurgicalTool ToolIsOnTray(Surgery surgery, string id) => ToolLiesAt(surgery, id, FreeTraySpot(surgery));

    /// <summary>Advance only the nurse's outstanding delivery and cooldown, without aging the patient.</summary>
    public static void NurseHasDelivered(Surgery surgery) {
        var nurse = surgery.Nurse;
        if (nurse.Current is { } order) {
            nurse.Tick(order.SecondsLeft, surgery);
        }
        nurse.Tick(nurse.CooldownLeft, surgery);
    }

    /// <summary>The nurse and shop are ready for the next ordinary test's order.</summary>
    public static void NurseIsReady(Surgery surgery) {
        NurseHasDelivered(surgery);
        surgery.Hud.Shop.ShowBoard(SurgeryStatus.Empty);
        surgery.Hud.Shop.SetNurseReady(surgery.Nurse.Idle);
    }

    /// <summary>The bag on the IV stand has run dry.</summary>
    public static void IvBagIsEmpty(Surgery surgery) {
        var bag = surgery.Tools.DripBag()!;
        surgery.Tools.AddLiquid(bag, -bag.Ml);
    }

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
    /// The ambulance has no tray: the spot is on the counter of the aisle <paramref name="near"/> is in.
    /// </summary>
    public static Vector3 FreeTraySpot(Surgery surgery, Vector3? near = null) {
        var spots = new List<Vector3>();
        if (surgery.Room.EnvironmentId == "ambulance") {
            // Empty counter beyond the tool compartments, on the same side as the doctor putting it down.
            var side = (near ?? surgery.LocalSurgeon!.GlobalPosition).Z >= 0f ? 1f : -1f;
            float[] xs = side > 0f ? [0.95f, 0.85f, 0.75f] : [-1f, -0.9f, -1.1f, -0.8f];
            foreach (var x in xs) {
                foreach (var z in (float[])[1.01f, 0.95f]) {
                    spots.Add(new Vector3(x, Room.TraySurface + 0.05f, side * z));
                }
            }
        } else {
            var rest = surgery.Room.TrayZone("");
            var strip = rest.End.X + 0.07f;
            foreach (var x in (float[])[strip, strip + 0.1f, rest.End.X - 0.05f, rest.Position.X + 0.05f]) {
                for (var i = 0; i < 9; i++) {
                    var along = i == 8 ? 0f : ((i / 2) + 1) * 0.06f * (i % 2 == 0 ? 1 : -1);
                    spots.Add(new Vector3(x, rest.Position.Y + 0.05f, rest.GetCenter().Z + along));
                }
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
    public static Surgeon SurgeonIsPoseFixture(Node3D parent, int peer, bool visible = true) {
        var surgeon = new Surgeon();
        parent.AddChild(surgeon);
        surgeon.Setup(peer, "", [], Transform3D.Identity);
        surgeon.SetPhysicsProcess(false);
        surgeon.BodyModel.Visible = visible;
        surgeon.FaceModel.Visible = visible;
        foreach (var hand in surgeon.Hands) {
            hand.Visible = visible;
        }
        return surgeon;
    }

    /// <summary>A received pose packet plus one animation tick on each peer, for exact model geometry and replication
    /// checks.</summary>
    public static void SurgeonPoseIsReceived(Surgeon owner, Surgeon puppet, float crouch, float pitch, float speed = 0f) {
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
    public static void SurgeonHandsAreAtHeight(Surgeon surgeon, float height) {
        foreach (var hand in surgeon.Hands) {
            hand.Target = surgeon.ToGlobal(new Vector3(hand.Index == 0 ? -0.17f : 0.17f, height, -0.38f));
            surgeon.SupportElbow(hand);
            hand.SnapPose(surgeon.VisualShoulder(hand.Index));
        }
    }

    /// <summary>A surgeon lies on the chosen side long enough for the normal physics animation to settle.</summary>
    public static void SurgeonIsKnockedOut(Surgeon surgeon, float side) {
        surgeon.Status.KnockedOut = 60f;
        surgeon.FallSide = side;
    }

    /// <summary>As if <paramref name="surgeon"/> had walked to <paramref name="at"/> and turned to
    /// <paramref name="yaw"/>.</summary>
    public static void SurgeonStandsAt(Surgeon surgeon, Vector3 at, float yaw) {
        surgeon.GlobalPosition = at;
        surgeon.Velocity = Vector3.Zero;
        surgeon.Rotation = surgeon.Rotation with { Y = yaw };
    }

    /// <summary>As if <paramref name="surgeon"/>'s racing heart had given out on their own peer: they lie on the floor,
    /// hands beside them, and the host waits for a defibrillator.</summary>
    public static void SurgeonsHeartHasStopped(Surgery surgery, Surgeon surgeon) {
        surgeon.FallSide = 1f;
        for (var i = 0; i < 2; i++) {
            surgeon.Hands[i].Target = surgeon.ToGlobal(Surgeon.LyingHand + new Vector3(-0.25f * i, 0f, 0f));
        }
        surgery.HeartStopped(surgeon);
    }

    /// <summary>A drug in a surgeon's blood, <paramref name="doses"/> times the right dose for their weight, given into
    /// the body.</summary>
    public static void SurgeonIsGiven(Surgeon surgeon, string drugId, float doses) =>
        surgeon.Status.Administer(drugId, Db.Drug(drugId)!.Dose * surgeon.Status.WeightKg * doses);

    /// <summary>A tool put straight into a surgeon's hand, the way a partner hands it over.</summary>
    public static void ToolIsPutInHand(Surgery surgery, SurgicalTool tool, int hand) =>
        surgery.Tools.RequestGrab(tool, hand);

    /// <summary>No stress-induced hand jitter during a camera or reach measurement.</summary>

    public static void SurgeonIsSteady(Surgeon surgeon) => surgeon.Status.Stress = 0f;

    /// <summary>Stressed enough that the held tool plainly shakes (SurgeonStatus.TremorAmount()).</summary>
    public static void SurgeonIsStressed(Surgeon surgeon) => surgeon.Status.Stress = 0.9f;

    /// <summary>A hand attached just inside its reach boundary; the walk cycle must not pull it around.</summary>
    public static void SurgeonHandIsAttached(Surgeon surgeon, int hand, Vector3 target) {
        surgeon.Hands[hand].Attached = true;
        surgeon.Hands[hand].Target = target;
    }

    /// <summary>A visible, non-colliding remote copy of an owner, for checking how other players see its poses.
    /// </summary>
    public static Surgeon SurgeonHasRemoteCopy(Surgeon owner) {
        var puppet = new Surgeon();
        owner.GetParent().AddChild(puppet);
        puppet.Setup(2, "", [], owner.GlobalTransform);
        puppet.CollisionLayer = 0;
        puppet.CollisionMask = 0;
        return puppet;
    }

    /// <summary>A normal owner packet delivered to the remote copy before its next physics tick.</summary>
    public static void SurgeonCopyHasOwnerState(Surgeon owner, Surgeon puppet) {
        puppet.GlobalTransform = owner.GlobalTransform;
        puppet.SyncState(owner.PackState());
    }

    /// <summary>
    /// Opens the site as wide as it goes, like a clamshell: an H-shaped incision through the muscle, both flaps folded
    /// back over the sides they're still attached to, slowly, by forceps all along both edges that keep holding them.
    /// Returns how many springs tore on the way.
    /// </summary>
    public static int SiteIsOpenedWide(Patient patient) {
        const float Middle = 0.51f;
        (Vector2 From, Vector2 To)[] cuts =
        [
            (new(0.03f, Middle), new(0.97f, Middle)), (new(0.03f, 0.02f), new(0.03f, 0.98f)),
            (new(0.97f, 0.02f), new(0.97f, 0.98f)),
        ];
        foreach (var (from, to) in cuts) {
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
        var hinges = new[] { 0, tissue.ResY }.Select(row => {
            var on = Enumerable.Range(0, tissue.ResX + 1).Select(i => tissue.Index(i, row)).Where(k => !tissue.Off[k]).ToList();
            return on.Aggregate(Vector3.Zero, (sum, k) => sum + tissue.Rest[k]) / Math.Max(on.Count, 1);
        }).ToArray();
        // Forceps about every 25 mm along each edge.
        var spacing = Math.Max(1, Mathf.RoundToInt(0.025f / (patient.Body.SiteSize.X / tissue.ResX)));
        var grips = new List<(int Key, Vector3 Hinge, Vector3 Arm, float Side)>();
        for (var i = 1; i < tissue.ResX; i += spacing) {
            foreach (var edge in (int[])[Mathf.FloorToInt(Middle * tissue.ResY), Mathf.CeilToInt(Middle * tissue.ResY)]) {
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
        for (var step = 0; step < Steps; step++) {
            var angle = Mathf.Pi * 0.85f * (step + 1) / Steps;
            foreach (var (key, hinge, arm, side) in grips) {
                tissue.MoveGrip(key, hinge + arm.Rotated(Vector3.Right, side * angle));
            }
            tissue.Substep();
        }
        for (var i = 0; i < 60; i++) {
            tissue.Substep();
        }
        tissue.BreakMult = breakMult;
        return tissue.Snapped.Count - snapped;
    }
}
