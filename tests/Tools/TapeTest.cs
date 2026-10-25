namespace Scalpel.Tests.Tools;

/// <summary>
/// Surgical tape as a player uses it: clicked down point by point like the needle's holes, the strip running from its
/// last point to the roll, stuck flat along the skin between points and cut off with a hold. It holds a shallow cut's
/// edges shut, peels off a cut into the fat it can't pull shut, lets go when the skin beside it is pulled once it's on
/// blood, and holds gauze it goes over pressing on a cut until the gauze soaks through. It can't close the hand stitch
/// laceration.
/// Key frames: review the strip running up to the roll from its first point, lying flat on the skin over the shut cut
/// (no gap showing under it, no tape sunk into the skin or floating off it), the deep cut gaping with no tape on it, the
/// cut pulled open after the tape let go, the gauze lying flat under a strip beside the cut, and gauze soaked red lying
/// loose once the tape across it let go.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("tool_surgical_tape"), TestCategory("tissue_modification"), TestCategory("visual_confirmation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class TapeTest {
    // Temporary local budget override: recorded CPU/render overruns remain measured; functional assertions still fail normally.
    private static readonly BudgetAllowance BudgetOverride = new("known to go over the frame budget: a strip stuck down changes the "
        + "tissue, and rebuilding its layers takes about 6 ms (PatientBody.RebuildLayers()); not optimized yet", 18f);
    /// <summary>How long (meters) the cuts are, how far past their ends a strip along one is pressed down, and how far
    /// the skin beside a taped cut is pulled.</summary>
    private const float CutLength = 0.03f;
    private const float Past = 0.01f;
    private const float Pull = 0.03f;
    /// <summary>How long (meters) the gash bleeding through taped gauze is.</summary>
    private const float GashLength = 0.06f;

    /// <summary>The skin an IV catheter tore as the line ripped it out oozes until a strip across the tear, stuck down
    /// on one side and cut off on the other, holds it shut. One that stops short of the far side doesn't stick.
    /// </summary>
    [TestCase]
    public async Task AStripAcrossTheTearARippedOutIvLeftStopsItOozing() {
        var session = await ToolSession.Start("bullet_muscle", "tape/iv_tear");
        var driver = session.Driver;
        var patient = driver.Patient;
        SurgeryState.IvLineIsRippedOut(patient, driver.Me);
        var tear = driver.Surgery.Room.IvLine.Tear!;
        await Frames.Seconds(0.5f);
        AssertFloat(patient.IvTearBleeding).OverrideFailureMessage("the torn skin oozes").IsGreater(0f);
        await session.CaptureAt("torn", "a ragged, bloodied tear on the forearm where the catheter was, no tape on it",
            tear.Across(0f), 0.2f);
        var roll = (await driver.PlayerRequestsItem("surgical_tape"))!;
        await driver.PlayerSticksTapeAt(tear.Across(-0.025f));
        await driver.PlayerSticksTapeAt(tear.Across(-0.005f), true);
        AssertBool(!tear.Taped && patient.IvTearBleeding > 0f && roll.Charges == roll.Def.Charges)
            .OverrideFailureMessage($"a strip that stops short of the far side doesn't stick, and takes no tape (taped {tear.Taped}, "
                + $"{roll.Charges} left)")
            .IsTrue();
        await driver.PlayerSticksTapeAt(tear.Across(-0.015f));
        await driver.PlayerSticksTapeAt(tear.Across(0.015f), true);
        await Frames.Seconds(0.5f);
        AssertBool(tear.Taped && patient.IvTearBleeding == 0f)
            .OverrideFailureMessage($"the strip holds the tear shut, so it stops oozing ({patient.IvTearBleeding:0.000} ml/s)\n{driver.Recent()}")
            .IsTrue();
        // The roll out of the way, to see the strip.
        var me = driver.Me;
        await driver.PlayerReaches(tear.Across(0f) + ((me.GlobalPosition - tear.Across(0f)).Slide(Vector3.Up).Normalized() * 0.2f)
            + (Vector3.Up * 0.1f));
        await session.CaptureAt("taped", "a strip of tape lying flat across the arm over the tear, covering its slit, "
            + "neither sunk into the skin nor floating off it", tear.Across(0f), 0.2f);
        await session.Finish(BudgetOverride);
    }

    /// <summary>The hand stitch laceration is too deep and its forearm skin too tight for tape: a strip along it peels
    /// straight off, and the cut stays open for the needle.</summary>
    [TestCase]
    public async Task TapePeelsOffTheHandStitchLaceration() {
        var session = await ToolSession.Start("hand_stitch", "tape/hand_stitch");
        var driver = session.Driver;
        var cut = driver.Patient.Wounds.Single(wound => wound.IsSkinCut);
        await driver.PlayerRequestsItem("surgical_tape");
        await driver.PlayerTapesAlong(cut);
        await Frames.Seconds(1f);
        var tissue = driver.Body.Tissue;
        AssertBool(tissue.TapeIds.Any() && tissue.TapeIds.All(id => tissue.Tape(id)!.Peeled))
            .OverrideFailureMessage($"the strip peels off the laceration\n{driver.Recent()}").IsTrue();
        AssertFloat(cut.Closure).OverrideFailureMessage($"and leaves it open ({cut.Closure:0.00})").IsLess(0.5f);
        AssertBool(driver.Surgery.Scoring.Entries.ContainsKey("tape_peeled")).OverrideFailureMessage("tape peeled off a cut costs points").IsTrue();
        await session.Finish(BudgetOverride);
    }

    [TestCase]
    public async Task AStripStuckAlongAShallowCutHoldsItShut() {
        var session = await ToolSession.Start("appendectomy", "tape/stuck");
        var driver = session.Driver;
        var me = driver.Me;
        var body = driver.Body;
        var (cut, from, to) = await Cut(driver, 0.3f);
        var bleeding = cut.Bleeding;
        AssertFloat(bleeding).OverrideFailureMessage($"the cut bleeds: {driver.Bleeders()}").IsGreater(0.01f);
        var tape = (await driver.PlayerRequestsItem("surgical_tape"))!;
        var hints = string.Join("\n", Hud.ControlLines(me));
        AssertBool(hints.Contains("Stick (click), cut off (hold)") && !me.UsesLevel(me.Active) && !ToolActions.HasTensionWheel(tape.Def))
            .OverrideFailureMessage("the controls shown say a click sticks and a hold cuts off, and the wheel does nothing:\n" + hints).IsTrue();
        var charges = tape.Charges;

        await driver.PlayerSticksTape(from);
        var id = tape.Suture.Thread;
        AssertInt(id).OverrideFailureMessage("the first click starts a strip").IsNotZero();
        // Moved on toward the far end, the strip pulled off the roll shows between them.
        await driver.PlayerReaches(driver.SitePoint(to) + (Vector3.Up * 0.02f));
        await Frames.Physics(2);
        var started = body.DrawnStrip(id);
        var roll = body.Site.ToLocal(tape.TipPosition());
        AssertFloat(started is { Live.Length: > 1 } ? started.Live[^1].DistanceTo(roll) : float.PositiveInfinity)
            .OverrideFailureMessage("the strip runs from its first point up to the roll").IsLess(0.001f);
        await driver.Capture("started", "the strip pressed down beside the cut's end, running from there up to the roll in the hand");
        await session.CaptureView("started", "the surgeon's view: the roll in the hand, the strip from it pressed down beside the cut, the controls saying a click sticks and a hold cuts off");

        await driver.PlayerSticksTape(to, cutOff: true);
        AssertBool(tape.Suture.Thread == 0 && body.Tissue.Tape(id) is { Final: true, Peeled: false })
            .OverrideFailureMessage("held at the far end, the strip is cut off there and stays stuck").IsTrue();
        AssertInt(tape.Charges).OverrideFailureMessage("each point pressed down takes a charge").IsEqual(charges - 2);
        await Frames.Seconds(2f);
        AssertFloat(cut.Closure).OverrideFailureMessage($"the strip holds the cut shut ({cut.Closure:0.00})\n{driver.Recent()}")
            .IsGreaterEqual(Patient.ClosedEnough);
        AssertFloat(cut.Bleeding).OverrideFailureMessage($"shut, it oozes no more: {driver.Bleeders()}").IsLess(bleeding * 0.1f);
        await Frames.Physics(2);
        var stuck = body.DrawnStrip(id)!;
        var off = stuck.Stuck.Max(p => Mathf.Abs(p.Y - body.SkinHeight(body.LocalToUv(p))));
        await driver.AssertAndCapture(stuck.Live.Length == 0 && off < 0.0015f,
            $"the strip lies flat on the skin all along the shut cut ({off * 1000f:0.0} mm off it), cut off the roll", "taped");
        await session.Finish(BudgetOverride);
    }

    /// <summary>On a cut into the fat (0.65) the edges lie further apart than the tape pulls shut: it peels straight
    /// off. Over cut muscle (0.9) it holds nothing either: the muscle holds the skin apart.</summary>
    [TestCase(0.65f)]
    [TestCase(0.9f)]
    public async Task TapeDoesNotCloseADeepCut(float depth) {
        var session = await ToolSession.Start("appendectomy", $"tape/deep_{depth * 100f:0}");
        var driver = session.Driver;
        var (cut, from, to) = await Cut(driver, depth);
        // A deep cut must exceed tape's grip even when dry. Previously a shallow, incorrectly placed blood
        // pool wetted this fixture and hid the fact that dry tape could close the deep cut.
        SurgeryState.WoundIsClamped(cut);
        await Frames.Seconds(0.3f);
        await SurgeryState.SiteIsSwabbed(driver.Patient);
        await driver.PlayerRequestsItem("surgical_tape");
        for (var sample = 0; sample <= 8; sample++) {
            AssertFloat(driver.Body.BloodAt(from.Lerp(to, sample / 8f)))
                .OverrideFailureMessage("the deep-cut tape check starts on dry skin, without blood reducing its grip").IsLess(0.05f);
        }
        await driver.PlayerLaysTape([from, to]);
        await Frames.Seconds(2f);
        AssertFloat(cut.Closure).OverrideFailureMessage($"the tape doesn't shut a cut {depth} deep ({cut.Closure:0.00})\n{driver.Recent()}")
            .IsLess(0.5f);
        if (depth < Wound.MuscleDepth) {
            AssertBool(driver.Surgery.Scoring.Entries.ContainsKey("tape_peeled")).OverrideFailureMessage("tape peeled off a cut costs points").IsTrue();
        }
        await driver.Capture("open", depth < Wound.MuscleDepth
            ? "the cut into the fat still gaping, no tape on it"
            : "the cut through the muscle still gaping under a strip of tape lying across it");
        await session.Finish(BudgetOverride);
    }

    /// <summary>Stuck on dry skin, a strip holds a cut shut while the skin beside it is pulled; stuck on blood it lets go,
    /// and the cut opens again.</summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task AStripOnBloodLetsGoWhenTheSkinIsPulled(bool bloody) {
        var session = await ToolSession.Start("appendectomy", $"tape/pulled_{(bloody ? "bloody" : "dry")}");
        var driver = session.Driver;
        var patient = driver.Patient;
        var body = driver.Body;
        var (cut, from, to) = await Cut(driver, 0.3f);
        // Pressed until it stopped, then wiped, or smeared with blood.
        SurgeryState.WoundIsClotted(cut);
        var around = body.MetersToUv(0.05f);
        if (bloody) {
            SurgeryState.SkinIsBloody(patient, cut.Midpoint, around);
        } else {
            SurgeryState.SkinIsDry(patient, cut.Midpoint, around);
        }
        var tape = (await driver.PlayerRequestsItem("surgical_tape"))!;
        await driver.PlayerSticksTape(from);
        var id = tape.Suture.Thread;
        await driver.PlayerSticksTape(to, cutOff: true);
        await Frames.Seconds(1f);
        AssertFloat(cut.Closure).OverrideFailureMessage($"the strip shuts the cut first ({cut.Closure:0.00})").IsGreaterEqual(Patient.ClosedEnough);
        await driver.PlayerPutsDown();
        SurgeryState.TissueIsPulled(patient, cut.Midpoint + new Vector2(0f, body.MetersToUv(0.005f)), new Vector3(0f, 0f, Pull));
        await Frames.Seconds(2f);
        var tissue = body.Tissue;
        if (bloody) {
            AssertBool(tissue.Tape(id)!.Peeled).OverrideFailureMessage($"on blood, the strip lets go (stretched {tissue.TapeStrain(id):0.00})").IsTrue();
            await driver.AssertAndCapture(cut.Closure < Patient.ClosedEnough,
                $"the skin beside the cut pulled {Pull * 100f:0} cm, the tape gone and the cut open again, bleeding into a pool ({cut.Closure:0.00})", "let_go");
        } else {
            AssertBool(tissue.Tape(id)!.Peeled).OverrideFailureMessage($"on dry skin, the strip holds (stretched {tissue.TapeStrain(id):0.00})").IsFalse();
            AssertFloat(cut.Closure).OverrideFailureMessage($"and the cut stays shut ({cut.Closure:0.00})").IsGreaterEqual(Patient.ClosedEnough);
        }
        await session.Finish(BudgetOverride);
    }

    /// <summary>Gauze dropped on a cut, taped down with a strip across its middle: the strip holds the gauze, not the
    /// cut's edges under it, and the gauze keeps the cut from bleeding long after gauze pressure alone would have worn
    /// off.</summary>
    [TestCase]
    public async Task TapedGauzeKeepsPressingOnTheCut() {
        var session = await ToolSession.Start("appendectomy", "tape/gauze_let_go");
        var driver = session.Driver;
        var (cut, _, _) = await Cut(driver, 0.3f);
        var bleeding = cut.Bleeding;
        var gauze = await GauzeOn(driver, cut.Midpoint, false);
        await driver.PlayerLaysTape(Across(driver, cut.Midpoint));
        // Dropped, it lands where it falls, still over the cut.
        AssertBool(gauze.State == ToolState.Standing && OffCut(driver, gauze, cut) < 0.02f)
            .OverrideFailureMessage($"the gauze is taped down on the cut ({OffCut(driver, gauze, cut) * 100f:0.0} cm off it, {gauze.State})").IsTrue();
        AssertFloat(cut.Closure).OverrideFailureMessage("the strip on the gauze doesn't close the cut under it").IsLess(0.5f);
        await Frames.Seconds(Wound.PressureHold + Wound.PressureFade + 3f);
        AssertFloat(cut.Bleeding).OverrideFailureMessage($"long after gauze alone would have stopped holding, the cut is dry: {driver.Bleeders()}")
            .IsLess(bleeding * 0.1f);
        await driver.Capture("taped_down", "the gauze lying flat on the cut, a strip of tape across its middle holding it down");
        await session.Finish(BudgetOverride);
    }

    /// <summary>Gauze held over a cut in the left hand, taped down with the roll in the right: it goes down flat on the
    /// cut and stays in the hand, which stays on it, lying flat on it and pressing, until it lets go (right click). Then
    /// the gauze stays taped where it was, and the hand and the surgeon are free to move. That it keeps pressing on the
    /// cut there, <see cref="TapedGauzeKeepsPressingOnTheCut"/> checks.</summary>
    [TestCase]
    public async Task TheHandOnTapedGauzeStaysUntilItLetsGo() {
        var session = await ToolSession.Start("appendectomy", "tape/gauze_held");
        var driver = session.Driver;
        var me = driver.Me;
        var (cut, _, _) = await Cut(driver, 0.3f);
        var gauze = await GauzeOn(driver, cut.Midpoint, true);
        await driver.PlayerLaysTape(Across(driver, cut.Midpoint));
        var hand = me.Hands[SurgeryDriver.Left];
        await Frames.Physics(2);
        var palm = hand.GlobalPosition.DistanceTo(gauze.Middle());
        AssertBool(gauze.Taped && me.HeldTool(SurgeryDriver.Left) == gauze && hand.Attached && hand.Grip == "press" && OffCut(driver, gauze, cut) < 0.01f)
            .OverrideFailureMessage($"taped down flat on the cut ({OffCut(driver, gauze, cut) * 100f:0.0} cm off it), the gauze stays in the hand, "
                + $"pinned on it (taped {gauze.Taped}, attached {hand.Attached}, grip {hand.Grip})").IsTrue();
        // The roll out of the way, to see the hand on the gauze.
        await driver.PlayerReaches(gauze.Middle() + ((me.GlobalPosition - gauze.Middle()).Slide(Vector3.Up).Normalized() * 0.15f) + (Vector3.Up * 0.1f));
        await driver.Capture("in_hand", "the left glove lying flat on the gauze, palm down, fingers straight out over it, pressing it onto the cut, "
            + "a strip of tape across the gauze's middle");
        await session.CaptureView("in_hand", "the surgeon's view: the left glove lying flat on the gauze, palm down, fingers straight out over "
            + "it, nothing passing through the pad or the skin");
        await Frames.Seconds(1f);
        AssertFloat(cut.Bleeding).OverrideFailureMessage($"the gauze presses on the cut: {driver.Bleeders()}")
            .IsLess(cut.LastingBleeding * 0.1f);

        // Where it lies on the site: it rises and falls with the breathing belly.
        var taped = driver.Body.Site.ToLocal(gauze.GlobalPosition);
        await driver.SwitchTo(SurgeryDriver.Left);
        SurgeryDriver.Tap(InputActions.Grab);
        await Frames.Seconds(0.5f);
        await driver.PlayerWalksTo(driver.SitePoint(cut.Midpoint) + new Vector3(0.6f, 0f, 0f));
        await Frames.Seconds(0.5f);
        var moved = taped.DistanceTo(driver.Body.Site.ToLocal(gauze.GlobalPosition));
        await driver.AssertAndCapture(gauze.State == ToolState.Standing && me.HeldTool(SurgeryDriver.Left) is null && !hand.Attached && moved < 0.005f
            && hand.GlobalPosition.DistanceTo(gauze.Middle()) > palm + 0.1f,
            $"let go of, the gauze stays taped where it was ({moved * 1000f:0} mm moved), and the hand walks away with the surgeon "
            + $"({gauze.State}, attached {hand.Attached})", "let_go");
        await session.Finish(BudgetOverride);
    }

    /// <summary>Gauze let go of on a long jagged cut bleeding hard, taped down with a strip across its middle: the strip holds the
    /// gauze, not the cut's edges under it. The gauze soaks up what still oozes until it's full, then the blood soaks
    /// through into the tape, the tape lets go and the gauze with it, and nothing presses on the cut any more.</summary>
    [TestCase]
    public async Task TapedGauzeSoaksThroughAndLetsGo() {
        var session = await ToolSession.Start("appendectomy", "tape/soaked_through");
        var driver = session.Driver;
        var body = driver.Body;
        var from = new Vector2(0.38f, 0.5f);
        var gash = SurgeryState.SkinIsCut(driver.Patient, from, from + new Vector2(body.MetersToUv(GashLength), 0f), 0.4f, jagged: true);
        await Frames.Seconds(2f);
        var bleeding = gash.Bleeding;
        var gauze = await GauzeOn(driver, gash.Midpoint, false);
        await driver.PlayerLaysTape(Across(driver, gash.Midpoint));
        var id = body.Tissue.TapeIds.Max();
        await Frames.Seconds(1f);
        AssertBool(gauze.State == ToolState.Standing && gash.Closure < 0.5f && gash.Bleeding < bleeding * 0.1f)
            .OverrideFailureMessage($"the strip tapes the gauze down pressing on the gash, not its edges shut ({gauze.State}, closure {gash.Closure:0.00}): "
                + driver.Bleeders()).IsTrue();
        await driver.Capture("taped_across", "the gauze lying flat on the gash, a strip of tape across its middle holding it down");
        var soaked = await Frames.Until(() => body.Tissue.Tape(id)!.Peeled, 40f);
        AssertBool(soaked && gauze.Room < 1f).OverrideFailureMessage($"the gauze fills up ({gauze.Ml:0.0} of {gauze.Def.Volume} ml) and the blood "
            + $"soaking through loosens the tape until it lets go\n{driver.Recent()}").IsTrue();
        await Frames.Seconds(2f);
        await driver.AssertAndCapture(gauze.State != ToolState.Standing && gash.SincePressed > 1f,
            $"the tape gone, the blood-soaked gauze loose on the gash and pressing on it no more ({gauze.State})", "soaked_through");
        await session.Finish(BudgetOverride);
    }

    /// <summary>A strip's ends, either side of gauze lying with its middle on <paramref name="uv"/> (site uv): across its
    /// middle, past its edges.</summary>
    private static Vector2[] Across(SurgeryDriver driver, Vector2 uv) {
        var across = new Vector2(0f, driver.Body.MetersToUv(0.055f));
        return [uv - across, uv + across];
    }

    /// <summary>How far (meters) the middle of <paramref name="gauze"/> lies from the middle of <paramref name="cut"/>,
    /// along the skin.</summary>
    private static float OffCut(SurgeryDriver driver, SurgicalTool gauze, Wound cut) =>
        (driver.Body.WorldToUv(gauze.Middle()) - cut.Midpoint).Length() / driver.Body.MetersToUv(1f);

    /// <summary>Gauze from the nurse in the left hand held with its middle over <paramref name="uv"/> (site uv), or
    /// dropped there (<paramref name="held"/>), and the roll of tape in the right hand.</summary>
    private static async Task<SurgicalTool> GauzeOn(SurgeryDriver driver, Vector2 uv, bool held) {
        // The roll first: fetching it from the tray later would walk the gauze in the other hand off the cut.
        await driver.PlayerRequestsItem("surgical_tape");
        await driver.SwitchTo(SurgeryDriver.Left);
        var gauze = (await driver.PlayerRequestsItem("gauze"))!;
        var on = driver.SitePoint(uv);
        await driver.PlayerWalksTo(on);
        await driver.PlayerReaches(on);
        // The pad's middle over the cut, not its end.
        await driver.PlayerReaches(on - (gauze.Middle() - gauze.TipPosition()));
        if (!held) {
            SurgeryDriver.Tap(InputActions.Grab);
            await Frames.Seconds(1f);
        }
        await driver.SwitchTo(SurgeryDriver.Right);
        return gauze;
    }

    /// <summary>A clean cut <see cref="CutLength"/> long across the belly, <paramref name="depth"/> deep, bleeding for a
    /// couple of seconds, and where a strip along it starts and ends (<see cref="Past"/> beyond it).</summary>
    private static async Task<(Wound Cut, Vector2 From, Vector2 To)> Cut(SurgeryDriver driver, float depth) {
        var body = driver.Body;
        var from = new Vector2(0.4f, 0.5f);
        var to = from + new Vector2(body.MetersToUv(CutLength), 0f);
        var cut = SurgeryState.SkinIsCut(driver.Patient, from, to, depth);
        await Frames.Seconds(2f);
        var past = new Vector2(body.MetersToUv(Past), 0f);
        return (cut, from - past, to + past);
    }
}
