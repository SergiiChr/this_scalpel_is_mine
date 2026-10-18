namespace Scalpel.Tests.Patients;

/// <summary>Fixed surface bleeds on three body slopes: a small seep stays visible beside a large pool and thin
/// downhill films. Stopping their supply leaves the deposited blood on the skin.</summary>
[TestSuite, RequireGodotRuntime]
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
        async Task AdvanceBlood(float seconds) {
            for (var frame = 0; frame < Mathf.RoundToInt(seconds * 60f); frame++) {
                await Frames.NextProcess();
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
        if (measureGpu) {
            RenderingServer.ViewportSetMeasureRenderTime(viewport, false);
            AssertInt(gpu.Count).OverrideFailureMessage("the host GPU supplies render timestamps").IsGreater(0);
            gpu.Sort();
            GD.Print($"blood GPU {scenario} ({RenderingServer.GetVideoAdapterName()}, {RenderingServer.GetCurrentRenderingMethod()}): "
                + $"mean {gpu.Average():0.000} ms, p95 {gpu[(int)(gpu.Count * 0.95)]:0.000} ms, max {gpu[^1]:0.000} ms");
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
