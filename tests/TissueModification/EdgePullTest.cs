namespace Scalpel.Tests.TissueModification;

/// <summary>
/// A 10 cm reference wound on each cuttable body part, and the thigh bullet's entry widened 5 cm with the scalpel,
/// held in the middle and pulled with paused, continuous or faster mouse strokes until they give no more. The skin should give easily at
/// first and hold the hand back more and more, never shake, and show its three stages: everything moving together,
/// the skin lifting with the fat stretched under it, then the skin peeled off the fat.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("tissue_modification"), TestCategory("skin_pull"), TestCategory("visual_confirmation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class EdgePullTest {
    private const float Length = 0.10f;
    /// <summary>How far the hand is steered in each stage, over <see cref="StageFrames"/>.</summary>
    private const float Stage = 0.0025f;
    private const int StageFrames = 20;
    /// <summary>Frames the hand is held still after each stage to see the tool settle.</summary>
    private const int HoldFrames = 30;
    private const float MaxPull = 0.10f;
    /// <summary>A stage that moves the tool less than this (meters) counts as the controls not giving.</summary>
    private const float Stalled = 0.00025f;
    /// <summary>How far (meters) the edge may come back toward the cut from the furthest it got.</summary>
    private const float SettleBack = 0.006f;
    // Keep develop's allowance while measuring the extra contact work; do not enlarge it for this change.
    private static readonly BudgetAllowance BudgetOverride =
        new("ordering and picking the tool up beside a fresh wound, and holding its edge, exceed the recorded CPU budget", 9.5f);

    [TestCase("appendectomy", "forceps")]
    [TestCase("appendectomy", "retractor")]
    [TestCase("knife_back", "forceps")]
    [TestCase("broken_ribs", "forceps")]
    [TestCase("brain_tumor", "forceps")]
    [TestCase("slit_throat", "forceps")]
    [TestCase("leg_extension", "forceps", Timeout = Limits.Slow)]
    [TestCase("hand_stitch", "forceps")]
    [TestCase("ambulance_bullet", "forceps")]
    public async Task EdgePullsInStages(string scenario, string toolId) => await PullEdge(scenario, toolId);

    [TestCase("forceps", 20, 30, 1, Timeout = Limits.Slow)]
    [TestCase("retractor", 20, 30, 1)]
    [TestCase("forceps", 20, 0, 1)]
    [TestCase("retractor", 20, 0, 1)]
    [TestCase("forceps", 5, 0, 1)]
    [TestCase("retractor", 5, 0, 1)]
    [TestCase("forceps", 5, 0, -1)]
    [TestCase("retractor", 5, 0, -1, Timeout = Limits.Slow)]
    public async Task ThighEntryEdgePullsWithBoundedMouseEffort(string toolId, int frames, int pause, int side) =>
        await PullEdge("bullet_muscle", toolId, frames, pause, side, entry: true);

    [TestCase]
    public void EdgePullMeasurementsCatchAbruptStopsReboundsAndWobble() {
        AssertBool(Progressive([1f, 1f, 0.5f, 0f])).OverrideFailureMessage("one partial stage before a hard stop is not gradual resistance").IsFalse();
        AssertBool(Progressive([1f, 0.8f, 0.6f, 0.4f, 0f, 1f]))
            .OverrideFailureMessage("resuming full movement after stopping must fail even after a gradual start").IsFalse();
        AssertBool(Progressive([0.5f, 0.6f, 0.7f, 0.8f]))
            .OverrideFailureMessage("steadily increasing movement is not increasing resistance").IsFalse();
        AssertBool(Progressive([1f, 0.8f, 0.6f, 0.4f, 0.2f, 0f, 0.1f, 0.2f, 0.3f]))
            .OverrideFailureMessage("several small recoveries after stopping must not hide a rebound").IsFalse();
        AssertBool(Progressive([1f, 0.8f, 0.6f, 0.4f, 0.2f, 0f]))
            .OverrideFailureMessage("a gradual decline to a stop is progressive resistance").IsTrue();
        AssertFloat(Shake([Vector3.Zero, new Vector3(0.001f, 0.005f, 0), new Vector3(0.002f, -0.005f, 0), new Vector3(0.003f, 0, 0)]))
            .OverrideFailureMessage("sideways oscillation counts even while progressing along the stroke").IsGreater(0.001f);
        AssertFloat(Shake([Vector3.Zero, Vector3.Zero, Vector3.Zero])).OverrideFailureMessage("a still hand has no wobble").IsEqual(0f);
        AssertFloat(Shake([Vector3.Zero, Vector3.Right * 0.003f, new Vector3(0.003f, 0.002f, 0)]))
            .OverrideFailureMessage("a pull changing from sideways to lifting without reversing is not wobble").IsEqual(0f);
    }

    [TestCase("gangrene_amputation", "forceps", Description = "BROKEN: on the narrow lower leg the skin's cut wall at one "
        + "of the wound's ends pokes through the skin beside it once the edge is peeled 20 mm up in a run without key "
        + "frames; with them it passes.")]
    [TestCase("leg_extension", "retractor", Description = "BROKEN: shows only in the key frames: a few centimeters from "
        + "the hook the thigh's fat wall folds over itself in a pleat while the edge is drawn aside (the same with the fat "
        + "walls unstretched).")]
    [TestCategory("broken")]
    public async Task EdgePullsInStagesBroken(string scenario, string toolId) => await PullEdge(scenario, toolId);

    private static async Task PullEdge(string scenario, string toolId, int frames = StageFrames, int pause = HoldFrames,
        int side = 1, bool entry = false) {
        var folder = entry ? $"{scenario}_{toolId}_{frames}_{pause}_{side}" : $"{scenario}_{toolId}";
        var reference = entry && toolId == "forceps" && frames == 20 && pause == 30 && side == 1;
        var progression = reference || entry && toolId == "retractor" && frames == 5 && side == -1;
        var session = await ToolSession.Start(scenario, $"edge_pull/{folder}");
        var driver = session.Driver;
        var body = driver.Body;
        if (!body.SiteActive) {
            await driver.PlayerTurnsPatient(PatientPose.FaceDown);
        }
        if (reference) {
            await driver.Capture("untouched", "the thigh before any tool touches it: the original bullet entry wound, not yet widened or pulled");
        }
        var length = entry ? 0.05f : Mathf.Min(Length, body.SiteSize.X * 0.6f);
        var half = new Vector2(length * 0.5f / body.SiteSize.X, 0f);
        var middle = entry ? driver.Patient.Wounds[0].Midpoint : ClearMiddle(driver, half);
        if (entry) {
            await driver.PlayerCutsSkin(middle - half, middle + half, 3);
            await driver.PlayerPutsDown();
            await Frames.Seconds(1f);
            if (reference) {
                await driver.Capture("incised", "the bullet entry widened 5 cm through the tissue with the scalpel, before either lip is pulled");
            }
        } else {
            await driver.Unbudgeted(async () => {
                SurgeryState.SkinIsCut(driver.Patient, middle - half, middle + half, ToolActions.DepthByLevel[2]);
                await Frames.Seconds(1f);
            });
        }
        var wounds = driver.Patient.Wounds.Count;
        // The lip: the first skin beside the opening, and a little into it so the jaws close on skin.
        var across = new Vector2(0f, side * 0.001f / body.SiteSize.Y);
        var lip = middle;
        for (var i = 0; i < 60 && body.LayerAt(lip) != "skin"; i++) {
            lip += across;
        }
        AssertString(body.LayerAt(lip)).OverrideFailureMessage("the wound's edge lies within 6 cm of its middle").IsEqual("skin");
        lip += across * 2f;
        var at = driver.SitePoint(lip);
        var along = (driver.SitePoint(middle + half) - driver.SitePoint(middle - half)).Normalized();
        var away = (driver.SitePoint(lip + across * 10f) - at).Slide(Vector3.Up).Normalized();
        session.Along = along;
        // The key frames look at the held edge across the opening, a little off square: whatever stands right across
        // the cut (a knife in the back) would hide it. The last one looks down from behind the edge, past the surgeon.
        var facing = (-away).Rotated(Vector3.Up, 0.5f);

        var tool = (await driver.PlayerRequestsItem(toolId))!;
        await driver.PlayerWalksTo(at);
        await driver.PlayerReaches(at);
        SurgeryDriver.Use();
        await Frames.Physics(10);
        SurgeryDriver.Use(false);
        await Frames.Physics(3);
        AssertBool(tool.Hold is SkinHold).OverrideFailureMessage($"the {toolId} holds the wound's edge ({tool.Hold})").IsTrue();
        var grip = (SkinHold)tool.Hold!;
        var miss = (grip.Anchor - lip) * body.SiteSize;
        AssertFloat(miss.Length()).OverrideFailureMessage("the tool grips the selected incision lip within 5 mm").IsLess(0.005f);
        var k = body.Tissue.GrippedParticle(tool.Uid);
        AssertFloat((body.Tissue.Pos[k].Z - body.SitePoint(middle, 0f).Z) * side)
            .OverrideFailureMessage("the simulation particle belongs to the approached side of the incision").IsGreater(0f);
        var gapRadius = body.MetersToUv(0.005f);
        // GapAt is site-local; patients have a uniform age/weight scale. Use physical meters here, like the
        // mouse-driven tip travel below, so the opening requirement doesn't vary with patient size.
        var siteScale = body.Site.GlobalBasis.X.Length();
        var gapBefore = body.Tissue.GapAt(middle, gapRadius) * siteScale;
        await session.CaptureOblique("grabbed", $"the {toolId} holding the middle of the wound's edge, seen across the "
            + "opening: skin, fat and muscle walls lie flat and even along the cut", at, facing, 0.12f, 30f);

        // Remove the patient's motion while retaining world meters: ToLocal also divides by the model's scale,
        // which would make an unresisted 5 mm mouse stroke look shorter on a scaled patient.
        var measureFrame = body.Site.GlobalBasis.Orthonormalized().Inverse();
        Vector3 Contact() => measureFrame * (tool.TipPosition() - body.Site.GlobalPosition);
        Vector3 SkinContact() => measureFrame * (body.Site.GlobalBasis * body.Tissue.Pos[k]);
        var pullDirection = measureFrame * away;
        var start = Contact();
        var rise0 = Rises(body, k);
        var hand = PlayerInput.HandKey(driver.Me);
        var input = 0f;
        var stage = entry ? Stage : 0.005f;
        var travel = 0f;
        var extent = Vector2.Zero;
        var gave = new List<float>();
        var lifted = new List<float>();
        var gaveFine = new List<float>();
        var liftedFine = new List<float>();
        var skinLifted = new List<float>();
        // A shorter incision has less loose edge to draw aside. Require a tenth of its length (up to 1 cm),
        // plus an independently measured useful opening and lift; neither motion can substitute for the other.
        var requiredTravel = Mathf.Min(0.01f, length * 0.1f);
        var pixels = 0f;
        var usefulPixels = -1f;
        var peeledPixels = -1f;
        var stalled = 0;
        var stretched = -1;
        var peeled = -1;
        var samples = 0;
        // A faster stroke can pass through the stretched-fat phase between stage endpoints. Observe each
        // delivered input, and capture the actual transition (KeyFrames pauses physics while taking the image).
        async Task ObserveLayers() {
            var rise = Rises(body, k) - rise0;
            var (skin, fat, muscle) = (rise.X, rise.Y, rise.Z);
            if (stretched < 0 && body.LayersApart(k) && skin - muscle > PatientBody.FatStretch * 0.5f && skin - fat < 0.0015f) {
                stretched = samples;
                await session.CaptureOblique("fat_stretched", "the edge lifted under its blood film, its fat wall stretched up with the skin: "
                    + "no gap between skin and fat, the skin bends smoothly, nothing pokes through it", at, facing, 0.12f, 30f);
            }
            if (peeled < 0 && body.LayersApart(k) && skin - fat > PatientBody.FatStretch * 0.75f) {
                if (scenario == "leg_extension") {
                    await driver.Unbudgeted(() => {
                        TissueLayers.AssertWallsDoNotCrossThemselves(body);
                        return Task.CompletedTask;
                    });
                }
                peeled = samples;
                peeledPixels = pixels;
                if (entry) {
                    AssertFloat(driver.Patient.CavityBloodMl)
                        .OverrideFailureMessage("the open thigh wound is still bleeding into its cavity").IsGreater(0f);
                    AssertInt(body.Blood.Pools.Count)
                        .OverrideFailureMessage("the thigh incision goes through muscle: its blood drains into the cavity instead of floating on a skin-depth pool")
                        .IsEqual(0);
                }
                await session.CaptureOblique("skin_peeled", "the skin peeled off the fat: a thin, blood-covered band of skin lifted in "
                    + "the jaws, the fat left lying on the muscle under it, no fat or muscle through the skin and no floating blood tab inside the opening",
                    at, facing, 0.12f, 30f);
            }
        }
        while (input < MaxPull - 0.0001f && stalled < 2) {
            SurgeryDriver.Press(hand);
            var before = Contact();
            var skinBefore = SkinContact();
            var pulled = new List<Vector3> { before };
            for (var f = 0; f < frames; f++) {
                var motion = PlayerInput.HandMotion(driver.Me, away * (stage / frames));
                pixels += motion.Length();
                PlayerInput.Mouse(motion);
                await PlayerInput.Delivered();
                pulled.Add(Contact());
                samples++;
                await ObserveLayers();
                if (progression && (f + 1 == frames / 2 || f + 1 == frames)) {
                    var commanded = input + stage * (f + 1) / frames;
                    // Local dense captures exposed a pop between 27.5 and 30 mm of input. Keep that transition;
                    // the material-to-mesh assertion below covers the rest of every thigh stroke.
                    if (commanded >= 0.027f && commanded <= 0.031f) {
                        await session.CaptureOblique($"lifting_{samples:D4}",
                            $"after {commanded * 1000f:0.00} mm of mouse pull, the lip rises gradually before peeling: skin follows the grip, "
                            + "without a sudden jump from the resting body to a lifted arch",
                            at, facing, 0.12f, 30f);
                    }
                }
                if (scenario == "leg_extension" && (f + 1 == frames / 2 || f + 1 == frames)) {
                    // Dense local captures exposed a fat-wall pleat between the original stages. Keep the
                    // intermediate geometry checks and the views during extension, when long columns converged.
                    if (peeled < 0) {
                        await session.CaptureOblique($"pulling_{samples:D4}",
                            $"after {(input + stage * (f + 1) / frames) * 1000f:0.00} mm of mouse pull, the opening widens gradually "
                            + "and the fat wall extends below the lifted skin: "
                            + "a continuous rounded wall without a sharp inward pleat or intersecting columns before peeling",
                            at, facing, 0.12f, 30f);
                    }
                    await driver.Unbudgeted(() => {
                        TissueLayers.AssertUnderSkin(body);
                        TissueLayers.AssertWallsDoNotCrossThemselves(body);
                        return Task.CompletedTask;
                    });
                }
            }
            if (pause > 0) {
                SurgeryDriver.Release(hand);
            }
            input += stage;
            var held = new List<Vector3> { Contact() };
            for (var f = 0; f < pause; f++) {
                await Frames.NextPhysics();
                held.Add(Contact());
            }
            var dragShake = Shake(pulled);
            var shake = Shake(held);
            var tip = Contact();
            await ObserveLayers();
            var nowTravel = (tip - start).Dot(pullDirection);
            var rise = Rises(body, k) - rise0;
            var (skin, fat, muscle) = (rise.X, rise.Y, rise.Z);
            var when = $"at {input * 1000f:0.0} mm";
            if (dragShake >= 0.001f) {
                GD.Print("pull trace mm: " + string.Join(", ", pulled.Select(p => (p - before) * 1000f)));
            }
            GD.Print($"edge pull {input * 1000f:0.0} mm ({pixels:0.0} mouse px): travel {nowTravel * 1000f:0.0} mm, lift {(tip - start).Y * 1000f:0.0} mm, "
                + $"skin/fat/muscle rise {skin * 1000f:0.0}/{fat * 1000f:0.0}/{muscle * 1000f:0.0} mm, shake {dragShake * 1000f:0.00}/{shake * 1000f:0.00} mm");

            AssertBool(tool.Hold == grip).OverrideFailureMessage($"the {toolId} keeps hold of the same edge {when}").IsTrue();
            AssertFloat(dragShake).OverrideFailureMessage($"the hand pulling on {when} doesn't shake ({dragShake * 1000f:0.00} mm)").IsLess(0.001f);
            AssertFloat(shake).OverrideFailureMessage($"the hand held still {when} doesn't shake ({shake * 1000f:0.00} mm)").IsLess(0.001f);
            // Peeled up, the edge comes back toward the cut a little: the skin lifted off has that much less to give.
            AssertFloat(nowTravel).OverrideFailureMessage($"the edge settles back toward the cut no more than {SettleBack * 1000f:0} mm "
                + $"{when} ({nowTravel * 1000f:0.0} mm, was {travel * 1000f:0.0} mm)").IsGreater(travel - SettleBack);
            AssertInt(driver.Patient.Wounds.Count).OverrideFailureMessage($"the pull doesn't tear the skin {when}").IsEqual(wounds);
            // Not gameplay: the check walks every triangle of the opened layers.
            await driver.Unbudgeted(() => {
                TissueLayers.AssertUnderSkin(body);
                if (entry) {
                    TissueLayers.AssertSkinWallJoinsSheet(body);
                    var material = body.LayerPoint(0, k);
                    var distance = TissueLayers.DistanceToSkin(body, material) * siteScale;
                    AssertFloat(distance).OverrideFailureMessage($"the drawn skin follows the gripped material {when} "
                        + $"({distance * 1000f:0.0} mm away), before and after fold activation").IsLess(0.004f);
                }
                // The long thigh reference exposed a fat-wall pleat as the skin peeled. Check that regression
                // through the whole stroke, in addition to every fixture's hard skin-intersection check.
                if (scenario == "leg_extension") {
                    TissueLayers.AssertWallsDoNotCrossThemselves(body);
                }
                return Task.CompletedTask;
            });

            // Measure sideways opening and peeling separately: a horizontal stroke can lift the edge once it
            // stops sliding along the body. Neither phase may hide a sudden stop or a later jump back to full give.
            gave.Add(Mathf.Max((tip - before).Dot(pullDirection), 0f) / stage);
            lifted.Add(Mathf.Max((tip - before).Y, 0f) / stage);
            gaveFine.AddRange(Gains(pulled, pullDirection, stage / frames));
            liftedFine.AddRange(Gains(pulled, Vector3.Up, stage / frames));
            skinLifted.Add(Mathf.Max(SkinContact().Y - skinBefore.Y, 0f) / stage);
            travel = Mathf.Max(travel, nowTravel);
            if (usefulPixels < 0f && nowTravel >= requiredTravel) {
                usefulPixels = pixels;
            }
            if (input <= stage + 0.0001f) {
                await driver.AssertAndCapture(Mathf.Abs(skin - fat) < 0.001f && Mathf.Abs(skin - muscle) < 0.001f,
                    "pulled a little, the edge slides along the body as one: skin, fat and muscle walls together, the skin "
                    + $"bending lightly ({skin * 1000f:0.0}/{fat * 1000f:0.0}/{muscle * 1000f:0.0} mm risen)", "together");
            }
            // A stop means no new reach in either direction. Torso breathing may return a held tip a little
            // within that reach; it must not masquerade as further give or prevent a stopped pull from finishing.
            var nextExtent = extent.Max(new Vector2(nowTravel, (tip - start).Y));
            stalled = peeled > 0 && nextExtent.DistanceTo(extent) < Stalled ? stalled + 1 : 0;
            extent = nextExtent;
        }
        SurgeryDriver.Release(hand);
        await PlayerInput.Delivered();
        gaveFine = Paired(gaveFine);
        liftedFine = Paired(liftedFine);
        skinLifted = Paired(skinLifted);
        var gaveText = string.Join(", ", gave.Select(g => g.ToString("0.00")));
        var initialGive = entry ? gaveFine[0] : gave[0];
        AssertFloat(initialGive).OverrideFailureMessage($"the initial unloaded input follows the hand ({initialGive:0.00}; stages {gaveText})")
            .IsGreaterEqual(0.9f);
        AssertBool(stretched > 0 && peeled > stretched).OverrideFailureMessage("the skin lifts with the fat stretched under it "
            + $"before it peels off the fat (stretched at sample {stretched}, peeled at sample {peeled})").IsTrue();
        // Up to where the hand stops, each stage gives no more than the one before (within the solver's settling), and
        // some give part of theirs.
        AssertResistance(gave, "sideways opening", gaveFine);
        // Peeling begins after the sideways phase, so check its own response from its highest gain onward.
        var peak = lifted.IndexOf(lifted.Max());
        var finePeak = liftedFine.IndexOf(liftedFine.Max());
        // A breathing torso can raise the fixed wrist again between pauses. Its vertical curve permits that
        // settling recovery; the stationary thigh must also pass the stricter full-history check.
        AssertResistance(lifted.Skip(peak).ToList(), "upward peeling", liftedFine.Skip(finePeak).ToList(), breathing: !entry);
        if (entry) {
            // The thigh is a stationary fixture for the tissue response curve. On a breathing torso, the
            // fixed wrist and moving body also change the particle's height during the pauses.
            var skinPeak = skinLifted.IndexOf(skinLifted.Max());
            AssertResistance(skinLifted.Skip(skinPeak).ToList(), "the skin edge's upward peeling");
        }
        AssertInt(stalled).OverrideFailureMessage("the pull reaches a real stop in 3D before its input budget is exhausted").IsGreaterEqual(2);
        AssertFloat(travel).OverrideFailureMessage($"the edge moves at least {requiredTravel * 1000f:0.0} mm sideways ({travel * 1000f:0.0} mm)")
            .IsGreaterEqual(requiredTravel);
        AssertFloat(new Vector2(travel, extent.Y).Length())
            .OverrideFailureMessage("including its lift, the edge reaches at least 13 mm away from the cut").IsGreaterEqual(0.013f);
        var gapGrowth = body.Tissue.GapAt(middle, gapRadius) * siteScale - gapBefore;
        const float RequiredOpening = 0.006f;
        AssertFloat(gapGrowth)
            .OverrideFailureMessage($"pulling the lip widens the actual skin opening by at least 6 mm ({gapGrowth * 1000f:0.00} mm)")
            .IsGreaterEqual(RequiredOpening);
        GD.Print($"edge effort: useful opening at {usefulPixels:0.0} mouse px, peeled at {peeledPixels:0.0} px, stopped at {pixels:0.0} px");
        if (entry) {
            AssertFloat(usefulPixels).OverrideFailureMessage("the 5 cm thigh incision's lip moves 5 mm within 35 mouse pixels at default sensitivity")
                .IsBetween(0f, 35f);
            AssertFloat(peeledPixels).OverrideFailureMessage("the thigh skin peels within 75 mouse pixels").IsBetween(0f, 75f);
            AssertFloat(pixels).OverrideFailureMessage("the thigh reaches its stop within 100 mouse pixels").IsLessEqual(100f);
        }
        await session.CaptureOblique("final", "the edge pulled as far as the hand goes, from above: the opening drawn "
            + "wide, the peeled skin lifted in the jaws, fat and muscle lying evenly below it under the blood film", at, -facing, 0.2f, 70f);
        if (entry) {
            var stopped = Contact();
            SurgeryDriver.Press(hand);
            // Stay within the shorter edge's available travel rather than dragging past its acquisition point.
            for (var f = 0; f < frames * 2; f++) {
                PlayerInput.Mouse(PlayerInput.HandMotion(driver.Me, -away * (Stage / frames)));
                await PlayerInput.Delivered();
                if (f == 0) {
                    AssertFloat((stopped - Contact()).Dot(pullDirection))
                        .OverrideFailureMessage("the first reverse mouse event moves the held edge inward without a dead zone")
                        .IsGreater(Stage / frames * 0.5f);
                }
            }
            SurgeryDriver.Release(hand);
            await PlayerInput.Delivered();
            var returned = (stopped - Contact()).Dot(pullDirection);
            AssertFloat(returned)
                .OverrideFailureMessage($"reversing 5 mm of mouse input brings the thigh edge back at least 4 mm ({returned * 1000f:0.00} mm)")
                .IsGreaterEqual(0.004f);
            AssertBool(tool.Hold == grip).OverrideFailureMessage("reversing keeps hold of the same thigh edge").IsTrue();
            AssertInt(driver.Patient.Wounds.Count).OverrideFailureMessage("reversing the thigh pull adds no tear").IsEqual(wounds);
            await session.CaptureOblique("returned", "the same thigh edge still held after reversing the mouse, returned toward the opening without a tear",
                at, facing, 0.12f, 30f);
        }
        if (reference && KeyFrames.Wanted()) {
            await driver.Unbudgeted(async () => {
                await driver.PlayerLooksAt(driver.SitePoint(middle));
                PlayerInput.Tap(InputActions.Zoom);
                await Frames.Seconds(0.5f);
            });
            await session.CaptureView("pulled", "zoomed in on the thigh entry, the surgeon holding its lifted lip with forceps and the opening visible beside the hand");
        }
        await session.Finish(BudgetOverride);
    }

    /// <summary>The middle of the site, or the nearest spot clear of the scenario's own wounds and targets with room for
    /// the cut and to pull its edge. Where there is none (the neck's own slit runs across it), the middle: the cut
    /// opens the wound there further.</summary>
    private static Vector2 ClearMiddle(SurgeryDriver driver, Vector2 half) {
        var body = driver.Body;
        var room = body.MetersToUv(0.03f);
        bool Clear(Vector2 uv) => driver.Patient.Wounds.All(w => w.DistanceTo(uv) > room)
            && driver.Patient.Targets.All(t => t.Uv.DistanceTo(uv) > room);
        return Enumerable.Range(0, 81).Select(i => new Vector2(0.3f + i % 9 * 0.05f, 0.3f + i / 9 * 0.05f))
            .Where(uv => body.OnBody(uv - half) && body.OnBody(uv + half) && Clear(uv) && Clear(uv - half) && Clear(uv + half))
            .OrderBy(uv => uv.DistanceTo(new Vector2(0.5f, 0.5f)))
            .DefaultIfEmpty(new Vector2(0.5f, 0.5f)).First();
    }

    /// <summary>How high the skin, fat and muscle stand over particle <paramref name="k"/> (x, y, z).</summary>
    private static Vector3 Rises(PatientBody body, int k) =>
        new(body.LayerPoint(0, k).Y, body.LayerPoint(1, k).Y, body.LayerPoint(2, k).Y);

    private static void AssertResistance(List<float> gains, string phase, List<float>? fine = null, bool breathing = false) {
        var text = string.Join(", ", gains.Select(g => g.ToString("0.00")));
        AssertBool(Progressive(gains, fine, breathing))
            .OverrideFailureMessage($"{phase} progressively resists over at least three partial samples, with no rebound after stopping ({text})").IsTrue();
    }

    private static bool Progressive(List<float> gains, List<float>? fine = null, bool breathing = false) {
        if (gains.Count < 3 || gains[^1] > gains[0] - 0.35f
            || (fine ?? gains).Count(g => g is > 0.1f and < 0.95f) < 3) { return false; }
        var least = gains[0];
        var previous = least;
        foreach (var gain in gains.Skip(1)) {
            // Allow settling noise, but compare against the whole history: several small recoveries must not
            // add up to resumed movement. Once stopped, even a partial return to giving is a rebound.
            if (gain - (breathing ? previous : least) >= 0.35f || least < 0.05f && gain > 0.2f) { return false; }
            least = Mathf.Min(least, gain);
            previous = gain;
        }
        return true;
    }

    /// <summary>Response during every frame of input, rather than just the settled endpoints of a long stage.</summary>
    private static IEnumerable<float> Gains(List<Vector3> points, Vector3 direction, float step) =>
        points.Zip(points.Skip(1), (before, after) => Mathf.Max((after - before).Dot(direction), 0f) / step);

    /// <summary>Pair the full 60 Hz trace across stage boundaries, including odd-frame continuous strokes, so a
    /// 30 Hz tissue update followed by an unchanged frame isn't mistaken for a stop/rebound.</summary>
    private static List<float> Paired(List<float> gains) => gains.Chunk(2).Select(pair => pair.Average()).ToList();

    /// <summary>Backtracking in each of the site's three axes (meters). Lateral wobble counts even while the tool
    /// progresses along the pull; a monotone bend from sideways into lifting is not shaking.</summary>
    private static float Shake(List<Vector3> tip) {
        var path = tip.Zip(tip.Skip(1), (before, after) => (after - before).Abs())
            .Aggregate(Vector3.Zero, (sum, move) => sum + move);
        return ((path - (tip[^1] - tip[0]).Abs()) * 0.5f).Length();
    }
}
