namespace Scalpel.Tests.TissueModification;

/// <summary>
/// Progressive scalpel depth and circular skin-graft removal on an arm, a thigh and a belly, close up: the scalpel in
/// the surgeon's hand is lowered, given a depth level and moved along the blade's edge on intact skin, step by step.
/// After every stage it checks what should be open and, in a run with key frames, saves a screenshot from straight
/// above the cut and one from 45° off the side and checks the frame budget; otherwise it's all headless assertions.
/// Then a skin graft on each: a circle cut out through the skin and lifted off with forceps. Review the key frames for
/// cohesive openings, aligned edges, organically separated layers, and the absence of blockiness, clipping or raised
/// plateaus.
/// <para>
/// Unlike the tool suites it works the hand's state (level, lowered, trigger, target) directly instead of sending
/// input, as the suite it was ported from did.
/// </para>
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("smoke"), TestCategory("tissue_modification"), TestCategory("tool_scalpel")]
[TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class SlicingTest
{
    private const string SurgeryScene = "res://scenes/surgery.tscn";
    private const string KeyFrameFolder = "slicing";
    /// <summary>Each move goes this far along the blade's edge, over <see cref="MoveTime"/> seconds (slow enough for a
    /// clean cut).</summary>
    private const float Move = 0.02f;
    private const float MoveTime = 1f;
    /// <summary>Pressing in place is held this long.</summary>
    private const float PressTime = 1f;
    /// <summary>A circle of skin cut out as a graft: its radius (meters), how far past its start the blade goes round
    /// to close it (radians) and how fast the hand goes round (m/s).</summary>
    private const float GraftRadius = 0.015f;
    private const float GraftOverlap = 0.4f;
    private const float GraftSpeed = 0.02f;
    /// <summary>How far (meters) the forceps lift the piece to show it pinched in place, and to take it off.</summary>
    private const float PinchLift = 0.004f;
    private const float TakeLift = 0.03f;
    /// <summary>How far from the cut (site uv) the gap is measured: severed springs lie up to half a grid cell off it.
    /// </summary>
    private const float GapRadius = 0.03f;
    /// <summary>The cameras: straight down from this far over the middle of the cut, and as far off at 45° from across
    /// the table. Far enough that the scalpel's handle doesn't fill the view, with a narrow lens for a close-up of the
    /// cut.</summary>
    private const float CameraDistance = 0.22f;
    private const float CameraFov = 20f;
    private const string BudgetBroken = "known to go over the frame budget, not profiled yet";

    /// <summary>Fat: a layer of fat lies between skin and muscle. Inside: what a cut through the muscle shows. Along:
    /// the cut runs along the limb (the surgeon turns to face along it), not across the table toward the surgeon.
    /// </summary>
    private static readonly SliceCase[] Cases =
    [
        new("arm", "hand_stitch", Fat: false, Inside: "bone", Along: true),
        new("thigh", "leg_extension", Fat: true, Inside: "bone", Along: true),
        new("belly", "appendectomy", Fat: true, Inside: "organs", Along: false),
    ];

    private bool _shots;
    private string _out = "";
    private Camera3D _camera = null!;
    private Surgery _surgery = null!;
    private SurgeonHand _hand = null!;
    private SurgicalTool _scalpel = null!;
    /// <summary>Where the blade's tip was when the cut started and after each move (site uv), and the way it moves (uv
    /// per meter).</summary>
    private readonly List<Vector2> _path = [];
    private Vector2 _edgeUv;
    /// <summary>The middle of the whole cut as planned, where the cameras look.</summary>
    private Vector2 _centerUv;
    /// <summary>Fastest the hand went during the last move (m/s): over 0.25 a cut comes out jagged.</summary>
    private float _topSpeed;
    /// <summary>Wall clock frame times (seconds) while the scalpel works, screenshots left out.</summary>
    private bool _measuring;
    private ulong _lastFrameUsec;
    private readonly List<float> _frameTimes = [];
    private readonly List<string> _report = [];
    private readonly FrameBudget _budget = new();

    private sealed record SliceCase(string Id, string Scenario, bool Fat, string Inside, bool Along);

    /// <summary>
    /// Visual review of the key frames shows the depth stages' openings as rectangular, discontinuous segments. The
    /// headless checks pass: what's broken is only how the incision looks.
    /// </summary>
    [TestCase(Timeout = Limits.Slow,
        Description = "BROKEN: visual review shows rectangular, discontinuous incision segments instead of one cohesive opening")]
    [TestCategory("broken")]
    public async Task ProgressiveDepth()
    {
        await RunCases(depth: true, grafts: false);
    }

    /// <summary>
    /// Visual review of the key frames shows the graft rims on the belly and the thigh as jagged or detached loops. The
    /// headless checks pass: what's broken is only how the rim looks.
    /// </summary>
    [TestCase(Timeout = Limits.Slow,
        Description = "BROKEN: belly and thigh graft rims form jagged or detached loops instead of a cohesive circular edge")]
    [TestCategory("broken")]
    public async Task CircularSkinGraftCutoutRemoval()
    {
        await RunCases(depth: false, grafts: true);
    }

    private async Task RunCases(bool depth, bool grafts)
    {
        _shots = KeyFrames.Wanted();
        _out = KeyFrames.Folder(KeyFrameFolder);
        _report.Clear();
        _frameTimes.Clear();
        _camera = new Camera3D { Fov = CameraFov, Near = 0.01f };
        Frames.Root.AddChild(_camera);
        // Drawn only for the screenshots: a software renderer would otherwise slow every frame of the surgery down.
        RenderingServer.RenderLoopEnabled = !_shots;
        Frames.Tree.ProcessFrame += TimeFrame;
        try
        {
            if (depth)
            {
                foreach (var slice in Cases)
                {
                    await Run(slice);
                }
            }
            if (grafts)
            {
                foreach (var slice in Cases)
                {
                    await RunGraft(slice);
                }
            }
            GD.Print(string.Join("\n", _report));
        }
        finally
        {
            Frames.Tree.ProcessFrame -= TimeFrame;
            if (GodotObject.IsInstanceValid(_surgery))
            {
                _surgery.QueueFree();
            }
            RenderingServer.RenderLoopEnabled = true;
            _camera.QueueFree();
            await Unmeasured(2);
        }
    }

    private async Task Run(SliceCase slice)
    {
        GD.Print("--- ", slice.Id);
        await Start(slice.Scenario, slice.Along, slice.Inside == "bone");
        var patient = _surgery.Patient;

        Check(patient.Wounds.Count == 0 && !Tissue.AnySevered, "the skin starts intact");
        var fat = patient.Body.FatThickness;
        Check(slice.Fat == (fat > 0f),
            $"the {slice.Id} has {(slice.Fat ? "fat" : "no fat")} under the skin ({fat * 1000f:0.0} mm)");
        await ShotsOf(slice.Id, "00_intact");

        // Lowered with no depth picked: the blade rests on the skin.
        _hand.Level = 0;
        _hand.Lowered = true;
        _hand.Trigger = true;
        await Hold(PressTime);
        Check(patient.Wounds.Count == 0 && !Tissue.AnySevered, "a lowered blade at level 0 does nothing");
        Check(_scalpel.Blood < 0.05f, "a blade resting on whole skin stays clean");
        await ShotsOf(slice.Id, "01_lowered");

        // Low: the point goes through the skin.
        await Press(1);
        _path.Clear();
        _path.Add(TipUv());
        Check(Tissue.AnySevered && Deepest() == TissueDepth.Skin, "pressed at low it goes through the skin only");
        await ShotsOf(slice.Id, "02_pressed_low");
        await MoveAlong();
        var first = Segment(0);
        Check(Open(first, TissueDepth.Skin) && !Open(first, TissueDepth.Fat),
            $"after the first 2 cm the skin is open, nothing under it is cut ({Layers(first)})");
        Check(Zipper(), $"the incision opens in the middle and closes at its ends ({Profile()})");
        await ShotsOf(slice.Id, "03_moved_low");

        // Medium: deeper, then 2 cm more.
        var before = TipDepth();
        await Press(2);
        Check(TipDepth() > before, $"pressed at medium it digs deeper ({before:0.0000} -> {TipDepth():0.0000} m)");
        await ShotsOf(slice.Id, "04_pressed_medium");
        await MoveAlong();
        var second = Segment(1);
        if (slice.Fat)
        {
            Check(Open(second, TissueDepth.Fat) && !Open(second, TissueDepth.Muscle),
                $"after the second 2 cm the fat is cut and the muscle shows ({Layers(second)})");
        }
        else
        {
            Check(Open(second, TissueDepth.Muscle), $"after the second 2 cm the muscle is cut ({Layers(second)})");
            Check(Under(second, "bone"), "a bone lies under the cut muscle");
        }
        Check(Zipper(), $"the incision opens in the middle and closes at its ends ({Profile()})");
        await ShotsOf(slice.Id, "05_moved_medium");

        // High.
        before = TipDepth();
        var pain = patient.Vitals.Pain;
        var scraped = patient.Flags.GetValueOrDefault("bone_scraped");
        await Press(3);
        if (!slice.Fat)
        {
            Check(patient.Flags.GetValueOrDefault("bone_scraped") > scraped, "pressed at high it hits the bone");
            Check(patient.Vitals.Pain > pain + 0.1f, "hitting the bone hurts through the local block "
                + $"(pain {pain:0.00} -> {patient.Vitals.Pain:0.00}, block {patient.Vitals.LocalBlock:0.00})");
            await ShotsOf(slice.Id, "06_pressed_high");
        }
        else
        {
            Check(TipDepth() > before, $"pressed at high it digs deeper ({before:0.0000} -> {TipDepth():0.0000} m)");
            await ShotsOf(slice.Id, "06_pressed_high");
            await MoveAlong();
            var third = Segment(2);
            Check(Open(third, TissueDepth.Muscle), $"after the third 2 cm the muscle is cut ({Layers(third)})");
            Check(Under(third, slice.Inside), $"{slice.Inside} lie under the cut muscle");
            Check(!patient.Wounds.Any(w => w.Kind == WoundKind.Internal),
                "cutting through the muscle nicks nothing inside");
            Check(Zipper(), $"the incision opens in the middle and closes at its ends ({Profile()})");
            await ShotsOf(slice.Id, "07_moved_high");
        }
        _hand.Lowered = false;
        _hand.Trigger = false;
        CheckSeam(patient.Body);
        ReportFrames(slice.Id);
        _surgery.QueueFree();
        await Unmeasured(3);
    }

    /// <summary>A circle of skin cut out through the skin only and taken off with forceps, as a graft: screenshots
    /// halfway round, with the circle closed, pinched in place, and of the wound once the piece is lifted away.
    /// </summary>
    private async Task RunGraft(SliceCase slice)
    {
        var id = slice.Id + "_graft";
        GD.Print("--- ", id);
        await Start(slice.Scenario, slice.Along, false, graft: true);
        var patient = _surgery.Patient;
        var body = patient.Body;
        var center = body.UvToWorld(_centerUv);
        var middle = Tissue.Nearest(_centerUv);
        await ShotsOf(id, "00_intact");

        _hand.Level = 0;
        _hand.Lowered = true;
        _hand.Trigger = true;
        await Hold(PressTime);
        await Press(1);
        var edge = ToolActions.BladeDirection(_scalpel);
        var across = body.Site.GlobalBasis.Y.Normalized().Cross(edge).Normalized();
        await CutArc(center, edge, across, 0f, Mathf.Pi);
        Check(Tissue.AnySevered && Deepest() == TissueDepth.Skin, "halfway round the skin is cut, nothing under it");
        Check(Tissue.PieceOf(middle).Count == 0, "halfway round the skin inside is still joined");
        await ShotsOf(id, "01_half_cut");
        // A little past where it started, so the circle surely closes.
        await CutArc(center, edge, across, Mathf.Pi, Mathf.Tau + GraftOverlap);
        var piece = Tissue.PieceOf(middle).Count;
        Check(piece > 0 && Deepest() == TissueDepth.Skin,
            $"the closed circle frees a piece of skin ({piece} grid points), cut through the skin only");
        Check(Tears() == 0, $"the circle is cut cleanly: {Tears()} tears, hand at most {_topSpeed:0.000} m/s");
        await ShotsOf(id, "02_cut");

        // The scalpel is lifted out of the way and the left hand takes forceps to the middle of the piece.
        _hand.Lowered = false;
        _hand.Trigger = false;
        _hand.LocalTarget += new Vector3(0.12f, 0.08f, 0.1f);
        var me = _surgery.LocalSurgeon!;
        var forceps = FreeTool("forceps");
        _surgery.Tools.RequestGrab(forceps, 0);
        me.Active = 0;
        var left = me.Hands[0];
        await ReachWith(left, forceps, _centerUv);
        left.Level = 1;
        left.Lowered = true;
        await Hold(0.3f);
        left.Trigger = true;
        await Hold(0.3f);
        Check(forceps.Hold is SkinHold { Piece: true }, "the forceps pinch the piece");
        await Lift(left, PinchLift, 0.3f);
        Check(Tissue.PieceOf(middle).Count == piece,
            $"pinched and lifted {Mathf.RoundToInt(PinchLift * 1000f)} mm, the piece is still in place");
        await ShotsOf(id, "03_picked_up");
        await Lift(left, TakeLift - PinchLift, 1f);
        var graft = _surgery.Tools.CarriedBy(forceps);
        Check(Tissue.Excised.Count(taken => taken) == piece, "lifted higher, the piece comes off whole");
        Check(graft is not null && graft.Def.Id == "skin_graft" && graft.Charges == 1,
            "the forceps hold it as a skin graft");
        // Taken well clear of the wound: up, to the side and back toward the surgeon, out of the cameras' view.
        left.LocalTarget += new Vector3(-0.15f, 0.12f, 0.12f);
        await Hold(0.8f);
        var under = slice.Fat ? "fat" : "muscle";
        var shown = body.LayerAt(_centerUv);
        Check(shown == under, $"the wound shows the {under} under the skin ({shown})");
        Check(Tears() == 0 && patient.Flags.ContainsKey("graft_taken"), "no tears around the wound");
        CheckSeam(body);
        await ShotsOf(id, "04_wound");
        left.Trigger = false;
        ReportFrames(id);
        _surgery.QueueFree();
        await Unmeasured(3);
    }

    /// <summary>Cuts the circle around <paramref name="center"/> from <paramref name="from"/> to <paramref name="to"/>
    /// (radians round from where it starts), steering the blade along the curve like a player: the hand rolls the
    /// scalpel about its length so the blade keeps facing the way it moves, and drifts back onto the line when the
    /// point strays.</summary>
    private async Task CutArc(Vector3 center, Vector3 edge, Vector3 across, float from, float to)
    {
        var me = _surgery.LocalSurgeon!;
        var frames = (int)((to - from) * GraftRadius / GraftSpeed * Engine.PhysicsTicksPerSecond);
        var flat = new Vector3(1f, 0f, 1f);
        _topSpeed = 0f;
        for (var i = 0; i < frames; i++)
        {
            var a = Mathf.Lerp(from, to, (float)i / frames);
            var b = Mathf.Lerp(from, to, (float)(i + 1) / frames);
            SteerBlade((edge * Mathf.Cos(b)) + (across * Mathf.Sin(b)));
            var step = CirclePoint(center, edge, across, b) - CirclePoint(center, edge, across, a);
            var drift = (CirclePoint(center, edge, across, a) - _scalpel.TipPosition()) * flat;
            _hand.LocalTarget += me.GlobalBasis.Inverse() * ((step * flat) + (drift * 0.3f));
            await MeasuredFrame();
            _topSpeed = Mathf.Max(_topSpeed, _hand.Speed);
        }
        await Hold(0.3f);
    }

    /// <summary>The point of the circle <paramref name="angle"/> round from where it starts (the side toward
    /// -<paramref name="across"/>).</summary>
    private static Vector3 CirclePoint(Vector3 center, Vector3 edge, Vector3 across, float angle) =>
        center + (((edge * Mathf.Sin(angle)) - (across * Mathf.Cos(angle))) * GraftRadius);

    /// <summary>Rolls the scalpel a little (as far as a hand turns it in a frame) so its blade lines up with
    /// <paramref name="direction"/> either way.</summary>
    private void SteerBlade(Vector3 direction)
    {
        var yaw = _hand.GetParent<Node3D>().GlobalRotation.Y;
        var best = _hand.Twist;
        var bestDot = -1f;
        for (var i = -30; i <= 30; i++)
        {
            var twist = _hand.Twist + (i * 0.005f);
            var side = new Basis(Vector3.Up, yaw) * new Basis(Vector3.Right, _hand.Tilt)
                * new Basis(Vector3.Forward, twist) * Vector3.Right;
            var dot = Mathf.Abs(side.Cross(Vector3.Up).Normalized().Dot(direction.Normalized()));
            if (dot > bestDot)
            {
                bestDot = dot;
                best = twist;
            }
        }
        _hand.Twist = best;
    }

    /// <summary>Brings <paramref name="hand"/>, holding <paramref name="tool"/>, over <paramref name="uv"/>: its tip
    /// there, a little above the skin.</summary>
    private async Task ReachWith(SurgeonHand hand, SurgicalTool tool, Vector2 uv)
    {
        var me = _surgery.LocalSurgeon!;
        var spot = _surgery.Patient.Body.UvToWorld(uv);
        await Unmeasured(10);
        for (var i = 0; i < 6; i++)
        {
            hand.LocalTarget += me.GlobalBasis.Inverse() * ((spot - tool.TipPosition()) * new Vector3(1f, 0f, 1f));
            await Unmeasured(20);
        }
    }

    /// <summary>Raises a hand by <paramref name="height"/> over <paramref name="seconds"/>, steadily. Holding onto
    /// something, it's pulled up; once it lets go, it's raised off the skin instead of coming back down onto it.
    /// </summary>
    private async Task Lift(SurgeonHand hand, float height, float seconds)
    {
        var frames = (int)(seconds * Engine.PhysicsTicksPerSecond);
        var up = new Vector3(0f, height / frames, 0f);
        for (var i = 0; i < frames; i++)
        {
            if (hand.Attached)
            {
                hand.Target += up;
            }
            else
            {
                hand.Lowered = false;
                hand.LocalTarget += up;
            }
            await MeasuredFrame();
        }
        await Hold(0.3f);
    }

    private int Tears() => _surgery.Patient.Wounds.Count(w => w.Kind == WoundKind.Tear);

    /// <summary>The first <paramref name="id"/> lying free in the room (on the tray).</summary>
    private SurgicalTool FreeTool(string id) =>
        _surgery.Tools.Tools.Values.First(tool => tool.Def.Id == id && tool.State == ToolState.Free);

    private void CheckSeam(PatientBody body)
    {
        var off = SkinSeam.Measure(body);
        Check(off < SkinSeam.Max,
            $"the simulated skin meets the body model without a step ({off * 1000f:0.00} mm off at its edge)");
    }

    /// <summary>
    /// Builds the scenario with the site untouched, an awake patient numbed with lidocaine and the scalpel in the right
    /// hand over the site, its edge along the limb or across the table. <paramref name="overBone"/>: the cut runs right
    /// over the bone nearest the middle of the site (a forearm's middle lies between its two bones).
    /// </summary>
    private async Task Start(string scenarioId, bool alongLimb, bool overBone, bool graft = false)
    {
        var scenario = Db.Scenario(scenarioId)! with
        {
            Wounds = [],
            Burns = [],
            Internal = [],
            Targets = [],
            Events = [],
        };
        var player = new LobbyPlayer("Tester", [new QuirkRoll("normal_dude", "")], Ready: true);
        Net.Instance.StartLocalSession(scenario, 42, player, []);
        _surgery = GD.Load<PackedScene>(SurgeryScene).Instantiate<Surgery>();
        Frames.Root.AddChild(_surgery);
        await Unmeasured(10);
        await SurgeryDriver.PlayerPutsCardBack(_surgery);
        _surgery.Hud.Visible = false;
        var patient = _surgery.Patient;
        var body = patient.Body;
        patient.Administer("lidocaine", DrugRoute.Direct);
        var me = _surgery.LocalSurgeon!;
        var flat = new Vector3(1f, 0f, 1f);
        // A blade's edge runs the way the surgeon faces. Stand at the table's side by the site, facing the table, or
        // turned to face along the limb like a player cutting along it.
        var site = body.Site.GlobalPosition;
        var side = Mathf.Sign(me.GlobalPosition.Z - site.Z);
        var along = new Vector3(0f, 0f, -side);
        if (alongLimb)
        {
            along = ((body.UvToWorld(new Vector2(0.6f, 0.5f)) - body.UvToWorld(new Vector2(0.4f, 0.5f))) * flat)
                .Normalized();
        }
        me.GlobalPosition = new Vector3(site.X, me.GlobalPosition.Y, site.Z) - (along * 0.2f)
            + new Vector3(0f, 0f, side * 0.3f);
        me.Rotation = me.Rotation with { Y = Mathf.Atan2(-along.X, -along.Z) };
        _scalpel = FreeTool("scalpel");
        _surgery.Tools.RequestGrab(_scalpel, 1);
        me.Active = 1;
        _hand = me.Hands[1];
        // Hands start turned in: aimed straight ahead (as Aim tool does), the edge runs the way the surgeon faces.
        _hand.Turn = 0f;
        PlaceHand(new Vector2(0.5f, 0.5f));
        await Unmeasured(30);
        // The edge in site uv, three moves of Move centered on the site.
        var edge = ToolActions.BladeDirection(_scalpel);
        var center = body.UvToWorld(new Vector2(0.5f, 0.5f));
        var edgeUv = body.WorldToUv(center + (edge * Move)) - new Vector2(0.5f, 0.5f);
        _edgeUv = edgeUv / Move;
        _centerUv = new Vector2(0.5f, 0.5f);
        if (overBone)
        {
            _centerUv = new Vector2(0.5f, MiddleOfNearestBone(body));
        }
        // The tip lands where the hand's height and tilt put it: nudge the hand until it's over the planned start.
        var start = _centerUv - (edgeUv * 1.5f);
        var travel = edgeUv * 3f;
        if (graft)
        {
            // The circle's far side no further over the table than the middle of the site (a hand at full stretch
            // can't steer), as long as the circle stays on the site (a forearm is narrow).
            var across = body.Site.GlobalBasis.Y.Normalized().Cross(edge).Normalized();
            var toward = Mathf.Sign(across.Dot(me.GlobalPosition - center));
            foreach (var shift in (float[])[1f, 0.5f, 0f])
            {
                var moved = center + (across * toward * GraftRadius * shift);
                if (Enumerable.Range(0, 16).All(n =>
                    body.Probe(CirclePoint(moved, edge, across, Mathf.Tau * n / 16)).Zone == SiteZone.Site))
                {
                    center = moved;
                    break;
                }
            }
            _centerUv = body.WorldToUv(center);
            // Round the circle from its point on the -across side, where it runs along the blade.
            start = body.WorldToUv(center - (across * GraftRadius));
            travel = body.WorldToUv(center + (across * GraftRadius)) - start;
        }
        PlaceHand(start);
        var lastReach = float.PositiveInfinity;
        for (var i = 0; i < 12; i++)
        {
            await Unmeasured(20);
            // The hand at the start or the end of the three moves out of reach from here: a step closer, like a player
            // would, while that still helps (the table stops them). With room to spare: at high the hand drops into
            // the opening, stretching the arm further.
            var finish = _hand.Target + ((body.UvToWorld(start + travel) - body.UvToWorld(start)) * flat);
            var shoulder = me.Shoulder(1);
            var reach = Mathf.Max(_hand.Target.DistanceTo(shoulder), finish.DistanceTo(shoulder));
            if (reach > Surgeon.Reach - 0.04f && reach < lastReach - 0.005f)
            {
                lastReach = reach;
                var keep = _hand.Target;
                me.GlobalPosition += ((body.UvToWorld(_centerUv) - me.GlobalPosition) * flat).Normalized() * 0.05f;
                // The body settles against the table over a frame or two: the hand stays where it was in the world.
                await Unmeasured(2);
                _hand.LocalTarget = me.ToLocal(keep);
                continue;
            }
            var miss = body.UvToWorld(start) - _scalpel.TipPosition();
            _hand.LocalTarget += me.GlobalBasis.Inverse() * (miss * flat);
        }
        await Unmeasured(20);
        GD.Print($"    blade along the site's long side: {Mathf.Abs(edge.Dot(along)):0.00}, tip at {TipUv()} for "
            + $"{start}, patient awake: {patient.Vitals.IsAwake}, local block {patient.Vitals.LocalBlock:0.00}");
    }

    /// <summary>The middle (v) of each run of v under which a bone lies just below the muscle: the one nearest the
    /// site's middle.</summary>
    private static float MiddleOfNearestBone(PatientBody body)
    {
        var runs = new List<Vector2>();
        var inside = false;
        for (var i = 0; i < 81; i++)
        {
            var v = 0.1f + (i * 0.01f);
            var bone = body.BoneAt(body.UvToWorld(new Vector2(0.5f, v), body.MuscleBottom + 0.006f), 0.001f).Length > 0;
            if (bone && !inside)
            {
                runs.Add(new Vector2(v, v));
            }
            else if (bone)
            {
                runs[^1] = runs[^1] with { Y = v };
            }
            inside = bone;
        }
        var nearest = 0.5f;
        var best = float.PositiveInfinity;
        foreach (var middle in runs.Select(run => (run.X + run.Y) * 0.5f))
        {
            if (Mathf.Abs(middle - 0.5f) < best)
            {
                best = Mathf.Abs(middle - 0.5f);
                nearest = middle;
            }
        }
        return nearest;
    }

    /// <summary>Puts the hand where the scalpel's tip comes down on <paramref name="uv"/>.</summary>
    private void PlaceHand(Vector2 uv)
    {
        var me = _surgery.LocalSurgeon!;
        var spot = _surgery.Patient.Body.UvToWorld(uv) + new Vector3(0f, 0.06f, 0f);
        _hand.Target = spot - ((_scalpel.TipPosition() - _hand.GlobalPosition) * new Vector3(1f, 0f, 1f));
        _hand.LocalTarget = me.ToLocal(_hand.Target);
    }

    private async Task Press(int level)
    {
        _hand.Level = level;
        _hand.Lowered = true;
        _hand.Trigger = true;
        await Hold(PressTime);
    }

    /// <summary>Moves the hand <see cref="Move"/> along the blade's edge over <see cref="MoveTime"/>, then checks the
    /// cut stayed clean.</summary>
    private async Task MoveAlong()
    {
        var body = _surgery.Patient.Body;
        var me = _surgery.LocalSurgeon!;
        var flat = new Vector3(1f, 0f, 1f);
        var at = _path[^1];
        var step = (body.UvToWorld(at + (_edgeUv * Move)) - body.UvToWorld(at)) * flat;
        var frames = (int)(MoveTime * Engine.PhysicsTicksPerSecond);
        _topSpeed = 0f;
        for (var i = 0; i < frames; i++)
        {
            // Like a player watching the blade: along the edge, and steered back onto the line if the tip drifts off it
            // (pressed deeper on a round limb, an angled blade's tip shifts sideways).
            var planned = body.UvToWorld(at + (_edgeUv * Move * (i + 1) / frames));
            var drift = (planned - _scalpel.TipPosition()) * flat;
            drift -= step.Normalized() * drift.Dot(step.Normalized());
            _hand.LocalTarget += me.GlobalBasis.Inverse() * ((step / frames) + (drift * 0.3f));
            await MeasuredFrame();
            _topSpeed = Mathf.Max(_topSpeed, _hand.Speed);
        }
        await Hold(0.5f);
        _path.Add(TipUv());
        var tears = Tears();
        Check(tears == 0 && _topSpeed < 0.25f,
            $"the incision stays clean: {tears} tears, hand at most {_topSpeed:0.000} m/s");
    }

    private Vector2 TipUv() => _surgery.Patient.Body.WorldToUv(_scalpel.TipPosition());

    /// <summary>The uv points along the n-th 2 cm of the cut, a little in from both ends.</summary>
    private Vector2[] Segment(int n)
    {
        var a = _path[n];
        var b = _path[n + 1];
        return [a.Lerp(b, 0.25f), a.Lerp(b, 0.5f), a.Lerp(b, 0.75f)];
    }

    private TissueSim Tissue => _surgery.Patient.Body.Tissue;

    /// <summary>The cut is visibly open down through <paramref name="depth"/> along these points: pulled apart as far
    /// as the meshes show it open.</summary>
    private bool Open(Vector2[] points, TissueDepth depth) =>
        Tissue.GapAlong(points, GapRadius, depth) > TissueSim.OpenGap * 0.5f;

    private string Layers(Vector2[] points) =>
        $"gap through skin {Tissue.GapAlong(points, GapRadius, TissueDepth.Skin) * 1000f:0.0}, "
        + $"fat {Tissue.GapAlong(points, GapRadius, TissueDepth.Fat) * 1000f:0.0}, "
        + $"muscle {Tissue.GapAlong(points, GapRadius, TissueDepth.Muscle) * 1000f:0.0} mm";

    /// <summary>Deepest any spring is cut so far.</summary>
    private TissueDepth Deepest() => Tissue.Severed.Select(Tissue.CutDepth).DefaultIfEmpty(TissueDepth.None).Max();

    /// <summary>How wide the skin gapes along the whole cut so far (mm), every 4 mm, from where it starts (the blade
    /// pressed in there cut as wide as itself, half of it behind the first move) to the blade.</summary>
    private List<float> Gaps()
    {
        var size = _surgery.Patient.Body.SiteSize;
        var gaps = new List<float> { SkinGapMm(_path[0] - (_edgeUv * ToolActions.StabLength * 0.5f)) };
        for (var n = 1; n < _path.Count; n++)
        {
            var a = _path[n - 1];
            var b = _path[n];
            var steps = Math.Max(1, Mathf.RoundToInt(((b - a) * size).Length() / 0.004f));
            for (var i = 0; i < steps + (n == _path.Count - 1 ? 1 : 0); i++)
            {
                gaps.Add(SkinGapMm(a.Lerp(b, (float)i / steps)));
            }
        }
        return gaps;
    }

    /// <summary>How wide the skin gapes at <paramref name="uv"/> (mm), at the springs within half a cell of it.
    /// </summary>
    private float SkinGapMm(Vector2 uv) => Tissue.GapAlong([uv], 0.5f / Tissue.ResX, TissueDepth.Skin) * 1000f;

    private string Profile() => string.Join(" ", Gaps().Select(gap => $"{gap:0.0}")) + " mm";

    /// <summary>Like opening a zipper: the cut gapes widest somewhere along it and narrows toward both ends. The end at
    /// the blade is measured at the springs nearest it, up to half a cell behind it on a coarse grid (a belly's), so it
    /// needn't be shut.</summary>
    private bool Zipper()
    {
        var gaps = Gaps();
        var widest = gaps.Max();
        return widest > TissueSim.OpenGap * 500f && gaps[0] < widest * 0.7f && gaps[^1] < widest * 0.7f;
    }

    /// <summary>There's a bone (or organs) right under the muscle along these points.</summary>
    private bool Under(Vector2[] points, string what)
    {
        var body = _surgery.Patient.Body;
        foreach (var uv in points)
        {
            var below = body.UvToWorld(uv, body.MuscleBottom + 0.006f);
            if (what == "bone" && body.BoneAt(below, 0.01f).Length > 0)
            {
                return true;
            }
            if (what == "organs" && body.OrganAt(below, 0.03f) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>How deep under the site's surface the scalpel's tip is now.</summary>
    private float TipDepth() => -_surgery.Patient.Body.HeightAboveSite(_scalpel.TipPosition());

    /// <summary>Logs the check and the state of the tip and the wounds after it (the log reads as the run's story),
    /// then asserts it.</summary>
    private void Check(bool ok, string what)
    {
        GD.Print((ok ? "    ok   " : "FAIL: ") + what);
        LogState();
        AssertBool(ok).OverrideFailureMessage(what).IsTrue();
    }

    private void LogState()
    {
        var patient = _surgery.Patient;
        var probe = patient.Body.Probe(_scalpel.TipPosition());
        var depths = patient.Wounds.GroupBy(wound => wound.Kind)
            .Select(kind => $"{kind.Key}: {kind.Max(wound => wound.Depth):0.00}");
        GD.Print($"         tip {probe.Zone} {probe.Depth:0.0000} m, wounds {{{string.Join(", ", depths)}}}, "
            + $"pain {patient.Vitals.Pain:0.00}, bone scraped {patient.Flags.GetValueOrDefault("bone_scraped"):0.00}");
    }

    private async Task Hold(float seconds)
    {
        for (var i = 0; i < (int)(seconds * Engine.PhysicsTicksPerSecond); i++)
        {
            await MeasuredFrame();
        }
    }

    /// <summary>A physics frame, timed. Untimed waits (<see cref="Unmeasured"/>, <see cref="Shot"/>) stop the timing
    /// until the next one.</summary>
    private async Task MeasuredFrame()
    {
        if (!_measuring)
        {
            _lastFrameUsec = Time.GetTicksUsec();
            _measuring = true;
            _budget.Resume();
        }
        await Frames.NextPhysics();
    }

    private async Task Unmeasured(int count)
    {
        _measuring = false;
        await Frames.Physics(count);
    }

    /// <summary>Wall clock time between frames, every drawn frame: the frame's delta is smoothed and capped, so it
    /// hides stalls.</summary>
    private void TimeFrame()
    {
        var now = Time.GetTicksUsec();
        if (_measuring)
        {
            _frameTimes.Add((now - _lastFrameUsec) / 1_000_000f);
            _budget.Sample();
        }
        _lastFrameUsec = now;
    }

    private void ReportFrames(string caseId)
    {
        if (_frameTimes.Count == 0)
        {
            return;
        }
        var sorted = _frameTimes.Order().ToList();
        var fps = sorted.Count / sorted.Sum();
        // 1% low: the frame time 99% of frames beat.
        var low = 1f / sorted[(int)(sorted.Count * 0.99f)];
        var drawn = _shots ? RenderingServer.GetCurrentRenderingMethod() : "no rendering";
        var line = $"{caseId}: {fps:0.0} fps average, {low:0.0} fps 1% low, {1f / sorted[^1]:0.0} fps worst frame "
            + $"({sorted.Count} frames, {drawn})";
        _report.Add(line);
        GD.Print("    " + line);
        _budget.Check(_shots, caseId, BudgetBroken);
        _frameTimes.Clear();
        _budget.Clear();
    }

    /// <summary>Both views of the middle of the cut: straight down, and 45° off the side from across the table, so the
    /// surgeon's arm isn't in the way.</summary>
    private async Task ShotsOf(string caseId, string file)
    {
        if (!_shots)
        {
            return;
        }
        var body = _surgery.Patient.Body;
        var middle = body.UvToWorld(_centerUv);
        var up = body.Site.GlobalBasis.Y.Normalized();
        var along = (body.UvToWorld(_centerUv + (_edgeUv * 0.01f)) - middle).Normalized();
        // Square to the cut, on the side away from the surgeon.
        var across = up.Cross(along).Normalized();
        if (across.Dot(middle - _surgery.LocalSurgeon!.GlobalPosition) < 0f)
        {
            across = -across;
        }
        // The hands would hide the cut from straight above; the scalpel stays in view.
        foreach (var hand in _surgery.LocalSurgeon.Hands)
        {
            hand.Visible = false;
        }
        _camera.Current = true;
        _camera.GlobalPosition = middle + (up * CameraDistance);
        _camera.LookAt(middle, along);
        await Shot(caseId, file + "_top");
        _camera.GlobalPosition = middle + ((up + across).Normalized() * CameraDistance);
        _camera.LookAt(middle, up);
        await Shot(caseId, file + "_side");
        foreach (var hand in _surgery.LocalSurgeon.Hands)
        {
            hand.Visible = true;
        }
    }

    private async Task Shot(string caseId, string file)
    {
        _measuring = false;
        var path = _out.PathJoin($"{caseId}_{file}.png");
        AssertBool(await KeyFrames.SaveViewport(path)).OverrideFailureMessage($"saved deliberate key frame {path}")
            .IsTrue();
    }
}
