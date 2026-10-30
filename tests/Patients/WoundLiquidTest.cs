namespace Scalpel.Tests.Patients;

[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("liquids"), TestCategory("visual_confirmation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class WoundLiquidTest {
    // The identical camera with no liquid reached 47.7 ms on the RTX 4080. Preserve that pre-existing scene
    // allowance with 30% headroom, rounded to a half-factor; CPU liquid work keeps the strict calibrated limit.
    private static readonly BudgetAllowance RenderAllowance = new("deep-wound scene spikes also measured without any liquid", 4f);

    // The software screenshot backend reached 18.5 ms; the same headless fixture stayed below 6 ms.
    // This capture-only allowance uses 30% headroom rounded to a half-factor and never applies to headless CPU.
    private static readonly BudgetAllowance ScreenshotAllowance = new("software screenshot renderer synchronizes dynamic mesh updates", 4.5f);

    [TestCase(5f, Timeout = Limits.Slow)]
    [TestCase(50f, Timeout = Limits.Slow)]
    public async Task IrrigationLiquidFollowsAndDrainsFromADeepOpening(float initialMl) {
        var captureVisuals = initialMl == 5f;
        var session = await ToolSession.Start("appendectomy", "wound_liquids", captureUntouched: captureVisuals);
        var driver = session.Driver;
        var body = driver.Body;
        var at = new Vector2(0.5f, 0.5f);
        SurgeryState.SkinIsCut(driver.Patient, at - new Vector2(0.08f, 0f), at + new Vector2(0.08f, 0f), 0.5f);
        SurgeryState.TissueIsPulled(driver.Patient, at + new Vector2(0f, body.MetersToUv(0.002f)), new Vector3(0f, 0f, 0.02f));
        AssertBool(await Frames.Until(() => body.Tissue.IsOpen(at, TissueDepth.Fat), 10f)).OverrideFailureMessage("the fixture opens through skin and fat onto the muscle").IsTrue();
        driver.Patient.SetPhysicsProcess(false);
        body.Blood.Sources.Clear();
        body.Blood.Pools.Clear();
        SurgeryState.CavityHoldsBlood(driver.Patient, 0f);
        body.SetCavityBlood(0f);
        await Frames.Process(12);
        if (captureVisuals) {
            await session.CaptureAt("dry_opening", "an open deep wound before clear liquid collects inside", driver.SitePoint(at), 0.17f);
        }
        // Keep the prepared wound fixed while measuring liquid accumulation and drainage.
        body.SetProcess(false);
        body.Blood.Irrigate(body.UvToWorld(at) - Vector3.Up, 5f, 0f);
        AssertBool(body.Site.GetChildren().OfType<MeshInstance3D>().Any(p => p.Name == "IrrigationPool")).IsFalse();
        if (FrameBudget.Mode == BudgetMode.Render) {
            var camera = new Camera3D { Fov = 35f, Near = 0.01f, CullMask = RenderLayers.Normal };
            driver.Surgery.AddChild(camera);
            camera.GlobalPosition = driver.SitePoint(at) + body.Site.GlobalBasis.Y.Normalized() * 0.17f;
            camera.LookAt(driver.SitePoint(at), body.Site.GlobalBasis.Z.Normalized());
            camera.Current = true;
            await Frames.Process(12);
        }
        driver.Note("clear irrigation gathering against the opening");
        driver.Budget.Clear();
        // Set a measured irrigation dose directly: this case verifies the liquid contour, not syringe handling.
        body.Blood.Irrigate(body.UvToWorld(at, body.OpeningDepth(at, 0.015f) * 0.5f), initialMl, 0f);
        await Frames.Process(8);
        var pool = body.Site.GetChildren().OfType<MeshInstance3D>().Single(p => p.Name == "IrrigationPool");
        await driver.Unbudgeted(() => {
            var faces = pool.Mesh.GetFaces();
            AssertInt(faces.Length).OverrideFailureMessage("clear liquid has a connected surface inside the opening").IsGreater(6);
            AssertBool(faces.All(p => body.Tissue.OpeningDistance(body.LocalToUv(p)) > -0.00025f))
                .OverrideFailureMessage($"clear liquid follows the wound opening rather than a circular bead (minimum clearance {faces.Min(p => body.Tissue.OpeningDistance(body.LocalToUv(p)))})").IsTrue();
            return Task.CompletedTask;
        });
        var material = (StandardMaterial3D)pool.MaterialOverride;
        AssertFloat(material.AlbedoColor.A).OverrideFailureMessage("saline remains translucent").IsBetween(0.1f, 0.8f);
        var clearRed = material.AlbedoColor.R;
        if (captureVisuals) {
            await session.CaptureAt("clear_pool", "translucent irrigation gathers along the deep wound boundary, with tissue still visible beneath it", driver.SitePoint(at), 0.17f);
        }
        body.Blood.Irrigate(body.UvToWorld(at), initialMl * 2f, 1f);
        await Frames.Process(8);
        AssertFloat(material.AlbedoColor.R).OverrideFailureMessage("blood carried into the clear liquid changes its tint").IsGreater(clearRed + 0.05f);
        if (captureVisuals) {
            await session.CaptureAt("mixed_pool", "blood and irrigation share the same wound-shaped pool, with a darker translucent surface", driver.SitePoint(at), 0.17f);
        }
        if (FrameBudget.Mode == BudgetMode.Render) {
            await driver.Unbudgeted(async () => {
                var viewport = driver.GetViewport().GetViewportRid();
                var gpu = new List<double>();
                RenderingServer.ViewportSetMeasureRenderTime(viewport, true);
                for (var frame = 0; frame < 60; frame++) {
                    await Frames.NextProcess();
                    var ms = RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport);
                    if (ms > 0.0) { gpu.Add(ms); }
                }
                RenderingServer.ViewportSetMeasureRenderTime(viewport, false);
                AssertInt(gpu.Count).IsGreater(0);
                gpu.Sort();
                GD.Print($"wound liquid GPU ({RenderingServer.GetVideoAdapterName()}): mean {gpu.Average():0.000} ms, "
                    + $"p95 {gpu[(int)(gpu.Count * 0.95)]:0.000} ms, max {gpu[^1]:0.000} ms");
            });
        }
        await Frames.Seconds(9f);
        AssertBool(pool.Visible).OverrideFailureMessage("irrigation drains after supply stops").IsFalse();
        if (captureVisuals) {
            await session.CaptureAt("drained", "the deep wound remains open after irrigation drains, with no floating liquid surface", driver.SitePoint(at), 0.17f);
        }
        body.Blood.Irrigate(body.UvToWorld(at, body.OpeningDepth(at, 0.015f) * 0.5f), 1f, 0f);
        await Frames.Process(8);
        AssertBool(pool.Visible).OverrideFailureMessage("fresh irrigation can gather again after the wound drains").IsTrue();
        AssertFloat(material.AlbedoColor.R).OverrideFailureMessage("drained blood does not tint the next clear dose")
            .IsEqualApprox(clearRed, 0.00001f);
        body.Blood.Irrigate(body.UvToWorld(at), 100f, 1f);
        await Frames.Process(8);
        var saturatedRed = material.AlbedoColor.R;
        body.Blood.Irrigate(body.UvToWorld(at), 100f, 0f);
        await Frames.Process(8);
        AssertFloat(material.AlbedoColor.R).OverrideFailureMessage("fresh saline dilutes blood even after the retained pool reaches its volume cap")
            .IsLess(saturatedRed - 0.05f);
        await session.Finish(FrameBudget.Mode == BudgetMode.Render ? RenderAllowance
            : FrameBudget.Mode == BudgetMode.Cpu && KeyFrames.Wanted() ? ScreenshotAllowance : null);
    }

    // The same live tissue-pull fixture exceeds the calibrated 6 ms CPU limit even with blood processing
    // disabled (6.9 ms). Its combined worst frame was 10.6 ms, including at most 1.35 ms of liquid work; with the
    // skin on XPBD it's 13.7 to 15.4 ms (5.9 ms limit on this machine).
    // Keep this pre-existing tissue update bounded with 30% headroom rounded to a half-factor.
    private static readonly BudgetAllowance TissuePullAllowance = new("live tissue-pull scene exceeds CPU budget without liquid processing", 3.5f);

    [TestCase(Timeout = Limits.Slow)]
    public async Task WoundLiquidsRefreshForGeometryChangesRatherThanSolverTicks() {
        var session = await ToolSession.Start("appendectomy", "wound_liquid_refresh", captureUntouched: false);
        var driver = session.Driver;
        var patient = driver.Patient;
        var body = driver.Body;
        var at = new Vector2(0.5f, 0.5f);
        var wound = SurgeryState.SkinIsCut(patient, at - new Vector2(0.08f, 0f), at + new Vector2(0.08f, 0f), 0.5f);
        SurgeryState.TissueIsPulled(patient, at + new Vector2(0f, body.MetersToUv(0.002f)), new Vector3(0f, 0f, 0.02f));
        AssertBool(await Frames.Until(() => body.Tissue.IsOpen(at, TissueDepth.Fat), 10f))
            .OverrideFailureMessage("the fixture opens a deep incision").IsTrue();
        patient.SetPhysicsProcess(false);
        SurgeryState.WoundIsClamped(wound);
        body.Blood.Sources.Clear();
        body.Blood.Pools.Clear();
        body.SetCavityBlood(0f);
        AssertBool(await Frames.Until(() => body.Tissue.IsSleeping, 10f))
            .OverrideFailureMessage("the pulled wound settles before the refresh checks").IsTrue();

        body.Blood.Irrigate(body.UvToWorld(at, body.OpeningDepth(at, 0.015f) * 0.5f), 100f, 0f);
        var rinseReady = await Frames.Until(() => body.Blood.RinseMeshRefreshes(wound.Id) > 0, 5f);
        AssertBool(rinseReady).OverrideFailureMessage("irrigation draws its opening contour").IsTrue();
        var cutRefreshes = body.Blood.CutPoolMeshRefreshes;
        body.Blood.Pools.Add(new CutPool(at, body.OpeningDepth(at, 0.015f), 0.6f));
        var cutReady = await Frames.Until(() => body.Blood.CutPoolMeshRefreshes > cutRefreshes, 5f);
        AssertBool(cutReady).OverrideFailureMessage("blood draws a recessed surface in this opening").IsTrue();
        await driver.Unbudgeted(() => {
            var faces = body.Site.GetNode<MeshInstance3D>("CutPools").Mesh.GetFaces();
            AssertInt(faces.Length).OverrideFailureMessage("the wound has a rendered blood surface").IsGreater(6);
            AssertBool(faces.All(point => body.Tissue.OpeningDistance(body.LocalToUv(point)) > -0.001f))
                .OverrideFailureMessage("the blood surface stays inside the local wound opening").IsTrue();
            return Task.CompletedTask;
        });

        // Wake the solver while the opening is at rest. Solver steps advance, but its sampled contour and support
        // stay within the sub-millimetre refresh tolerance.
        driver.Note("settled wound contours survive solver ticks and refresh for a tissue pull");
        driver.Budget.Clear();
        var rinseRefreshes = body.Blood.RinseMeshRefreshes(wound.Id);
        cutRefreshes = body.Blood.CutPoolMeshRefreshes;
        var stepsBefore = body.Tissue.StepsDone;
        for (var frame = 0; frame < 24; frame++) {
            body.Tissue.Wake();
            await Frames.NextProcess();
        }
        AssertInt(body.Tissue.StepsDone).OverrideFailureMessage("the settled tissue still advances solver steps")
            .IsGreater(stepsBefore);
        AssertInt(body.Blood.RinseMeshRefreshes(wound.Id)).OverrideFailureMessage("stationary solver steps reuse the irrigation contour")
            .IsEqual(rinseRefreshes);
        AssertInt(body.Blood.CutPoolMeshRefreshes).OverrideFailureMessage("stationary solver steps reuse the blood contour")
            .IsEqual(cutRefreshes);

        var pulledAt = at + new Vector2(body.MetersToUv(0.025f), 0f);
        SurgeryState.TissueIsPulled(patient, pulledAt, new Vector3(0.015f, 0f, 0f));
        var moved = await Frames.Until(() => body.Blood.RinseMeshRefreshes(wound.Id) > rinseRefreshes
            && body.Blood.CutPoolMeshRefreshes > cutRefreshes, 5f);
        AssertBool(moved).OverrideFailureMessage("both wound-liquid meshes rebuild when a real tissue pull changes their contour")
            .IsTrue();
        await session.Finish(FrameBudget.Mode == BudgetMode.Render ? RenderAllowance
            : FrameBudget.Mode == BudgetMode.Cpu && KeyFrames.Wanted() ? ScreenshotAllowance
            : FrameBudget.Mode == BudgetMode.Cpu ? TissuePullAllowance : null);
    }
}
