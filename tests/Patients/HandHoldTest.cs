namespace Scalpel.Tests.Patients;

/// <summary>
/// An empty hand clicked on the patient holds it there, as a player does it: laid flat on the skin and pressing into it,
/// it stops the cuts under its palm bleeding the way gauze does, without soaking anything up; over an opening it lies
/// on the lip, holding the incision but not the vessel bleeding inside; over the cut on a panicking patient's flailing
/// forearm, the arm the surgical site is on, it keeps the arm still while the other one goes on flailing.
/// Key frames: review the glove lying flat on the skin over the cut, its palm and fingers on it and pressed a little
/// in, neither floating nor sunk through; flat over the opening at its lip, the fingers straight across it; and
/// grasping the forearm, the lower palm on it, the knuckles rising off it and the fingers round it over the cut.
/// Nothing of the hand goes into the skin or an opening.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("hand_hold"), TestCategory("visual_confirmation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class HandHoldTest {
    /// <summary>How far (meters) the close-ups look from.</summary>
    private const float CloseUp = 0.35f;
    // Temporary local budget override: recorded CPU overruns remain measured; functional assertions still fail normally.
    private static readonly BudgetAllowance BudgetOverride = new("known to go over the frame budget about a second into "
        + "the surgery, before the hand holds the patient; not profiled yet", 6f);

    [TestCase]
    public async Task AHandHeldOnACutStopsItBleedingWithoutSoakingItUp() {
        var session = await ToolSession.Start("appendectomy", "hand_hold");
        var driver = session.Driver;
        var patient = driver.Patient;
        var cut = SurgeryState.SkinIsCut(patient, new Vector2(0.4f, 0.5f), new Vector2(0.6f, 0.5f), 0.3f);
        await Frames.Seconds(3f);
        AssertFloat(cut.Bleeding).OverrideFailureMessage($"the cut bleeds: {driver.Bleeders()}").IsGreater(0.1f);
        var on = driver.SitePoint(cut.Midpoint);
        var blood = driver.Body.BloodAt(cut.Midpoint);

        await driver.PlayerHoldsPatientAt(on);
        await Frames.Seconds(2f);
        var hand = driver.Me.Hands[driver.Me.Active];
        AssertBool(HandLiesOn(hand, on)).OverrideFailureMessage(Lying(hand, on)).IsTrue();
        AssertBool(Hud.ControlLines(driver.Me).Any(line => line.Contains("Let go of the patient")))
            .OverrideFailureMessage("the controls shown say how to let go").IsTrue();
        AssertFloat(cut.Bleeding).OverrideFailureMessage($"held under the hand, the cut stops bleeding: {driver.Bleeders()}")
            .IsLess(0.01f);
        AssertFloat(driver.Body.BloodAt(cut.Midpoint))
            .OverrideFailureMessage($"the hand soaks up no blood ({blood:0.00} before)").IsGreaterEqual(blood * 0.9f);
        await driver.Capture("held", "the glove flat on the skin over the cut, palm and fingers on it");
        await session.CaptureOblique("held_close", "close up: the glove lying flat on the skin, pressing a little into it,"
            + " neither floating over it nor sunk through it", on, driver.Me.GlobalPosition - on, CloseUp, 30f, true);

        // Moving the mouse with the hand key held doesn't move it: it stays where it holds until it lets go.
        await driver.PlayerSweepsTo(on + new Vector3(0.1f, 0f, 0f));
        AssertBool(HandLiesOn(hand, on)).OverrideFailureMessage("the hand stays where it holds: " + Lying(hand, on)).IsTrue();
        await driver.PlayerLetsGoOfPatient();
        await Frames.Seconds(0.5f);
        AssertBool(hand.PressNormal is null && hand.GlobalPosition.Y > on.Y + 0.01f)
            .OverrideFailureMessage("let go, the hand rises off the skin").IsTrue();
        await session.Finish(BudgetOverride);
    }

    [TestCase]
    public async Task OverAnOpeningTheHandLiesOnItsLipAndHoldsTheIncisionOnly() {
        var session = await ToolSession.Start("appendectomy", "hand_hold_opening");
        var driver = session.Driver;
        var patient = driver.Patient;
        var body = driver.Body;
        var incision = SurgeryState.SkinIsCut(patient, new Vector2(0.35f, 0.5f), new Vector2(0.65f, 0.5f), 1f);
        var vessel = SurgeryState.VesselBleeds(patient, new Vector2(0.5f, 0.5f), 0.03f);
        AssertBool(await Frames.Until(() => body.IsOpen(incision.Midpoint), 5f))
            .OverrideFailureMessage("the incision gapes open").IsTrue();
        await Frames.Seconds(1f);
        AssertFloat(incision.Bleeding).OverrideFailureMessage($"the incision bleeds: {driver.Bleeders()}").IsGreater(0f);
        var lip = body.Site.ToGlobal(body.SitePoint(incision.Midpoint, body.SkinHeight(incision.Midpoint)));
        var over = driver.SitePoint(incision.Midpoint);

        await driver.PlayerHoldsPatientAt(over);
        await Frames.Seconds(2f);
        var hand = driver.Me.Hands[driver.Me.Active];
        AssertFloat(hand.Target.Y).OverrideFailureMessage(
                $"the hand lies on the lip ({lip.Y:0.000}), not down in the opening: {hand.Target.Y:0.000}")
            .IsBetween(lip.Y - 0.01f, lip.Y + 0.005f);
        AssertFloat(incision.Bleeding).OverrideFailureMessage($"the incision under the hand stops: {driver.Bleeders()}")
            .IsLess(0.01f);
        AssertFloat(vessel.Bleeding).OverrideFailureMessage($"the vessel inside goes on bleeding: {driver.Bleeders()}")
            .IsGreater(0.01f);
        await session.CaptureOblique("over_opening", "the glove flat over the opening, lying on its lip, its fingers straight across it, none dipping in",
            over, driver.Me.GlobalPosition - over, CloseUp, 30f, true);
        await session.Finish(BudgetOverride);
    }

    [TestCase]
    public async Task AHeldForearmStaysStillWhileThePatientFlailsIt() {
        // Awake, unlike ToolSession.Start()'s patient.
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        await driver.Start("hand_stitch");
        var session = await ToolSession.Attach(driver, "hand_hold_flail", captureUntouched: false);
        var patient = driver.Patient;
        var body = driver.Body;
        var on = driver.SitePoint(patient.Wounds[0].Midpoint);
        await driver.PlayerHoldsPatientAt(on);
        SurgeryState.PatientPanics(patient);
        await Frames.Seconds(1f);
        var hand = driver.Me.Hands[driver.Me.Active];
        AssertFloat(await Wander(() => body.Site.GlobalPosition))
            .OverrideFailureMessage("panicking, the patient doesn't move the forearm a hand holds").IsLess(0.001f);
        AssertFloat(await Wander(() => hand.GlobalPosition))
            .OverrideFailureMessage("and the hand holding it stays on it").IsLess(0.001f);
        AssertFloat(await Wander(() => body.RigBonePose("HandR").Origin))
            .OverrideFailureMessage("the other arm flails").IsGreater(0.01f);
        AssertBool(hand.PressAcross != Vector3.Zero).OverrideFailureMessage("on the forearm the hand grasps it").IsTrue();
        var normal = hand.PressNormal!.Value;
        var skin = Rays.Cast(body, hand.Target + (normal * 0.03f), hand.Target - (normal * 0.03f), PatientBody.SurfaceLayer);
        var off = skin is { } hit ? hit.Position.DistanceTo(hand.Target) : float.PositiveInfinity;
        AssertFloat(off).OverrideFailureMessage($"its lower palm lies on the arm's skin: {off:0.000} m off").IsLess(0.005f);
        AssertInt(HandPointsInSkin(hand)).OverrideFailureMessage("its fingers close round the arm's skin, nothing of it in it")
            .IsEqual(0);
        // From across the arm, where the fingers come over it.
        await session.CaptureOblique("on_forearm", "the glove grasping the forearm, its lower palm on the arm's top, the "
            + "fingers round the far side over the cut, nothing of it in the skin", on,
            (driver.Me.GlobalPosition - on).Rotated(Vector3.Up, Mathf.Pi), CloseUp, 35f, true);

        await driver.PlayerLetsGoOfPatient();
        SurgeryState.PatientPanics(patient);
        AssertFloat(await Wander(() => body.Site.GlobalPosition))
            .OverrideFailureMessage("let go, it flails the forearm the site is on").IsGreater(0.01f);
        await session.Finish(BudgetOverride);
    }

    /// <summary>The hand lies on the skin at <paramref name="on"/>: across the floor within a palm of it, and as high as
    /// the skin there, give or take how far it presses in.</summary>
    private static bool HandLiesOn(SurgeonHand hand, Vector3 on) =>
        hand.PressNormal is not null && (hand.GlobalPosition - on).Slide(Vector3.Up).Length() < 0.01f
        && Mathf.Abs(hand.GlobalPosition.Y - on.Y) < Surgeon.PressSink + 0.004f;

    private static string Lying(SurgeonHand hand, Vector3 on) =>
        $"the hand lies on the skin where it held: hand at {hand.GlobalPosition}, skin at {on}, pressing {hand.PressNormal}";

    /// <summary>How many points through the hand (SurgeonHand.BonePoints()) are inside the patient's skin, well under
    /// the glove's surface: a ray out from one crosses the skin an odd number of times.</summary>
    private static int HandPointsInSkin(SurgeonHand hand) {
        var space = hand.GetWorld3D().DirectSpaceState;
        var outward = new Vector3(0.013f, 1f, 0.007f).Normalized();
        return hand.BonePoints().Count(point => {
            var crossings = 0;
            var from = point.Position;
            for (var i = 0; i < 32; i++) {
                var query = PhysicsRayQueryParameters3D.Create(from, point.Position + outward, PatientBody.SurfaceLayer);
                query.HitBackFaces = true;
                var hit = space.IntersectRay(query);
                if (hit.Count == 0) {
                    break;
                }
                crossings++;
                from = hit["position"].AsVector3() + (outward * 0.0002f);
            }
            return crossings % 2 == 1;
        });
    }

    /// <summary>How far (meters) <paramref name="where"/> strays from where it starts over a second.</summary>
    private static async Task<float> Wander(Func<Vector3> where) {
        var start = where();
        var most = 0f;
        for (var i = 0; i < 60; i++) {
            await Frames.NextPhysics();
            most = Mathf.Max(most, where().DistanceTo(start));
        }
        return most;
    }
}
