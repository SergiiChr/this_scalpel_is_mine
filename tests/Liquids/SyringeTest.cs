namespace Scalpel.Tests.Liquids;

/// <summary> Headless syringe test: the wheel works the plunger 1 ml a notch in every case of the shared syringe bench
/// (<see cref="SyringeBench.Cases"/>, the source of truth for all syringe and IV cases), and the syringe, its target
/// and what the patient got all add up after every notch. Also how a syringe is held and seen, the IV catheter and bag,
/// and what stress and sedatives do to a surgeon's hands. </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("smoke"), TestCategory("liquids"), TestCategory("tool_syringe_3"),
 TestCategory("tool_syringe_10"), TestCategory("tool_syringe_50"), TestCategory("tool_iv_catheter")]
public class SyringeTest
{
    /// <summary>Where debug mode says each case's syringe pushed its liquid.</summary>
    private static readonly Dictionary<string, string> PushedInto = new()
    {
        ["vial"] = "the vial", ["dish"] = "the kidney dish", ["drip"] = "the IV bag", ["vein"] = "the vein",
        ["skin"] = "the skin", ["fat"] = "the fat", ["muscle"] = "the muscle", ["own_hand"] = "Tester's hand",
        ["doctor_hand"] = "Partner's hand", ["doctor_body"] = "Partner's body", ["doctor_down"] = "Partner's hand",
    };

    private SyringeBench _bench = null!;
    /// <summary>Doses the host gave surgeons.</summary>
    private readonly List<(int Peer, string Drug, float Amount)> _doses = [];
    /// <summary>Messages shown on screen since the case started (debug mode is on).</summary>
    private readonly List<string> _toasts = [];
    private bool _debugWas;

    private Surgery Surgery => _bench.Surgery;
    private Surgeon Me => _bench.Surgery.LocalSurgeon!;
    private SurgicalTool Syringe => _bench.Syringe!;

    /// <summary>Starts the bench with debug mode on, collecting the doses surgeons get and the messages
    /// shown.</summary>
    private async Task Begin()
    {
        _bench = SyringeBench.Create();
        await _bench.Start();
        Surgery.SurgeonDosed += (peer, drug, amount) => _doses.Add((peer, drug, amount));
        _debugWas = Settings.Debug;
        Settings.Debug = true;
        Surgery.Hud.Toasted += _toasts.Add;
    }

    private async Task End()
    {
        Settings.Debug = _debugWas;
        await _bench.Stop();
    }

    /// <summary> Y1-Y3: a syringe picked up sits with its printed scale toward the eyes, on the inner side of the hand.
    /// The wheel moves the plunger instead of setting a level, and the hints say so. The last zoom step fades the hands
    /// and leaves the camera at the eyes; once the needle is in with Use tool held, it frames the needle with the
    /// printed scale turned to the camera. </summary>
    [TestCase(Timeout = Limits.Slow)]
    public async Task HeldSyringeShowsItsScaleAndTheWheelWorksItsPlunger()
    {
        await Begin();
        // Held out over the floor: over a vial it would stand upright in it.
        await _bench.Stage(SyringeBench.Cases[12]);
        var me = Me;
        var hand = me.Hands[me.Active];
        var camera = me.Camera;
        var along = Syringe.GlobalBasis.Z.Normalized();
        var printed = -Syringe.GlobalBasis.Y.Normalized();
        // A roll about its length can only face the eyes across the barrel, not along it.
        var toEyes = (camera.GlobalPosition - Syringe.Middle()).Slide(along).Normalized();
        var inward = me.GlobalBasis.X * (me.Active == 1 ? -1f : 1f);
        AssertBool(printed.Dot(toEyes) > 0.9f && printed.Dot(inward) > 0f)
            .OverrideFailureMessage("a syringe picked up shows its printed scale to the eyes, on the inner side of "
                + $"the hand ({printed.Dot(toEyes):0.00}, {printed.Dot(inward):0.00})")
            .IsTrue();
        await _bench.Stage(SyringeBench.Cases[0]);
        await SyringeBench.Notch(true);
        await SyringeBench.Notch(true);
        await SyringeBench.Notch(false);
        AssertBool(hand.Level == 0 && !hand.Lowered)
            .OverrideFailureMessage("the wheel works a syringe's plunger, not an effort level, without Use tool held")
            .IsTrue();
        AssertFloat(Syringe.Ml)
            .OverrideFailureMessage($"two notches down and one up leave 1 ml in the syringe ({Syringe.Ml:0.00})")
            .IsEqualApprox(1f, 0.00001f);
        var hints = string.Join("\n", Hud.ControlLines(me));
        AssertBool(hints.Contains("Pull plunger 1 ml") && hints.Contains("Push plunger 1 ml")
                && !hints.Contains("Wheel  Plunger"))
            .OverrideFailureMessage("the controls shown name the plunger on the wheel:\n" + hints).IsTrue();
        AssertBool(hints.Contains("Rotate"))
            .OverrideFailureMessage(
                "the controls shown leave out rolling a syringe, which turns to the eyes on its own:\n" + hints)
            .IsFalse();
        await _bench.ZoomTo(Surgeon.ZoomFov.Length - 1);
        await Frames.Physics(40);
        AssertBool(camera.Transform.IsEqualApprox(Transform3D.Identity) && Fade(hand) > 0.5f)
            .OverrideFailureMessage(
                $"the last zoom step fades the hands and leaves the camera at the eyes ({Fade(hand):0.00})")
            .IsTrue();
        AssertString(string.Join("\n", Hud.ControlLines(me)))
            .OverrideFailureMessage("the controls shown say when the hands are see-through").IsNotEqual(hints);
        var tipBefore = Syringe.TipPosition();
        await SyringeBench.Press();
        await Frames.Physics(35);
        along = Syringe.GlobalBasis.Z.Normalized();
        printed = -Syringe.GlobalBasis.Y.Normalized();
        var toCamera = (camera.GlobalPosition - Syringe.Middle()).Slide(along).Normalized();
        AssertFloat(printed.Dot(toCamera))
            .OverrideFailureMessage("with the needle in, the needle view turns the syringe's printed scale toward "
                + $"the camera ({printed.Dot(toCamera):0.00})")
            .IsGreater(0.98f);
        AssertFloat(Syringe.TipPosition().DistanceTo(tipBefore))
            .OverrideFailureMessage(
                $"turning the scale leaves the needle in the vial "
                    + $"({Syringe.TipPosition().DistanceTo(tipBefore):0.0000} m)")
            .IsLess(0.01f);
        var head = camera.GetParent<Node3D>();
        AssertFloat(camera.GlobalPosition.DistanceTo(head.GlobalPosition))
            .OverrideFailureMessage("with the needle in, the camera moves over to it").IsGreater(0.05f);
        foreach (var point in (Vector3[])[Syringe.GlobalPosition, Syringe.TipPosition(), _bench.Container!.Middle()])
        {
            AssertBool(camera.IsPositionInFrustum(point))
                .OverrideFailureMessage($"the needle view shows the syringe and the vial ({point})").IsTrue();
        }
        await SyringeBench.Release();
        await Frames.Physics(40);
        AssertBool(camera.Transform.IsEqualApprox(Transform3D.Identity) && Fade(hand) > 0.5f)
            .OverrideFailureMessage("Use tool let go, the camera goes back to the eyes and the hands stay see-through")
            .IsTrue();
        along = Syringe.GlobalBasis.Z.Normalized();
        printed = -Syringe.GlobalBasis.Y.Normalized();
        toEyes = (head.GlobalPosition - Syringe.Middle()).Slide(along).Normalized();
        AssertFloat(printed.Dot(toEyes))
            .OverrideFailureMessage(
                $"Use tool let go, the syringe rolls its scale back toward the eyes ({printed.Dot(toEyes):0.00})")
            .IsGreater(0.9f);
        await _bench.ZoomTo(0);
        await Frames.Physics(40);
        AssertFloat(Fade(hand)).OverrideFailureMessage("zooming out makes the hands solid").IsEqual(0f);
        await End();
    }

    /// <summary>Y3b: the syringe is held with the thumb on the plunger: drawing 8 ml pulls the plunger out and the
    /// thumb goes back with it, along the syringe as far as the plunger went, staying on its press.</summary>
    [TestCase(Timeout = Limits.Slow)]
    public async Task ThumbGoesBackWithThePlunger()
    {
        await Begin();
        await _bench.Stage(SyringeBench.Cases[0]);
        var me = Me;
        var hand = me.Hands[me.Active];
        var behind = new List<float>();
        var gaps = new List<float>();
        foreach (var pull in (int[])[0, 8])
        {
            for (var i = 0; i < pull; i++)
            {
                await SyringeBench.Notch(true);
            }
            await Frames.Physics(5);
            // The thumb's pad: its last bone, posed, carried on to the tip.
            var skeleton = hand.GloveSkeleton!;
            var last = skeleton.FindBone("Thumb3");
            var posed = skeleton.GetBoneGlobalPose(last);
            var toTip = hand.ThumbAtRest().Bones[2];
            var pad = skeleton.GlobalTransform
                * (posed.Origin + (posed.Basis * skeleton.GetBoneGlobalRest(last).Basis.Inverse() * toTip));
            // Along the syringe (+Z is back, toward the plunger), as the syringe holds it.
            behind.Add((Syringe.GlobalTransform.AffineInverse() * pad).Z);
            gaps.Add(pad.DistanceTo(Syringe.GlobalTransform * new Vector3(0f, 0f, hand.Press + SurgeonHand.PressPad)));
        }
        var travel = Syringe.Def.Length * Surgeon.SyringeTravel * 0.8f;
        var moved = behind[1] - behind[0];
        AssertBool(moved > 0.8f * travel && moved < 1.2f * travel)
            .OverrideFailureMessage(
                $"drawing 8 ml, the thumb goes back with the plunger ({moved * 100f:0.0} cm of {travel * 100f:0.0})")
            .IsTrue();
        AssertBool(gaps.All(gap => gap < 0.01f))
            .OverrideFailureMessage(
                $"the thumb stays on the plunger's press ({gaps[0] * 100f:0.0} and {gaps[1] * 100f:0.0} cm from it)")
            .IsTrue();
        await End();
    }

    /// <summary>Y4-Y10: one case of <see cref="SyringeBench.Cases"/>, checked after every notch and once the needle is
    /// out.</summary>
    [TestCase("vial_pull", Timeout = Limits.Slow)]
    [TestCase("vial_push", Timeout = Limits.Slow)]
    [TestCase("dish_push", Timeout = Limits.Slow)]
    [TestCase("dish_pull", Timeout = Limits.Slow)]
    [TestCase("vein_push", Timeout = Limits.Slow)]
    [TestCase("vein_pull", Timeout = Limits.Slow)]
    [TestCase("skin_push", Timeout = Limits.Slow)]
    [TestCase("fat_push", Timeout = Limits.Slow)]
    [TestCase("muscle_push", Timeout = Limits.Slow)]
    [TestCase("skin_pull", Timeout = Limits.Slow)]
    [TestCase("fat_pull", Timeout = Limits.Slow)]
    [TestCase("muscle_pull", Timeout = Limits.Slow)]
    [TestCase("air_pull", Timeout = Limits.Slow)]
    [TestCase("drip_push", Timeout = Limits.Slow)]
    [TestCase("drip_pull", Timeout = Limits.Slow)]
    [TestCase("doctor_hand_push", Timeout = Limits.Slow)]
    [TestCase("doctor_body_push", Timeout = Limits.Slow)]
    [TestCase("doctor_down_push", Timeout = Limits.Slow)]
    [TestCase("own_hand_pull", Timeout = Limits.Slow)]
    [TestCase("own_hand_push", Timeout = Limits.Slow)]
    public async Task PlungerAddsUp(string name)
    {
        await Begin();
        var @case = SyringeBench.Cases.Single(entry => entry.Name == name);
        await _bench.Stage(@case);
        var syringe = Syringe;
        var patient = Surgery.Patient;
        var target = _bench.NeedleTarget();
        var kind = @case.Target switch
        {
            "vial" or "dish" or "drip" => "container",
            "vein" or "air" => @case.Target,
            _ => "tissue",
        };
        var intoSurgeon = SyringeBench.SurgeonTargets.Contains(@case.Target);
        var peer = @case.Target == "own_hand" ? 1 : 2;
        if (intoSurgeon)
        {
            var part = @case.Target == "doctor_body" ? "body" : "hand";
            AssertBool(target is SurgeonTarget into && into.Peer == peer && into.Part == part)
                .OverrideFailureMessage($"{name}: the needle is in surgeon {peer}'s {part} ({target})").IsTrue();
        }
        else
        {
            AssertBool(Kind(target) == kind && ((target as TissueTarget)?.Layer ?? @case.Target) == @case.Target)
                .OverrideFailureMessage($"{name}: the needle is in the {@case.Target} ({target})").IsTrue();
        }
        var pull = @case.Notches > 0;
        var moves = !(pull && (kind == "tissue" || intoSurgeon));
        var drug = Db.Tool(@case.Vial)!.Drug;
        var givenBefore = InBody(patient.Drugs, drug);
        var me = Me;
        me.Status.Drugs.Clear();
        _doses.Clear();
        _toasts.Clear();
        var container = _bench.Container;
        for (var i = 0; i < Math.Abs(@case.Notches); i++)
        {
            var mlBefore = syringe.Ml;
            var airBefore = syringe.Air;
            var redBefore = syringe.Red;
            var outsideBefore = container?.Ml ?? 0f;
            var rednessBefore = Redness(syringe);
            var bloodBefore = patient.Vitals.BloodMl;
            var stepsBefore = Engine.GetPhysicsFrames();
            await SyringeBench.Notch(pull);
            var steps = Engine.GetPhysicsFrames() - stepsBefore;
            var step = moves ? 1f : 0f;
            var expectedMove = pull ? step : -step;
            var plunger = syringe.Ml + syringe.Air - (mlBefore + airBefore);
            AssertBool(Mathf.IsEqualApprox(plunger, expectedMove))
                .OverrideFailureMessage($"{name}: notch {i + 1} moves the plunger {expectedMove:+0;-0;+0} ml "
                    + $"({plunger:+0.00;-0.00})")
                .IsTrue();
            if (@case.Target == "air")
            {
                AssertBool(Mathf.IsEqualApprox(syringe.Air - airBefore, 1f) && syringe.Ml == mlBefore)
                    .OverrideFailureMessage($"{name}: pulling in the air draws air, the liquid stays {mlBefore:0} ml")
                    .IsTrue();
            }
            else if (container is not null)
            {
                // A drug pushed into the IV bag starts down the line at once: the bag keeps what hasn't run yet. It
                // runs every physics step the notch took, however many the frames it waited for held.
                var ran = @case.Target == "drip" && !pull
                    ? ToolActions.DripRate * steps / Engine.PhysicsTicksPerSecond
                    : 0f;
                var change = container.Ml - outsideBefore;
                AssertFloat(Mathf.Abs(change - (pull ? -1f : 1f)))
                    .OverrideFailureMessage($"{name}: the {container.Def.Name}'s level changes by the same 1 ml, "
                        + $"less what ran down the line ({change:+0.000;-0.000} over {steps} physics steps)")
                    .IsLessEqual(ran + 0.0001f);
            }
            if (@case.Target == "vein" && pull)
            {
                AssertBool(syringe.Red > redBefore && Redness(syringe) > rednessBefore)
                    .OverrideFailureMessage(
                        $"{name}: drawing blood turns the liquid redder ({redBefore:0.00} -> {syringe.Red:0.00})")
                    .IsTrue();
                // The patient keeps bleeding from the test's cuts meanwhile, a little.
                AssertFloat(Mathf.Abs(bloodBefore - patient.Vitals.BloodMl - 1f))
                    .OverrideFailureMessage(
                        $"{name}: the patient loses the 1 ml drawn ({bloodBefore - patient.Vitals.BloodMl:0.00})")
                    .IsLess(0.3f);
            }
            CheckShown(name, syringe);
            if (container is not null)
            {
                CheckShown(name, container);
            }
        }
        var expected = @case.Ml + (moves && @case.Target != "air" ? @case.Notches : 0f);
        AssertFloat(syringe.Ml)
            .OverrideFailureMessage($"{name}: the syringe ends with {expected:0} ml ({syringe.Ml:0.00})")
            .IsEqualApprox(Mathf.Max(expected, 0f), 0.00001f);
        if (@case.Target == "vein" && pull)
        {
            AssertFloat(syringe.Red)
                .OverrideFailureMessage(
                    $"{name}: the liquid is {100f * @case.Notches / expected:0}% blood ({syringe.Red:0.00})")
                .IsEqualApprox(@case.Notches / expected, 0.00001f);
        }
        if (@case.Target == "drip" && !pull)
        {
            AssertFloat(InBody(patient.Drugs, drug))
                .OverrideFailureMessage(
                    $"{name}: the drug starts down the line as it's pushed into the bag, the needle still in")
                .IsGreater(givenBefore);
        }
        var pushed = @case.Ml - syringe.Ml;
        await _bench.Withdraw();
        if (@case.Target == "drip" && !pull)
        {
            AssertFloat(container!.Bolus)
                .OverrideFailureMessage($"{name}: the drug runs down the line over time, not all at once")
                .IsGreater(0f);
            await Frames.Physics(Mathf.CeilToInt(pushed / ToolActions.DripRate * Engine.PhysicsTicksPerSecond) + 10);
            var told = _toasts.Where(toast => toast.Contains("reached the patient over IV", StringComparison.Ordinal))
                .ToList();
            var ticks = Enumerable.Range(0, Mathf.RoundToInt(pushed)).Select(ml =>
                $"[debug] 1.0 ml of {Db.Drug(SyringeBench.Drug)!.Name} reached the patient over IV ({ml + 1f:0.0} ml "
                    + $"total)");
            AssertBool(told.SequenceEqual(ticks))
                .OverrideFailureMessage($"{name}: debug mode tells each of the {pushed:0} ml as it reaches the "
                    + $"patient over IV, with the total so far ({string.Join(", ", told)})")
                .IsTrue();
        }
        if (!pull && kind != "air")
        {
            var named = $"[debug] Injected {pushed:0.0} ml of {Db.Drug(drug)!.Name} into {PushedInto[@case.Target]}";
            AssertBool(_toasts.Contains(named))
                .OverrideFailureMessage(
                    $"{name}: debug mode says what went where once the needle is out: {named} "
                        + $"({string.Join(", ", _toasts)})")
                .IsTrue();
            AssertBool(_toasts.Contains("[debug] Hit the vein") == (kind == "vein"))
                .OverrideFailureMessage(
                    $"{name}: debug mode says the needle hit the vein only for the vein ({string.Join(", ", _toasts)})")
                .IsTrue();
        }
        if (intoSurgeon)
        {
            var amount = Math.Abs(@case.Notches) * Db.Tool(@case.Vial)!.Concentration;
            if (pull)
            {
                AssertBool(_doses.Count == 0 && me.Status.Drugs.Entries.Count == 0)
                    .OverrideFailureMessage($"{name}: pulling from a hand draws nothing and gives nothing").IsTrue();
            }
            else
            {
                var total = _doses.Where(dose => dose.Peer == peer && dose.Drug == drug).Sum(dose => dose.Amount);
                AssertBool(Mathf.IsEqualApprox(total, amount) && _doses.Count == Math.Abs(@case.Notches))
                    .OverrideFailureMessage($"{name}: surgeon {peer} gets {amount:0.0} {drug}, some with every push "
                        + $"({string.Join(", ", _doses)})")
                    .IsTrue();
            }
            if (@case.Target == "own_hand" && !pull)
            {
                var entry = me.Status.Drugs.Find(drug);
                var onset = Db.Drug(drug)!.Onset * DrugDef.DirectOnset;
                AssertBool(entry is not null && Mathf.IsEqualApprox(entry.Onset, onset))
                    .OverrideFailureMessage(
                        $"{name}: the surgeon's own dose takes effect like a direct injection (onset {entry?.Onset})")
                    .IsTrue();
            }
            AssertFloat(InBody(patient.Drugs, drug)).OverrideFailureMessage($"{name}: the patient gets nothing")
                .IsLessEqual(givenBefore + 0.0001f);
            me.Status.Drugs.Clear();
        }
        else if (!pull && (kind is "vein" or "tissue" || @case.Target == "drip"))
        {
            var def = Db.Drug(drug)!;
            var share = pushed * Db.Tool(SyringeBench.Vial)!.Concentration / (def.Dose * patient.WeightKg);
            var given = InBody(patient.Drugs, drug) - givenBefore;
            var intoBlood = kind == "vein" || @case.Target == "drip";
            var onset = def.Onset * (intoBlood ? 1.5f : DrugDef.DirectOnset);
            var entryOnset = patient.Drugs.Find(drug)?.Onset ?? 0f;
            var how = @case.Target == "drip" ? "through the IV line"
                : intoBlood ? "into the blood"
                : "as a direct injection";
            AssertBool(Mathf.Abs(given - share) < share * 0.05f && Mathf.IsEqualApprox(entryOnset, onset))
                .OverrideFailureMessage(
                    $"{name}: all {pushed:0} ml are given {how} ({given:0.000} of {share:0.000} doses)")
                .IsTrue();
        }
        else if (kind != "air")
        {
            AssertFloat(InBody(patient.Drugs, drug)).OverrideFailureMessage($"{name}: nothing is given")
                .IsLessEqual(givenBefore + 0.0001f);
        }
        await End();
    }

    /// <summary>Y11-Y12: the IV catheter zoomed in on goes into the forearm (<see cref="SyringeBench.CatheterCases"/>).
    /// On the vein the line works; beside it the catheter still sticks and the tubing runs to it, but a drug in the IV
    /// drip stays in the bag.</summary>
    [TestCase("catheter_vein", Timeout = Limits.Slow)]
    [TestCase("catheter_miss", Timeout = Limits.Slow)]
    public async Task CatheterGoesIntoTheForearm(string name)
    {
        await Begin();
        var miss = SyringeBench.CatheterCases.Single(entry => entry.Name == name).Miss;
        await _bench.StageCatheter(miss);
        var me = Me;
        var patient = Surgery.Patient;
        var hit = miss == 0f;
        await _bench.ZoomTo(Surgeon.ZoomFov.Length - 1);
        await Frames.Physics(40);
        var hand = me.Hands[me.Active];
        AssertBool(me.Camera.Transform.IsEqualApprox(Transform3D.Identity) && Fade(hand) > 0.5f)
            .OverrideFailureMessage(
                $"{name}: the last zoom step fades the hands over the catheter, the camera at the eyes "
                    + $"({Fade(hand):0.00})")
            .IsTrue();
        // Checked before it goes in: the needle hurts, an awake patient's arm flinches and the vein moves with it.
        var off = (_bench.Catheter!.TipPosition() - _bench.VeinPoint()) * new Vector3(1f, 0f, 1f);
        AssertBool(hit ? off.Length() < 0.01f : off.Length() > 0.02f)
            .OverrideFailureMessage($"{name}: the needle is over {(hit ? "the vein" : "the arm beside the vein")} "
                + $"({off.Length() * 100f:0.0} cm across)")
            .IsTrue();
        _toasts.Clear();
        await SyringeBench.Press();
        var stuck = patient.IvSet && Surgery.Room.IvLine.IsAttached;
        AssertBool(stuck && patient.IvInVein == hit)
            .OverrideFailureMessage($"{name}: the catheter sticks with the tubing run to it, "
                + (hit ? "in the vein" : "but outside the vein"))
            .IsTrue();
        AssertBool(_toasts.Contains("[debug] Hit the vein") == hit)
            .OverrideFailureMessage(
                $"{name}: debug mode says the catheter hit the vein only when it did ({string.Join(", ", _toasts)})")
            .IsTrue();
        var drip = Surgery.Tools.DripBag()!;
        var givenBefore = InBody(patient.Drugs, SyringeBench.Drug);
        Surgery.Tools.AddLiquid(drip, 5f,
            new Dictionary<string, float> { [SyringeBench.Drug] = 5f * Db.Tool(SyringeBench.Vial)!.Concentration });
        drip.Bolus = 5f;
        await Frames.Physics(Mathf.CeilToInt(5f / ToolActions.DripRate * Engine.PhysicsTicksPerSecond) + 10);
        var given = InBody(patient.Drugs, SyringeBench.Drug) - givenBefore;
        AssertBool((given > 0.001f) == hit && drip.Contents.ContainsKey(SyringeBench.Drug) != hit)
            .OverrideFailureMessage($"{name}: a drug in the IV drip "
                + $"{(hit ? "runs into the patient" : "stays in the bag")}")
            .IsTrue();
        await _bench.ZoomTo(0);
        await Frames.Physics(20);
        await End();
    }

    /// <summary>Y13: the IV stand offers "Swap IV bag" only to a hand holding a bag; swapping hangs a full bag in place
    /// of the old one and runs it into a working line.</summary>
    [TestCase(Timeout = Limits.Slow)]
    public async Task SwapIvBag()
    {
        await Begin();
        var me = Me;
        var patient = Surgery.Patient;
        var swap = Surgery.Room.FindChildren("*", "", false, false).OfType<Interactable>()
            .First(interactable => interactable.Prompt == "Swap IV bag");
        await _bench.StageCatheter(0f);
        await SyringeBench.Press();
        AssertBool(patient.IvWorking).OverrideFailureMessage("swap_bag: a working line is in").IsTrue();
        var drip = Surgery.Tools.DripBag()!;
        Surgery.Tools.AddLiquid(drip, -drip.Ml);
        AssertBool(swap.OfferedTo(me)).OverrideFailureMessage("swap_bag: an empty hand isn't offered Swap IV bag")
            .IsFalse();
        var bag = _bench.Spawn("saline_bag", me.GlobalPosition + Vector3.Up);
        await Frames.Physics(2);
        Surgery.Tools.RequestGrab(bag, me.Active);
        await Frames.Physics(2);
        AssertBool(swap.OfferedTo(me)).OverrideFailureMessage("swap_bag: a hand holding a bag is offered Swap IV bag")
            .IsTrue();
        patient.Flags.Remove("drug_saline");
        swap.Interact(me);
        await Frames.Physics(2);
        var hung = Mathf.IsEqualApprox(drip.Ml, SurgicalTool.DripFluid) && bag.State == ToolState.Consumed;
        // It works once enough has run in: most of it by its onset through a line.
        await Frames.Physics(Mathf.CeilToInt(Db.Drug("saline")!.Onset * 1.5f * Engine.PhysicsTicksPerSecond));
        AssertBool(hung && patient.Flags.ContainsKey("drug_saline"))
            .OverrideFailureMessage($"swap_bag: the bag hangs on the stand full ({drip.Ml:0} ml) and runs into the "
                + $"line")
            .IsTrue();
        await End();
    }

    /// <summary>Y15-Y16: stress shakes the hands in three steps, quirks only set how low stress drains, and Steady
    /// hands stops it all. A status on its own, its Update() driven directly.</summary>
    [TestCase]
    public void StressShakesTheHands()
    {
        var status = new SurgeonStatus(new Modifiers());
        var shakes = new List<float>();
        foreach (var stress in (float[])[0.2f, 0.45f, 0.8f])
        {
            status.Stress = stress;
            shakes.Add(status.TremorAmount());
        }
        AssertBool(shakes[0] == 0f && shakes[1] > 0f && shakes[2] > shakes[1] * 2f)
            .OverrideFailureMessage("stress: calm hands keep the tool still, a third stressed shake it lightly, two "
                + $"thirds plainly ({string.Join(", ", shakes)})")
            .IsTrue();
        status.Stress = 0.2f;
        var twitches = 0;
        for (var i = 0; i < 600; i++)
        {
            status.Update(1f / 60f, default);
            twitches += status.Shiver() > 0f ? 1 : 0;
        }
        AssertBool(twitches is > 0 and < 500)
            .OverrideFailureMessage($"stress: calm, the glove still twitches now and then ({twitches} of 600 frames)")
            .IsTrue();
        var shaky = StatusWith(new() { ["stress_floor"] = "0.65" });
        shaky.Update(0.1f, default);
        AssertBool(Mathf.IsEqualApprox(shaky.Stress, 0.65f) && shaky.TremorAmount() > shakes[1])
            .OverrideFailureMessage($"stress: Shaky hands never drain below 65% and shake plainly "
                + $"({shaky.Stress:0.00})")
            .IsTrue();
        var steady = StatusWith(new() { ["stress_floor"] = "0.65", ["tremor_mult"] = "0" });
        steady.ColdTremor = 0.0015f;
        steady.Stress = 0.95f;
        AssertBool(steady.TremorAmount() == 0f && steady.Shiver() == 0f)
            .OverrideFailureMessage("stress: Steady hands with Shaky hands, stressed and cold, don't shake at all")
            .IsTrue();
        AssertBool(SurgeonStatus.WeightOf(new Modifiers()) == 80f
                && StatusWith(new() { ["weight_kg"] = "-20" }).WeightKg == 60f)
            .OverrideFailureMessage("stress: a surgeon weighs 80 kg, small hands 60 kg").IsTrue();
    }

    /// <summary>Y17-Y18: diazepam given to a surgeon. The right dose for their weight stops stress shaking (not the
    /// cold) and delays hand moves; past 1.4 times the dose the view darkens and the delay grows; twice the dose knocks
    /// them out for five minutes. Flumazenil brings them round at once; adrenaline only while it lasts.</summary>
    [TestCase]
    public void DiazepamSedatesASurgeon()
    {
        const float right = 0.2f * 80f;
        var status = Dosed(right);
        status.Stress = 0.8f;
        status.ColdTremor = 0.0015f;
        // Ten seconds in, the dose has faded a little from its peak.
        AssertBool(status.Calm > 0.95f && Mathf.Abs(status.TremorAmount() - 0.0015f) < 0.0003f)
            .OverrideFailureMessage(
                $"sedation: the right dose steadies stress shaking, the cold stays ({status.TremorAmount():0.0000})")
            .IsTrue();
        AssertBool(Mathf.Abs(status.InputDelay() - SurgeonStatus.SedatedDelay) < 0.005f && status.Overdose == 0f
                && !status.IsOut)
            .OverrideFailureMessage(
                $"sedation: the right dose delays hand moves {status.InputDelay() * 1000f:0} ms, nothing more")
            .IsTrue();
        var more = Dosed(right * 1.8f);
        AssertBool(more.Overdose > 0.4f && more.InputDelay() > SurgeonStatus.SedatedDelay + 0.05f && !more.IsOut)
            .OverrideFailureMessage(
                $"sedation: 1.8 doses darken the view and lag more ({more.Overdose:0.00}, "
                    + $"{more.InputDelay() * 1000f:0} ms)")
            .IsTrue();
        var events = new List<StatusEvent>();
        var knocked = Dosed(right * 2.5f, events);
        AssertBool(events.Contains(StatusEvent.KnockedOut) && knocked.IsOut
                && knocked.KnockedOut > SurgeonStatus.KnockoutTime - 10f)
            .OverrideFailureMessage(
                $"sedation: 2.5 doses knock the surgeon out for five minutes ({knocked.KnockedOut:0} s left)")
            .IsTrue();
        RunStatus(knocked, 60f, events);
        knocked.Administer("flumazenil", 0.01f * 80f);
        events.Clear();
        RunStatus(knocked, 2f, events);
        AssertBool(events.Contains(StatusEvent.CameRound) && !knocked.IsOut && knocked.Calm == 0f)
            .OverrideFailureMessage(
                $"sedation: flumazenil brings them round and ends the diazepam ({string.Join(", ", events)})")
            .IsTrue();
        var kept = Dosed(right * 3.5f);
        kept.Administer("adrenaline", 0.01f * 80f);
        events.Clear();
        RunStatus(kept, 3f, events);
        AssertBool(events.Contains(StatusEvent.CameRound) && !kept.IsOut && kept.Calm > 0.9f)
            .OverrideFailureMessage("sedation: adrenaline gets them up, still sedated").IsTrue();
        RunStatus(kept, Db.Drug("adrenaline")!.Duration, events);
        AssertBool(kept.IsOut)
            .OverrideFailureMessage("sedation: once the adrenaline wears off, 3.5 doses put them down again")
            .IsTrue();
    }

    /// <summary>Y19: the surgeon in the room, sedated: afterimages trail the gloves, mouse moves reach the hand late;
    /// knocked out they lie on the floor with the table in view, and flumazenil gets them up again.</summary>
    [TestCase(Timeout = Limits.Slow)]
    public async Task SedatedSurgeonInTheRoom()
    {
        await Begin();
        var me = Me;
        me.Status.Drugs.Clear();
        me.Status.Administer("diazepam", 0.2f * 80f);
        await Frames.Physics(200);
        var hand = me.Hands[me.Active];
        var trail = hand.FindChild("Trail", false, false);
        AssertBool(me.Status.Calm > 0.9f && trail is not null && trail.GetChildCount() == SurgeonHand.TrailCopies)
            .OverrideFailureMessage("sedated_surgeon: afterimages follow the gloves").IsTrue();
        var before = hand.Target;
        await _bench.Steer(new Vector2(40f, 0f));
        await Frames.Physics(2);
        var early = hand.Target.DistanceTo(before);
        await Frames.Tree.ToSignal(Frames.Tree.CreateTimer(0.2), SceneTreeTimer.SignalName.Timeout);
        await Frames.Physics(2);
        AssertBool(early < 0.001f && hand.Target.DistanceTo(before) > 0.01f)
            .OverrideFailureMessage("sedated_surgeon: a mouse move reaches the hand only after the delay "
                + $"({early:0.000} m, then {hand.Target.DistanceTo(before):0.000} m)")
            .IsTrue();
        me.Status.Administer("diazepam", 0.2f * 80f * 2f);
        await Frames.Physics(240);
        var camera = me.Camera;
        var patient = Surgery.Patient;
        var eyes = camera.GlobalPosition.Y - me.GlobalPosition.Y;
        AssertBool(me.Status.IsKnockedOut && me.IsDown && eyes < 0.4f)
            .OverrideFailureMessage($"sedated_surgeon: knocked out, the surgeon lies on the floor (eyes {eyes:0.00} "
                + $"m up)")
            .IsTrue();
        AssertBool(camera.IsPositionInFrustum(patient.GlobalPosition))
            .OverrideFailureMessage("sedated_surgeon: lying there, the patient on the table is in view").IsTrue();
        AssertBool(me.Hands.All(h => h.GlobalPosition.Y - me.GlobalPosition.Y < 0.15f))
            .OverrideFailureMessage("sedated_surgeon: the hands lie on the floor").IsTrue();
        me.Status.Administer("flumazenil", 0.01f * 80f);
        await Frames.Physics(200);
        AssertBool(!me.Status.IsOut && !me.IsDown && camera.GlobalPosition.Y - me.GlobalPosition.Y > 1.4f)
            .OverrideFailureMessage("sedated_surgeon: flumazenil gets them back on their feet").IsTrue();
        await End();
    }

    /// <summary> Y20-Y22: a syringe swept over a vial snaps smoothly in through its cap, only when the cap faces the
    /// surgeon, and lets go soon after; zoomed in, a mouse move takes the hand as far across the screen as it does
    /// zoomed out; in the needle view the mouse moves the hand as seen on screen; the needle rests on the back of the
    /// other hand; a needle pressed into the skin keeps its tip in place, the mouse tilting the syringe about it, and
    /// tears out when pulled on sideways; the aim shows where the needle goes in at any angle; a syringe brought to the
    /// IV bag from waist height snaps its needle into the middle of the bag from the hand's side, and moved on, comes
    /// out of it. </summary>
    [TestCase(Timeout = Limits.Slow)]
    public async Task NeedleHandling()
    {
        await Begin();
        var me = Me;
        var hand = me.Hands[me.Active];
        await _bench.Stage(SyringeBench.Cases[0]);
        await VialSnapChecks(me, hand);
        var across = new List<float>();
        for (var step = 0; step < Surgeon.ZoomFov.Length; step++)
        {
            await _bench.ZoomTo(step);
            await Frames.Physics(40);
            var screenBefore = me.Camera.UnprojectPosition(hand.GlobalPosition);
            await _bench.Steer(new Vector2(20f, 0f));
            await Frames.Physics(2);
            across.Add(me.Camera.UnprojectPosition(hand.GlobalPosition).X - screenBefore.X);
            await _bench.Steer(new Vector2(-20f, 0f));
            await Frames.Physics(2);
        }
        AssertBool(across[0] > 0f && Mathf.Abs((across[1] / across[0]) - 1f) < 0.15f)
            .OverrideFailureMessage("needle_hand: zoomed in, a mouse move takes the hand as far across the screen as "
                + $"zoomed out ({across[0]:0.0} px, {across[1]:0.0} px)")
            .IsTrue();
        // Into the dish: a vial would hold the needle where it snapped.
        await _bench.Stage(SyringeBench.Cases[2]);
        await SyringeBench.Press();
        await Frames.Physics(40);
        var right = (me.Camera.GlobalBasis.X * new Vector3(1f, 0f, 1f)).Normalized();
        var away = (me.Camera.GlobalBasis.Y * new Vector3(1f, 0f, 1f)).Normalized();
        foreach (var motion in (Vector2[])[new(10f, 0f), new(0f, -10f)])
        {
            var before = hand.Target;
            for (var i = 0; i < 5; i++)
            {
                await _bench.Steer(motion);
                await Frames.Physics(1);
            }
            var moved = (hand.Target - before) * new Vector3(1f, 0f, 1f);
            var sideways = motion.X > 0f;
            var along = moved.Dot(sideways ? right : away);
            // The camera follows the syringe, so it turns a little as the hand moves.
            AssertBool(along > 0.8f * moved.Length() && along > 0.005f)
                .OverrideFailureMessage($"needle_hand: in the needle view the mouse {(sideways ? "right" : "up")} "
                    + $"moves the hand {(sideways ? "right" : "away")} on screen "
                    + $"({along:0.000} m of {moved.Length():0.000})")
                .IsTrue();
        }
        await SyringeBench.Release();
        await _bench.ZoomTo(0);
        await Frames.Physics(40);
        await BackOfHandChecks();
        await _bench.Stage(SyringeBench.Cases[6]);
        // Moved once the camera has come round to the needle view: turning, it would turn the mouse's way with it.
        await Frames.Until(() => me.NeedleFraming >= 0.99f, 1f);
        var tip = Syringe.TipPosition();
        var tilt = hand.Tilt;
        var grip = hand.GlobalPosition;
        for (var i = 0; i < 5; i++)
        {
            await _bench.Steer(new Vector2(0f, 10f));
            await Frames.Physics(1);
        }
        await Frames.Physics(5);
        var drift = Syringe.TipPosition().DistanceTo(tip);
        AssertBool(drift < 0.001f && !Mathf.IsEqualApprox(hand.Tilt, tilt)
                && hand.GlobalPosition.DistanceTo(grip) > 0.005f)
            .OverrideFailureMessage($"needle_hand: a needle in the skin keeps its tip in place ({drift:0.0000} m) "
                + $"and the mouse tilts the syringe about it (tilt {tilt:0.00} -> {hand.Tilt:0.00})")
            .IsTrue();
        AssertObject(me.NeedleAnchor).OverrideFailureMessage("needle_hand: the needle is in the skin").IsNotNull();
        var wentIn = me.NeedleAnchor!.Value;
        var body = Surgery.Patient.Body;
        var beadsBefore = Beads(body);
        for (var i = 0; i < 60 && !me.NeedleTorn; i++)
        {
            await _bench.Steer(new Vector2(20f, 0f));
            await Frames.Physics(1);
        }
        var scratch = body.WoundMap.Value(WoundMap.Layer.Wounds, WoundMap.Cut, body.WorldToUv(wentIn));
        AssertBool(me.NeedleTorn && scratch > 0f)
            .OverrideFailureMessage(
                $"needle_hand: pulled on, the needle tears out and leaves a scratch where it was in ({scratch:0.00})")
            .IsTrue();
        var tornBeads = Beads(body).Except(beadsBefore).ToList();
        AssertInt(tornBeads.Count).OverrideFailureMessage("needle_hand: tearing out leaves one bead").IsEqual(1);
        AssertFloat(((tornBeads[0].GlobalPosition - wentIn) * new Vector3(1f, 0f, 1f)).Length())
            .OverrideFailureMessage("needle_hand: even after tearing out, the bead stays at the injection point")
            .IsLess(0.0005f);
        await _bench.Withdraw();
        await AimLands(me, hand);
        await SweepOntoPatient(me, hand);
        await _bench.Stage(SyringeBench.Cases[13]);
        // Use tool up: brought over from waist height.
        await SyringeBench.Release();
        var bag = Surgery.Tools.DripBag()!;
        hand.LocalTarget = me.ToLocal(me.GlobalPosition + (me.GlobalBasis * new Vector3(0.1f, 1f, -0.2f)));
        await Frames.Physics(20);
        var ownTilt = hand.Tilt;
        var under = bag.Middle() * new Vector3(1f, 0f, 1f);
        for (var i = 0; i < 30; i++)
        {
            // The hand's own spot, not where the snap holds it.
            var free = me.ToGlobal(hand.LocalTarget);
            hand.LocalTarget = me.ToLocal(free.Lerp(under + (Vector3.Up * free.Y), 0.1f));
            await Frames.Physics(1);
        }
        await Frames.Physics(15);
        var off = Syringe.TipPosition().DistanceTo(bag.Middle());
        // Pointing a little up into it: how far the tip end (-Z) rises.
        var rise = (-Syringe.GlobalBasis.Z.Normalized()).Dot(Vector3.Up);
        var side = ((hand.GlobalPosition - bag.Middle()) * new Vector3(1f, 0f, 1f))
            .Dot(me.ToGlobal(hand.LocalTarget) - bag.Middle());
        AssertBool(InContainer(_bench.NeedleTarget(), bag) && off < 0.005f
                && Mathf.Abs(rise - Mathf.Sin(Surgeon.DripTilt)) < 0.05f && side > 0f)
            .OverrideFailureMessage("needle_hand: a syringe brought to the IV bag from waist height snaps its needle a "
                + $"little upward into the middle of the bag, from the hand's side ({off * 100f:0.0} cm off, rising "
                + $"{rise:0.00}, hand at {hand.GlobalPosition.Y:0.00} m)")
            .IsTrue();
        for (var i = 0; i < 10; i++)
        {
            await _bench.Steer(new Vector2(0f, 30f));
            await Frames.Physics(1);
        }
        await Frames.Physics(20);
        AssertBool(!InContainer(_bench.NeedleTarget(), bag) && Mathf.IsEqualApprox(hand.Tilt, ownTilt))
            .OverrideFailureMessage("needle_hand: moved on from the bag, the needle comes out of it and the hand "
                + "holds the syringe as before "
                + $"({Syringe.TipPosition().DistanceTo(bag.Middle()):0.00} m from its middle)")
            .IsTrue();
        await End();
    }

    /// <summary>A syringe swept over a vial's cap on the tray at a steady 0.15 m/s, Use tool up. Standing (as
    /// delivered), its needle snaps in through the cap along the vial, without jumping (no frame moves the tip more
    /// than 1 cm), and lets go again soon after the hand passes it, the hand holding the syringe as before. Lying with
    /// its cap toward the surgeon it snaps in level; with the cap turned away it doesn't snap at all.</summary>
    private async Task VialSnapChecks(Surgeon me, SurgeonHand hand)
    {
        var standing = await Sweep(me, hand);
        AssertInt(standing.Snapped)
            .OverrideFailureMessage("needle_hand: swept over a standing vial's cap, the needle snaps in through it "
                + "along the vial")
            .IsGreater(0);
        AssertFloat(standing.Jump)
            .OverrideFailureMessage(
                $"needle_hand: snapping in and out, the needle moves smoothly (largest step "
                    + $"{standing.Jump * 1000f:0.0} mm a frame)")
            .IsLess(0.01f);
        AssertInt(standing.Snapped)
            .OverrideFailureMessage(
                $"needle_hand: swept past at 0.15 m/s, the needle holds on the vial only briefly ({standing.Snapped} "
                    + $"frames)")
            .IsLess(40);
        AssertBool(standing.Out)
            .OverrideFailureMessage("needle_hand: past the vial, the needle is out of it and the hand holds the "
                + "syringe as before")
            .IsTrue();
        foreach (var toward in (bool[])[true, false])
        {
            VialLies(me, toward);
            await Frames.Physics(30);
            var lying = await Sweep(me, hand);
            if (toward)
            {
                AssertInt(lying.Snapped).OverrideFailureMessage(
                        "needle_hand: a vial lying with its cap toward the surgeon takes the needle in level through "
                            + "the cap")
                    .IsGreater(0);
            }
            else
            {
                AssertInt(lying.Snapped).OverrideFailureMessage(
                        $"needle_hand: a vial lying with its cap turned away doesn't snap the needle "
                            + $"({lying.Snapped} frames)")
                    .IsEqual(0);
            }
        }
    }

    /// <summary>Lays the case's vial on its side where it stands, its cap toward the surgeon or away.</summary>
    private void VialLies(Surgeon me, bool toward)
    {
        var vial = _bench.Container!;
        var at = vial.Middle();
        var toMe = ((me.GlobalPosition - at) * new Vector3(1f, 0f, 1f)).Normalized() * (toward ? 1f : -1f);
        var bottom = (vial.GlobalTransform * vial.Bounds).Position.Y;
        // Looking at a point puts a tool's tip (-Z) toward it.
        var lying = new Transform3D(Basis.LookingAt(toMe, Vector3.Up), new Vector3(at.X, bottom + 0.0125f, at.Z));
        vial.GlobalTransform = lying.Translated(-(lying.Basis * new Vector3(0f, 0f, -vial.Def.Length * 0.5f)));
        vial.LinearVelocity = Vector3.Zero;
        vial.AngularVelocity = Vector3.Zero;
    }

    /// <param name="Snapped">Frames the needle was in the cap along the vial.</param>
    /// <param name="Jump">The largest step the tip took in a frame.</param>
    /// <param name="Out">Past it, the needle is out and the hand holds the syringe its own way again.</param>
    private readonly record struct SweepResult(int Snapped, float Jump, bool Out);

    /// <summary>Sweeps the syringe's tip across the vial's cap at 0.15 m/s from 12 cm before it to 12 cm past it, the
    /// hand moved its own way.</summary>
    private async Task<SweepResult> Sweep(Surgeon me, SurgeonHand hand)
    {
        var vial = _bench.Container!;
        var length = Syringe.Def.Length;
        var cap = vial.TipPosition();
        var along = (vial.GlobalPosition - cap).Normalized();
        var across = me.GlobalBasis.X;
        var start = cap - (across * 0.12f);
        // Off the vial first, so the hand holds the syringe its own way again.
        hand.LocalTarget = me.ToLocal(me.ToGlobal(hand.LocalTarget) - (across * 0.12f));
        await Frames.Physics(40);
        var own = new Vector2(hand.Tilt, hand.Turn);
        var free = start - hand.TipOffsetAt(length, own.X, own.Y);
        hand.LocalTarget = me.ToLocal(new Vector3(free.X, me.ToGlobal(hand.LocalTarget).Y, free.Z));
        await Frames.Physics(20);
        var last = Syringe.TipPosition();
        var jump = 0f;
        var snapped = 0;
        var frames = (int)(0.24f / 0.15f * 60f);
        for (var i = 0; i < frames; i++)
        {
            var tip = start.Lerp(cap + (across * 0.12f), (float)(i + 1) / frames);
            free = tip - hand.TipOffsetAt(length, own.X, own.Y);
            hand.LocalTarget = me.ToLocal(new Vector3(free.X, me.ToGlobal(hand.LocalTarget).Y, free.Z));
            await Frames.Physics(1);
            var now = Syringe.TipPosition();
            jump = Mathf.Max(jump, now.DistanceTo(last));
            last = now;
            // The syringe's tip end (-Z) points along the vial, into it.
            var lengthwise = (-Syringe.GlobalBasis.Z.Normalized()).Dot(along) > 0.97f;
            if (lengthwise && now.DistanceTo(cap) < 0.006f)
            {
                snapped++;
            }
        }
        await Frames.Physics(20);
        var isOut = !InContainer(_bench.NeedleTarget(), vial) && Mathf.IsEqualApprox(hand.Tilt, own.X);
        return new SweepResult(snapped, jump, isOut);
    }

    /// <summary>Held over the skin at a shallow and a steep angle (turned with Aim tool), the aim shows where the
    /// needle goes in: Use tool puts its tip right there.</summary>
    private async Task AimLands(Surgeon me, SurgeonHand hand)
    {
        await _bench.Stage(SyringeBench.Cases[6]);
        await SyringeBench.Release();
        foreach (var tilt in (float[])[-0.4f, -1.3f])
        {
            await SyringeBench.Aim(new Vector2(0f, (hand.Tilt - tilt) / Surgeon.AimSensitivity));
            await Frames.Physics(20);
            var aim = me.AimPoint();
            var hovering = Syringe.TipPosition().DistanceTo(aim);
            AssertFloat(hovering)
                .OverrideFailureMessage(
                    $"needle_hand: held at tilt {tilt:0.0} over the skin, the needle's tip is on the aim "
                        + $"({hovering * 1000f:0.0} mm off)")
                .IsLess(0.004f);
            await SyringeBench.Press();
            await Frames.Physics(20);
            var tip = Syringe.TipPosition();
            var sideways = new Vector2(tip.X - aim.X, tip.Z - aim.Z).Length();
            AssertBool(sideways < 0.002f && Mathf.Abs(tip.Y - aim.Y) < 0.006f)
                .OverrideFailureMessage($"needle_hand: held at tilt {tilt:0.0}, the needle goes in where the aim shows "
                    + $"({sideways * 1000f:0.0} mm across, {(aim.Y - tip.Y) * 1000f:0.0} mm down)")
                .IsTrue();
            await SyringeBench.Release();
            await Frames.Physics(10);
        }
    }

    /// <summary>Swept quickly from low beside the patient up onto the belly, Use tool up, the needle stays over the
    /// skin: it never sinks under it, where a wheel notch would inject.</summary>
    private async Task SweepOntoPatient(Surgeon me, SurgeonHand hand)
    {
        await _bench.Stage(SyringeBench.Cases[6]);
        await SyringeBench.Release();
        var belly = Surgery.Patient.Body.UvToWorld(SyringeBench.SkinUv);
        var beside = belly + (((me.GlobalPosition - belly) * new Vector3(1f, 0f, 1f)).Normalized() * 0.3f);
        hand.LocalTarget = me.ToLocal(beside - me.OwnTipOffset(me.Active));
        await Frames.Physics(30);
        var low = Syringe.TipPosition().Y;
        var inside = 0;
        for (var i = 0; i < 12; i++)
        {
            var tip = beside.Lerp(belly, (i + 1) / 12f);
            hand.LocalTarget = me.ToLocal(new Vector3(tip.X, me.ToGlobal(hand.LocalTarget).Y, tip.Z)
                - (me.OwnTipOffset(me.Active) * new Vector3(1f, 0f, 1f)));
            await Frames.Physics(1);
            var tipNow = Syringe.TipPosition();
            inside += me.SurfaceBelow(tipNow).Y - tipNow.Y > 0.003f ? 1 : 0;
        }
        await Frames.Physics(10);
        var risen = Syringe.TipPosition().Y - low;
        AssertBool(risen > 0.05f && inside == 0)
            .OverrideFailureMessage($"needle_hand: swept up onto the belly ({risen * 100f:0} cm higher), the needle "
                + $"never sinks under the skin ({inside} frames)")
            .IsTrue();
    }

    /// <summary>The needle aimed anywhere along the back of the surgeon's other hand, wrist to fingertips, rests on the
    /// glove and is in that hand: it doesn't sink into the glove or drop through it to what's under it.</summary>
    private async Task BackOfHandChecks()
    {
        await _bench.Stage(SyringeBench.Cases.Single(entry => entry.Name == "own_hand_push"));
        var me = Me;
        var hand = me.Hands[me.Active];
        var other = me.Hands[1 - me.Active];
        var glove = other.Glove;
        foreach (var along in (float[])[0.02f, 0.06f, 0.1f, 0.14f, 0.18f])
        {
            var aim = glove.GlobalPosition + (glove.GlobalBasis.X.Normalized() * along);
            for (var i = 0; i < 40; i++)
            {
                hand.LocalTarget = me.ToLocal(aim - hand.TipOffset(Syringe.Def.Length) + (Vector3.Up * 0.04f));
                await Frames.NextPhysics();
            }
            // Held still there, it glides down onto the glove (Surgeon.SyringeGlide).
            await Frames.Physics(20);
            var tip = Syringe.TipPosition();
            var back = other.GloveMiddle(tip).Y + SurgeonHand.PalmHalfThickness;
            var target = _bench.NeedleTarget();
            var inHand = target is SurgeonTarget { Part: "hand" } && Syringe.State == ToolState.Held;
            AssertBool(inHand && Mathf.Abs(tip.Y - back) < 0.004f)
                .OverrideFailureMessage($"needle_hand: {along * 100f:0} cm from the wrist the needle rests on the "
                    + $"back of the other hand ({(tip.Y - back) * 1000f:0.0} mm off it) and is in it ({target})")
                .IsTrue();
        }
        await _bench.Withdraw();
    }

    /// <summary>The liquid, air and plunger shown match what's in it exactly, against the full Level part (the
    /// graduation).</summary>
    private static void CheckShown(string name, SurgicalTool tool)
    {
        var share = tool.Ml / tool.Def.Volume;
        var level = tool.FindChild("Level", true, false) as MeshInstance3D;
        var part = level ?? (MeshInstance3D)tool.FindChild("Pool", true, false);
        var shown = level is not null ? part.Scale.Z : part.Scale.Y;
        AssertBool(part.Visible == tool.Ml > 0f && (tool.Ml == 0f || Mathf.Abs(shown - share) < 0.001f))
            .OverrideFailureMessage($"{name}: the {tool.Def.Name} shows {tool.Ml:0.0} ml "
                + $"({shown * tool.Def.Volume:0.0})")
            .IsTrue();
        AssertBool(tool.Red < 0.5f || Redness(tool) > 0f)
            .OverrideFailureMessage($"{name}: the {tool.Def.Name}'s liquid, {tool.Red * 100f:0}% blood, looks red")
            .IsTrue();
        if (tool.Def.Action != "syringe")
        {
            return;
        }
        var travel = level!.GetAabb().Size.Z;
        var plunger = (Node3D)tool.FindChild("Plunger", true, false);
        AssertFloat(Mathf.Abs(plunger.Position.Z - (travel * (tool.Ml + tool.Air) / tool.Def.Volume)))
            .OverrideFailureMessage($"{name}: the plunger sits behind {tool.Ml + tool.Air:0} ml of liquid and air")
            .IsLess(0.0005f);
        var airGap = level.Position.Z - tool.LevelRest!.Value.Z;
        AssertFloat(Mathf.Abs(airGap - (travel * tool.Air / tool.Def.Volume)))
            .OverrideFailureMessage($"{name}: the air shows at the needle end").IsLess(0.0005f);
    }

    /// <summary>How much redder than blue the liquid shown is.</summary>
    private static float Redness(SurgicalTool tool)
    {
        var part = (MeshInstance3D)tool.FindChild("Level", true, false);
        var albedo = ((ShaderMaterial)part.GetSurfaceOverrideMaterial(0)).GetShaderParameter("albedo").AsColor();
        return albedo.R - albedo.B;
    }

    /// <summary>How see-through the hand is drawn (0 solid).</summary>
    private static float Fade(SurgeonHand hand)
    {
        var glove = (GeometryInstance3D)hand.FindChildren("*", nameof(GeometryInstance3D), true, false)[0];
        return glove.MaterialOverride is StandardMaterial3D ghost ? 1f - ghost.AlbedoColor.A : 0f;
    }

    private static List<MeshInstance3D> Beads(Node body) =>
        [.. body.FindChildren("BloodBead*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>()];

    /// <summary>What kind of thing the needle is in, as the cases name it.</summary>
    private static string Kind(NeedleTarget target) => target switch
    {
        ContainerTarget => "container",
        VeinTarget => "vein",
        TissueTarget => "tissue",
        SurgeonTarget => "surgeon",
        _ => "air",
    };

    private static bool InContainer(NeedleTarget target, SurgicalTool container) =>
        target is ContainerTarget into && into.Container == container;

    /// <summary>How much of a drug went into a body so far, in the blood or still soaking in (shares of the right
    /// dose).</summary>
    private static float InBody(DrugLevels levels, string drug) =>
        levels.Find(drug) is { } entry ? entry.Depot + entry.Level : 0f;

    private static SurgeonStatus StatusWith(Dictionary<string, string> effects)
    {
        var mods = new Modifiers();
        mods.Add(effects);
        return new SurgeonStatus(mods);
    }

    /// <summary>A status given <paramref name="mg"/> of diazepam, run 10 seconds on (past its onset), the events it
    /// gave collected.</summary>
    private static SurgeonStatus Dosed(float mg, List<StatusEvent>? events = null)
    {
        var status = new SurgeonStatus(new Modifiers());
        status.Administer("diazepam", mg);
        RunStatus(status, 10f, events ?? []);
        return status;
    }

    private static void RunStatus(SurgeonStatus status, float seconds, List<StatusEvent> events)
    {
        for (var i = 0; i < (int)(seconds * 10f); i++)
        {
            events.AddRange(status.Update(0.1f, default));
        }
    }
}
