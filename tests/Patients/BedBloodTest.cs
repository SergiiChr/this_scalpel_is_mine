namespace Scalpel.Tests.Patients;

[TestSuite, RequireGodotRuntime]
[TestCategory("liquid_contours")]
[TestCategory("liquids"), TestCategory("visual_confirmation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class BedBloodTest {
    // The same bed camera without liquid reached 45.4 ms on the RTX 4080. Preserve those existing scene
    // spikes with 30% headroom rounded to a half-factor; the headless CPU check keeps its strict limit.
    private static readonly BudgetAllowance RenderAllowance = new("bed scene spikes also measured without any liquid", 4f);

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
        foreach (var at in new[] { new Vector2(0.405f, 0f), new Vector2(0.999f, -0.279f) }) {
            AssertBool(mattress.Landing(new Vector3(at.X, 1.1f, at.Y), new Vector3(at.X, 0.7f, at.Y)).HasValue)
                .OverrideFailureMessage("drops miss gaps between pads and the unsupported outer corner").IsFalse();
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
        for (var frame = 0; frame < 120; frame++) {
            if (frame < 60) { foreach (var at in supplies) { blood.Pool(at); } }
            await Frames.NextProcess();
            if (blood.Drops.Count > 0) {
                var shape = blood.Drops[0].Shape;
                shapes.Add(new Vector2(Mathf.Round(shape.X * 100000f), Mathf.Round(shape.Y * 100000f)));
            }
            var necks = blood.AttachedDrips.ToArray();
            AssertBool(necks.All(at => at.Y < mattress.DripHeight + 0.001f))
                .OverrideFailureMessage("attached drops begin only at the bottom frame lip, never midway down the mattress").IsTrue();
            if (frame < 20) {
                AssertInt(blood.Drops.Count).OverrideFailureMessage("runoff wets the bed side before any free drops detach").IsEqual(0);
            }
            headStreams = Math.Max(headStreams, necks.Count(at => at.X > Room.TableHead - 0.045f));
            var atSide = necks.Count(at => at.Z < -Room.TableHalfWidth + 0.012f && Mathf.Abs(at.X - supplies[2].X) < 0.1f);
            if (!shownNecks && atSide >= 2) {
                shownNecks = true;
                await session.CaptureOblique("ligaments", "uneven rivulets stay attached down the pad and frame; necks and drops begin at the bottom lip",
                    new Vector3(supplies[2].X, height - 0.05f, -Room.TableHalfWidth), Vector3.Forward, 0.28f);
            }
        }
        AssertInt(shapes.Count).OverrideFailureMessage("falling drops vary their width and stretched length").IsGreater(3);
        AssertInt(headStreams).OverrideFailureMessage("a wetted bed end feeds more than one attached stream").IsGreaterEqual(2);
        var meshWait = System.Diagnostics.Stopwatch.StartNew();
        while (!blood.GroundMeshesReady && meshWait.Elapsed.TotalSeconds < 5) { await Frames.NextProcess(); }
        AssertBool(blood.GroundMeshesReady).OverrideFailureMessage("bed liquid geometry catches up with the final deposited volume").IsTrue();
        await session.CaptureAt("edge_contact", "a thin blood pool follows the rounded pad and narrows into attached side rivulets", supplies[1], 0.35f);
        await session.CaptureOblique("rounded_corner", "a thin wet coating narrows into irregular rivulets down to the bottom frame before dripping",
            supplies[4], new Vector3(1f, 0f, -1f).Normalized(), 0.3f);
        await session.CaptureOblique("overflow", "blood stays supported by the bed end, drips fall beyond it and collect below", view, Vector3.Forward, 1.8f);
        await driver.Unbudgeted(() => {
            var beds = Deposits(blood).Where(pool => pool.Position.Y > Room.TableHeight).ToArray();
            AssertInt(beds.Length).OverrideFailureMessage("all four edges and a corner retain a supported pool").IsEqual(supplies.Length);
            foreach (var pool in beds) {
                var side = pool.GetNode<MeshInstance3D>("BedRivulets");
                AssertInt(side.Mesh.GetSurfaceCount()).OverrideFailureMessage("every overflowing pool leaves attached rivulets down the bed side").IsGreater(0);
                using var sideArrays = side.Mesh.SurfaceGetArrays(0);
                var wet = sideArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Select(p => p + pool.Position).ToArray();
                AssertFloat(wet.Min(p => p.Y)).OverrideFailureMessage("attached side coating reaches the lower frame lip").IsLessEqual(mattress.DripHeight + 0.001f);
                if (Mathf.Abs(pool.Position.X - 0.39f) < 0.001f) {
                    AssertBool(wet.Any(p => p.Y > 0.795f && p.Y < 0.81f && Mathf.Abs(p.Z) > 0.1f && Mathf.Abs(p.Z) < 0.24f))
                        .OverrideFailureMessage("gap runoff visibly traverses the supporting frame top before reaching its outer side").IsTrue();
                }
                var heightRange = new List<float>();
                using var arrays = pool.Mesh.SurfaceGetArrays(0);
                foreach (var vertex in arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array()) {
                    var at = pool.Position + vertex;
                    var support = driver.Surgery.Room.BedSurface.HeightAt(new Vector2(at.X, at.Z));
                    AssertBool(support.HasValue).OverrideFailureMessage($"every bed liquid vertex has mattress beneath it, including rounded corners: {at}").IsTrue();
                    AssertFloat(at.Y - support!.Value).OverrideFailureMessage("liquid adheres within a millimetre of the actual mattress mesh").IsBetween(0.0001f, 0.0004f);
                    heightRange.Add(at.Y);
                }
                AssertFloat(heightRange.Max() - heightRange.Min()).OverrideFailureMessage("edge pools bend down the mattress instead of using a flat plane").IsGreater(0.005f);
                AssertFloat(pool.Position.X + pool.Mesh.GetAabb().Position.X).OverrideFailureMessage("no pool hangs beyond the foot of the bed").IsGreaterEqual(Room.TableFoot - 0.00001f);
                AssertFloat(pool.Position.X + pool.Mesh.GetAabb().End.X).OverrideFailureMessage("no pool hangs beyond the head of the bed").IsLessEqual(Room.TableHead + 0.00001f);
                AssertFloat(Mathf.Max(Mathf.Abs(pool.Position.Z + pool.Mesh.GetAabb().Position.Z), Mathf.Abs(pool.Position.Z + pool.Mesh.GetAabb().End.Z))).OverrideFailureMessage("no pool hangs beyond either side of the bed").IsLessEqual(Room.TableHalfWidth + 0.00001f);
            }
            return Task.CompletedTask;
        });
        await Frames.Seconds(1f);
        var floorArea = 0f;
        await driver.Unbudgeted(() => {
            var floors = Deposits(blood).Where(pool => pool.Position.Y < 0.01f).ToArray();
            AssertInt(floors.Length).OverrideFailureMessage("overflow from each tested bed edge reaches a separate floor pool").IsGreaterEqual(supplies.Length);
            floorArea = floors.Sum(pool => FootprintArea(pool));
            AssertFloat(Deposits(blood).Sum(pool => FootprintArea(pool)))
                .OverrideFailureMessage("all incoming blood remains on the bed or reaches the floor after staggered release")
                .IsEqualApprox((supplies.Length * 60 + 2) * BloodFlow.PoolAreaPerDrop, 0.00001f);
            return Task.CompletedTask;
        });
        for (var frame = 0; frame < 60; frame++) {
            foreach (var at in supplies) { blood.Pool(at); }
            await Frames.NextProcess();
        }
        await Frames.Seconds(1f);
        await driver.Unbudgeted(() => {
            AssertFloat(Deposits(blood).Where(pool => pool.Position.Y < 0.01f).Sum(pool => FootprintArea(pool)))
                .OverrideFailureMessage("continued bed overflow grows the floor pools").IsGreater(floorArea);
            return Task.CompletedTask;
        });
        await session.CaptureOblique("floor_pool", "the bed pool remains inside its edge while the floor pool grows from later overflow", view, Vector3.Forward, 1.8f);
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
        await session.Finish(FrameBudget.Mode == BudgetMode.Render ? RenderAllowance : null);
    }

    private static float FootprintArea(MeshInstance3D pool) {
        using var arrays = pool.Mesh.SurfaceGetArrays(0);
        var faces = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var area = 0f;
        for (var i = 0; i + 2 < faces.Length; i += 3) {
            var a = faces[i];
            var b = faces[i + 1];
            var c = faces[i + 2];
            area += Mathf.Abs(new Vector2(b.X - a.X, b.Z - a.Z).Cross(new Vector2(c.X - a.X, c.Z - a.Z))) / (2f * Mathf.Pi);
        }
        return area;
    }

    private static IEnumerable<MeshInstance3D> Deposits(BloodFlow blood) => blood.GetChildren().OfType<MeshInstance3D>();
}
