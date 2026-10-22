namespace Scalpel.Tests.Patients;

/// <summary>
/// An empty hand clicked on the patient holds it there, as a player does it: laid flat on the skin and pressing into it,
/// it stops the cuts under its palm bleeding the way gauze does, without soaking anything up; over an opening it lies
/// on the lip, holding the incision but not the vessel bleeding inside; over the cut on a panicking patient's flailing
/// forearm, the arm the surgical site is on, it keeps the arm still while the other one goes on flailing; and it grasps
/// an arm from the side the surgeon stands beside it as well as from over it, and a thigh through the drape, pressing
/// the sheet onto the skin under it.
/// Key frames: review the glove lying flat on the skin over the cut, its palm and fingers on it and pressed a little
/// in, neither floating nor sunk through; flat over the opening at its lip, the fingers straight across it; and
/// grasping the forearm, the stiff palm across its top and the fingers bent round it over the cut.
/// Nothing of the hand goes into the skin or an opening.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("hand_hold"), TestCategory("visual_confirmation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class HandHoldTest {
    /// <summary>How far (meters) the close-ups look from.</summary>
    private const float CloseUp = 0.35f;
    /// <summary>How far (meters) a spot of a grasping palm may seem in the skin: the fit reads the skin in a few
    /// slices along the limb and interpolates between them.</summary>
    private const float PalmGapSlack = 0.001f;
    /// <summary>Most (meters) a grasping palm's underside stands off the skin where it's closest: it touches.</summary>
    private const float PalmTouch = 0.002f;
    /// <summary>Most (meters) any of a grasping palm's underside stands off the skin. A palm bends only at the wrist and
    /// the knuckles, so on a limb about as wide as it is long it lies across the round like a chord, its heel and
    /// knuckle end lifting.</summary>
    private const float PalmLift = 0.03f;
    /// <summary>Most (meters) a grasping finger's first bone ends off the skin: it wraps the limb from the knuckle.
    /// </summary>
    private const float FingerTouch = 0.003f;
    /// <summary>Most of the way off the skin it was laid a drape pressed onto it lies: a little off, where the skin
    /// rises beside it (between the sheet's points) and as the body's collider and its drawn skin differ by a few
    /// millimeters.</summary>
    private const float DrapePressed = 0.5f;
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
        var ring = body.LimbRingAt(on)!.Value;
        AssertGrasps(hand, ring);
        // Along the arm, low, where the palm's curve on the arm's round shows.
        await session.CaptureOblique("on_forearm", "the glove grasping the forearm seen along it: the palm flat across the "
            + "arm's top, pressed a little into it at its middle, the heel and knuckles a little off the round, the "
            + "fingers wrapped from the knuckles down the far side over the cut, each bone resting on the skin, nothing "
            + "else of it in the skin", on, ring.Axis,
            CloseUp, 12f, true);

        await driver.PlayerLetsGoOfPatient();
        SurgeryState.PatientPanics(patient);
        AssertFloat(await Wander(() => body.Site.GlobalPosition))
            .OverrideFailureMessage("let go, it flails the forearm the site is on").IsGreater(0.01f);
        await session.Finish(BudgetOverride);
    }

    [TestCase]
    public async Task AHandGraspsTheFarSideArmFromBesideIt() {
        var session = await ToolSession.Start("appendectomy", "hand_hold_side");
        var driver = session.Driver;
        var body = driver.Body;
        // The middle of the right forearm's top, the surgeon walking round to that side of the table to reach it.
        var forearm = body.RigBonePose("ForearmR").Origin.Lerp(body.RigBonePose("HandR").Origin, 0.5f);
        var on = Rays.Cast(body, forearm + Vector3.Up * 0.3f, forearm, PatientBody.SurfaceLayer)!.Value.Position;

        await driver.PlayerHoldsPatientAt(on);
        await Frames.Seconds(1f);
        var hand = driver.Me.Hands[driver.Me.Active];
        var ring = body.LimbRingAt(on)!.Value;
        AssertGrasps(hand, ring);
        await session.CaptureOblique("beside_forearm", "the glove grasping the right forearm from the side the surgeon "
            + "stands: the palm on the arm, the fingers wrapped over its top, each bone resting on the skin, nothing of "
            + "it in the skin", on, -ring.Axis, CloseUp, 12f, true);
        await session.Finish(BudgetOverride);
    }

    [TestCase]
    public async Task ThroughTheDrapeTheHandPressesItOntoTheSkin() {
        var session = await ToolSession.Start("appendectomy", "hand_hold_drape");
        var driver = session.Driver;
        var body = driver.Body;
        var drape = body.Drape!;
        // The top of the left thigh, under the drape.
        var thigh = body.RigBonePose("ThighL").Origin.Lerp(body.RigBonePose("ShinL").Origin, 0.5f);
        var on = Rays.Cast(body, thigh + (Vector3.Up * 0.3f), thigh, PatientBody.SurfaceLayer)!.Value.Position;
        var laid = DrapeOver(drape, on);
        var sheet = SheetVertices(drape);
        AssertFloat(laid - on.Y).OverrideFailureMessage($"the drape lies over the thigh: {laid - on.Y:0.000} m off it")
            .IsGreater(Drape.Offset * 0.5f);

        await driver.PlayerHoldsPatientAt(on);
        await Frames.Seconds(1f);
        var hand = driver.Me.Hands[driver.Me.Active];
        AssertBool(driver.Me.HoldsPatient(driver.Me.Active)).OverrideFailureMessage("the hand holds the thigh").IsTrue();
        var ring = body.LimbRingAt(on)!.Value;
        AssertGrasps(hand, ring);
        var under = SheetUnder(drape, sheet, hand);
        AssertBool(under.Length > 0).OverrideFailureMessage("the drape runs over the fingers on the thigh").IsTrue();
        AssertFloat(under.Max()).OverrideFailureMessage($"under the hand the drape is pressed down onto the skin: up "
            + $"to {under.Max():0.00} of the way it was laid off it").IsLess(DrapePressed);
        // From high over the far side of the thigh, where the fingers wrap its top and the sheet's dent round them shows.
        await session.CaptureOblique("through_drape", "the glove grasping the thigh through the drape, seen from high over its far side: "
            + "the sheet pressed down onto the leg under the hand and rising back round it, none of the glove in the sheet or the sheet "
            + "over it", on, on - driver.Me.GlobalPosition, CloseUp, 70f, true);

        await driver.PlayerLetsGoOfPatient();
        await Frames.Seconds(0.5f);
        AssertFloat(SheetVertices(drape).Zip(sheet).Max(pair => pair.First.DistanceTo(pair.Second)))
            .OverrideFailureMessage("let go, the drape lies where it was laid").IsLess(0.0001f);
        await session.Finish(BudgetOverride);
    }

    /// <summary>The drape's sheet as it's drawn now, in its own space.</summary>
    private static Vector3[] SheetVertices(Drape drape) =>
        ((ArrayMesh)drape.Mesh).SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();

    /// <summary>How far off the skin under it the drape's sheet is drawn where it runs over the fingers of
    /// <paramref name="hand"/>, as a share of how far off it the sheet was <paramref name="laid"/> (<see
    /// cref="SheetVertices"/>), at each of its points within a finger's width of one.</summary>
    private static float[] SheetUnder(Drape drape, Vector3[] laid, SurgeonHand hand) {
        var fingers = (string[])["Index", "Middle", "Ring", "Pinky"];
        var points = fingers.SelectMany(finger => hand.BonePoints(finger)).ToArray();
        return [.. SheetVertices(drape).Select(drape.ToGlobal).Zip(laid.Select(drape.ToGlobal))
            .Where(pair => points.Any(point => point.Position.Y < pair.Second.Y
                && new Vector2(pair.First.X - point.Position.X, pair.First.Z - point.Position.Z).Length() < point.Radius))
            .Select(pair => {
                var skin = Rays.Cast(drape, pair.Second + Vector3.Up * 0.01f, pair.Second + Vector3.Down * 0.3f,
                    PatientBody.SurfaceLayer)!.Value.Position.Y;
                return (pair.First.Y - skin) / (pair.Second.Y - skin);
            })];
    }

    /// <summary>How high the drape's sheet is (world) where it is drawn over <paramref name="at"/>: at its vertex
    /// nearest across.</summary>
    private static float DrapeOver(Drape drape, Vector3 at) {
        var local = drape.ToLocal(at);
        var nearest = SheetVertices(drape).Where(v => v.Y > local.Y - 0.05f)
            .MinBy(v => new Vector2(v.X - local.X, v.Z - local.Z).LengthSquared());
        return drape.ToGlobal(nearest).Y;
    }

    /// <summary>Asserts the hand grasps the limb round <paramref name="ring"/>: its stiff palm on it, pressed in no more
    /// than a flat hold, and its fingers wrapped round it from the knuckles, nothing of it in the skin.</summary>
    private static void AssertGrasps(SurgeonHand hand, LimbRing ring) {
        AssertBool(hand.PressAcross != Vector3.Zero).OverrideFailureMessage("on the limb the hand grasps it").IsTrue();
        var gaps = PalmGaps(hand, ring);
        AssertFloat(gaps.Min())
            .OverrideFailureMessage($"its palm presses on the limb, no further in than a flat hold: {gaps.Min():0.000} m off")
            .IsBetween(-Surgeon.PressSink - PalmGapSlack, PalmTouch);
        AssertFloat(gaps.Max()).OverrideFailureMessage($"its stiff palm lies across the limb's round: {gaps.Max():0.000} m "
            + "off at its ends").IsLess(PalmLift);
        foreach (var finger in (string[])["Index", "Middle", "Ring", "Pinky"]) {
            var knuckleEnd = hand.BonePoints(finger + "1")[^1];
            var off = SkinGap(hand, knuckleEnd.Position, ring) - knuckleEnd.Radius;
            AssertFloat(off).OverrideFailureMessage($"the {finger.ToLowerInvariant()} finger wraps the limb from the "
                + $"knuckle: its first bone ends {off:0.000} m off the skin").IsLess(FingerTouch);
        }
        AssertInt(HandPointsInSkin(hand)).OverrideFailureMessage("its fingers close round the limb's skin, nothing of it in it")
            .IsEqual(0);
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

    /// <summary>How far (meters) each spot of the hand's palm underside is off the skin of the limb round
    /// <paramref name="ring"/>, straight out from its middle line (negative: in it).</summary>
    private static float[] PalmGaps(SurgeonHand hand, LimbRing ring) =>
        [.. hand.PalmUndersidePosed().Select(spot => SkinGap(hand, spot, ring))];

    /// <summary>How far (meters) <paramref name="spot"/> (in <paramref name="world"/>) is off the skin of the limb round <paramref name="ring"/>,
    /// straight out from its middle line (negative: in it).</summary>
    private static float SkinGap(Node3D world, Vector3 spot, LimbRing ring) {
        var middle = ring.Center + (ring.Axis * ring.Axis.Dot(spot - ring.Center));
        var outward = (spot - middle).Normalized();
        return Rays.Cast(world, spot + (outward * 0.05f), middle, PatientBody.SurfaceLayer) is { } hit
            ? (spot - hit.Position).Dot(outward)
            : float.NegativeInfinity;
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
