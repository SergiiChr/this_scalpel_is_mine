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
        if (scenario == "knife_back") {
            await driver.Unbudgeted(() => {
                foreach (var pool in blood.GroundDeposits.Where(pool => pool.OnBed && Mathf.Abs(pool.Center.Y - BloodFlow.TableTop) < 0.001f)) {
                    foreach (var vertex in pool.TopVertices) {
                        var world = pool.Center + vertex;
                        var supported = driver.Surgery.Room.BedSurface.HeightAt(new Vector2(world.X, world.Z));
                        AssertBool(supported.HasValue).OverrideFailureMessage("the back's deposited pool remains over the actual mattress").IsTrue();
                        AssertFloat(world.Y).OverrideFailureMessage("the mattress pool stays on the mattress, independent of patient orientation")
                            .IsEqualApprox(supported!.Value + BedLiquidSurface.FilmDepth, 0.0001f);
                    }
                }
                return Task.CompletedTask;
            });
            await session.CaptureOblique("settled_contact", "blood on the upright back drains onto a horizontal mattress pool supported below the patient",
                new Vector3(0.18f, 0.86f, -0.11f), Vector3.Forward, 0.7f);
        }
        if (scenario == "hand_stitch") {
            await driver.Unbudgeted(() => {
                AssertBool(checkedDelayedBed).OverrideFailureMessage("attached narrowing runoff is visible before the first bed deposit").IsTrue();
                var rendered = driver.Body.SkinMesh.GetParent().GetNode<MeshInstance3D>("BodyRunoff");
                AssertObject(rendered.MaterialOverride).OverrideFailureMessage("runoff shares the live skin material").IsSame(driver.Body.SkinMaterial!);
                AssertFloat((float)driver.Body.SkinMaterial!.GetShaderParameter("blood_cover_depth"))
                    .OverrideFailureMessage("attached runoff and the volume-backed film use the same optical depth").IsEqual(SurfaceBlood.CoverDepth);
                AssertFloat((float)rendered.GetInstanceShaderParameter("rivulet_depth"))
                    .OverrideFailureMessage("the actual runoff material uses its conserved mean film depth").IsEqual(BodyLiquidSurface.FilmDepth);
                using var arrays = rendered.Mesh.SurfaceGetArrays(0);
                var vertexCount = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
                AssertInt(arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array().Length)
                    .OverrideFailureMessage("the rendered tails carry their rounded cross-section coordinates").IsEqual(vertexCount);
                AssertInt(arrays[(int)Mesh.ArrayType.TexUV2].AsVector2Array().Length)
                    .OverrideFailureMessage("the rendered tails preserve the surgical site map coordinates in UV2").IsEqual(vertexCount);
                AssertInt(arrays[(int)Mesh.ArrayType.Bones].AsInt32Array().Length)
                    .OverrideFailureMessage("the rendered tails carry four skin influences per vertex").IsEqual(vertexCount * 4);
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
        var accounted = blood.SurfaceAccountedMl;
        AssertFloat(accounted).OverrideFailureMessage($"skin, attached streaks, queued liquid, drops and ground deposits conserve the supplied milliliters: {accounted:0.000} ml accounted of {blood.SurfaceSuppliedMl:0.000} ml supplied")
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
        var accountedDose = blood.SurfaceAccountedMl - beforeDose;
        var suppliedDose = blood.SurfaceSuppliedMl - beforeSupply;
        AssertFloat(accountedDose)
            .OverrideFailureMessage($"a later poured dose increases the accounted liquid inventory by exactly the added dose, including spurts: {accountedDose:0.000} ml accounted of {suppliedDose:0.000} ml supplied")
            .IsEqualApprox(suppliedDose, 0.01f);
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

    [TestCase(Timeout = Limits.Slow)]
    public async Task ModelRunoffKeepsSeparatedSourcesAndRetracesForTheCurrentPose() {
        var session = await ToolSession.Start("hand_stitch", "blood_flow/runoff_pose");
        var driver = session.Driver;
        var patient = driver.Patient;
        var body = driver.Body;
        var site = body.Site;
        var originalPose = body.Pose;
        patient.SetPhysicsProcess(false);
        patient.SetProcess(false);
        body.SetProcess(false);

        var skinMesh = body.SkinMesh;
        var skeleton = skinMesh.GetNodeOrNull<Skeleton3D>(skinMesh.Skeleton)!;
        var mesh = new MeshInstance3D {
            Mesh = LiquidSurface.Primed(withUv: true, withSkin: true, withUv2: true),
            MaterialOverride = body.SkinMaterial,
            Skin = skinMesh.Skin,
            Skeleton = skinMesh.Skeleton,
            Transform = skinMesh.Transform,
        };
        mesh.SetInstanceShaderParameter("rivulet_depth", BodyLiquidSurface.FilmDepth);
        skinMesh.GetParent().AddChild(mesh);
        var runoff = new BodyRunoff(body.LiquidContact, site, driver.Surgery.Room.BedSurface, mesh, skinMesh, skeleton, body.SiteSize);
        const float FrameDelta = 1f / 60f;
        var releasedMl = 0f;
        var suppliedMl = 0f;
        var firstUv = new Vector2(0f, 0.3f);
        var secondUv = new Vector2(0f, 0.31f);
        var thirdUv = new Vector2(0f, 0.32f);
        var firstSurfaceUv = new Vector2(0.4f, 0.5f);
        var secondSurfaceUv = new Vector2(0.6f, 0.5f);
        var thirdSurfaceUv = new Vector2(0.43f, 0.5f);
        var firstSource = site.ToLocal(body.UvToWorld(firstSurfaceUv));
        var secondSource = site.ToLocal(body.UvToWorld(secondSurfaceUv));
        var thirdSource = site.ToLocal(body.UvToWorld(thirdSurfaceUv));
        AssertInt(SurfaceBlood.RunoffBucket(firstUv)).OverrideFailureMessage("the first two runoff labels share a bucket")
            .IsEqual(SurfaceBlood.RunoffBucket(secondUv));
        AssertFloat(firstSource.DistanceTo(secondSource)).OverrideFailureMessage("the model sources are actually separated")
            .IsGreater(0.04f);

        async Task<bool> Until(Func<bool> condition, int maxFrames = 600) {
            for (var frame = 0; frame < maxFrames; frame++) {
                runoff.Tick(FrameDelta);
                await Frames.NextProcess();
                if (condition()) { return true; }
            }
            return condition();
        }
        void Add(Vector2 label, Vector3 source, float ml) {
            runoff.Add(label, source, ml);
            suppliedMl += ml;
        }
        void AssertConserved(string phase) => AssertFloat(runoff.HeldMl + releasedMl)
            .OverrideFailureMessage($"attached coating, queued drops and releases conserve the supplied dose {phase}")
            .IsEqualApprox(suppliedMl, 0.01f);

        Add(firstUv, firstSource, 0.05f);
        var growing = await Until(() => runoff.HasGrowingStreak);
        AssertBool(growing).OverrideFailureMessage("the first model source forms a growing attached streak").IsTrue();
        var uploaded = await Until(() => !runoff.DrawPending && mesh.Mesh.GetSurfaceCount() > 0
            && mesh.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length > 3);
        AssertBool(uploaded).OverrideFailureMessage("the runoff worker uploaded skinned path geometry").IsTrue();
        AssertObject(mesh.Skin).OverrideFailureMessage("the runoff renderer is bound to the source skin").IsSame(skinMesh.Skin);
        AssertObject(mesh.Skeleton).OverrideFailureMessage("the runoff renderer is bound to the source skeleton").IsEqual(skinMesh.Skeleton);
        using (var arrays = mesh.Mesh.SurfaceGetArrays(0)) {
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
            var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
            AssertInt(bones.Length).OverrideFailureMessage("every runoff vertex carries four bone bindings").IsEqual(vertices.Length * 4);
            AssertInt(weights.Length).OverrideFailureMessage("every runoff vertex carries four skin weights").IsEqual(vertices.Length * 4);
            var sample = runoff.GrowingPath!.Samples[1];
            var expectedLocal = body.LiquidContact.SiteToMesh * sample.At;
            var vertexIndex = Enumerable.Range(0, vertices.Length).MinBy(index => vertices[index].DistanceSquaredTo(expectedLocal));
            AssertFloat(vertices[vertexIndex].DistanceTo(expectedLocal))
                .OverrideFailureMessage("the uploaded runoff vertex maps to the sampled original body surface point")
                .IsLess(0.0001f);
            var influence = new BodyLiquidSurface.SkinWeights(
                new Vector4I(bones[vertexIndex * 4], bones[vertexIndex * 4 + 1], bones[vertexIndex * 4 + 2], bones[vertexIndex * 4 + 3]),
                new Vector4(weights[vertexIndex * 4], weights[vertexIndex * 4 + 1], weights[vertexIndex * 4 + 2], weights[vertexIndex * 4 + 3]));
            var pose = body.LiquidContact.CaptureSkinPose(skinMesh, skeleton);
            var uploadedWorld = pose.Deform(vertices[vertexIndex], influence);
            var sourceWorld = body.LiquidContact.Deform(sample.At, sample.AtSkin, skinMesh, skeleton);
            AssertFloat(uploadedWorld.DistanceTo(sourceWorld))
                .OverrideFailureMessage("the rendered runoff vertex deforms onto the same animated body-surface point")
                .IsLess(0.0001f);
        }
        var firstPath = runoff.GrowingPath!;
        var firstPaths = runoff.ActivePaths.ToArray();
        var animatedSample = firstPath.Samples[^1];
        var beforeLimbMotion = body.LiquidContact.Deform(animatedSample.At, animatedSample.AtSkin, skinMesh, skeleton);
        var skin = skinMesh.Skin!;
        var bind = animatedSample.AtSkin.Bones.X;
        var weightedBone = skin.GetBindBone(bind);
        var boneName = weightedBone >= 0 ? skeleton.GetBoneName(weightedBone).ToString() : skin.GetBindName(bind).ToString();
        AssertBool(weightedBone >= 0 || boneName.ToString().Length > 0)
            .OverrideFailureMessage("the attached streak endpoint retains its skin bone binding").IsTrue();
        var limbRig = new BoneRig(skeleton);
        limbRig.Rotate(boneName, new Basis(Vector3.Forward, 0.35f));
        await Frames.NextProcess();
        var afterLimbMotion = body.LiquidContact.Deform(animatedSample.At, animatedSample.AtSkin, skinMesh, skeleton);
        AssertFloat(afterLimbMotion.DistanceTo(beforeLimbMotion))
            .OverrideFailureMessage("the runoff endpoint follows the animated skin bone after its trace starts").IsGreater(0.001f);
        AssertInt(firstPaths.Length).OverrideFailureMessage("the initial pose has one active source path").IsEqual(1);
        AssertConserved("after the first source");

        Add(secondUv, secondSource, 0.05f);
        var secondRoute = await Until(() => runoff.ActivePaths.Any(path => path.Samples.Length > 0
            && path.Samples[0].At.DistanceTo(secondSource) < 0.01f));
        AssertBool(secondRoute).OverrideFailureMessage("a second source in the same runoff bucket gets its own route")
            .IsTrue();
        var originalSecondPath = runoff.ActivePaths.First(path => path.Samples.Length > 0
            && path.Samples[0].At.DistanceTo(secondSource) < 0.01f);
        AssertFloat(originalSecondPath.Samples[0].At.DistanceTo(firstSource))
            .OverrideFailureMessage("the second route starts near its own physical source, not the older source")
            .IsGreater(0.04f);
        AssertInt(runoff.ActivePaths.Count()).OverrideFailureMessage("both separated sources remain active")
            .IsEqual(2);
        AssertConserved("after the second source");

        // Start both a trace and an upload, then change gravity before either task is consumed.
        Add(thirdUv, thirdSource, 0.03f);
        runoff.Tick(1f / 15f);
        AssertBool(runoff.TracePending).OverrideFailureMessage("a third inlet starts an asynchronous model trace")
            .IsTrue();
        AssertBool(runoff.DrawPending).OverrideFailureMessage("the flowing first streak has an asynchronous mesh upload pending")
            .IsTrue();
        body.SetPose(PatientPose.Side);
        runoff.Tick(FrameDelta);
        var sideRoutes = await Until(() => runoff.ActivePaths.Count(path => path.Samples.Length > 0) >= 3);
        AssertBool(sideRoutes).OverrideFailureMessage("the side pose gets fresh routes for all three physical inlets")
            .IsTrue();
        var sidePaths = runoff.ActivePaths.Where(path => path.Samples.Length > 0).ToArray();
        foreach (var (source, label) in new[] { (firstSource, "first"), (secondSource, "second"), (thirdSource, "third") }) {
            AssertBool(sidePaths.Any(path => path.Samples[0].At.DistanceTo(source) < 0.01f))
                .OverrideFailureMessage($"the {label} current-pose trace starts at its own source after stale work is discarded")
                .IsTrue();
        }
        foreach (var path in sidePaths) {
            var start = site.ToGlobal(path.Samples[0].At);
            var end = site.ToGlobal(path.End);
            AssertFloat(start.Y - end.Y).OverrideFailureMessage("each side-pose path descends under current world gravity")
                .IsGreater(0.005f);
        }
        AssertConserved("after stale trace and draw work is invalidated by the side pose");

        body.SetPose(originalPose);
        runoff.Tick(FrameDelta);
        var returned = await Until(() => runoff.ActivePaths.Any(path => ReferenceEquals(path, firstPath))
            && runoff.ActivePaths.Any(path => ReferenceEquals(path, originalSecondPath)));
        AssertBool(returned).OverrideFailureMessage("returning to the original pose reuses both existing source coatings")
            .IsTrue();
        AssertConserved("after returning to the original pose without adding blood");

        Add(secondUv, secondSource, 20f);
        var fullRoute = await Until(() => runoff.HasReachedEnd, 900);
        AssertBool(fullRoute).OverrideFailureMessage("the larger supplied dose reaches a traced outlet")
            .IsTrue();
        _ = await Until(() => false, 30); // Let the outlet pulse become ready without consuming it yet.
        patient.GlobalPosition += Vector3.Up * 2f;
        var releasedAboveBed = false;
        var releaseOnBed = true;
        var releasePoint = Vector3.Zero;
        for (var frame = 0; frame < 300 && !releasedAboveBed; frame++) {
            runoff.Tick(FrameDelta);
            if (runoff.TryRelease(out releasePoint, out var ml, out releaseOnBed)) {
                releasedMl += ml;
                releasedAboveBed = true;
                break;
            }
            await Frames.NextProcess();
        }
        AssertBool(releasedAboveBed).OverrideFailureMessage("a real queued outlet releases after the patient is translated")
            .IsTrue();
        AssertFloat(releasedMl).OverrideFailureMessage("the release contains supplied liquid").IsGreater(0f);
        AssertBool(releaseOnBed).OverrideFailureMessage("release contact is checked against the patient's current translated position")
            .IsFalse();
        var closestCurrentEndpoint = runoff.ActivePaths.Where(path => path.Samples.Length > 0)
            .Select(path => body.LiquidContact.Deform(path.EndSample.At, path.EndSample.AtSkin, skinMesh, skeleton)
                .DistanceTo(releasePoint)).Min();
        AssertFloat(closestCurrentEndpoint)
            .OverrideFailureMessage("a queued release follows its cached endpoint through the current skin pose and body transform")
            .IsLess(0.0001f);
        AssertObject(driver.Surgery.Room.BedSurface.Landing(releasePoint + Vector3.Up * 0.001f,
            releasePoint - Vector3.Up * 0.002f)).OverrideFailureMessage("there is no bed contact below the translated release")
            .IsNull();
        AssertConserved("after release with no current bed contact");
        await session.Finish(FrameBudget.Mode == BudgetMode.Render ? RenderAllowance : CpuAllowance);
    }

    [TestCase(Timeout = Limits.Slow)]
    public async Task ModelRunoffFollowsBreathingSkinAndConservesLiquid() {
        var session = await ToolSession.Start("appendectomy", "blood_flow/runoff_breathing", captureUntouched: false);
        var driver = session.Driver;
        var patient = driver.Patient;
        var body = driver.Body;
        patient.SetPhysicsProcess(false);
        patient.SetProcess(false);
        body.SetProcess(false);
        var skinMesh = body.SkinMesh;
        var skeleton = skinMesh.GetNodeOrNull<Skeleton3D>(skinMesh.Skeleton)!;
        var runoffMesh = new MeshInstance3D {
            Name = "BreathingRunoff",
            Mesh = LiquidSurface.Primed(withUv: true, withSkin: true, withUv2: true),
            MaterialOverride = body.SkinMaterial,
            Skin = skinMesh.Skin,
            Skeleton = skinMesh.Skeleton,
            Transform = skinMesh.Transform,
        };
        runoffMesh.SetInstanceShaderParameter("rivulet_depth", BodyLiquidSurface.FilmDepth);
        skinMesh.GetParent().AddChild(runoffMesh);
        var runoff = new BodyRunoff(body.LiquidContact, body.Site, driver.Surgery.Room.BedSurface,
            runoffMesh, skinMesh, skeleton, body.SiteSize);
        const float Supplied = 0.04f;
        var uv = new Vector2(0.5f, 0.5f);
        runoff.Add(uv, body.Site.ToLocal(body.UvToWorld(uv)), Supplied);
        BodyLiquidSurface.Path? path = null;
        for (var frame = 0; frame < 300 && path is null; frame++) {
            runoff.Tick(1f / 60f);
            path = runoff.GrowingPath;
            await Frames.NextProcess();
        }
        AssertObject(path).OverrideFailureMessage("a trunk outlet forms an attached streak before the breathing check").IsNotNull();
        var skin = skinMesh.Skin!;
        bool HasTorsoWeight(BodyLiquidSurface.SkinWeights influence) {
            for (var i = 0; i < 4; i++) {
                var weight = i switch { 0 => influence.Weights.X, 1 => influence.Weights.Y, 2 => influence.Weights.Z, _ => influence.Weights.W };
                if (weight <= 0f) { continue; }
                var bind = i switch { 0 => influence.Bones.X, 1 => influence.Bones.Y, 2 => influence.Bones.Z, _ => influence.Bones.W };
                var bone = bind >= 0 && bind < skin.GetBindCount() ? skin.GetBindBone(bind) : -1;
                if (bone < 0 && bind >= 0 && bind < skin.GetBindCount()) { bone = skeleton.FindBone(skin.GetBindName(bind)); }
                if (bone >= 0 && bone < skeleton.GetBoneCount()
                    && skeleton.GetBoneName(bone).ToString().Contains("Torso", StringComparison.Ordinal)) { return true; }
            }
            return false;
        }
        var sample = path!.Samples.FirstOrDefault(candidate => HasTorsoWeight(candidate.AtSkin));
        AssertBool(HasTorsoWeight(sample.AtSkin)).OverrideFailureMessage("the abdominal streak samples a positive, resolved torso skin influence").IsTrue();
        var before = body.LiquidContact.Deform(sample.At, sample.AtSkin, skinMesh, skeleton);
        var initialBreath = body.Breath;
        for (var step = 0; step < 30 && Mathf.Abs(body.Breath - initialBreath) < 0.6f; step++) {
            body.Animator.Animate(patient.Vitals, true, 0.2f);
        }
        await Frames.Process(2);
        var after = body.LiquidContact.Deform(sample.At, sample.AtSkin, skinMesh, skeleton);
        AssertFloat(Mathf.Abs(body.Breath - initialBreath)).OverrideFailureMessage("the patient animator advanced the breathing cycle").IsGreater(0.01f);
        AssertFloat(after.DistanceTo(before)).OverrideFailureMessage($"the attached runoff sample follows the animated torso skin (movement {after.DistanceTo(before):0.000000} m, breath {initialBreath:0.000}->{body.Breath:0.000})").IsGreater(0.001f);
        AssertFloat(runoff.HeldMl).OverrideFailureMessage("breathing changes the skin pose without changing the held liquid volume")
            .IsEqualApprox(Supplied, 0.0001f);
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
