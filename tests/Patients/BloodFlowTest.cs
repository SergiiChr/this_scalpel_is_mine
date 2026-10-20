namespace Scalpel.Tests.Patients;

/// <summary>Fixed surface bleeds on three body slopes: a small seep stays visible beside a large pool and thin
/// downhill films. Stopping their supply leaves the deposited blood on the skin.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("liquid_contours")]
[TestCategory("liquids"), TestCategory("visual_confirmation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class BloodFlowTest {
    // Before this change, these isolated whole-scene fixtures reached 26.7 ms CPU (6.0 ms calibrated limit)
    // and 37.2 ms rendered on the RTX 4080. Keep those existing spikes measured, with 30% headroom rounded up
    // to the next half-factor, while reporting GPU timestamps separately from whole-frame time.
    private static readonly BudgetAllowance CpuAllowance = new("whole-scene spikes also measured before the blood changes", 6f);
    private static readonly BudgetAllowance RenderAllowance = new("whole-scene spikes also measured before the blood changes", 3.5f);

    [TestCase("hand_stitch")]
    [TestCase("appendectomy")]
    [TestCase("knife_back")]
    public async Task SurfaceBloodFollowsTheBodySlope(string scenario) {
        // Keep the scenario's body and posture, without an existing incision or embedded tool hiding the surface.
        var original = Db.Scenario(scenario)!;
        ToolSession session;
        Db.ReplaceScenario(original with { Wounds = [], Targets = [] });
        try {
            session = await ToolSession.Start(scenario, $"blood_flow/{scenario}");
        } finally {
            Db.ReplaceScenario(original);
        }
        var driver = session.Driver;
        if (scenario == "knife_back") {
            // Across the back, its crest hides the far-side blood. Look along the spine instead.
            session.Along = driver.Body.Site.GlobalBasis.X;
        }
        await driver.Capture("untouched", "the site before any tool touches it: intact skin, nothing in it");
        await driver.PlayerLooksAt(driver.Body.Site.GlobalPosition);
        driver.Budget.Clear();
        var blood = driver.Body.Blood;
        var gpu = new List<double>(840);
        var viewport = driver.GetViewport().GetViewportRid();
        var measureGpu = FrameBudget.Mode == BudgetMode.Render;
        if (measureGpu) {
            // Measure visible blood on every posture, including the back turned away from the player's spawn.
            var site = driver.Body.Site;
            var camera = new Camera3D { Fov = 35f, Near = 0.01f, CullMask = RenderLayers.Normal };
            driver.Surgery.AddChild(camera);
            camera.GlobalPosition = site.GlobalPosition + site.GlobalBasis.Y.Normalized() * 0.45f;
            camera.LookAt(site.GlobalPosition, site.GlobalBasis.Z.Normalized());
            camera.Current = true;
            await Frames.Process(12);
            driver.Budget.Clear();
            RenderingServer.ViewportSetMeasureRenderTime(viewport, true);
        }
        var checkedDelayedBed = false;
        var capturedRunoff = false;
        async Task AdvanceBlood(float seconds) {
            for (var frame = 0; frame < Mathf.RoundToInt(seconds * 60f); frame++) {
                await Frames.NextProcess();
                if (scenario == "hand_stitch" && blood.ModelRunoff.HasGrowingStreak && !capturedRunoff) {
                    if (!blood.ModelRunoff.HasReachedEnd) {
                        AssertFloat(blood.GroundLiquidArea).OverrideFailureMessage("the bed stays clean until an attached stream reaches its end").IsEqual(0f);
                        checkedDelayedBed = true;
                    }
                    capturedRunoff = true;
                    var path = blood.ModelRunoff.GrowingPath!;
                    var at = driver.Body.Site.ToGlobal(path.Samples[0].At);
                    var side = (driver.Body.Site.GlobalBasis * path.Samples[0].Normal).Slide(Vector3.Up).Normalized();
                    await session.CaptureOblique("attached_runoff", "a narrowing wet streak runs down the actual curved limb, with its growing tip still attached before blood reaches the bed", at, side, 0.24f);
                }
                if (measureGpu) {
                    var ms = RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport);
                    if (ms > 0.0) {
                        gpu.Add(ms);
                    }
                }
            }
        }
        // Isolate the visual simulation from the scenario's wounds and changing vital signs.
        driver.Patient.SetPhysicsProcess(false);
        var seep = new Vector2(0.3f, 0.35f);
        var pool = new Vector2(0.55f, 0.45f);
        blood.Sources = [new(seep, 0.08f), new(pool, 2.8f), new(new Vector2(0.65f, 0.6f), 1.4f)];
        blood.Pools.Clear();
        driver.Note("surface bleeds supplying fresh blood");
        await AdvanceBlood(1f);
        AssertFloat(Amount(driver.Body, seep)).OverrideFailureMessage("even a small fresh seep remains visible").IsGreater(0.5f);
        var freshCover = 0f;
        await driver.Unbudgeted(() => {
            freshCover = Cover(driver.Body);
            return Task.CompletedTask;
        });
        await driver.Capture("fresh", "small fresh red seep beside a larger bleed, both visible against the skin");
        driver.Note("surface bleeds feeding pools and trails");
        await AdvanceBlood(7f);
        AssertFloat(Amount(driver.Body, pool)).OverrideFailureMessage("the significant bleed retains a dense pool").IsGreater(0.8f);
        await driver.Capture("flowing", "connected spreading wet patches and broad downhill streams, dense blood at the supplies, soft edges; no isolated wiry tendrils or repeated bright ticks");
        blood.Sources.Clear();
        driver.Note("surface supply stopped, trails settling");
        await AdvanceBlood(6f);
        await driver.Unbudgeted(() => {
            AssertFloat(Cover(driver.Body)).OverrideFailureMessage("the stopped supply leaves a spreading coating rather than disappearing")
                .IsGreater(freshCover * 1.5f);
            return Task.CompletedTask;
        });
        await driver.Capture("settled", "the supply stopped: persistent pool and thin residual films, no disappearing significant bleed");
        if (scenario == "hand_stitch") {
            await driver.Unbudgeted(() => {
                AssertBool(checkedDelayedBed).OverrideFailureMessage("attached narrowing runoff is visible before the first bed deposit").IsTrue();
                var paths = blood.ModelRunoff.Paths.Where(p => p.Length > 0.015f).ToArray();
                AssertInt(paths.Length).OverrideFailureMessage("several model-attached paths carry runoff").IsGreater(1);
                AssertBool(paths.Any(p => p.Samples.Any(s => !driver.Body.OnBody(driver.Body.WorldToUv(driver.Body.Site.ToGlobal(s.At))))))
                    .OverrideFailureMessage("the narrowing streaks continue beyond the surgical mesh onto model skin").IsTrue();
                foreach (var path in paths) {
                    AssertFloat(path.InletWidth / path.Samples[^1].Width).IsGreater(2f);
                    AssertFloat((driver.Body.Site.ToGlobal(path.Samples[0].At) - driver.Body.Site.ToGlobal(path.End)).Y).IsGreater(0.005f);
                    foreach (var sample in path.Samples) {
                        AssertFloat(driver.Body.LiquidContact.DistanceTo(sample.At))
                            .OverrideFailureMessage("every attached streak sample remains within a millimetre of actual model triangles").IsLess(0.001f);
                    }
                }
                return Task.CompletedTask;
            });
        }
        AssertFloat(blood.SurfaceAccountedMl).OverrideFailureMessage("skin, attached streaks, queued liquid, drops and ground deposits conserve the supplied milliliters")
            .IsEqualApprox(blood.SurfaceSuppliedMl, 0.01f);
        AssertFloat(blood.ModelRunoff.VisibleMl).IsLessEqual(blood.ModelRunoff.HeldMl + 0.0001f);
        if (measureGpu) {
            RenderingServer.ViewportSetMeasureRenderTime(viewport, false);
            AssertInt(gpu.Count).OverrideFailureMessage("the host GPU supplies render timestamps").IsGreater(0);
            gpu.Sort();
            GD.Print($"blood GPU {scenario} ({RenderingServer.GetVideoAdapterName()}, {RenderingServer.GetCurrentRenderingMethod()}): "
                + $"mean {gpu.Average():0.000} ms, p95 {gpu[(int)(gpu.Count * 0.95)]:0.000} ms, max {gpu[^1]:0.000} ms");
        }
        // A second, strong dose also exercises spurts: thrown blood must come out of the surface dose.
        var beforeDose = blood.SurfaceAccountedMl;
        var beforeSupply = blood.SurfaceSuppliedMl;
        blood.Sources = [new(pool, 12f)];
        var sawSpurt = false;
        for (var frame = 0; frame < 60; frame++) {
            await Frames.NextProcess();
            sawSpurt |= blood.Drops.Count > 0;
            foreach (var drop in blood.Drops) {
                var visible = blood.DropShapeMl * drop.Shape.X * drop.Shape.Y * drop.Shape.Z;
                AssertFloat(visible).OverrideFailureMessage("random drop shape preserves its supplied volume")
                    .IsEqualApprox(drop.Area * BloodFlow.DropMl / BloodFlow.PoolAreaPerDrop, 0.0001f);
            }
        }
        blood.Sources.Clear();
        AssertBool(sawSpurt).IsTrue();
        AssertFloat(blood.SurfaceAccountedMl - beforeDose)
            .OverrideFailureMessage("a later poured dose increases the accounted liquid inventory by exactly the added dose, including spurts")
            .IsEqualApprox(blood.SurfaceSuppliedMl - beforeSupply, 0.01f);
        if (scenario == "hand_stitch") {
            // No supporting model is a real detachment case, even when there is only one finite dose.
            using var emptySurface = new ArrayMesh();
            var emptyMesh = new MeshInstance3D { Mesh = emptySurface };
            var detached = new BodyRunoff(new BodyLiquidSurface([], []), driver.Body.Site, driver.Surgery.Room.BedSurface, emptyMesh);
            detached.Add(new Vector2(0f, 0.5f), Vector3.Up * 10f, 0.03f);
            var released = 0f;
            for (var frame = 0; frame < 120 && released <= 0f; frame++) {
                detached.Tick(1f / 60f);
                if (detached.TryRelease(out _, out var ml, out _)) { released += ml; }
                await Frames.NextProcess();
            }
            AssertFloat(released).OverrideFailureMessage("one unsupported finite dose releases after its pulse instead of becoming stuck in a zero-capacity path")
                .IsEqualApprox(0.03f, 0.00001f);
            AssertFloat(detached.HeldMl).IsEqual(0f);
            emptyMesh.Free();
        }
        await session.Finish(measureGpu ? RenderAllowance : CpuAllowance);
    }

    private static float Amount(PatientBody body, Vector2 uv) => body.WoundMap.Value(WoundMap.Layer.Fluids, WoundMap.Blood, uv);
    private static float Cover(PatientBody body) {
        var map = body.WoundMap;
        var visible = 0;
        for (var y = 2; y < map.Size; y += 4) {
            for (var x = 2; x < map.Size; x += 4) {
                if (map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, new Vector2(x, y) / map.Size) > 0.04f) { visible++; }
            }
        }
        return visible;
    }
}
