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
            await driver.Unbudgeted(() => {
                AssertFilmsMeet(driver.Body, film);
                if (scenario == "hand_stitch") { AssertSmoothHandoff(driver.Body, film); }
                return Task.CompletedTask;
            });
            if (scenario == "hand_stitch") {
                // Look at the flank where the site's film hands over, from beside it: further down, under a limb, the
                // drape hides it. The turned back's flank lies in the mattress, where no camera can look at it; what
                // runs off it shows where it meets the bed (settled_contact).
                Vector3 Normal(int k) => (skin.GlobalBasis * film.NormalAt(k)).Normalized();
                var site = driver.Body.Site.GlobalPosition;
                var flank = wet.Where(k => Mathf.Abs(Normal(k).Y) < 0.4f).DefaultIfEmpty(wet[0])
                    .MinBy(k => (skin.GlobalTransform * film.At(k)).DistanceTo(site));
                var at = skin.GlobalTransform * film.At(flank);
                var side = Normal(flank).Slide(Vector3.Up).Normalized();
                await session.CaptureOblique("attached_runoff", "blood runs on from the site's edge down the body's side as one film, its edge fading, no separate streaks or stubs",
                    at, side.IsZeroApprox() ? Vector3.Forward : side, 0.3f);
            }
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
                AssertBool(driver.Body.SkinMesh.GetParent().HasNode("BodyFilm"))
                    .OverrideFailureMessage("blood uses the skin's shared layer, without a separate runoff overlay").IsFalse();
                foreach (var material in driver.Body.BloodMaterials) {
                    AssertObject(material.GetShaderParameter("body_blood_atlas").AsGodotObject())
                        .OverrideFailureMessage("body and surgical skin read the same blood layer").IsSame(blood.Layer.Texture);
                }
                AssertFloat(blood.Layer.CoverageAt(driver.Body.SkinPoint(pool)))
                    .OverrideFailureMessage("the shared body layer covers the significant source on the skin").IsGreater(0.5f);
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
    public async Task SmallArmCutFeedsSurfaceRunoff() {
        var original = Db.Scenario("hand_stitch")!;
        ToolSession session;
        Db.ReplaceScenario(original with { Wounds = [], Targets = [] });
        try {
            session = await ToolSession.Start("hand_stitch", "blood_flow/small_arm_cut");
        } finally {
            Db.ReplaceScenario(original);
        }
        var driver = session.Driver;
        var body = driver.Body;
        var blood = body.Blood;
        driver.Patient.SetPhysicsProcess(false);
        blood.Sources.Clear();
        blood.Pools.Clear();
        await driver.BloodFilmsBuilt();
        var skin = body.SkinMesh;
        var film = blood.BodyFilm;
        Vector3 Normal(int k) => (skin.GlobalBasis * film.NormalAt(k)).Normalized();
        // The same framing as attached_runoff, chosen from the arm geometry before any blood is added.
        var flank = Enumerable.Range(0, film.Count).Where(k => Mathf.Abs(Normal(k).Y) < 0.4f)
            .MinBy(k => (skin.GlobalTransform * film.At(k)).DistanceTo(body.Site.GlobalPosition));
        var at = skin.GlobalTransform * film.At(flank);
        var side = Normal(flank).Slide(Vector3.Up).Normalized();
        Task SideView(string name, string claim) => session.CaptureOblique(name, claim, at, side, 0.3f);
        var source = new Vector2(0.55f, 0.4f);
        var halfCut = new Vector2(0.003f / body.SiteSize.X, 0f);
        SurgeryState.SkinIsCut(driver.Patient, source - halfCut, source + halfCut, 0.1f);
        await driver.Capture("small_cut", "a short superficial cut on the upper slope of the arm, before bleeding");
        await SideView("small_cut_side", "the same side-on arm framing, a small cut above the flank");
        blood.Sources = [new(source, 0.03f)];
        driver.Note("the small cut supplies a slow surface bleed");
        await Frames.Process(1200);
        blood.FinishFilms();
        await driver.Capture("running", "a small upper-arm source feeding a localized trail down the slope");
        await SideView("running_side", "blood from the small cut runs from the upper slope down the side, staying attached across the handoff");
        blood.Sources.Clear();
        driver.Note("the small cut's supply stops, leaving its trail");
        await Frames.Process(360);
        blood.FinishFilms();
        AssertFloat(blood.BodyFilm.HeldMl).OverrideFailureMessage("the small surface bleed reaches the body's flank").IsGreater(0f);
        AssertFloat(blood.SurfaceAccountedMl).OverrideFailureMessage("the small bleed conserves its supplied blood")
            .IsEqualApprox(blood.SurfaceSuppliedMl, 0.01f);
        await SideView("settled_side", "the localized runoff remains on the arm after its small source stops");
        await session.Finish(CpuAllowance);
    }

    private static void AssertSmoothHandoff(PatientBody body, SurfaceFilm film) {
        var skin = body.SkinMesh;
        var pose = BodyLiquidSurface.CaptureSkinPose(skin, skin.GetNode<Skeleton3D>(skin.Skeleton));
        var up = body.Site.GlobalBasis.Y.Normalized();
        var checkedPoints = 0;
        var worst = 0f;
        foreach (var sample in film.Samples) {
            if (sample.Coverage < 0.5f) { continue; }
            var weights = film.SkinAt(sample.Node);
            var at = pose.Deform(sample.At, weights);
            var normal = (pose.Deform(sample.At + sample.Normal, weights) - at).Normalized();
            var facing = normal.Dot(up);
            if (Mathf.Abs(body.Site.ToLocal(at).X) > 0.03f || facing < 0.25f || facing > 0.65f) { continue; }
            var step = up.Slide(normal).Normalized() * BodyBloodLayer.CellSize;
            var center = body.Blood.Layer.CoverageOn(at, normal);
            var before = body.Blood.Layer.CoverageOn(at - step, normal);
            var after = body.Blood.Layer.CoverageOn(at + step, normal);
            worst = Mathf.Max(worst, Mathf.Abs(before + after - 2f * center));
            checkedPoints++;
        }
        AssertInt(checkedPoints).OverrideFailureMessage("the wet arm fixture exercises skin at the solver handoff").IsGreater(20);
        AssertFloat(worst).OverrideFailureMessage("the shared film has no narrow coverage ripple along the site/body handoff")
            .IsLess(0.06f);
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

    /// <summary>The site's film and the body's meet along one line: where the site's film carries blood, the body's
    /// has no point on that skin, and where the site's hands over, the body's film is there to take it. Both are laid
    /// out on cells and film points, so a point may sit within a film spacing and half a cell of the line.</summary>
    private static void AssertFilmsMeet(PatientBody body, SurfaceFilm film) {
        var blood = body.Blood;
        var meshToSite = body.LiquidContact.SiteToMesh.AffineInverse();
        var half = body.SiteSize * 0.5f;
        var cell = body.SiteSize / SurfaceBlood.Size;
        var slack = film.Spacing + cell.Length() * 0.5f;
        var handsOver = new List<Vector2>();
        var boundary = new List<Vector2>();
        for (var y = 0; y < SurfaceBlood.Size; y++) {
            for (var x = 0; x < SurfaceBlood.Size; x++) {
                var uv = new Vector2(x + 0.5f, y + 0.5f) / SurfaceBlood.Size;
                if (blood.SiteFilmCarries(uv)) { continue; }
                var at = new Vector2((uv.X - 0.5f) * body.SiteSize.X, (uv.Y - 0.5f) * body.SiteSize.Y);
                handsOver.Add(at);
                // A steep cell on the body beside a carried one: the line between the films runs between them.
                var beside = false;
                for (var dy = -1; dy <= 1 && !beside; dy++) {
                    for (var dx = -1; dx <= 1 && !beside; dx++) {
                        beside = blood.SiteFilmCarries(new Vector2(x + dx + 0.5f, y + dy + 0.5f) / SurfaceBlood.Size);
                    }
                }
                if (beside && body.LiquidSupport(uv) >= 0f) { boundary.Add(at); }
            }
        }
        var onSite = new List<Vector2>();
        var shared = 0;
        var sharedAt = new Rect2();
        for (var k = 0; k < film.Count; k++) {
            if (!film.CarriesAt(k)) { continue; }
            var p = meshToSite * film.At(k);
            if (Mathf.Abs(p.X) >= half.X || Mathf.Abs(p.Z) >= half.Y || (meshToSite.Basis * film.NormalAt(k)).Y <= 0f) { continue; }
            var at = new Vector2(p.X, p.Z);
            onSite.Add(at);
            // The site's edge hands over too: a face of the body's film beyond it reaches in by up to a spacing.
            var toEdge = Mathf.Min(half.X - Mathf.Abs(p.X), half.Y - Mathf.Abs(p.Z));
            if (blood.SiteFilmCarries(body.LocalToUv(p), p.Y) && toEdge > slack && handsOver.Select(c => c.DistanceTo(at)).DefaultIfEmpty(float.PositiveInfinity).Min() > slack) {
                sharedAt = shared++ == 0 ? new Rect2(body.LocalToUv(p), Vector2.Zero) : sharedAt.Expand(body.LocalToUv(p));
            }
        }
        AssertInt(shared).OverrideFailureMessage($"the body's film keeps off the skin the site's film carries ({shared} of {onSite.Count} points over the site lie on it, over uv {sharedAt}; {handsOver.Count} of {SurfaceBlood.Size * SurfaceBlood.Size} cells hand over)").IsEqual(0);
        var orphaned = boundary.Count(c => onSite.Select(p => p.DistanceTo(c)).DefaultIfEmpty(float.PositiveInfinity).Min() > slack);
        AssertInt(orphaned).OverrideFailureMessage($"the body's film takes over right where the site's film hands over ({orphaned} of {boundary.Count} flank cells beside the site's film have no body film point)").IsEqual(0);
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
