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
        await driver.BloodFilmsBuilt();
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
        AssertFloat(Amount(driver.Body, pool.Lerp(new Vector2(0.65f, 0.6f), 0.5f)))
            .OverrideFailureMessage("neighbouring bleeds join into a spreading film rather than staying separate parallel strips").IsGreater(0.5f);
        if (scenario == "hand_stitch") {
            await driver.Unbudgeted(() => {
                var continued = 0;
                for (var y = 0; y < SurfaceBlood.Size; y++) {
                    for (var x = 0; x < SurfaceBlood.Size; x++) {
                        var uv = new Vector2(x + 0.5f, y + 0.5f) / SurfaceBlood.Size;
                        if (!driver.Body.OnBody(uv) && driver.Body.LiquidSupport(uv) > 0f && driver.Body.BloodAt(uv) > 0.5f) { continued++; }
                    }
                }
                AssertInt(continued).OverrideFailureMessage("the continuous wet film crosses the surgical boundary before it narrows into attached model runoff").IsGreater(3);
                return Task.CompletedTask;
            });
        }
        await driver.Capture("flowing", "the bleeds spread into connected pools round the supplies, dense blood at the supplies, a sharp edge with a lighter rim; no isolated wiry tendrils or repeated bright ticks");
        blood.Sources.Clear();
        driver.Note("surface supply stopped, trails settling");
        await AdvanceBlood(6f);
        await driver.Unbudgeted(() => {
            AssertFloat(Cover(driver.Body)).OverrideFailureMessage("the stopped supply leaves a spreading coating rather than disappearing")
                .IsGreater(freshCover * 1.5f);
            return Task.CompletedTask;
        });
        await driver.Capture("settled", "the supply stopped: the pools stay where they spread, none of the significant bleed disappearing");
        if (scenario is "hand_stitch" or "knife_back") {
            blood.FinishFilms();
            var film = blood.BodyFilm;
            var skin = driver.Body.SkinMesh;
            // Down the upright back it runs on into the mattress, leaving the thin coat a run leaves behind.
            var coat = scenario == "knife_back" ? 0.15f : 0.5f;
            var wet = Enumerable.Range(0, film.Count).Where(k => film.CoverageAt(k) > coat).ToArray();
            AssertInt(wet.Length).OverrideFailureMessage($"blood runs on past the site's edge onto the body's own skin ({wet.Length} points wet, {film.HeldMl:0.00} ml on the body, {blood.GroundMl:0.00} ml off it)").IsGreater(10);
            var lowest = wet.MinBy(k => (skin.GlobalTransform * film.At(k)).Y);
            var at = skin.GlobalTransform * film.At(lowest);
            var side = (skin.GlobalBasis * film.NormalAt(lowest)).Slide(Vector3.Up).Normalized();
            await session.CaptureOblique("attached_runoff", "blood runs on from the site's edge down the body's side as one film, its edge fading, no separate streaks or stubs",
                at, side.IsZeroApprox() ? Vector3.Forward : side, 0.3f);
        }
        if (scenario == "knife_back") {
            await driver.Unbudgeted(() => {
                var bed = blood.BedFilm;
                var site = driver.Body.Site.GlobalPosition;
                var reached = Enumerable.Range(0, bed.Count).Count(k => bed.At(k).DistanceTo(site) < 0.4f && bed.CoverageAt(k) > 0.3f);
                AssertInt(reached).OverrideFailureMessage("blood running down the upright back reaches the mattress and spreads on it").IsGreater(0);
                return Task.CompletedTask;
            });
            await session.CaptureOblique("settled_contact", "blood on the upright back runs down onto the mattress and spreads there, no hard edge where they meet",
                new Vector3(0.18f, 0.86f, -0.11f), Vector3.Forward, 0.7f);
        }
        if (scenario == "hand_stitch") {
            await driver.Unbudgeted(() => {
                var rendered = driver.Body.SkinMesh.GetParent().GetNode<MeshInstance3D>("BodyFilm");
                AssertObject(rendered.Skin).OverrideFailureMessage("the body's film moves with the skin's bones").IsSame(driver.Body.SkinMesh.Skin);
                using var arrays = rendered.Mesh.SurfaceGetArrays(0);
                var vertexCount = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
                AssertInt(arrays[(int)Mesh.ArrayType.Bones].AsInt32Array().Length)
                    .OverrideFailureMessage("every drawn film vertex carries four skin influences").IsEqual(vertexCount * 4);
                AssertInt(arrays[(int)Mesh.ArrayType.Color].AsColorArray().Length)
                    .OverrideFailureMessage("every drawn film vertex carries its coverage").IsEqual(vertexCount);
                return Task.CompletedTask;
            });
        }
        var accounted = blood.SurfaceAccountedMl;
        AssertFloat(accounted).OverrideFailureMessage($"site, body, drops, bed and floor conserve the supplied milliliters: {accounted:0.000} ml accounted of {blood.SurfaceSuppliedMl:0.000} ml supplied")
            .IsEqualApprox(blood.SurfaceSuppliedMl, 0.01f);
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
                    .IsEqualApprox(drop.Ml, 0.0001f);
            }
        }
        blood.Sources.Clear();
        AssertBool(sawSpurt).IsTrue();
        var accountedDose = blood.SurfaceAccountedMl - beforeDose;
        var suppliedDose = blood.SurfaceSuppliedMl - beforeSupply;
        AssertFloat(accountedDose)
            .OverrideFailureMessage($"a later poured dose increases the accounted liquid inventory by exactly the added dose, including spurts: {accountedDose:0.000} ml accounted of {suppliedDose:0.000} ml supplied")
            .IsEqualApprox(suppliedDose, 0.01f);
        await session.Finish(measureGpu ? RenderAllowance : CpuAllowance);
    }

    [TestCase]
    public async Task BodyFilmRunsDownhillInTheCurrentPose() {
        var session = await ToolSession.Start("hand_stitch", "blood_flow/body_film_pose", captureUntouched: false);
        var driver = session.Driver;
        var body = driver.Body;
        driver.Patient.SetPhysicsProcess(false);
        var blood = body.Blood;
        blood.Sources.Clear();
        blood.Pools.Clear();
        await driver.BloodFilmsBuilt();
        var film = blood.BodyFilm;
        var skin = body.SkinMesh;
        // Film points are in the skin mesh's rest space; the skeleton places them on the posed body.
        Func<int, Vector3> Posed() {
            var pose = BodyLiquidSurface.CaptureSkinPose(skin, skin.GetNode<Skeleton3D>(skin.Skeleton));
            return k => pose.Deform(film.At(k), film.SkinAt(k));
        }
        // The height of all the blood once the films' running steps finish: on the body, falling, on the bed and floor.
        // What drips off leaves the body from its lowest points, so the body's blood alone can seem to rise while all of
        // it runs down. Measured outside the frame budget: it places every film point on the posed body.
        async Task<float> Height() {
            var (mass, moment) = (0f, 0f);
            await driver.Unbudgeted(() => {
                blood.FinishFilms();
                var world = Posed();
                for (var k = 0; k < film.Count; k++) { mass += film.MlAt(k); moment += film.MlAt(k) * world(k).Y; }
                foreach (var drop in blood.Drops) { mass += drop.Ml; moment += drop.Ml * drop.Position.Y; }
                foreach (var ground in new[] { blood.BedFilm, blood.FloorFilm }) {
                    for (var k = 0; k < ground.Count; k++) { mass += ground.MlAt(k); moment += ground.MlAt(k) * ground.At(k).Y; }
                }
                return Task.CompletedTask;
            });
            return moment / mass;
        }
        // The highest point of the body's film beside the site, where runoff from the site starts.
        var top = -1;
        await driver.Unbudgeted(() => {
            var posed = Posed();
            top = Enumerable.Range(0, film.Count).Where(k => posed(k).DistanceTo(body.Site.GlobalPosition) < 0.12f).MaxBy(k => posed(k).Y);
            return Task.CompletedTask;
        });
        driver.Note("blood running down the lying patient's skin");
        film.Add(top, 1.5f);
        await Frames.Seconds(0.2f);
        var start = await Height();
        await Frames.Seconds(1.5f);
        var lying = await Height();
        AssertFloat(start - lying).OverrideFailureMessage($"blood on the skin runs downhill ({(start - lying) * 100f:0.0} cm)").IsGreater(0.01f);
        driver.Note("the patient turned on their side");
        body.SetPose(PatientPose.Side);
        await Frames.NextProcess();
        await driver.Unbudgeted(() => { blood.FinishFilms(); return Task.CompletedTask; });
        var (turnedOnBody, turnedOnBed) = (film.HeldMl, blood.BedFilm.HeldMl);
        await Frames.Seconds(1.5f);
        await driver.Unbudgeted(() => { blood.FinishFilms(); return Task.CompletedTask; });
        // Turned, the body sinks into the mattress: blood runs down to where they meet and passes onto the bed there.
        AssertFloat(blood.BedFilm.HeldMl - turnedOnBed).OverrideFailureMessage(
            $"turned on its side, the blood runs down the body onto the bed ({turnedOnBody:0.00} ml on the body, {blood.BedFilm.HeldMl - turnedOnBed:0.00} ml passed on)")
            .IsGreater(turnedOnBody * 0.5f);
        AssertFloat(film.HeldMl + blood.GroundMl).OverrideFailureMessage("the body, drops, bed and floor keep every milliliter")
            .IsEqualApprox(1.5f, 0.0001f);
        await session.Finish(FrameBudget.Mode == BudgetMode.Render ? RenderAllowance : CpuAllowance);
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
