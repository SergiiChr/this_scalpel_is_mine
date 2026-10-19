namespace Scalpel.Tests.Patients;

[TestSuite, RequireGodotRuntime]
[TestCategory("liquid_contours")]
[TestCategory("liquids"), TestCategory("visual_confirmation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class WoundLiquidTest {
    // The identical camera with no liquid reached 47.7 ms on the RTX 4080. Preserve that pre-existing scene
    // allowance with 30% headroom, rounded to a half-factor; CPU liquid work keeps the strict calibrated limit.
    private static readonly BudgetAllowance RenderAllowance = new("deep-wound scene spikes also measured without any liquid", 4f);

    // The software screenshot backend reached 18.5 ms; the same headless fixture stayed below 6 ms.
    // This capture-only allowance uses 30% headroom rounded to a half-factor and never applies to headless CPU.
    private static readonly BudgetAllowance ScreenshotAllowance = new("software screenshot renderer synchronizes dynamic mesh updates", 4.5f);

    [TestCase]
    public async Task IrrigationLiquidFollowsAndDrainsFromADeepOpening() {
        var session = await ToolSession.Start("appendectomy", "wound_liquids");
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
        await session.CaptureAt("dry_opening", "an open deep wound before clear liquid collects inside", driver.SitePoint(at), 0.17f);
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
        body.Blood.Irrigate(body.UvToWorld(at, body.OpeningDepth(at, 0.015f) * 0.5f), 5f, 0f);
        await Frames.Process(8);
        var pool = body.Site.GetChildren().OfType<MeshInstance3D>().Single(p => p.Name == "IrrigationPool");
        await driver.Unbudgeted(() => {
            var faces = pool.Mesh.GetFaces();
            AssertInt(faces.Length).OverrideFailureMessage("clear liquid has a connected surface inside the opening").IsGreater(6);
            AssertBool(faces.All(p => body.Tissue.OpeningDistance(body.LocalToUv(p)) > -0.00025f))
                .OverrideFailureMessage("clear liquid follows the wound opening rather than a circular bead").IsTrue();
            return Task.CompletedTask;
        });
        var material = (StandardMaterial3D)pool.MaterialOverride;
        AssertFloat(material.AlbedoColor.A).OverrideFailureMessage("saline remains translucent").IsBetween(0.1f, 0.8f);
        var clearRed = material.AlbedoColor.R;
        await session.CaptureAt("clear_pool", "translucent irrigation gathers along the deep wound boundary, with tissue still visible beneath it", driver.SitePoint(at), 0.17f);
        body.Blood.Irrigate(body.UvToWorld(at), 10f, 1f);
        await Frames.Process(8);
        AssertFloat(material.AlbedoColor.R).OverrideFailureMessage("blood carried into the clear liquid changes its tint").IsGreater(clearRed + 0.05f);
        await session.CaptureAt("mixed_pool", "blood and irrigation share the same wound-shaped pool, with a darker translucent surface", driver.SitePoint(at), 0.17f);
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
        await session.CaptureAt("drained", "the deep wound remains open after irrigation drains, with no floating liquid surface", driver.SitePoint(at), 0.17f);
        body.Blood.Irrigate(body.UvToWorld(at, body.OpeningDepth(at, 0.015f) * 0.5f), 1f, 0f);
        await Frames.Process(8);
        AssertBool(pool.Visible).OverrideFailureMessage("fresh irrigation can gather again after the wound drains").IsTrue();
        AssertFloat(material.AlbedoColor.R).OverrideFailureMessage("drained blood does not tint the next clear dose")
            .IsEqualApprox(clearRed, 0.00001f);
        await session.Finish(FrameBudget.Mode == BudgetMode.Render ? RenderAllowance
            : FrameBudget.Mode == BudgetMode.Cpu && KeyFrames.Wanted() ? ScreenshotAllowance : null);
    }
}
