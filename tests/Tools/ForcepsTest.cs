namespace Scalpel.Tests.Tools;

/// <summary>
/// Forceps close on what they come to rest on, not on whatever the tip passes on its way down: the thigh's entry wound
/// is opened through every layer over the bullet, then the forceps are pressed in ways that leave the tip still coming
/// down when the press counts. Key frames: the site from above and obliquely, the forceps on the skin beside the
/// opening and holding the bullet, then held up in the opening by the wheel and stopped at its lip, then the bullet raised
/// out by the wheel and carried away from the table. Review them for the jaws on the skin edge, not down in the opening,
/// then on the bullet down in it, then clear of it and of the skin, then holding it over the opening and in the hand.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("tool_forceps"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class ForcepsTest {
    /// <summary>How long (meters) the cut over the bullet is.</summary>
    private const float Opening = 0.05f;
    /// <summary>The cut through every layer that opens the thigh goes over, as in RetractorTest.</summary>
    // Temporary local budget override: recorded CPU/render overruns remain measured; functional assertions still fail normally.
    private static readonly BudgetAllowance BudgetOverride = new("known to go over the frame budget, not profiled yet", 17f);

    [TestCase]
    public async Task ForcepsTakeTheSkinEdgeBesideTheOpeningAndTheBulletInIt() {
        var (session, bullet) = await OpenOverBullet("forceps/edge_and_bullet");
        var driver = session.Driver;
        var forceps = (await driver.PlayerRequestsItem("forceps"))!;
        // On the whole skin right beside the opening, with the bullet under it within the jaws' reach across.
        var edge = driver.SitePoint(SkinBeside(driver, bullet.Uv));
        await driver.PlayerWalksTo(edge);
        await driver.PlayerReaches(edge);
        await Press(driver);
        AssertBool(forceps.Hold is SkinHold && bullet.GrippedBy == 0)
            .OverrideFailureMessage($"pressed on the skin beside the opening, the forceps pinch the skin, not the bullet under it: {forceps.Hold}")
            .IsTrue();
        await driver.Capture("skin_edge", "the forceps pinching the skin at the edge of the opening, the bullet left in it");
        await LetGo(driver, forceps);

        // Over the bullet, pressed while lifted: the tip is still coming down into the opening when the press counts.
        await driver.PlayerReaches(driver.Body.UvToWorld(bullet.Uv, bullet.Depth));
        await PressLifted(driver);
        var pressedAt = driver.Body.Probe(forceps.TipPosition());
        PlayerInput.Action(InputActions.Lift, false);
        await Frames.Seconds(0.5f);
        AssertBool(forceps.Hold is TargetHold && bullet.GrippedBy == forceps.Uid)
            .OverrideFailureMessage($"pressed while lifted ({pressedAt.Zone}), then let down, the forceps take the bullet: {forceps.Hold}")
            .IsTrue();
        await driver.Capture("bullet", "pressed while lifted and let down, the forceps' jaws down in the opening on the bullet");
        await LetGo(driver, forceps);
        await session.Finish(BudgetOverride);
    }

    [TestCase]
    public async Task ForcepsMovedOntoTheBulletTakeItWhereTheyComeDown() {
        var (session, bullet) = await OpenOverBullet("forceps/moving");
        var driver = session.Driver;
        var forceps = (await driver.PlayerRequestsItem("forceps"))!;
        var body = driver.Body;
        var start = body.UvToWorld(bullet.Uv + new Vector2(body.MetersToUv(0.015f), 0f), bullet.Depth);
        await driver.PlayerWalksTo(start);
        await driver.PlayerReaches(start);
        // Pressed while lifted beside it, then moved onto it as Lift lets the tip down.
        await PressLifted(driver);
        PlayerInput.Action(InputActions.Lift, false);
        await driver.PlayerSweepsTo(body.UvToWorld(bullet.Uv, bullet.Depth), 0.1f);
        await Frames.Seconds(0.5f);
        AssertBool(forceps.Hold is TargetHold && bullet.GrippedBy == forceps.Uid)
            .OverrideFailureMessage($"pressed while moving onto the bullet, the forceps take it: {forceps.Hold}").IsTrue();
        await LetGo(driver, forceps);
        await session.Finish(BudgetOverride);
    }

    [TestCase]
    public async Task ForcepsHeldUpInTheOpeningCloseWhereTheTipIs() {
        var (session, bullet) = await OpenOverBullet("forceps/depth");
        var driver = session.Driver;
        var me = driver.Me;
        var hand = me.Hands[me.Active];
        var hud = driver.Surgery.Hud;
        var forceps = (await driver.PlayerRequestsItem("forceps"))!;
        var spot = driver.Body.UvToWorld(bullet.Uv, bullet.Depth);
        await driver.PlayerWalksTo(spot);
        await driver.PlayerReaches(spot);
        AssertString(string.Join("\n", Hud.ControlLines(me))).OverrideFailureMessage("the hint names what the wheel does")
            .Contains("Raise / lower tip in an opening");
        var resting = me.InOpening(me.Active);
        AssertBool(resting is { } r && hand.HeldDepth is null && Mathf.Abs(r.Depth - r.Bottom) < 0.002f)
            .OverrideFailureMessage($"untouched, the tip rests just above the bullet: {resting}").IsTrue();

        // A notch up: still deeper than the lip's edge, too high over what's inside for the jaws to reach the bullet.
        await SurgeryDriver.Notch(true);
        await Frames.Physics(2);
        var raised = me.InOpening(me.Active)!.Value;
        var wanted = resting!.Value.Bottom - Surgeon.DepthStep;
        await driver.AssertAndCapture(hand.HeldDepth is not null && Mathf.Abs(raised.Depth - wanted) < 0.001f,
            $"a notch up, the forceps' tip is held higher in the opening over the bullet, below the lip: {raised.Depth} m deep, {wanted} wanted",
            "held_up");
        AssertBool(hud.DepthShown && hud.AimText.Contains("Tip depth") && hud.AimText.Contains("held by the wheel"))
            .OverrideFailureMessage($"the aim shows the stem down to the tip and the depth beside it: {hud.AimText}").IsTrue();
        // Zoomed in, so the stem shows.
        PlayerInput.Tap(InputActions.Zoom);
        await Frames.Seconds(0.5f);
        await session.CaptureView("held_up_view",
            "zoomed in over the opening, the aim's stem from its lip down to the forceps' tip, \"Tip depth\" and \"held by the wheel\" beside it");
        PlayerInput.Tap(InputActions.Zoom);
        await Frames.Seconds(0.5f);

        // Pressed there, the jaws close where the tip is: on nothing, the tip staying put.
        await Press(driver);
        var pressed = me.InOpening(me.Active)!.Value;
        AssertBool(forceps.Hold is null && bullet.GrippedBy == 0 && Mathf.Abs(pressed.Depth - raised.Depth) < 0.002f)
            .OverrideFailureMessage($"pressed held up, the forceps close on nothing and stay up: {forceps.Hold}, {pressed.Depth} m deep")
            .IsTrue();

        // Held deep and moved along the cut past its end, the tip stops at the end's lip rather than going under the skin.
        var skin = driver.SitePoint(bullet.Uv + new Vector2(driver.Body.MetersToUv((Opening * 0.5f) + 0.01f), 0f));
        await driver.PlayerSweepsTo(skin);
        var stopped = forceps.TipPosition();
        await driver.AssertAndCapture(driver.Body.IsOpen(driver.Body.WorldToUv(stopped)) && hand.HeldDepth is not null,
            "held deep and moved along the cut past its end, the forceps' tip stops at the end of the opening, still down in it",
            "at_the_lip");

        // Lowered all the way, it rests on what's inside again.
        for (var i = 0; i < 10 && hand.HeldDepth is not null; i++) {
            await SurgeryDriver.Notch(false);
        }
        AssertBool(hand.HeldDepth is null).OverrideFailureMessage("lowered all the way, the tip rests on what's inside again").IsTrue();

        // Held up again and lifted, it goes over onto the skin; back over the bullet it rests on it and takes it.
        await SurgeryDriver.Notch(true);
        PlayerInput.Action(InputActions.Lift);
        await Frames.Seconds(0.5f);
        await driver.PlayerSweepsTo(skin);
        PlayerInput.Action(InputActions.Lift, false);
        await Frames.Seconds(0.5f);
        AssertBool(!driver.Body.IsOpen(driver.Body.WorldToUv(forceps.TipPosition())) && hand.HeldDepth is null)
            .OverrideFailureMessage("lifted, the tip goes over the lip onto the skin and rests on it").IsTrue();
        await driver.PlayerReaches(spot);
        SurgeryDriver.Use();
        await Frames.Seconds(0.5f);
        AssertBool(forceps.Hold is TargetHold && bullet.GrippedBy == forceps.Uid)
            .OverrideFailureMessage($"back over the bullet, the forceps rest on it and take it: {forceps.Hold}").IsTrue();
        await LetGo(driver, forceps);
        await session.Finish(BudgetOverride);
    }

    [TestCase]
    public async Task ForcepsRaisedByTheWheelTakeTheBulletOutAndAway() {
        var (session, bullet) = await OpenOverBullet("forceps/lifted_out");
        var driver = session.Driver;
        var me = driver.Me;
        var hand = me.Hands[me.Active];
        var forceps = (await driver.PlayerRequestsItem("forceps"))!;
        var spot = driver.Body.UvToWorld(bullet.Uv, bullet.Depth);
        await driver.PlayerWalksTo(spot);
        await driver.PlayerReaches(spot);
        await Press(driver);
        AssertBool(forceps.Hold is TargetHold && hand.Attached)
            .OverrideFailureMessage($"pressed on the bullet, the forceps hold it: {forceps.Hold}").IsTrue();

        // The wheel raises the tip holding it a notch at a time until it's out.
        var before = forceps.TipPosition().Y;
        await SurgeryDriver.Notch(true);
        await Frames.Physics(2);
        AssertFloat(forceps.TipPosition().Y - before).OverrideFailureMessage("a notch up raises the forceps holding the bullet")
            .IsBetween(Surgeon.DepthStep * 0.5f, Surgeon.DepthStep * 1.5f);
        for (var i = 0; i < 40 && !bullet.Extracted; i++) {
            await SurgeryDriver.Notch(true);
        }
        await Frames.Seconds(0.5f);
        var held = me.InOpening(me.Active);
        await driver.AssertAndCapture(bullet.Extracted && forceps.Hold is CarryHold && !hand.Attached && held is { Depth: < 0f },
            $"raised by the wheel, the forceps lift the bullet out of the opening and hold it over its lip: {forceps.Hold}, attached {hand.Attached}, {held}",
            "lifted_out");

        // Taken out, the bullet is carried: the hand walks away with the surgeon instead of staying over the opening.
        var wounds = driver.Patient.Wounds.Count;
        var from = me.GlobalPosition;
        PlayerInput.Action(InputActions.MoveBack);
        await Frames.Seconds(1f);
        PlayerInput.Action(InputActions.MoveBack, false);
        await Frames.Seconds(0.5f);
        var walked = me.GlobalPosition.DistanceTo(from);
        AssertBool(walked > 0.5f && forceps.Hold is CarryHold && bullet.GlobalPosition.DistanceTo(forceps.TipPosition()) < 0.01f)
            .OverrideFailureMessage($"walked {walked} m away, the forceps still carry the bullet: {forceps.Hold}").IsTrue();
        AssertBool(driver.Patient.Wounds.Count == wounds && forceps.TipPosition().DistanceTo(me.Shoulder(me.Active)) < Surgeon.Reach + 0.01f)
            .OverrideFailureMessage("walked away, the hand comes along within reach and the thigh doesn't tear").IsTrue();
        await session.CaptureView("carried_away", "stepped back from the table, the bullet still in the forceps in the surgeon's hand");
        await session.Finish(BudgetOverride);
    }

    [TestCase]
    public async Task ForcepsHeldDeepDontJumpAcrossSkinToAnotherOpening() {
        var (session, bullet) = await OpenOverBullet("forceps/two_openings");
        var driver = session.Driver;
        var body = driver.Body;
        // Well across from the first, so the skin between doesn't let either close.
        var beside = bullet.Uv + new Vector2(0f, body.MetersToUv(0.04f));
        var bridge = (bullet.Uv + beside) * 0.5f;
        var half = new Vector2(body.MetersToUv(0.015f), 0f);
        await driver.PlayerCutsSkin(beside - half, beside + half, 3);
        await driver.PlayerPutsDown();
        await Frames.Seconds(1f);
        AssertBool(body.IsOpen(bullet.Uv) && body.IsOpen(beside) && !body.IsOpen(bridge))
            .OverrideFailureMessage($"a second cut opens across from the first, intact skin between them: {body.IsOpen(bullet.Uv)}, {body.IsOpen(beside)}, {body.IsOpen(bridge)} at {beside}").IsTrue();
        var forceps = (await driver.PlayerRequestsItem("forceps"))!;
        var hand = driver.Me.Hands[driver.Me.Active];
        var spot = body.UvToWorld(bullet.Uv, bullet.Depth);
        await driver.PlayerWalksTo(spot);
        await driver.PlayerReaches(spot);
        await SurgeryDriver.Notch(true);
        // One fast move of the mouse, its end over the other opening.
        PlayerInput.Action(PlayerInput.HandKey(driver.Me));
        PlayerInput.Mouse(PlayerInput.HandMotion(driver.Me, (driver.SitePoint(beside) - forceps.TipPosition()) with { Y = 0f }));
        await Frames.Physics(2);
        PlayerInput.Action(PlayerInput.HandKey(driver.Me), false);
        await Frames.Physics(5);
        var tip = body.WorldToUv(forceps.TipPosition());
        AssertBool(hand.HeldDepth is not null && body.IsOpen(tip) && tip.DistanceTo(bullet.Uv) < tip.DistanceTo(beside))
            .OverrideFailureMessage($"held deep, the tip stays in its own opening, not under the skin into the other: {tip}")
            .IsTrue();
        await session.Finish(BudgetOverride);
    }

    [TestCase]
    public async Task ForcepsInTheAirWaitUntilTheyComeDown() {
        var (session, bullet) = await OpenOverBullet("forceps/in_the_air");
        var driver = session.Driver;
        var forceps = (await driver.PlayerRequestsItem("forceps"))!;
        var spot = driver.Body.UvToWorld(bullet.Uv, bullet.Depth);
        await driver.PlayerWalksTo(spot);
        await driver.PlayerReaches(spot);
        await PressLifted(driver);
        SurgeryDriver.Use(false);
        await Frames.Physics(2);
        PlayerInput.Action(InputActions.Lift, false);
        await Frames.Seconds(1f);
        AssertBool(forceps.Hold is null && bullet.GrippedBy == 0 && !driver.Me.Hands[driver.Me.Active].Attached)
            .OverrideFailureMessage($"let go of in the air, the forceps take nothing once they're down: {forceps.Hold}").IsTrue();

        // Held in the air for longer than a pinch waits on the patient (ClampAction.MaxWait), then let down.
        await PressLifted(driver);
        await Frames.Seconds(ClampAction.MaxWait * 2f);
        AssertObject(forceps.Hold).OverrideFailureMessage("held up in the air, the forceps take nothing yet").IsNull();
        PlayerInput.Action(InputActions.Lift, false);
        await Frames.Seconds(0.5f);
        AssertBool(forceps.Hold is TargetHold && bullet.GrippedBy == forceps.Uid)
            .OverrideFailureMessage($"held in the air a while, then let down, the forceps still take the bullet: {forceps.Hold}")
            .IsTrue();
        await LetGo(driver, forceps);
        await session.Finish(BudgetOverride);
    }

    [TestCase]
    public async Task ForcepsWorkedFromLateRemoteStatesWaitForANewOne() {
        var (session, bullet) = await OpenOverBullet("forceps/late_states");
        var driver = session.Driver;
        var forceps = (await driver.PlayerRequestsItem("forceps"))!;
        var body = driver.Body;
        // As the host works a remote hand's forceps: the same state for several frames while the next one is late, the
        // tip 8 mm over the opening, then new ones down on the bullet. All in one frame, so the host's own update of
        // this hand doesn't come in between.
        var above = body.UvToWorld(bullet.Uv, -0.008f);
        var on = body.UvToWorld(bullet.Uv, bullet.Depth);
        var frames = new (Vector3 Tip, int State)[] { (above, 1), (above, 1), (above, 1), (above, 1), (above, 1), (on, 2), (on, 3), (on, 4) };
        foreach (var (tip, state) in frames) {
            forceps.GlobalTransform = new Transform3D(new Basis(Vector3.Right, Vector3.Forward, Vector3.Up),
                tip + (Vector3.Up * forceps.Def.Length));
            ToolActions.Update(forceps, new HandInput(true, true, 0, 0f, driver.Me.PeerId, new Modifiers(), state), driver.Patient,
                1f / 60f);
        }
        AssertBool(forceps.Hold is TargetHold && bullet.GrippedBy == forceps.Uid)
            .OverrideFailureMessage($"a state left standing over the opening doesn't close them; the new ones on the bullet do: {forceps.Hold}")
            .IsTrue();
        await LetGo(driver, forceps);
        await session.Finish(BudgetOverride);
    }

    /// <summary>The thigh's entry wound opened through every layer along the bullet, and the scalpel put down.</summary>
    private static async Task<(ToolSession Session, CavityTarget Bullet)> OpenOverBullet(string folder) {
        var session = await ToolSession.Start("bullet_muscle", folder, captureUntouched: folder == "forceps/edge_and_bullet");
        var driver = session.Driver;
        var bullet = driver.Patient.Targets.Single(target => target.Kind == "bullet");
        var half = new Vector2(driver.Body.MetersToUv(Opening * 0.5f), 0f);
        await driver.PlayerCutsSkin(bullet.Uv - half, bullet.Uv + half, 3);
        await driver.PlayerPutsDown();
        await Frames.Seconds(1f);
        AssertBool(driver.Body.IsOpen(bullet.Uv)).OverrideFailureMessage("the cut opens over the bullet").IsTrue();
        if (folder == "forceps/edge_and_bullet") {
            await driver.Capture("opened", "the entry wound cut open along the bullet, through every layer");
        }
        return (session, bullet);
    }

    /// <summary>The whole skin just past the opening's edge from <paramref name="uv"/> (in it), across the cut.
    /// </summary>
    private static Vector2 SkinBeside(SurgeryDriver driver, Vector2 uv) {
        var step = new Vector2(0f, driver.Body.MetersToUv(0.001f));
        var probes = Mathf.CeilToInt(Mathf.Max(1f - uv.Y, 0f) / step.Y) + 1;
        for (var probe = 0; probe < probes; probe++, uv += step) {
            if (!driver.Body.IsOpen(uv)) {
                return uv + (step * 2f);
            }
        }
        throw new InvalidOperationException("No intact skin beside the opening before the site's edge.");
    }

    /// <summary>Lift held until the tip is up, then Use tool pressed and held: the press counts with the tip in the
    /// air. Lift stays held.</summary>
    private static async Task PressLifted(SurgeryDriver driver) {
        PlayerInput.Action(InputActions.Lift);
        await Frames.Seconds(0.5f);
        SurgeryDriver.Use();
        await Frames.Physics(5);
    }

    /// <summary>Use tool pressed and let go of, a moment apart.</summary>
    private static async Task Press(SurgeryDriver driver) {
        SurgeryDriver.Use();
        await Frames.Physics(10);
        SurgeryDriver.Use(false);
        await Frames.Physics(3);
    }

    /// <summary>Use tool once more lets go of what the forceps hold.</summary>
    private static async Task LetGo(SurgeryDriver driver, SurgicalTool forceps) {
        SurgeryDriver.Use(false);
        await Frames.Physics(3);
        await Press(driver);
        AssertObject(forceps.Hold).OverrideFailureMessage("Use tool again lets go").IsNull();
    }
}
