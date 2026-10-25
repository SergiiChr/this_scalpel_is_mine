namespace Scalpel.Patients;

/// <summary>
/// Tape (host only): a strip pulled off the roll and pressed down point by point, each stretch between two points stuck
/// down along its whole length. It pulls a small cut's edges under it together, at the tension skin closes at, and lays
/// gauze it goes over on the wound, pressing. How firmly it sticks is its grip (<see cref="Grip"/>): the tape's own,
/// less on blood and on a cut into the fat. It can only pull edges together that lie no further apart than its grip
/// (the stretch a stitch joining them would start at, TissueSim.TapeLoad()), and once they're shut it lets go when
/// they're pulled apart again by a share of that (<see cref="TapeHold"/>): a hand pulling on the skin beside it, a
/// strain. Blood oozing under it soaks in and loosens it more. Let go, it peels off and the cut opens again. It can't
/// close skin over open muscle.
/// A stretch laid over gauze holds the gauze, not the edges under it. The gauze soaks up what its pressure holds back,
/// and once it's full the blood soaks through into the tape, until the tape lets go and the gauze with it.
/// </summary>
public partial class Patient {
    /// <summary>Dry tape's grip on a cut through the skin, per unit of its <see cref="ToolDef.Power"/>: it pulls together
    /// edges as far apart as a stitch stretched 1 + Power * TapeGrip would join. A cut through the skin starts at about
    /// 1.6.</summary>
    public const float TapeGrip = 1.1f;
    /// <summary>The share of its grip tape loses stuck on blood (0..1 on the skin), and on a cut as deep as the muscle.
    /// </summary>
    public const float WetGrip = 0.35f;
    public const float DeepGrip = 0.6f;
    /// <summary>The share of its grip that holds the edges once they're shut: they're pulled apart again only by a hand
    /// pulling on the skin or a strain, and the tape lets go at a stretch of 1 + (grip - 1) * TapeHold.</summary>
    public const float TapeHold = 0.12f;
    /// <summary>How fast blood oozing under a strip soaks it: 1 / TapeSoak ml soak it through.</summary>
    public const float TapeSoak = 0.05f;
    /// <summary>Gauze within this height (meters) over the skin under a stretch of tape is taped down with it: lying on
    /// the skin, or held over it by the other hand.</summary>
    public const float GauzeReach = 0.05f;
    /// <summary>Taped gauze soaks up all that its pressure holds back from the cut under it: a cut bleeding hard fills
    /// it, a small one barely stains it. Full, the blood soaks through into the tape: 1 / SoakThrough ml soak it through.
    /// </summary>
    public const float SoakThrough = 0.2f;
    /// <summary>ml oozing into taped gauze it takes in at once, so a tick doesn't send every drop.</summary>
    private const float SeepStep = 0.5f;
    /// <summary>How fast (ml a second) the skin a ripped-out IV catheter tore oozes until it's taped shut.</summary>
    public const float IvTearOoze = 0.03f;
    /// <summary>Points along each stretch where what it lies on is looked at.</summary>
    private const int TapeSamples = 8;

    /// <summary>A strip's tape, how wet it is (0..1), the deepest cut it lies on, the wounds it lies on, the gauze it
    /// holds down and the blood (ml) oozing into that gauze not taken in yet.</summary>
    private sealed class Taping(ToolDef def) {
        public ToolDef Def { get; } = def;
        public float Wet { get; set; }
        public float Depth { get; set; }
        public HashSet<Wound> Wounds { get; } = [];
        public List<SurgicalTool> Gauze { get; } = [];
        public float Seeping { get; set; }
    }

    private readonly Dictionary<int, Taping> _tapings = [];

    /// <summary>A new strip for a roll of tape: its id for <see cref="PlaceTapePoint"/> and the rest. Shared with the
    /// threads' ids, so a tool's SutureState.Thread names either.</summary>
    public int NewTape() => NewSuture();

    /// <summary>True once nothing more goes on the strip: cut off the roll, or come off the skin.</summary>
    public bool TapeDone(int id) => Body.Tissue.Tape(id) is { Final: true };

    /// <summary>Presses strip <paramref name="id"/> of <paramref name="def"/> down at <paramref name="uv"/> where the
    /// skin is drawn, starting it there if it's new, and sticks the stretch from its previous point down. False when
    /// nothing was pressed down (the same spot again, a strip already cut off).</summary>
    public bool PlaceTapePoint(int id, Vector2 uv, ToolDef def) {
        var tissue = Body.Tissue;
        if (TapeDone(id)) {
            return false;
        }
        var (point, at) = Body.RestSpot(TissueDepth.Skin, uv, _ => true) ?? (tissue.Nearest(uv), uv);
        var before = tissue.Tape(id)?.Anchors.Count ?? 0;
        Rpc(MethodName.TapeAnchor, id, at, point, def.Radius, def.Color);
        if (tissue.Tape(id)!.Anchors.Count == before) {
            return false;
        }
        if (!_tapings.TryGetValue(id, out var taping)) {
            taping = new Taping(def);
            _tapings[id] = taping;
        }
        if (before > 0) {
            StickStretch(id, taping);
        }
        return true;
    }

    /// <summary>What the skin a ripped-out IV catheter tore oozes now (ml a second): nothing once it's taped shut.
    /// </summary>
    internal float IvTearBleeding { get; private set; }

    /// <summary>A strip of <paramref name="def"/> stuck on the forearm from <paramref name="from"/> to
    /// <paramref name="to"/> (world): across the tear a ripped-out IV catheter left, it holds it shut. Anywhere else it
    /// doesn't stick. False when it didn't.</summary>
    public bool TapeIvTear(Vector3 from, Vector3 to, ToolDef def) {
        if (Session.Room.IvLine.Tear is not { Taped: false } tear) {
            return false;
        }
        var (a, b) = (tear.OnArm(from), tear.OnArm(to));
        if (!IvTear.Covers(a, b, def.Radius)) {
            TearNotice("The strip didn't stick. Lay it across the tear, from one side of it to the other.");
            return false;
        }
        Rpc(MethodName.IvTearTaped, a.Y, b.Y, def.Radius, def.Color);
        // The blood the catheter left on the skin is under the tape now.
        Session.WipeBeads(tear.Across(0f), def.Radius * 1.5f);
        return true;
    }

    /// <summary>Cuts the strip off the roll. One pressed down at a single point holds nothing: it comes away with the
    /// roll.</summary>
    public void CutTape(int id) {
        if (Body.Tissue.Tape(id) is not { Final: false } tape) {
            return;
        }
        Rpc(tape.Anchors.Count < 2 ? MethodName.TapePeel : MethodName.TapeFinish, id);
    }

    /// <summary>The newest stretch of the strip goes down on the skin: it tapes down the gauze it goes over, or else
    /// takes the wet of the blood on the skin under it and the depth of the cuts it lies on, and holds their edges shut
    /// where it sticks well enough.</summary>
    private void StickStretch(int id, Taping taping) {
        var tape = Body.Tissue.Tape(id)!;
        var (a, b) = (tape.Points[^2], tape.Points[^1]);
        if (TapeGauze(taping, a, b, tape.HalfWidth)) {
            return;
        }
        var reach = Body.MetersToUv(tape.HalfWidth);
        var samples = Enumerable.Range(0, TapeSamples + 1).Select(i => a.Lerp(b, (float)i / TapeSamples)).ToList();
        var under = Wounds.Where(w => w.IsSkinCut && samples.Any(p => w.DistanceTo(p) < reach)).ToList();
        if (under.Any(w => w.ThroughMuscle && samples.Any(p => w.DistanceTo(p) < reach
            && Body.Tissue.MuscleOpenNear(w.ClosestPoint(p), MuscleReach)))) {
            TearNotice("Tape won't hold the skin over the open muscle. Sew the muscle first.");
        }
        taping.Wet = Mathf.Max(taping.Wet, samples.Max(Body.BloodAt));
        taping.Depth = under.Select(w => w.Depth).Append(taping.Depth).Max();
        var tension = StitchTension["closed"];
        if (!Body.Tissue.Holds(Body.Tissue.TapeLoad(id, tension), Grip(taping))) {
            PeelTape(id, "The cut gapes wider than the tape can pull shut. It peeled off.");
            return;
        }
        Rpc(MethodName.TapeStretch, id, tension, Hold(taping));
        foreach (var wound in under) {
            taping.Wounds.Add(wound);
            ClosedWith(wound, wound.Depth > 0.6f ? taping.Def.Quality * 0.5f : taping.Def.Quality);
        }
        if (under.Count > 0 && taping.Def.Id == "duct_tape") {
            AddFlag("duct_tape");
        }
    }

    /// <summary>How far apart a strip pulls edges together, as the stretch of a stitch joining them: the tape's own
    /// grip, less for the blood it's soaked in and the depth of the cut it's on.</summary>
    private static float Grip(Taping taping) {
        var deep = Mathf.Clamp((taping.Depth - SkinDepth) / (Wound.MuscleDepth - SkinDepth), 0f, 1f);
        return 1f + (taping.Def.Power * TapeGrip * (1f - (WetGrip * taping.Wet)) * (1f - (DeepGrip * deep)));
    }

    /// <summary>How far a strip's hold on edges it shut stretches before it lets go (see <see cref="Spring.Break"/>).
    /// </summary>
    private static float Hold(Taping taping) => 1f + ((Grip(taping) - 1f) * TapeHold);

    /// <summary>Gauze lying on the skin, or held on it, under the stretch from <paramref name="a"/> to
    /// <paramref name="b"/> (uv) is taped down there. False when the stretch lies on no gauze, this strip's own
    /// included.</summary>
    private bool TapeGauze(Taping taping, Vector2 a, Vector2 b, float halfWidth) {
        var tools = Session.Tools;
        var over = false;
        foreach (var gauze in tools.Tools.Values.Where(t => t.Def.Id == "gauze"
            && (t.State is ToolState.Free or ToolState.Held || taping.Gauze.Contains(t))).ToList()) {
            var middle = gauze.Middle();
            var uv = Body.WorldToUv(middle);
            var off = ((uv - Geometry2D.GetClosestPointToSegment(uv, a, b)) * Body.SiteSize).Length();
            if (!Body.OnBody(uv) || off >= halfWidth + (gauze.Def.Width * 0.5f) || middle.Y - Body.UvToWorld(uv).Y >= GauzeReach) {
                continue;
            }
            over = true;
            if (!taping.Gauze.Contains(gauze)) {
                tools.TapeDown(gauze);
                taping.Gauze.Add(gauze);
            }
        }
        return over;
    }

    /// <summary>Each tick: blood oozing from the wounds under a strip, or through the gauze it holds down, soaks into
    /// it, so it sticks less, and the gauze presses on the wound under it. Soaked through, it lets go of the gauze; gauze
    /// taken off takes its strip with it.</summary>
    private void UpdateTapes(float dt) {
        foreach (var (id, taping) in _tapings.Where(entry => Body.Tissue.Tape(entry.Key) is { Peeled: false }).ToList()) {
            var soak = taping.Wounds.Sum(w => w.Bleeding) * TapeSoak * dt;
            if (soak > 0f && taping.Wet < 1f) {
                taping.Wet = Mathf.Min(taping.Wet + soak, 1f);
                Body.Tissue.TapeStrength(id, Hold(taping));
            }
            if (taping.Gauze.Any(gauze => gauze.State != ToolState.Standing && !gauze.Taped)) {
                PeelTape(id, "The tape came off with the gauze.");
                continue;
            }
            // On every cut its pad lies on.
            foreach (var gauze in taping.Gauze) {
                var toPad = gauze.GlobalTransform.AffineInverse();
                foreach (var wound in Wounds) {
                    if (wound.IsSkinCut && Covered(gauze, toPad, wound)) {
                        Press(wound, dt);
                        SeepInto(taping, gauze, wound.LastingBleeding * dt);
                    }
                }
            }
            if (taping.Wet >= 1f && taping.Gauze.Count > 0) {
                PeelTape(id, "Blood soaked through the gauze and the tape let go.");
            }
        }
    }

    /// <summary>Blood oozing into taped <paramref name="gauze"/>: it soaks it up until it's full, and the rest soaks
    /// through into the tape.</summary>
    private void SeepInto(Taping taping, SurgicalTool gauze, float ml) {
        taping.Seeping += ml;
        if (taping.Seeping < SeepStep) {
            return;
        }
        var taken = Mathf.Clamp(gauze.Room, 0f, taping.Seeping);
        if (taken > 0f) {
            Session.Tools.AddLiquid(gauze, taken, new Dictionary<string, float> { ["blood"] = taken });
            Session.Tools.AddBlood(gauze, taken / gauze.Def.Volume);
        }
        taping.Wet = Mathf.Min(taping.Wet + ((taping.Seeping - taken) * SoakThrough), 1f);
        taping.Seeping = 0f;
    }

    /// <summary>The strip comes off the skin: what it held opens again and the gauze it held lies loose, or comes up in
    /// the hand still holding it. Said with
    /// <paramref name="notice"/> and scored, or quietly along with a closure bursting open (null).</summary>
    private void PeelTape(int id, string? notice) {
        Rpc(MethodName.TapePeel, id);
        var taping = _tapings[id];
        foreach (var gauze in taping.Gauze) {
            if (gauze.Taped) {
                Session.Tools.Untape(gauze);
            } else if (gauze.State == ToolState.Standing) {
                Session.Tools.LetFall(gauze);
            }
        }
        taping.Gauze.Clear();
        if (notice is not null) {
            Session.Scoring.Add("tape_peeled");
            Session.Announce(notice, true);
        }
    }

    /// <summary>Strips holding <paramref name="wound"/> come off with it as it bursts open.</summary>
    private void PeelTapesOn(Wound wound) {
        foreach (var id in _tapings.Where(entry => entry.Value.Wounds.Contains(wound)).Select(entry => entry.Key).ToList()) {
            if (Body.Tissue.Tape(id) is { Peeled: false }) {
                PeelTape(id, null);
            }
        }
    }
}
