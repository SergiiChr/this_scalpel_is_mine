namespace Scalpel.Tests.Patients;

[TestSuite, RequireGodotRuntime]
[TestCategory("liquids"), TestCategory("visual_confirmation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class BedBloodTest {
    // The same bed camera without liquid reached 45.4 ms on the RTX 4080. Preserve those existing scene
    // spikes with 30% headroom rounded to a half-factor; the headless CPU check keeps its strict limit.
    private static readonly BudgetAllowance RenderAllowance = new("bed scene spikes also measured without any liquid", 4f);

    // Software key-frame capture reached 8.5 ms while strict headless CPU passed and native GPU
    // viewport p95 was 2.7 ms. This capture-only allowance includes 30% headroom, rounded to a half-factor.
    private static readonly BudgetAllowance ScreenshotAllowance = new("software screenshot renderer synchronizes bed mesh updates", 2f);

    [TestCase]
    public async Task BloodAtEveryBedEdgeDripsOntoTheFloor() {
        var session = await ToolSession.Start("appendectomy", "blood_bed_edges", captureUntouched: false);
        var driver = session.Driver;
        driver.Patient.SetPhysicsProcess(false);
        // The bed runoff fixture holds the patient still; tissue simulation is covered by the body-flow suites.
        driver.Body.SetProcess(false);
        var blood = driver.Body.Blood;
        blood.Sources.Clear();
        blood.Pools.Clear();
        var height = BloodFlow.TableTop;
        Vector3[] supplies = [
            new(Room.TableFoot + 0.025f, height, 0.05f),
            new(Room.TableHead - 0.025f, height, 0.05f),
            new(0.1f, height, -Room.TableHalfWidth + 0.025f),
            new(-0.4f, height, Room.TableHalfWidth - 0.025f),
            new(Room.TableHead - 0.02f, height, -Room.TableHalfWidth + 0.02f),
            new(0.39f, height, 0f),
        ];
        var mattress = driver.Surgery.Room.BedSurface;
        foreach (var at in new[] { new Vector2(0.92f, 0f), new Vector2(0.96f, -0.22f) }) {
            var landing = mattress.Landing(new Vector3(at.X, 1.1f, at.Y), new Vector3(at.X, 0.7f, at.Y));
            AssertBool(landing.HasValue).OverrideFailureMessage("a falling drop contacts both the flat centre and rounded mattress slope").IsTrue();
            AssertFloat(landing!.Value.Y).IsEqualApprox(mattress.HeightAt(at)!.Value, 0.00001f);
        }
        var frameShelf = mattress.Landing(new Vector3(0.405f, 1.1f, 0.2f), new Vector3(0.405f, 0.7f, 0.2f));
        AssertBool(frameShelf.HasValue).OverrideFailureMessage("a falling drop contacts the actual frame shelf beneath the pad gap").IsTrue();
        AssertFloat(frameShelf!.Value.Y).OverrideFailureMessage("frame contact uses the imported support mesh height").IsBetween(0.79f, 0.83f);
        foreach (var at in new[] { new Vector2(0.405f, 0.31f), new Vector2(0.999f, -0.279f) }) {
            AssertBool(mattress.Landing(new Vector3(at.X, 1.1f, at.Y), new Vector3(at.X, 0.7f, at.Y)).HasValue)
                .OverrideFailureMessage("drops miss the unsupported frame edge and outer corner").IsFalse();
        }
        var view = new Vector3(0.7f, 0.6f, -Room.TableHalfWidth);
        await session.CaptureOblique("untouched", "the empty bed end and floor before blood lands", view, Vector3.Forward, 1.8f);
        if (FrameBudget.Mode == BudgetMode.Render) {
            var camera = new Camera3D { Fov = 35f, Near = 0.01f, CullMask = RenderLayers.Normal };
            driver.Surgery.AddChild(camera);
            camera.GlobalPosition = view + (Vector3.Up + Vector3.Forward).Normalized() * 1.8f;
            camera.LookAt(view);
            camera.Current = true;
            await Frames.Process(12);
        }
        var gpu = new List<double>();
        var viewport = driver.GetViewport().GetViewportRid();
        driver.Note("blood supplying the bed ends, sides and corner");
        driver.Budget.Clear();
        blood.Pool(supplies[0]);
        blood.Pool(supplies[0] + Vector3.Back * 0.035f);
        AssertInt(Deposits(blood).Count()).OverrideFailureMessage("nearby landed drops merge before the next mesh update").IsEqual(1);
        var shapes = new HashSet<Vector2>();
        var headStreams = 0;
        var shownNecks = false;
        // Controlled landed drops isolate bed runoff from the patient's wound and vital signs.
        // Retaining the side film adds a filling phase before the first free drop.
        for (var frame = 0; frame < 360; frame++) {
            if (frame < 60) { foreach (var at in supplies) { blood.Pool(at, BloodFlow.PoolAreaPerDrop * 3f); } }
            await Frames.NextProcess();
            if (blood.Drops.Count > 0) {
                var shape = blood.Drops[0].Shape;
                shapes.Add(new Vector2(Mathf.Round(shape.X * 100000f), Mathf.Round(shape.Y * 100000f)));
            }
            if (frame == 45) {
                AssertBool(Deposits(blood).Any(pool => pool.SideVertices.Count > 0))
                    .OverrideFailureMessage("side coating starts while the pool is still being supplied and growing").IsTrue();
            }
            var belowLip = true; var heads = 0; var atSide = 0;
            foreach (var at in blood.AttachedDrips) {
                belowLip &= at.Y < mattress.DripHeight + 0.001f;
                if (at.X > Room.TableHead - 0.045f) { heads++; }
                if (at.Z < -Room.TableHalfWidth + 0.012f && Mathf.Abs(at.X - supplies[2].X) < 0.1f) { atSide++; }
            }
            AssertBool(belowLip)
                .OverrideFailureMessage("attached drops begin only at the bottom frame lip, never midway down the mattress").IsTrue();
            if (frame < 20) {
                AssertInt(blood.Drops.Count).OverrideFailureMessage("runoff wets the bed side before any free drops detach").IsEqual(0);
            }
            headStreams = Math.Max(headStreams, heads);
            if (!shownNecks && atSide >= 2) {
                shownNecks = true;
                // The headless dummy renderer returns identity for MultiMesh transforms.
                if (KeyFrames.Wanted() || FrameBudget.Mode == BudgetMode.Render) {
                    AssertFloat(blood.DrawnNeckMl).OverrideFailureMessage("random attached neck geometry cannot exceed its pending milliliters")
                        .IsLessEqual(blood.PendingDripMl + 0.00001f);
                }
                await session.CaptureOblique("ligaments", "uneven rivulets stay attached down the pad and frame; necks and drops begin at the bottom lip",
                    new Vector3(supplies[2].X, height - 0.05f, -Room.TableHalfWidth), Vector3.Forward, 0.28f);
            }
        }
        AssertInt(shapes.Count).OverrideFailureMessage($"falling drops vary their width and stretched length ({shapes.Count} observed)").IsGreater(3);
        AssertInt(headStreams).OverrideFailureMessage("a wetted bed end feeds more than one attached stream").IsGreaterEqual(2);
        var meshWait = System.Diagnostics.Stopwatch.StartNew();
        while (!blood.GroundMeshesReady && meshWait.Elapsed.TotalSeconds < 5) { await Frames.NextProcess(); }
        AssertBool(blood.GroundMeshesReady).OverrideFailureMessage("bed liquid geometry catches up with the final deposited volume").IsTrue();
        await session.CaptureAt("edge_contact", "a thin blood pool follows the rounded pad and narrows into attached side rivulets", supplies[1], 0.35f);
        await session.CaptureOblique("rounded_corner", "a thin wet coating narrows into irregular rivulets down to the bottom frame before dripping",
            supplies[4], new Vector3(1f, 0f, -1f).Normalized(), 0.3f);
        await session.CaptureOblique("overflow", "blood stays supported by the bed end, drips fall beyond it and collect below", view, Vector3.Forward, 1.8f);
        await driver.Unbudgeted(() => {
            var beds = Deposits(blood).Where(pool => pool.OnBed).ToArray();
            AssertInt(beds.Length).OverrideFailureMessage("all four edges and a corner retain a supported pool").IsEqual(supplies.Length);
            foreach (var pool in beds) {
                var wet = pool.SideVertices.Select(p => p + pool.Center).ToArray();
                AssertFloat(wet.Min(p => p.Y)).OverrideFailureMessage($"attached side coating reaches the lower frame lip: {pool.Center}, bottom {wet.Min(p => p.Y)}, expected {mattress.DripHeight}").IsLessEqual(mattress.DripHeight + 0.001f);
                if (Mathf.Abs(pool.Center.X - 0.39f) < 0.001f) {
                    AssertBool(wet.Any(p => p.Y > 0.795f && p.Y < 0.81f && Mathf.Abs(p.Z) > 0.1f && Mathf.Abs(p.Z) < 0.24f))
                        .OverrideFailureMessage("gap runoff visibly traverses the supporting frame top before reaching its outer side").IsTrue();
                }
                var heightRange = new List<float>();
                foreach (var vertex in pool.TopVertices) {
                    var at = pool.Center + vertex;
                    var support = driver.Surgery.Room.BedSurface.HeightAt(new Vector2(at.X, at.Z));
                    // Frame-only pools occupy the real shelf beneath the pad gap; use the same actual
                    // mesh landing query when the pad surface has no support at this vertex.
                    support ??= driver.Surgery.Room.BedSurface.Landing(at + Vector3.Up * 0.01f, at - Vector3.Up * 0.01f)?.Y;
                    AssertBool(support.HasValue).OverrideFailureMessage($"every bed liquid vertex has pad or frame beneath it: {at}, pool {pool.Center}").IsTrue();
                    AssertFloat(at.Y - support!.Value).OverrideFailureMessage($"liquid adheres within a millimetre of the actual bed mesh (vertex {at}, support {support}, pool {pool.Center})").IsBetween(0.0001f, 0.0004f);
                    heightRange.Add(at.Y);
                }
                AssertFloat(heightRange.Max() - heightRange.Min()).OverrideFailureMessage("edge pools bend down the mattress instead of using a flat plane").IsGreater(0.005f);
                var worldVertices = pool.TopVertices.Select(p => p + pool.Center).ToArray();
                AssertFloat(worldVertices.Min(p => p.X)).OverrideFailureMessage("no pool hangs beyond the foot of the bed").IsGreaterEqual(Room.TableFoot - 0.00001f);
                AssertFloat(worldVertices.Max(p => p.X)).OverrideFailureMessage("no pool hangs beyond the head of the bed").IsLessEqual(Room.TableHead + 0.00001f);
                AssertFloat(worldVertices.Max(p => Mathf.Abs(p.Z))).OverrideFailureMessage("no pool hangs beyond either side of the bed").IsLessEqual(Room.TableHalfWidth + 0.00001f);
            }
            return Task.CompletedTask;
        });
        await Frames.Seconds(1f);
        var floorArea = 0f;
        await driver.Unbudgeted(() => {
            AssertFloat(blood.PendingDripMl).OverrideFailureMessage("a completed finite supply does not strand pulses at almost-complete paths").IsLess(0.0001f);
            var floors = Deposits(blood).Where(pool => !pool.OnBed).ToArray();
            AssertInt(floors.Length).OverrideFailureMessage("overflow from each tested bed edge reaches a separate floor pool").IsGreaterEqual(supplies.Length);
            floorArea = floors.Sum(pool => FootprintArea(pool));
            AssertFloat(Deposits(blood).Sum(pool => FootprintArea(pool) * (pool.OnBed ? BloodFlow.BedAreaVolumeRatio : 1f) + SideCoatingArea(pool)))
                .OverrideFailureMessage("all incoming blood remains in the bed pool, its retained side coating, or the floor after staggered release")
                .IsEqualApprox((supplies.Length * 60 * 3 + 2) * BloodFlow.PoolAreaPerDrop, 0.00001f);
            return Task.CompletedTask;
        });
        for (var frame = 0; frame < 60; frame++) {
            foreach (var at in supplies) { blood.Pool(at); }
            await Frames.NextProcess();
        }
        await Frames.Seconds(1f);
        await driver.Unbudgeted(() => {
            AssertFloat(Deposits(blood).Where(pool => !pool.OnBed).Sum(pool => FootprintArea(pool)))
                .OverrideFailureMessage("continued bed overflow grows the floor pools").IsGreater(floorArea);
            AssertFloat(blood.GroundLiquidArea)
                .OverrideFailureMessage("continued overflow conserves deposited liquid and liquid still travelling down the bed")
                .IsEqualApprox((supplies.Length * 240 + 2) * BloodFlow.PoolAreaPerDrop, 0.00001f);
            return Task.CompletedTask;
        });
        await session.CaptureOblique("floor_pool", "the bed pool remains inside its edge while the floor pool grows from later overflow", view, Vector3.Forward, 1.8f);
        // A floor has no unsupported share to spill away: continued supply must grow beyond the former cap.
        var floorHeight = Deposits(blood).First(pool => !pool.OnBed).Center.Y;
        var largeAt = new Vector3(2f, floorHeight, 2f);
        blood.Pool(largeAt, 0.05f);
        AssertBool(await Frames.Until(() => blood.GroundMeshesReady, 5f))
            .OverrideFailureMessage("the first large floor dose finishes its mesh build").IsTrue();
        blood.Pool(largeAt, 0.05f);
        AssertBool(await Frames.Until(() => blood.GroundMeshesReady, 5f))
            .OverrideFailureMessage("the added floor dose finishes its mesh build before its rendered volume is measured").IsTrue();
        await driver.Unbudgeted(() => {
            var large = Deposits(blood).Single(pool => pool.Center.DistanceTo(largeAt) < 0.001f);
            AssertFloat(FootprintArea(large)).OverrideFailureMessage("continued floor pooling retains the full dose beyond the old radius cap")
                .IsEqualApprox(0.1f, 0.00001f);
            return Task.CompletedTask;
        });
        if (FrameBudget.Mode == BudgetMode.Render) {
            // GPU timestamp queries synchronize the render thread. Profile a separate replay so their waits
            // cannot distort the whole-frame budget already sampled through the behavior above.
            await driver.Unbudgeted(async () => {
                RenderingServer.ViewportSetMeasureRenderTime(viewport, true);
                for (var frame = 0; frame < 60; frame++) {
                    foreach (var at in supplies) { blood.Pool(at); }
                    await Frames.NextProcess();
                    var ms = RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport);
                    if (ms > 0.0) { gpu.Add(ms); }
                }
                RenderingServer.ViewportSetMeasureRenderTime(viewport, false);
                AssertInt(gpu.Count).OverrideFailureMessage("the host GPU supplies render timestamps").IsGreater(0);
                gpu.Sort();
                GD.Print($"bed liquid GPU ({RenderingServer.GetVideoAdapterName()}): mean {gpu.Average():0.000} ms, "
                    + $"p95 {gpu[(int)(gpu.Count * 0.95)]:0.000} ms, max {gpu[^1]:0.000} ms");
            });
        }
        var floorBeforeCenter = Deposits(blood).Where(pool => !pool.OnBed).Sum(pool => FootprintArea(pool));
        var centerAt = new Vector3(0f, height, 0f);
        blood.Pool(centerAt, 0.1f);
        await Frames.Process(5);
        blood.Pool(centerAt, 0.02f);
        await Frames.Seconds(2f);
        var centerWait = System.Diagnostics.Stopwatch.StartNew();
        while (!blood.GroundMeshesReady && centerWait.Elapsed.TotalSeconds < 5) { await Frames.NextProcess(); }
        AssertBool(blood.GroundMeshesReady).IsTrue();
        await driver.Unbudgeted(() => {
            var middle = Deposits(blood).Single(pool => pool.OnBed && new Vector2(pool.Center.X - centerAt.X, pool.Center.Z - centerAt.Z).Length() < 0.001f);
            AssertFloat(FootprintArea(middle)).OverrideFailureMessage("an interior pool spreads beyond the old radius cap until it can drain").IsGreater(0.04f);
            AssertFloat(Deposits(blood).Where(pool => !pool.OnBed).Sum(pool => FootprintArea(pool)))
                .OverrideFailureMessage("a central supply eventually drains to the floor instead of accumulating invisible runoff").IsGreater(floorBeforeCenter + 0.005f);
            return Task.CompletedTask;
        });
        await session.Finish(FrameBudget.Mode == BudgetMode.Render ? RenderAllowance
            : FrameBudget.Mode == BudgetMode.Cpu && KeyFrames.Wanted() ? ScreenshotAllowance : null);
    }

    [TestCase]
    public async Task BedSideCoatingRetainsAFiniteDoseBeforeReleasingExcess() {
        var session = await ToolSession.Start("appendectomy", "bed_finite_coating", captureUntouched: false);
        var driver = session.Driver;
        driver.Patient.SetPhysicsProcess(false);
        driver.Body.SetProcess(false);
        var blood = driver.Body.Blood;
        blood.Sources.Clear();
        blood.Pools.Clear();
        // Locate the imported top's actual end: its rounded shoulder ends before the bed's box bound.
        var supported = Room.TableHead - 0.02f;
        var unsupported = Room.TableHead + 0.01f;
        for (var step = 0; step < 20; step++) {
            var x = (supported + unsupported) * 0.5f;
            if (driver.Surgery.Room.BedSurface.Contact(new Vector3(x, 1.1f, 0f), new Vector3(x, 0.7f, 0f)).HasValue) { supported = x; } else { unsupported = x; }
        }
        var at = new Vector3(supported - 0.00001f, BloodFlow.TableTop, 0f);
        var dose = BloodFlow.PoolAreaPerDrop * 0.01f;
        blood.Pool(at, dose);
        await Frames.Seconds(2f);
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (!blood.GroundMeshesReady && wait.Elapsed.TotalSeconds < 5) { await Frames.NextProcess(); }
        AssertBool(blood.GroundMeshesReady).IsTrue();
        await driver.Unbudgeted(() => {
            var pool = Deposits(blood).Single();
            var side = SideCoatingArea(pool);
            AssertFloat(side).OverrideFailureMessage($"a small finite runoff wets a visible part of the bed side (bed {pool.OnBed}, frame {pool.FrameOnly}, support {pool.Support}, center {pool.Center}, area {pool.Area}, incoming {pool.Incoming}, runoff {pool.Runoff}, outlets {string.Join(";", pool.Outlets.Where(o => o.Active).Select(o => $"{o.CoatingMl}/{o.CapacityMl} progress {o.Progress}/{o.DrawnProgress}/{o.AllowedProgress}"))})").IsGreater(0f);
            AssertFloat(side).OverrideFailureMessage("the actual generated side geometry cannot contain more liquid than the dose").IsLessEqual(dose);
            AssertFloat(FootprintArea(pool) * BloodFlow.BedAreaVolumeRatio + side)
                .OverrideFailureMessage("the finite dose remains in the pool and its attached coating").IsEqualApprox(dose, dose * 0.02f);
            AssertInt(blood.Drops.Count).OverrideFailureMessage("the side coating retains its small supply before it can reach the lower lip").IsEqual(0);
            AssertFloat(pool.SideVertices.Min(p => p.Y + pool.Center.Y)).IsGreater(driver.Surgery.Room.BedSurface.DripHeight + 0.01f);
            AssertFloat(blood.GroundLiquidArea).IsEqualApprox(dose, dose * 0.001f);
            return Task.CompletedTask;
        });
        blood.Pool(at, BloodFlow.PoolAreaPerDrop * 5f);
        await Frames.Seconds(3f);
        await driver.Unbudgeted(() => {
            AssertBool(Deposits(blood).Any(pool => !pool.OnBed))
                .OverrideFailureMessage("later supply fills the side coating and releases only its excess to the floor").IsTrue();
            AssertFloat(blood.GroundLiquidArea).IsEqualApprox(dose + BloodFlow.PoolAreaPerDrop * 5f, 0.000001f);
            return Task.CompletedTask;
        });
        await session.Finish(FrameBudget.Mode == BudgetMode.Render ? RenderAllowance
            : FrameBudget.Mode == BudgetMode.Cpu && KeyFrames.Wanted() ? ScreenshotAllowance : null);
    }

    [TestCase]
    public async Task FrameShelfPoolStaysVisibleAndDrainsConservedBlood() {
        var session = await ToolSession.Start("appendectomy", "bed_frame_shelf", captureUntouched: false);
        var driver = session.Driver;
        driver.Patient.SetPhysicsProcess(false);
        driver.Body.SetProcess(false);
        var blood = driver.Body.Blood;
        blood.Sources.Clear();
        blood.Pools.Clear();
        var bed = driver.Surgery.Room.BedSurface;
        var shelf = new Vector3(0.405f, BloodFlow.TableTop, 0.26f);
        var contact = bed.Landing(new Vector3(shelf.X, 1.1f, shelf.Z), new Vector3(shelf.X, 0.7f, shelf.Z));
        AssertBool(contact.HasValue).OverrideFailureMessage("a drop lands on the imported frame shelf between mattress pads").IsTrue();
        AssertFloat(contact!.Value.Y).OverrideFailureMessage("the shelf hit follows the actual frame triangles").IsBetween(0.79f, 0.83f);

        var dose = BloodFlow.PoolAreaPerDrop * 0.05f;
        blood.Pool(shelf, dose);
        await Frames.Seconds(0.5f);
        var first = Deposits(blood).Single(pool => pool.OnBed);
        AssertInt(first.TopVertices.Count).OverrideFailureMessage("a frame-supported deposit paints its actual frame surface").IsGreater(0);
        await session.CaptureOblique("frame_shelf", "a thin blood film stays on the frame shelf between pads", shelf, Vector3.Right, 0.28f);
        blood.Spray(new Vector3(shelf.X, 1.1f, shelf.Z), 1, 0f);
        await Frames.Seconds(0.5f);

        const int Supplies = 200;
        for (var i = 0; i < Supplies; i++) {
            blood.Pool(shelf, dose);
            await Frames.NextProcess();
        }
        await Frames.Seconds(2f);
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (!blood.GroundMeshesReady && wait.Elapsed.TotalSeconds < 5) { await Frames.NextProcess(); }
        AssertBool(blood.GroundMeshesReady).IsTrue();
        AssertBool(Deposits(blood).Any(pool => !pool.OnBed))
            .OverrideFailureMessage("blood on the frame shelf runs down its supporting frame and reaches the floor").IsTrue();
        AssertFloat(blood.GroundLiquidArea).OverrideFailureMessage("frame shelf coating and floor runoff conserve every finite deposit")
            .IsEqualApprox(BloodFlow.PoolAreaPerDrop + (Supplies + 1) * dose, 0.000001f);
        await session.CaptureOblique("frame_shelf_drip", "blood spreads across the exposed frame shelf without crossing adjacent pad boundaries", shelf, Vector3.Right, 0.28f);
        await session.Finish(FrameBudget.Mode == BudgetMode.Render ? RenderAllowance
            : FrameBudget.Mode == BudgetMode.Cpu && KeyFrames.Wanted() ? ScreenshotAllowance : null);
    }

    [TestCase]
    public async Task NearbyFrameAndPadDepositsKeepTheirOwnSupport() {
        var session = await ToolSession.Start("appendectomy", "bed_mixed_support", captureUntouched: false);
        var driver = session.Driver;
        driver.Patient.SetPhysicsProcess(false);
        driver.Body.SetProcess(false);
        var blood = driver.Body.Blood;
        blood.Sources.Clear();
        blood.Pools.Clear();
        var bed = driver.Surgery.Room.BedSurface;
        var dose = BloodFlow.PoolAreaPerDrop * 0.05f;
        var pairs = new[] {
            (Frame: new Vector2(0.405f, 0.2f), Pad: new Vector2(0.425f, 0.2f)),
            (Frame: new Vector2(0.835f, 0.2f), Pad: new Vector2(0.82f, 0.2f)),
        };
        for (var index = 0; index < pairs.Length; index++) {
            var (frame, pad) = pairs[index];
            AssertBool(bed.FrameSupports(frame)).IsTrue();
            AssertBool(bed.FrameSupports(pad)).IsFalse();
            AssertFloat(frame.DistanceTo(pad)).OverrideFailureMessage("these impacts are close enough to merge if support is ignored").IsLess(0.03f);
            var first = index == 0 ? frame : pad;
            var second = index == 0 ? pad : frame;
            blood.Pool(new Vector3(first.X, BloodFlow.TableTop, first.Y), dose);
            blood.Pool(new Vector3(second.X, BloodFlow.TableTop, second.Y), dose);
            AssertInt(Deposits(blood).Count()).OverrideFailureMessage("pad and frame impacts remain separate in either arrival order")
                .IsEqual((index + 1) * 2);
            var framePool = Deposits(blood).Single(pool => pool.OnBed && Mathf.Abs(pool.Center.X - frame.X) < 0.001f && Mathf.Abs(pool.Center.Z - frame.Y) < 0.001f);
            var padPool = Deposits(blood).Single(pool => pool.OnBed && Mathf.Abs(pool.Center.X - pad.X) < 0.001f && Mathf.Abs(pool.Center.Z - pad.Y) < 0.001f);
            AssertBool(framePool.Support != padPool.Support).OverrideFailureMessage("connected frame and pad faces have distinct support identities").IsTrue();
            AssertFloat(Mathf.Abs(framePool.Center.Y - padPool.Center.Y)).OverrideFailureMessage("pad and shelf centers retain their actual contact heights").IsGreater(0.005f);
        }
        AssertBool(await Frames.Until(() => blood.GroundMeshesReady, 5f)).IsTrue();
        AssertFloat(blood.GroundLiquidArea).OverrideFailureMessage("separating support classes retains every supplied milliliter")
            .IsEqualApprox(pairs.Length * 2 * dose, 0.000001f);
        await session.CaptureOblique("separate_supports", "nearby blood films stay on their own pad or lower frame shelf without jumping between heights",
            new Vector3(0.415f, 0.81f, 0.2f), Vector3.Back, 0.35f);
        await session.Finish(FrameBudget.Mode == BudgetMode.Render ? RenderAllowance
            : FrameBudget.Mode == BudgetMode.Cpu && KeyFrames.Wanted() ? ScreenshotAllowance : null);
    }

    private static float SideCoatingArea(BloodFlow.GroundPool pool) {
        var vertices = pool.SideVertices;
        if (vertices.Count == 0) { return 0f; }
        var area = 0f;
        for (var i = 0; i + 2 < vertices.Count; i += 3) {
            area += (vertices[i + 1] - vertices[i]).Cross(vertices[i + 2] - vertices[i]).Length() * 0.5f;
        }
        return area / Mathf.Pi * BloodFlow.BedAreaVolumeRatio;
    }

    private static float FootprintArea(BloodFlow.GroundPool pool) {
        var faces = pool.TopVertices;
        var area = 0f;
        for (var i = 0; i + 2 < faces.Count; i += 3) {
            var a = faces[i];
            var b = faces[i + 1];
            var c = faces[i + 2];
            area += Mathf.Abs(new Vector2(b.X - a.X, b.Z - a.Z).Cross(new Vector2(c.X - a.X, c.Z - a.Z))) / (2f * Mathf.Pi);
        }
        return area;
    }

    [TestCase]
    public async Task SustainedFloorDripCoalescesIntoConservedPools() {
        var session = await ToolSession.Start("appendectomy", "bed_sustained_floor", captureUntouched: false);
        var driver = session.Driver;
        driver.Patient.SetPhysicsProcess(false);
        driver.Body.SetProcess(false);
        var blood = driver.Body.Blood;
        blood.Sources.Clear();
        blood.Pools.Clear();
        // Ten seconds of a steady drip off the bed's head end, scattering over the floor like real splashes.
        var drip = new Vector3(Room.TableHead + 0.15f, BloodFlow.FloorY, 0f);
        var rng = new RandomNumberGenerator { Seed = 7 };
        const int Drops = 600;
        var most = 0;
        driver.Note("a steady drip spreading over the floor");
        for (var i = 0; i < Drops; i++) {
            var offset = Vector2.FromAngle(rng.Randf() * Mathf.Tau) * Mathf.Sqrt(rng.Randf()) * 0.12f;
            blood.Pool(drip + new Vector3(offset.X, 0f, offset.Y));
            most = Math.Max(most, Deposits(blood).Count());
            await Frames.NextProcess();
        }
        AssertBool(await Frames.Until(() => blood.GroundMeshesReady, 5f)).IsTrue();
        await driver.Unbudgeted(() => {
            var floor = Deposits(blood).Where(pool => !pool.OnBed).ToArray();
            foreach (var (a, b) in floor.SelectMany((a, i) => floor.Skip(i + 1).Select(b => (a, b)))) {
                var apart = new Vector2(a.Center.X - b.Center.X, a.Center.Z - b.Center.Z).Length();
                AssertFloat(apart)
                    .OverrideFailureMessage($"touching floor pools merge ({a.Radius:0.000} m, {b.Radius:0.000} m)")
                    .IsGreaterEqual(a.Radius + b.Radius);
            }
            AssertInt(floor.Length).OverrideFailureMessage(
                    $"a steady drip ends as a few pools, not one per landing (at most {most} on the way)")
                .IsLessEqual(3);
            AssertFloat(blood.GroundLiquidArea).OverrideFailureMessage("merging keeps every supplied milliliter")
                .IsEqualApprox(Drops * BloodFlow.PoolAreaPerDrop, Drops * BloodFlow.PoolAreaPerDrop * 0.0001f);
            return Task.CompletedTask;
        });
        await session.CaptureOblique("sustained_floor_pool",
            "one broad, smooth floor pool where the drip landed, no separate puddles overlapping inside it",
            drip, Vector3.Back, 0.8f);
        await session.Finish(FrameBudget.Mode == BudgetMode.Render ? RenderAllowance
            : FrameBudget.Mode == BudgetMode.Cpu && KeyFrames.Wanted() ? ScreenshotAllowance : null);
    }

    private static IEnumerable<BloodFlow.GroundPool> Deposits(BloodFlow blood) => blood.GroundDeposits;
}
