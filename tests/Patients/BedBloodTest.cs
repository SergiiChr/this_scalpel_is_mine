namespace Scalpel.Tests.Patients;

[TestSuite, RequireGodotRuntime]
[TestCategory("liquids"), TestCategory("visual_confirmation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class BedBloodTest {
    [TestCase]
    public async Task BloodAtEveryBedEdgeDripsOntoTheFloor() {
        var session = await ToolSession.Start("appendectomy", "blood_bed_edges", captureUntouched: false);
        var driver = session.Driver;
        driver.Patient.SetPhysicsProcess(false);
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
        ];
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
        driver.Note("blood supplying the bed ends, sides and corner");
        driver.Budget.Clear();
        // Controlled landed drops isolate bed runoff from the patient's wound and vital signs.
        for (var frame = 0; frame < 60; frame++) {
            foreach (var at in supplies) { blood.Pool(at); }
            await Frames.NextProcess();
        }
        await session.CaptureOblique("overflow", "blood stays supported by the bed end, drips fall beyond it and collect below", view, Vector3.Forward, 1.8f);
        var beds = Deposits(blood).Where(pool => pool.Position.Y > Room.TableHeight).ToArray();
        AssertInt(beds.Length).OverrideFailureMessage("all four edges and a corner retain a supported pool").IsEqual(supplies.Length);
        foreach (var pool in beds) {
            AssertFloat(pool.Position.X - pool.Scale.X).OverrideFailureMessage("no pool hangs beyond the foot of the bed").IsGreaterEqual(Room.TableFoot - 0.00001f);
            AssertFloat(pool.Position.X + pool.Scale.X).OverrideFailureMessage("no pool hangs beyond the head of the bed").IsLessEqual(Room.TableHead + 0.00001f);
            AssertFloat(Mathf.Abs(pool.Position.Z) + pool.Scale.Z).OverrideFailureMessage("no pool hangs beyond either side of the bed").IsLessEqual(Room.TableHalfWidth + 0.00001f);
        }
        await Frames.Seconds(1f);
        var floors = Deposits(blood).Where(pool => pool.Position.Y < 0.01f).ToArray();
        AssertInt(floors.Length).OverrideFailureMessage("overflow from each tested bed edge reaches a separate floor pool").IsEqual(supplies.Length);
        var floorArea = floors.Sum(pool => pool.Scale.X * pool.Scale.Z);
        AssertFloat(Deposits(blood).Sum(pool => pool.Scale.X * pool.Scale.Z))
            .OverrideFailureMessage("all incoming blood remains on the bed or reaches the floor, even when the droplet limit fills")
            .IsEqualApprox(supplies.Length * 60 * BloodFlow.PoolAreaPerDrop, 0.00001f);
        for (var frame = 0; frame < 60; frame++) {
            foreach (var at in supplies) { blood.Pool(at); }
            await Frames.NextProcess();
        }
        await Frames.Seconds(1f);
        AssertFloat(Deposits(blood).Where(pool => pool.Position.Y < 0.01f).Sum(pool => pool.Scale.X * pool.Scale.Z))
            .OverrideFailureMessage("continued bed overflow grows the floor pools").IsGreater(floorArea);
        await session.CaptureOblique("floor_pool", "the bed pool remains inside its edge while the floor pool grows from later overflow", view, Vector3.Forward, 1.8f);
        await session.Finish();
    }

    private static IEnumerable<MeshInstance3D> Deposits(BloodFlow blood) => blood.GetChildren().OfType<MeshInstance3D>();
}
