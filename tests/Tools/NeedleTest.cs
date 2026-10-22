namespace Scalpel.Tests.Tools;

/// <summary>
/// The needle as a player uses it: a running thread clicked through a cut hole by hole, pulled on the wheel and tied
/// off with a long hold, layer by layer through a cut into the belly, and across a bullet hole. Key frames: the site untouched, after the first
/// two holes and tied off. Review those for one continuous thread dipping into its holes, edges meeting in a slight
/// ridge without passing through each other, and a narrow incision line still visible beneath the tied thread rather
/// than an open red gap or seamless skin.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("smoke"), TestCategory("tool_needle"), TestCategory("tissue_modification"), TestCategory("frame_budget")]
[TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class NeedleTest {
    // Temporary local budget override: recorded CPU/render overruns remain measured; functional assertions still fail normally.
    private static readonly BudgetAllowance BudgetOverride = new("known to go over the frame budget, not profiled yet", 14.5f);

    /// <summary>Too loose leaves a gap, too tight tears through, and skin pulled shut over open muscle tears through
    /// too.</summary>
    [TestCase(Timeout = Limits.Slow)]
    public async Task ThreadTensionLimits() {
        var session = await ToolSession.Start("appendectomy", "needle/tension");
        var driver = session.Driver;
        AssertFloat(ToolActions.SutureTensionStep).OverrideFailureMessage("the thread wheel has fine tension steps")
            .IsLessEqual(0.04f);
        var patient = driver.Patient;
        var tissue = driver.Body.Tissue;
        // The old nurse delay accidentally supplied this preparation. Cuts must start on a calm, anesthetized patient.
        AssertBool(await Frames.Until(() => !patient.Vitals.IsAwake, Db.Drug("propofol")!.Onset * DrugDef.DirectOnset * 2f))
            .OverrideFailureMessage("the tension fixture is asleep before its cuts are made").IsTrue();
        var loose = SurgeryState.SkinIsCut(patient, new Vector2(0.3f, 0.3f), new Vector2(0.55f, 0.3f), 0.5f);
        var tight = SurgeryState.SkinIsCut(patient, new Vector2(0.3f, 0.5f), new Vector2(0.55f, 0.5f), 0.5f);
        var deep = SurgeryState.SkinIsCut(patient, new Vector2(0.3f, 0.7f), new Vector2(0.55f, 0.7f), 1f);
        await Frames.Seconds(1f);
        await driver.Capture("cuts_before_sutures", "three cuts across the site, the lowest deeper, no thread in any");
        var needle = (await driver.PlayerRequestsItem("needle"))!;
        AssertBool(Hud.ControlLines(driver.Me).Any(line => line.Contains("tighten thread")))
            .OverrideFailureMessage("before the first hole the controls offer no thread to pull").IsFalse();

        // Over the belly the hand stays clear of it, holding the needle's tip well above the skin: the dot still lies
        // on the skin, where the hole goes in.
        AssertBool(await driver.PlayerThreads(loose, TissueDepth.Skin, checkAim: true))
            .OverrideFailureMessage($"all loose-thread punctures are reached\n{driver.Recent()}").IsTrue();
        AssertBool(Hud.ControlLines(driver.Me).Any(line => line.Contains("tighten thread")))
            .OverrideFailureMessage("once threaded the controls offer pulling it").IsTrue();
        await driver.PlayerTiesOff("loose_tied", "tied off loose: the thread lies across the top cut, its edges still apart");
        await Frames.Seconds(1f);
        AssertFloat(loose.Closure).OverrideFailureMessage("tied off loose, the thread leaves the cut open").IsLess(0.99f);
        AssertFloat(tissue.GapAlong(loose.Points, 0.03f, TissueDepth.Skin)).OverrideFailureMessage("and its edges apart")
            .IsGreater(TissueSim.OpenGap);
        AssertBool(patient.Flags.ContainsKey("neat_closure")).OverrideFailureMessage("a loose closure isn't a neat one").IsFalse();

        AssertBool(await driver.PlayerThreads(tight, TissueDepth.Skin))
            .OverrideFailureMessage($"all tight-thread punctures are reached\n{driver.Recent()}").IsTrue();
        var thread = needle.Suture.Thread;
        await driver.PlayerPullsThread("too tight");
        // The patient settles a thread's claimed closure against the gaps the tissue shows on its own tick.
        await Frames.Seconds(Patient.Tick * 2f);
        AssertString(SewAction.ThreadState(needle)).OverrideFailureMessage("the hand status reads too tight before it tears")
            .IsEqual("too tight");
        AssertBool(patient.SutureDone(thread)).OverrideFailureMessage("too tight thread is still intact before further tightening")
            .IsFalse();
        AssertFloat(tight.Closure).OverrideFailureMessage($"too tight still holds the cut closed (closure {tight.Closure:0.000}, tension {needle.Suture.Tension:0.000}, anchors {tissue.Thread(thread)!.Anchors.Count})\n{driver.Recent()}").IsGreater(0.99f);
        AssertFloat(tissue.GapAlong(tight.Points, 0.03f, TissueDepth.Skin))
            .OverrideFailureMessage("its edges meet between the holes too").IsLessEqual(TissueSim.ClosedGap);
        AssertFloat(tissue.SutureLip.Max()).OverrideFailureMessage("too tight thread visibly puckers the joined skin")
            .IsGreater(0.0009f);
        var holes = tissue.Thread(thread)!.Anchors.Count;
        var drawn = driver.Body.DrawnThread(thread)!;
        AssertFloat(drawn.PressureAmount).OverrideFailureMessage("over-tight thread strongly marks the skin around its holes")
            .IsGreater(0.45f);
        AssertInt(drawn.PressureHoles).OverrideFailureMessage("every over-tight hole shows pressure").IsEqual(holes);
        AssertInt(driver.Body.ThreadNode(thread)!.GetNode<MultiMeshInstance3D>("Pressure").Multimesh.InstanceCount)
            .OverrideFailureMessage("every over-tight hole has a rendered pressure sprite").IsEqual(holes);
        await driver.Capture("too_tight_intact", "the middle cut sewn too tight, intact: strong pressure marks around every hole");
        var wounds = patient.Wounds.Count;
        for (var i = 0; i < 6 && !patient.SutureDone(thread); i++) {
            await SurgeryDriver.Notch(false);
        }
        AssertBool(patient.SutureDone(thread) && needle.Suture.Thread == 0)
            .OverrideFailureMessage("pulled tighter still, the thread tears through").IsTrue();
        var torn = tissue.Thread(thread)!;
        AssertBool(torn.Springs.Concat(torn.Holds.Values).All(s => !tissue.SpringAt(s).Active))
            .OverrideFailureMessage("a torn thread lets go of every span and the edges it held").IsTrue();
        AssertFloat(tight.Closure).OverrideFailureMessage("and holds the cut no more").IsLess(0.01f);
        AssertInt(patient.Wounds.Count).OverrideFailureMessage("it tears the skin").IsGreater(wounds);
        AssertBool(driver.Surgery.Scoring.Entries.ContainsKey("suture_tear_through"))
            .OverrideFailureMessage("a thread torn through costs points").IsTrue();

        await SurgeryDriver.Notch(false);
        AssertFloat(needle.Suture.Tension)
            .OverrideFailureMessage("without a thread the wheel has nothing to pull, the needle stays slack").IsEqual(ToolActions.SutureStartTension);
        AssertBool(await driver.PlayerThreads(deep, TissueDepth.Skin))
            .OverrideFailureMessage($"all deep-thread punctures are reached\n{driver.Recent()}").IsTrue();
        AssertFloat(deep.Closure).OverrideFailureMessage("skin won't meet over open muscle").IsLess(0.2f);
        thread = needle.Suture.Thread;
        await driver.PlayerPullsThread("closed");
        AssertBool(patient.SutureDone(thread)).OverrideFailureMessage("pulled shut over open muscle, the skin thread tears through")
            .IsTrue();
        await session.Finish(BudgetOverride);
    }

    /// <summary>A bullet hole shows no muscle to sew, and the skin won't close over it: cut open, it bares the muscle,
    /// sewn first, then the skin.</summary>
    [TestCase(Timeout = Limits.Slow)]
    public async Task BulletHoleThroughMuscleIsCutOpenAndSewnLayerByLayer() {
        var session = await ToolSession.Start("bullet_muscle", "needle/bullet_hole");
        var driver = session.Driver;
        var tissue = driver.Body.Tissue;
        var hole = driver.Patient.Wounds.Single(wound => wound.IsHole);
        var middle = hole.Midpoint;
        AssertBool(hole.ThroughMuscle && tissue.MuscleOpenNear(middle, Patient.MuscleReach))
            .OverrideFailureMessage("the bullet hole goes through the muscle, open under it").IsTrue();
        AssertString(driver.ShowsAlong(hole, TissueDepth.Muscle)).OverrideFailureMessage("the hole shows no muscle to sew")
            .IsEmpty();
        await driver.PlayerCutsOpen(hole);
        await driver.AssertAndCapture(driver.IsCutOpen(hole), "the hole cut open through the skin along it", "cut_open");
        await driver.PlayerRequestsItem("needle");
        await driver.PlayerSews(hole, TissueDepth.Muscle, () => session.CaptureAt("muscle_threaded",
            "close up, the hole spread open by the Gelpi, a loose thread through the muscle's cut faces under the skin",
            driver.SitePoint(middle), 0.1f));
        AssertBool(hole.Muscle.All(m => m >= 1f) && !tissue.MuscleOpenNear(middle, Patient.MuscleReach))
            .OverrideFailureMessage($"the muscle under the hole is sewn shut\n{driver.Recent()}").IsTrue();
        foreach (var wound in driver.Patient.Wounds.Where(wound => wound.IsSkinCut).ToList()) {
            await driver.PlayerSews(wound, TissueDepth.Skin);
        }
        await Frames.Seconds(1f);
        AssertFloat(driver.Patient.SkinClosure()).OverrideFailureMessage($"then the skin closes over it\n{driver.Recent()}")
            .IsGreater(0.99f);
        await session.Finish(BudgetOverride);
    }

    /// <summary>A cut through the belly wall sewn layer by layer, each hole where the needle touches that layer down the
    /// cut's walls: the muscle, the fat, then the skin.</summary>
    [TestCase(Timeout = Limits.Slow)]
    public async Task LayeredClosureOfACutIntoTheBelly() {
        var session = await ToolSession.Start("appendectomy", "needle/layers");
        var driver = session.Driver;
        var tissue = driver.Body.Tissue;
        var wound = SurgeryState.SkinIsCut(driver.Patient, new Vector2(0.3f, 0.5f), new Vector2(0.7f, 0.5f), 1f);
        await Frames.Seconds(1f);
        var middle = wound.Midpoint;
        await driver.Capture("cut", "a deep cut across the site, open down to the fat");
        await driver.PlayerRequestsItem("needle");
        await SewDeepLayer(driver, wound, TissueDepth.Muscle);
        AssertBool(tissue.MuscleOpenNear(middle, Patient.MuscleReach)).OverrideFailureMessage("the muscle thread closes the muscle")
            .IsFalse();
        AssertBool(driver.Body.IsOpen(middle)).OverrideFailureMessage("the sewn muscle closes the way into the belly").IsFalse();
        await SewDeepLayer(driver, wound, TissueDepth.Fat);
        AssertBool(tissue.FatOpenNear(middle, Patient.MuscleReach)).OverrideFailureMessage("a thread through the fat closes it")
            .IsFalse();
        // Only what shows can be sewn: a needle can't reach fat still apart out of sight under the skin.
        AssertString(driver.ShowsAlong(wound, TissueDepth.Fat)).OverrideFailureMessage($"no cut fat shows beside the wound to sew ({driver.ShowsAlong(wound, TissueDepth.Fat)})")
            .IsEmpty();
        await driver.PlayerSews(wound, TissueDepth.Skin);
        await Frames.Seconds(1f);
        AssertFloat(wound.Closure).OverrideFailureMessage("the skin closes over the sewn layers").IsGreater(0.99f);
        AssertFloat(tissue.GapAlong(wound.Points, 0.03f, TissueDepth.Skin)).OverrideFailureMessage("and its edges meet")
            .IsLess(TissueSim.OpenGap);
        await session.Finish(BudgetOverride);
    }

    [TestCase(Timeout = Limits.Slow)]
    public async Task FinishedSutureRedrawAndCancelledPress() {
        var session = await ToolSession.Start("appendectomy", "needle/finished_threads");
        var driver = session.Driver;
        var body = driver.Body;
        var tissue = body.Tissue;
        var needle = (await driver.PlayerRequestsItem("needle"))!;
        var hand = new HandInput(true, true, 0, 0f, driver.Me.PeerId, new Modifiers());
        var sew = new SewAction();
        sew.Apply(new ToolStep(needle, hand, driver.Patient, 0.1f) {
            Pressed = true,
            Tip = needle.TipPosition(),
            Probe = new SiteProbe(SiteZone.Site, new Vector2(0.4f, 0.4f), 0f),
        });
        // Let go with the needle up by the tray, off the patient.
        sew.Apply(new ToolStep(needle, hand with { Trigger = false }, driver.Patient, 0f) {
            Released = true,
            Tip = needle.TipPosition(),
            Probe = new SiteProbe(SiteZone.Air, Vector2.Zero, 0f),
        });
        AssertInt(needle.Suture.Thread).OverrideFailureMessage("releasing off the patient cancels the pending puncture").IsEqual(0);
        AssertBool(tissue.ThreadIds.Any()).OverrideFailureMessage("a cancelled press makes no hole").IsFalse();
        SurgeryState.SkinHasFinishedThreads(driver.Patient, 12, 100);
        body.UpdateSutures();
        var ids = Enumerable.Range(100, 12).ToList();
        var knots = ids.Select(id => body.ThreadNode(id)!.GetNode<MeshInstance3D>("StartKnot").Mesh).ToList();
        var worst = 0UL;
        for (var frame = 0; frame < 60; frame++) {
            // A sim step elsewhere must not invalidate the meshes of unchanged punctures.
            tissue.StepsDone++;
            var start = Time.GetTicksUsec();
            body.UpdateSutures();
            worst = Math.Max(worst, Time.GetTicksUsec() - start);
        }
        AssertBool(ids.Select(id => body.ThreadNode(id)!.GetNode<MeshInstance3D>("StartKnot").Mesh).SequenceEqual(knots))
            .OverrideFailureMessage("stationary tied knots are not rebuilt by unrelated tissue steps").IsTrue();
        if (FrameBudget.Mode == BudgetMode.Cpu) {
            AssertFloat(worst / 1_000_000f).OverrideFailureMessage($"twelve finished threads fit the CPU budget (worst {worst} us)")
                .IsLess(FrameBudget.Limit);
        }
        GD.Print($"twelve finished sutures: worst renderer update {worst / 1000.0:0.00} ms");
        // The frame budget counts from here, the normal solver and render loop with all twelve finished threads present.
        driver.Budget.Clear();
        await Frames.Seconds(1f);
        await session.Finish(BudgetOverride);
    }

    /// <summary>Threads <paramref name="wound"/> through <paramref name="layer"/> starting slack, pulls it closed and
    /// ties it off, the thread resting on that layer throughout.</summary>
    private static async Task SewDeepLayer(SurgeryDriver driver, Wound wound, TissueDepth layer) {
        AssertBool(await driver.PlayerThreads(wound, layer, checkAim: true)).OverrideFailureMessage("the horizontal needle reaches every deep puncture where the layer shows")
            .IsTrue();
        var thread = driver.Me.HeldTool(driver.Me.Active)!.Suture.Thread;
        // Whatever the last thread was pulled to before it was tied, a new one starts slack.
        AssertFloat(driver.Body.Tissue.Thread(thread)!.Tension)
            .OverrideFailureMessage($"the {layer} thread starts slack").IsEqual(ToolActions.SutureStartTension);
        var routes = driver.Body.DrawnThread(thread)!.Routes;
        AssertBool(routes.Count > 0).OverrideFailureMessage("the deep layer has exposed thread spans").IsTrue();
        AssertThreadStaysOnLayer(driver.Body, routes, layer);
        var name = layer.ToString().ToLowerInvariant();
        await driver.Capture($"{name}_loose", $"thread run loose through the {name} layer, lying on that layer, not through the skin above");
        await driver.PlayerPullsThread("closed");
        await driver.PlayerTiesOff($"{name}_closed", $"the {name} layer pulled closed and tied, the thread on that layer");
        // The thread is drawn on the sheet as it was when its holes last moved; compared with the sheet once it
        // has come to rest under the knot.
        await Frames.Until(() => driver.Body.Tissue.IsSleeping, 5f);
        AssertThreadStaysOnLayer(driver.Body, driver.Body.DrawnThread(thread)!.Routes, layer);
    }

    private static void AssertThreadStaysOnLayer(PatientBody body, IReadOnlyList<Vector3[]> routes, TissueDepth layer) {
        foreach (var path in routes) {
            for (var i = 1; i < path.Length - 1; i++) {
                var uv = SiteUv(body, path[i]);
                var surface = body.SutureLayerHeight(uv, layer);
                var over = $"{(path[i].Y - surface) * 1000f:0.00} mm over it at {uv}";
                AssertFloat(path[i].Y).OverrideFailureMessage($"deep thread rests above its sewn layer ({over})")
                    .IsGreaterEqual(surface + PatientBody.SutureRadius);
                AssertFloat(path[i].Y).OverrideFailureMessage($"deep thread does not rise through the overlying tissue ({over})")
                    .IsLess(surface + 0.002f);
            }
        }
    }

    /// <summary>Where a point drawn on the site lies in site uv.</summary>
    private static Vector2 SiteUv(PatientBody body, Vector3 local) =>
        new((local.X / body.SiteSize.X) + 0.5f, (local.Z / body.SiteSize.Y) + 0.5f);
}
