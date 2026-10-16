namespace Scalpel.Tests.Tools;

/// <summary>
/// Surgical tape as a player uses it: clicked down point by point like the needle's holes, the strip running from its
/// last point to the roll, stuck flat along the skin between points and cut off with a hold. It holds a shallow cut's
/// edges shut, peels off a cut into the fat it can't pull shut, lets go when the skin beside it is pulled once it's on
/// blood, and holds gauze it goes over pressing on a cut. It can't close the hand stitch laceration.
/// Key frames: review the strip running up to the roll from its first point, lying flat on the skin over the shut cut
/// (no gap showing under it, no tape sunk into the skin or floating off it), the deep cut gaping with no tape on it, the
/// cut pulled open after the tape let go, and the gauze lying flat under a strip beside the cut.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("tool_surgical_tape"), TestCategory("tissue_modification"), TestCategory("visual_confirmation")]
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
        await driver.PlayerRequestsItem("surgical_tape");
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
                $"the skin beside the cut pulled {Pull * 100f:0} cm, the tape gone and the cut open again ({cut.Closure:0.00})", "let_go");
        } else {
            AssertBool(tissue.Tape(id)!.Peeled).OverrideFailureMessage($"on dry skin, the strip holds (stretched {tissue.TapeStrain(id):0.00})").IsFalse();
            AssertFloat(cut.Closure).OverrideFailureMessage($"and the cut stays shut ({cut.Closure:0.00})").IsGreaterEqual(Patient.ClosedEnough);
        }
        await session.Finish(BudgetOverride);
    }

    /// <summary>Gauze pressed on a cut, held there or let go of, taped down with a strip beside the cut: it stays, and
    /// keeps the cut from bleeding long after gauze pressure alone would have worn off.</summary>
    [TestCase(true)]
    [TestCase(false)]
    public async Task TapedGauzeKeepsPressingOnTheCut(bool held) {
        var session = await ToolSession.Start("appendectomy", $"tape/gauze_{(held ? "held" : "let_go")}");
        var driver = session.Driver;
        var me = driver.Me;
        var body = driver.Body;
        var (cut, _, _) = await Cut(driver, 0.3f);
        var bleeding = cut.Bleeding;
        // The roll first: fetching it from the tray later would walk the gauze in the other hand off the cut.
        await driver.PlayerRequestsItem("surgical_tape");
        await driver.SwitchTo(SurgeryDriver.Left);
        var gauze = (await driver.PlayerRequestsItem("gauze"))!;
        var on = driver.SitePoint(cut.Midpoint);
        await driver.PlayerWalksTo(on);
        await driver.PlayerReaches(on);
        // The pad's middle on the cut, not its end.
        await driver.PlayerReaches(on - (gauze.Middle() - gauze.TipPosition()));
        await driver.SetLevel(3);
        SurgeryDriver.Use();
        await Frames.Seconds(1f);
        if (!held) {
            SurgeryDriver.Tap(InputActions.Grab);
            await Frames.Seconds(1f);
        }
        SurgeryDriver.Use(false);
        await driver.SwitchTo(SurgeryDriver.Right);
        // Along the cut, beside it: the strip holds the gauze down, not the cut's edges.
        var beside = new Vector2(0f, body.MetersToUv(0.025f));
        var along = new Vector2(body.MetersToUv(0.035f), 0f);
        await driver.PlayerLaysTape([cut.Midpoint + beside - along, cut.Midpoint + beside + along]);
        var onCut = (body.WorldToUv(gauze.Middle()) - cut.Midpoint).Length() / body.MetersToUv(1f);
        AssertBool(gauze.State == ToolState.Standing && me.HeldTool(SurgeryDriver.Left) is null && onCut < 0.01f)
            .OverrideFailureMessage($"the gauze is taped down on the cut ({onCut * 100f:0.0} cm off it), out of the hand ({gauze.State})").IsTrue();
        AssertFloat(cut.Closure).OverrideFailureMessage("the strip beside the cut doesn't close it").IsLess(0.5f);
        await Frames.Seconds(Wound.PressureHold + Wound.PressureFade + 3f);
        AssertFloat(cut.Bleeding).OverrideFailureMessage($"long after gauze alone would have stopped holding, the cut is dry: {driver.Bleeders()}")
            .IsLess(bleeding * 0.1f);
        await driver.Capture("taped_down", "the gauze lying flat on the cut, a strip of tape across it beside the cut holding it down, the strip riding over the gauze");
        await session.Finish(BudgetOverride);
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
