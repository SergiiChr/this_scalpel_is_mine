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
    public async Task BloodAtEveryBedEdgeRunsDownItsSidesAndDripsOntoTheFloor() {
        var session = await ToolSession.Start("appendectomy", "blood_bed_edges", captureUntouched: false);
        var driver = session.Driver;
        var blood = HoldStill(driver);
        var height = BloodFlow.TableTop;
        Vector3[] supplies = [
            new(Room.TableFoot + 0.025f, height, 0.05f),
            new(Room.TableHead - 0.025f, height, 0.05f),
            new(0.1f, height, -Room.TableHalfWidth + 0.025f),
            new(-0.4f, height, Room.TableHalfWidth - 0.025f),
            new(Room.TableHead - 0.02f, height, -Room.TableHalfWidth + 0.02f),
        ];
        var mattress = driver.Surgery.Room.BedSurface;
        foreach (var at in new[] { new Vector2(0.92f, 0f), new Vector2(0.96f, -0.22f) }) {
            var landing = mattress.Landing(new Vector3(at.X, 1.1f, at.Y), new Vector3(at.X, 0.7f, at.Y));
            AssertBool(landing.HasValue).OverrideFailureMessage("a falling drop meets both the flat middle and rounded edge of the mattress").IsTrue();
            AssertFloat(landing!.Value.Y).IsEqualApprox(mattress.HeightAt(at)!.Value, 0.00001f);
        }
        var frameShelf = mattress.Landing(new Vector3(0.405f, 1.1f, 0.2f), new Vector3(0.405f, 0.7f, 0.2f));
        AssertBool(frameShelf.HasValue).OverrideFailureMessage("a falling drop meets the frame shelf in the gap between pads").IsTrue();
        AssertFloat(frameShelf!.Value.Y).OverrideFailureMessage("frame contact uses the imported frame mesh's height").IsBetween(0.79f, 0.83f);
        foreach (var at in new[] { new Vector2(0.405f, 0.31f), new Vector2(0.999f, -0.279f) }) {
            AssertBool(mattress.Landing(new Vector3(at.X, 1.1f, at.Y), new Vector3(at.X, 0.7f, at.Y)).HasValue)
                .OverrideFailureMessage("drops miss past the frame's edge and the outer corner").IsFalse();
        }
        var view = new Vector3(0.7f, 0.6f, -Room.TableHalfWidth);
        await session.CaptureOblique("untouched", "the empty bed end and floor before blood lands", view, Vector3.Forward, 1.8f);
        await LookFrom(driver, view, 1.8f);
        driver.Note("blood supplying the bed ends, sides and corner");
        driver.Budget.Clear();
        var supplied = 0f;
        for (var frame = 0; frame < 360; frame++) {
            if (frame < 60) {
                foreach (var at in supplies) { blood.Deposit(at, 0.45f); }
                supplied += supplies.Length * 0.45f;
            }
            await Frames.NextProcess();
        }
        blood.FinishFilms();
        await driver.Unbudgeted(() => {
            var bed = blood.BedFilm;
            foreach (var at in supplies) {
                var side = Enumerable.Range(0, bed.Count).Where(k => Mathf.Abs(bed.NormalAt(k).Y) < 0.5f
                    && new Vector2(bed.At(k).X - at.X, bed.At(k).Z - at.Z).Length() < 0.08f).Select(bed.CoverageAt).DefaultIfEmpty(0f).Max();
                AssertFloat(side).OverrideFailureMessage($"blood at {at} runs over the bed's edge and down its side").IsGreater(0.3f);
            }
            AssertFloat(blood.FloorFilm.HeldMl).OverrideFailureMessage("what runs off the bed drips onto the floor").IsGreater(1f);
            AssertFloat(blood.GroundMl).OverrideFailureMessage("bed, drops and floor keep every supplied milliliter")
                .IsEqualApprox(supplied, supplied * 0.0001f);
            return Task.CompletedTask;
        });
        await session.CaptureOblique("rounded_corner", "the blood follows the rounded pad over the corner and runs down the side, its edges fading out",
            supplies[4], new Vector3(1f, 0f, -1f).Normalized(), 0.3f);
        await session.CaptureOblique("overflow", "blood on the bed end runs down its side to the frame's lip and drips onto a pool on the floor",
            view, Vector3.Forward, 1.8f);
        await session.Finish(Allowance());
    }

    [TestCase]
    public async Task BloodOnTheFrameShelfDrainsToTheFloor() {
        var session = await ToolSession.Start("appendectomy", "bed_frame_shelf", captureUntouched: false);
        var driver = session.Driver;
        var blood = HoldStill(driver);
        var bed = driver.Surgery.Room.BedSurface;
        var shelf = new Vector3(0.405f, BloodFlow.TableTop, 0.26f);
        var contact = bed.Landing(new Vector3(shelf.X, 1.1f, shelf.Z), new Vector3(shelf.X, 0.7f, shelf.Z));
        AssertBool(contact.HasValue).OverrideFailureMessage("a drop lands on the frame shelf between the mattress pads").IsTrue();
        AssertFloat(contact!.Value.Y).OverrideFailureMessage("the shelf hit follows the actual frame triangles").IsBetween(0.79f, 0.83f);
        const int Supplies = 200;
        const float Dose = 0.1f;
        for (var i = 0; i < Supplies; i++) {
            blood.Deposit(shelf, Dose);
            await Frames.NextProcess();
        }
        await Frames.Seconds(3f);
        blood.FinishFilms();
        await driver.Unbudgeted(() => {
            var film = blood.BedFilm;
            var onShelf = Enumerable.Range(0, film.Count).Where(k => film.At(k).DistanceTo(contact.Value) < 0.02f).Max(film.CoverageAt);
            AssertFloat(onShelf).OverrideFailureMessage("blood stands on the shelf where it lands").IsGreater(0.5f);
            AssertFloat(blood.FloorFilm.HeldMl).OverrideFailureMessage("the shelf drains over the frame onto the floor").IsGreater(0f);
            AssertFloat(blood.GroundMl).OverrideFailureMessage("shelf, frame, drops and floor keep every supplied milliliter")
                .IsEqualApprox(Supplies * Dose, Supplies * Dose * 0.0001f);
            return Task.CompletedTask;
        });
        await session.CaptureOblique("frame_shelf_drip", "blood spreads over the frame shelf between pads and runs down the frame, its edges fading out",
            contact.Value, Vector3.Right, 0.28f);
        await session.Finish(Allowance());
    }

    [TestCase]
    public async Task SustainedFloorDripSpreadsIntoOneConnectedPool() {
        var session = await ToolSession.Start("appendectomy", "bed_sustained_floor", captureUntouched: false);
        var driver = session.Driver;
        var blood = HoldStill(driver);
        // Ten seconds of a steady drip off the bed's head end, scattering over the floor like real splashes.
        var drip = new Vector3(Room.TableHead + 0.15f, BloodFlow.FloorY, 0f);
        var rng = new RandomNumberGenerator { Seed = 7 };
        const int Drops = 600;
        driver.Note("a steady drip spreading over the floor");
        for (var i = 0; i < Drops; i++) {
            var offset = Vector2.FromAngle(rng.Randf() * Mathf.Tau) * Mathf.Sqrt(rng.Randf()) * 0.12f;
            blood.Deposit(drip + new Vector3(offset.X, 0f, offset.Y), BloodFlow.DropMl);
            await Frames.NextProcess();
        }
        await Frames.Seconds(1f);
        blood.FinishFilms();
        await driver.Unbudgeted(() => {
            var floor = blood.FloorFilm;
            var wet = Enumerable.Range(0, floor.Count).Where(k => floor.CoverageAt(k) > 0.5f).ToHashSet();
            AssertInt(Connected(floor, wet)).OverrideFailureMessage("the drip's splashes run together into one pool").IsEqual(1);
            var reach = wet.Max(k => new Vector2(floor.At(k).X - drip.X, floor.At(k).Z - drip.Z).Length());
            AssertFloat(reach).OverrideFailureMessage($"the pool spreads past where the splashes landed ({reach * 100f:0} cm)").IsGreater(0.12f);
            AssertFloat(blood.GroundMl).OverrideFailureMessage("the floor keeps every milliliter that landed")
                .IsEqualApprox(Drops * BloodFlow.DropMl, Drops * BloodFlow.DropMl * 0.0001f);
            return Task.CompletedTask;
        });
        await session.CaptureOblique("sustained_floor_pool", "one smooth pool where the drip landed, its edge fading out, nothing floating off the floor",
            drip, Vector3.Back, 0.8f);
        await session.Finish(Allowance());
    }

    /// <summary>The bed fixtures hold the patient still: the patient's bleeding and tissue are other suites' work.</summary>
    private static BloodFlow HoldStill(SurgeryDriver driver) {
        driver.Patient.SetPhysicsProcess(false);
        driver.Body.SetProcess(false);
        var blood = driver.Body.Blood;
        blood.Sources.Clear();
        blood.Pools.Clear();
        return blood;
    }

    private static async Task LookFrom(SurgeryDriver driver, Vector3 view, float distance) {
        if (FrameBudget.Mode != BudgetMode.Render) { return; }
        var camera = new Camera3D { Fov = 35f, Near = 0.01f, CullMask = RenderLayers.Normal };
        driver.Surgery.AddChild(camera);
        camera.GlobalPosition = view + (Vector3.Up + Vector3.Forward).Normalized() * distance;
        camera.LookAt(view);
        camera.Current = true;
        await Frames.Process(12);
    }

    private static BudgetAllowance? Allowance() => FrameBudget.Mode == BudgetMode.Render ? RenderAllowance
        : FrameBudget.Mode == BudgetMode.Cpu && KeyFrames.Wanted() ? ScreenshotAllowance : null;

    /// <summary>How many separate patches the <paramref name="wet"/> points make, joined where they lie within two
    /// of the film's spacings of each other.</summary>
    private static int Connected(SurfaceFilm film, HashSet<int> wet) {
        var left = new HashSet<int>(wet);
        var patches = 0;
        while (left.Count > 0) {
            patches++;
            var pending = new Queue<int>([left.First()]);
            left.Remove(pending.Peek());
            while (pending.TryDequeue(out var k)) {
                foreach (var other in left.Where(o => film.At(o).DistanceTo(film.At(k)) < 0.031f).ToList()) {
                    left.Remove(other);
                    pending.Enqueue(other);
                }
            }
        }
        return patches;
    }
}
