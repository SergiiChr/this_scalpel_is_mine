namespace Scalpel.Tests.Tools;

/// <summary>
/// The needle as a player uses it: a running thread clicked through a cut hole by hole, pulled on the wheel and tied
/// off with a long hold, layer by layer through a cut into the belly. Key frames: the site untouched, after the first
/// two holes and tied off. Review those for one continuous thread dipping into its holes, edges meeting in a slight
/// ridge without passing through each other, and a narrow incision line still visible beneath the tied thread rather
/// than an open red gap or seamless skin.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("smoke"), TestCategory("tool_needle"), TestCategory("tissue_modification")]
[TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class NeedleTest
{
    private const string BudgetBroken = "known to go over the frame budget, not profiled yet";

    [TestCase(Timeout = Limits.Slow)]
    public async Task RunningSutureClosesAForearmCut()
    {
        var session = await ToolSession.Start("hand_stitch", "needle/forearm_skin");
        var driver = session.Driver;
        var patient = driver.Patient;
        var body = driver.Body;
        var tissue = body.Tissue;
        var wound = patient.Wounds[0];
        var needle = (await driver.PlayerRequestsItem("needle"))!;
        AssertFloat(driver.Me.AimPoint().DistanceTo(needle.TipPosition())).OverrideFailureMessage("the aim point is the needle tip")
            .IsLess(0.0001f);
        AssertObject(needle.FindChild("Thread", true, false)).OverrideFailureMessage("an idle needle has no placeholder thread")
            .IsNull();
        var jawA = (Node3D)needle.FindChild("JawA", true, false);
        var jawB = (Node3D)needle.FindChild("JawB", true, false);
        AssertFloat(Mathf.Abs(jawA.Transform.Basis.GetEuler().Y) + Mathf.Abs(jawB.Transform.Basis.GetEuler().Y))
            .OverrideFailureMessage("the holder stays closed around its needle").IsLess(0.001f);
        var sutures = body.Site.GetNode("Sutures");
        AssertInt(sutures.GetChildCount()).OverrideFailureMessage("no live thread is drawn before the needle is anchored").IsEqual(0);
        await driver.PlayerWalksTo(driver.SitePoint(wound.Midpoint));
        await driver.PlayerReaches(driver.SitePoint(wound.Midpoint));
        AssertInt(sutures.GetChildCount()).OverrideFailureMessage("hovering the needle over a wound does not create thread")
            .IsEqual(0);
        AssertFloat(Mathf.Abs(needle.GlobalBasis.Z.Dot(Vector3.Up)))
            .OverrideFailureMessage("the loaded needle holder rests nearly horizontal").IsLess(0.35f);
        AssertFloat(driver.Me.AimPoint().DistanceTo(needle.TipPosition()))
            .OverrideFailureMessage("the horizontal pose keeps the aiming point on the sharp tip").IsLess(0.0001f);
        await session.CaptureView("needle_ready");
        AssertBool(await driver.PlayerThreads(wound, TissueDepth.Skin)).OverrideFailureMessage("holes go in beside the cut").IsTrue();
        var thread = needle.Suture.Thread;
        var info = tissue.Thread(thread)!;
        AssertInt(info.Springs.Count).OverrideFailureMessage("one thread runs through every hole, a span between each two")
            .IsEqual(info.Anchors.Count - 1);
        var drawn = body.ThreadNode(thread)!;
        var live = drawn.GetNode<MeshInstance3D>("Live");
        AssertObject(live.Mesh).OverrideFailureMessage("the newest hole has a live strand").IsNotNull();
        AssertBool(live.GetAabb().Grow(0.001f).HasPoint(body.Site.ToLocal(needle.TipPosition())))
            .OverrideFailureMessage("the live strand ends at the needle tip").IsTrue();
        // Sub-threshold moves must add up against the drawn endpoint, not disappear one frame at a time. These are thirty
        // made-up updates in one frame: the frame budget isn't sampled during them.
        var pose = needle.GlobalTransform;
        for (var frame = 0; frame < 30; frame++)
        {
            needle.GlobalPosition += Vector3.Right * 0.0001f;
            body.UpdateSutures();
            AssertFloat(body.DrawnThread(thread)!.LivePath[^1].DistanceTo(body.Site.ToLocal(needle.TipPosition())))
                .OverrideFailureMessage("slow movement keeps the strand within the redraw threshold").IsLessEqual(0.00021f);
        }
        needle.GlobalTransform = pose;
        body.UpdateSutures();
        var loose = body.DrawnThread(thread)!;
        var loosePath = loose.Routes[0];
        AssertFloat(loose.PressureAmount).OverrideFailureMessage("loose thread puts no visible compression around its holes")
            .IsEqual(0f);
        AssertObject(drawn.GetNode<MeshInstance3D>("StartKnot").Mesh)
            .OverrideFailureMessage("the beginning of the running thread has a compact anchor knot").IsNotNull();
        AssertThreadClearsSkin(body, loosePath, "loose exposed thread");
        AssertFloat(wound.Closure).OverrideFailureMessage("the thread as it comes leaves the cut open").IsLess(0.99f);
        var looseGap = tissue.GapAlong(wound.Points, 0.03f, TissueDepth.Skin);
        await driver.PlayerPullsThread("closed");
        // Live, the thread only pulls its holes: the edges between them meet once it's tied off (below).
        AssertFloat(tissue.GapAlong(wound.Points, 0.03f, TissueDepth.Skin))
            .OverrideFailureMessage("pulled until it reads closed, the thread draws the edges together").IsLess(looseGap);
        var tight = body.DrawnThread(thread)!;
        AssertFloat(PathLength(tight.Routes[0])).OverrideFailureMessage("tightening takes visible slack out of the thread")
            .IsLess(PathLength(loosePath));
        AssertFloat(tight.PressureAmount).OverrideFailureMessage("closing pressure appears around every puncture").IsGreater(0f);
        AssertInt(tight.PressureHoles).OverrideFailureMessage("every pressured hole has a skin crease sprite")
            .IsEqual(info.Anchors.Count);
        var marks = drawn.GetNode<MultiMeshInstance3D>("Pressure");
        AssertBool(marks.Visible).OverrideFailureMessage("the pressure sprites render while the thread holds").IsTrue();
        AssertInt(marks.Multimesh.InstanceCount).OverrideFailureMessage("the renderer has one pressure sprite per puncture")
            .IsEqual(info.Anchors.Count);
        AssertThreadClearsSkin(body, tight.Routes[0], "tight exposed thread");
        await driver.PlayerTiesOff();
        await Frames.Seconds(1f);
        AssertBool(patient.SutureDone(thread) && needle.Suture.Thread == 0).OverrideFailureMessage("a long hold ties the thread off")
            .IsTrue();
        AssertFloat(tissue.GapAlong(wound.Points, 0.03f, TissueDepth.Skin))
            .OverrideFailureMessage("the tied off thread holds the edges together").IsLess(TissueSim.OpenGap);
        AssertFloat(wound.Closure).OverrideFailureMessage("so the cut is closed along its whole length").IsGreater(0.99f);
        AssertInt(tissue.Severed.Count(s => tissue.CutDepth(s) >= TissueDepth.Skin))
            .OverrideFailureMessage("no split edge is left along the seam").IsEqual(0);
        AssertFloat(tissue.SutureLip.Max())
            .OverrideFailureMessage("the pressed edges rise into a lip instead of passing through each other").IsGreater(0.0005f);
        var map = body.WoundMap;
        AssertFloat(map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, wound.Midpoint))
            .OverrideFailureMessage("no blood stands in the closed seam").IsLess(0.12f);
        var seam = map.Value(WoundMap.Layer.Wounds, WoundMap.Cut, wound.Midpoint);
        AssertFloat(seam).OverrideFailureMessage("a tied wound keeps a visible incision line").IsGreater(0.06f);
        AssertFloat(seam).OverrideFailureMessage("the incision line is healed-looking rather than an open groove").IsLess(0.16f);
        AssertFloat(map.Value(WoundMap.Layer.Seams, WoundMap.ClosedSeam, wound.Midpoint))
            .OverrideFailureMessage("the sewn incision has an explicit seam mask independent of closure quality").IsGreater(0.9f);
        AssertBool(patient.Flags.ContainsKey("neat_closure")).OverrideFailureMessage("a closed, tied off thread is a neat closure")
            .IsTrue();
        AssertObject(live.Mesh).OverrideFailureMessage("tying off releases the live end from the needle").IsNull();
        AssertObject(drawn.GetNode<MeshInstance3D>("StartKnot").Mesh)
            .OverrideFailureMessage("the tied thread keeps its starting knot").IsNotNull();
        AssertObject(drawn.GetNode<MeshInstance3D>("EndKnot").Mesh)
            .OverrideFailureMessage("the tied thread ends in a second visible knot").IsNotNull();
        var routes = body.DrawnThread(thread)!.Routes;
        AssertInt(routes.Count).OverrideFailureMessage("the running thread alternates exposed and subcutaneous spans")
            .IsEqual((info.Springs.Count + 1) / 2);
        for (var i = 0; i < routes.Count; i++)
        {
            AssertThreadClearsSkin(body, routes[i], $"exposed span {i}");
        }
        await session.Finish(BudgetBroken);
    }

    /// <summary>Too loose leaves a gap, too tight tears through, and skin pulled shut over open muscle tears through
    /// too.</summary>
    [TestCase(Timeout = Limits.Slow)]
    public async Task ThreadTensionLimits()
    {
        var session = await ToolSession.Start("appendectomy", "needle/tension");
        var driver = session.Driver;
        AssertFloat(ToolActions.SutureTensionStep).OverrideFailureMessage("the thread wheel has fine tension steps")
            .IsLessEqual(0.04f);
        var patient = driver.Patient;
        var tissue = driver.Body.Tissue;
        var loose = SurgeryState.SkinIsCut(patient, new Vector2(0.3f, 0.3f), new Vector2(0.55f, 0.3f), 0.5f);
        var tight = SurgeryState.SkinIsCut(patient, new Vector2(0.3f, 0.5f), new Vector2(0.55f, 0.5f), 0.5f);
        var deep = SurgeryState.SkinIsCut(patient, new Vector2(0.3f, 0.7f), new Vector2(0.55f, 0.7f), 1f);
        await Frames.Seconds(1f);
        await driver.Capture("cuts_before_sutures");
        var needle = (await driver.PlayerRequestsItem("needle"))!;

        await driver.PlayerThreads(loose, TissueDepth.Skin);
        await driver.PlayerTiesOff("loose_tied");
        await Frames.Seconds(1f);
        AssertFloat(loose.Closure).OverrideFailureMessage("tied off loose, the thread leaves the cut open").IsLess(0.99f);
        AssertFloat(tissue.GapAlong(loose.Points, 0.03f, TissueDepth.Skin)).OverrideFailureMessage("and its edges apart")
            .IsGreater(TissueSim.OpenGap);
        AssertBool(patient.Flags.ContainsKey("neat_closure")).OverrideFailureMessage("a loose closure isn't a neat one").IsFalse();

        await driver.PlayerThreads(tight, TissueDepth.Skin);
        var thread = needle.Suture.Thread;
        await driver.PlayerPullsThread("too tight");
        AssertString(SewAction.ThreadState(needle)).OverrideFailureMessage("the hand status reads too tight before it tears")
            .IsEqual("too tight");
        AssertBool(patient.SutureDone(thread)).OverrideFailureMessage("too tight thread is still intact before further tightening")
            .IsFalse();
        AssertFloat(tight.Closure).OverrideFailureMessage("too tight still holds the cut closed").IsGreater(0.99f);
        AssertFloat(tissue.SutureLip.Max()).OverrideFailureMessage("too tight thread visibly puckers the joined skin")
            .IsGreater(0.0009f);
        var holes = tissue.Thread(thread)!.Anchors.Count;
        var drawn = driver.Body.DrawnThread(thread)!;
        AssertFloat(drawn.PressureAmount).OverrideFailureMessage("over-tight thread strongly marks the skin around its holes")
            .IsGreater(0.45f);
        AssertInt(drawn.PressureHoles).OverrideFailureMessage("every over-tight hole shows pressure").IsEqual(holes);
        AssertInt(driver.Body.ThreadNode(thread)!.GetNode<MultiMeshInstance3D>("Pressure").Multimesh.InstanceCount)
            .OverrideFailureMessage("every over-tight hole has a rendered pressure sprite").IsEqual(holes);
        await driver.Capture("too_tight_intact");
        var wounds = patient.Wounds.Count;
        for (var i = 0; i < 6 && !patient.SutureDone(thread); i++)
        {
            await SurgeryDriver.Notch(false);
        }
        AssertBool(patient.SutureDone(thread) && needle.Suture.Thread == 0)
            .OverrideFailureMessage("pulled tighter still, the thread tears through").IsTrue();
        AssertBool(tissue.Thread(thread)!.Springs.All(s => !tissue.SpringAt(s).Active))
            .OverrideFailureMessage("a torn thread lets go of every span").IsTrue();
        AssertFloat(tight.Closure).OverrideFailureMessage("and holds the cut no more").IsLess(0.01f);
        AssertInt(patient.Wounds.Count).OverrideFailureMessage("it tears the skin").IsGreater(wounds);
        AssertBool(driver.Surgery.Scoring.Entries.ContainsKey("suture_tear_through"))
            .OverrideFailureMessage("a thread torn through costs points").IsTrue();

        await driver.PlayerPullsThread("loose");
        await driver.PlayerThreads(deep, TissueDepth.Skin);
        AssertFloat(deep.Closure).OverrideFailureMessage("skin won't meet over open muscle").IsLess(0.2f);
        thread = needle.Suture.Thread;
        await driver.PlayerPullsThread("closed");
        AssertBool(patient.SutureDone(thread)).OverrideFailureMessage("pulled shut over open muscle, the skin thread tears through")
            .IsTrue();
        await session.Finish(BudgetBroken);
    }

    /// <summary>A cut through the belly wall sewn layer by layer from inside the opening: the muscle, the fat, then the
    /// skin.</summary>
    [TestCase(Timeout = Limits.Slow)]
    public async Task LayeredClosureOfACutIntoTheBelly()
    {
        var session = await ToolSession.Start("appendectomy", "needle/layers");
        var driver = session.Driver;
        var tissue = driver.Body.Tissue;
        // Where the belly's sides hang off the table the site isn't drawn at all: what the fat layer shows uncut.
        var wholeFat = tissue.Triangles(TissueDepth.Fat).Length;
        var wound = SurgeryState.SkinIsCut(driver.Patient, new Vector2(0.3f, 0.5f), new Vector2(0.7f, 0.5f), 1f);
        await Frames.Seconds(1f);
        var middle = wound.Midpoint;
        await driver.Capture("cut");
        await driver.PlayerRequestsItem("needle");
        await SewDeepLayer(driver, wound, TissueDepth.Muscle);
        AssertBool(tissue.MuscleOpenNear(middle, Patient.MuscleReach)).OverrideFailureMessage("the muscle thread closes the muscle")
            .IsFalse();
        AssertBool(driver.Body.IsOpen(middle)).OverrideFailureMessage("the sewn muscle closes the way into the belly").IsFalse();
        await SewDeepLayer(driver, wound, TissueDepth.Fat);
        AssertBool(tissue.FatOpenNear(middle, Patient.MuscleReach)).OverrideFailureMessage("a thread through the fat closes it")
            .IsFalse();
        AssertInt(tissue.Triangles(TissueDepth.Fat).Length).OverrideFailureMessage("the fat layer shows no opening").IsEqual(wholeFat);
        await driver.PlayerSews(wound, TissueDepth.Skin);
        await Frames.Seconds(1f);
        AssertFloat(wound.Closure).OverrideFailureMessage("the skin closes over the sewn layers").IsGreater(0.99f);
        AssertFloat(tissue.GapAlong(wound.Points, 0.03f, TissueDepth.Skin)).OverrideFailureMessage("and its edges meet")
            .IsLess(TissueSim.OpenGap);
        await session.Finish(BudgetBroken);
    }

    [TestCase(Timeout = Limits.Slow)]
    public async Task FinishedSutureRedrawAndCancelledPress()
    {
        var session = await ToolSession.Start("appendectomy", "needle/finished_threads");
        var driver = session.Driver;
        var body = driver.Body;
        var tissue = body.Tissue;
        var needle = (await driver.PlayerRequestsItem("needle"))!;
        var hand = new HandInput(true, true, 0, 0f, driver.Me.PeerId, new Modifiers());
        var sew = new SewAction();
        sew.Apply(new ToolStep(needle, hand, driver.Patient, 0.1f)
        {
            Pressed = true, Tip = needle.TipPosition(), Probe = new SiteProbe(SiteZone.Site, new Vector2(0.4f, 0.4f), 0f),
        });
        // Let go with the needle up by the tray, off the patient.
        sew.Apply(new ToolStep(needle, hand with { Trigger = false }, driver.Patient, 0f)
        {
            Released = true, Tip = needle.TipPosition(), Probe = new SiteProbe(SiteZone.Air, Vector2.Zero, 0f),
        });
        AssertInt(needle.Suture.Thread).OverrideFailureMessage("releasing off the patient cancels the pending puncture").IsEqual(0);
        AssertBool(tissue.ThreadIds.Any()).OverrideFailureMessage("a cancelled press makes no hole").IsFalse();
        SurgeryState.SkinHasFinishedThreads(driver.Patient, 12, 100);
        body.UpdateSutures();
        var ids = Enumerable.Range(100, 12).ToList();
        var knots = ids.Select(id => body.ThreadNode(id)!.GetNode<MeshInstance3D>("StartKnot").Mesh).ToList();
        var worst = 0UL;
        for (var frame = 0; frame < 60; frame++)
        {
            // A sim step elsewhere must not invalidate the meshes of unchanged punctures.
            tissue.StepsDone++;
            var start = Time.GetTicksUsec();
            body.UpdateSutures();
            worst = Math.Max(worst, Time.GetTicksUsec() - start);
        }
        AssertBool(ids.Select(id => body.ThreadNode(id)!.GetNode<MeshInstance3D>("StartKnot").Mesh).SequenceEqual(knots))
            .OverrideFailureMessage("stationary tied knots are not rebuilt by unrelated tissue steps").IsTrue();
        AssertInt((int)worst).OverrideFailureMessage($"twelve finished threads fit the 16 ms update budget (worst {worst} us)")
            .IsLess(16000);
        GD.Print($"twelve finished sutures: worst renderer update {worst / 1000.0:0.00} ms");
        // The frame budget counts from here, the normal solver and render loop with all twelve finished threads present.
        driver.Budget.Clear();
        await Frames.Seconds(1f);
        await session.Finish(BudgetBroken);
    }

    /// <summary>Threads <paramref name="wound"/> through <paramref name="layer"/>, pulls it closed and ties it off, the
    /// thread resting on that layer throughout.</summary>
    private static async Task SewDeepLayer(SurgeryDriver driver, Wound wound, TissueDepth layer)
    {
        AssertBool(await driver.PlayerThreads(wound, layer)).OverrideFailureMessage("the horizontal needle reaches every deep puncture")
            .IsTrue();
        var thread = driver.Me.HeldTool(driver.Me.Active)!.Suture.Thread;
        var routes = driver.Body.DrawnThread(thread)!.Routes;
        AssertBool(routes.Count > 0).OverrideFailureMessage("the deep layer has exposed thread spans").IsTrue();
        AssertThreadStaysOnLayer(driver.Body, routes, layer);
        var name = layer.ToString().ToLowerInvariant();
        await driver.Capture($"{name}_loose");
        await driver.PlayerPullsThread("closed");
        await driver.PlayerTiesOff($"{name}_closed");
        AssertThreadStaysOnLayer(driver.Body, driver.Body.DrawnThread(thread)!.Routes, layer);
    }

    private static void AssertThreadStaysOnLayer(PatientBody body, IReadOnlyList<Vector3[]> routes, TissueDepth layer)
    {
        foreach (var path in routes)
        {
            for (var i = 1; i < path.Length - 1; i++)
            {
                var surface = body.SutureLayerHeight(SiteUv(body, path[i]), layer);
                AssertFloat(path[i].Y).OverrideFailureMessage("deep thread rests above its sewn layer")
                    .IsGreaterEqual(surface + PatientBody.SutureRadius);
                AssertFloat(path[i].Y).OverrideFailureMessage("deep thread does not rise through the overlying tissue")
                    .IsLess(surface + 0.002f);
            }
        }
    }

    private static void AssertThreadClearsSkin(PatientBody body, Vector3[] path, string label)
    {
        for (var i = 1; i < path.Length - 1; i++)
        {
            AssertFloat(path[i].Y).OverrideFailureMessage($"{label} stays above the skin at sample {i}")
                .IsGreaterEqual(body.SkinHeight(SiteUv(body, path[i])) + PatientBody.SutureRadius);
        }
    }

    /// <summary>Where a point drawn on the site (site-local) lies in site uv.</summary>
    private static Vector2 SiteUv(PatientBody body, Vector3 local) =>
        new((local.X / body.SiteSize.X) + 0.5f, (local.Z / body.SiteSize.Y) + 0.5f);

    private static float PathLength(Vector3[] path) =>
        Enumerable.Range(1, Math.Max(path.Length - 1, 0)).Sum(i => path[i - 1].DistanceTo(path[i]));
}
