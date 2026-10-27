namespace Scalpel.Tests.Patients;

/// <summary>
/// The patient's per-frame and per-tick simulation allocates nothing once warmed up (<see cref="AllocationBudget"/>):
/// skin held and pulled beside a cut, paint on the wound map, and drugs wearing off. A whole surgery's frames stay
/// within their measured budgets, a lip drawn aside in the thigh too.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("allocation_budget")]
[GodotArgs("--fixed-fps", "60")]
public class SteadyStateAllocationTest {
    private static readonly Vector2 Mid = new(0.5f, 0.5f);

    /// <summary>A grip pulls the skin next to a cut back and forth, so the window keeps refreshing (its lists at the
    /// size the warm-up grew them to), and the body takes the region every step as it does every other frame.</summary>
    [TestCase]
    public void MovingSkinBesideACut() {
        var sim = new TissueSim { Tearing = true };
        sim.Build(new Vector2(0.3f, 0.25f), _ => 0f);
        sim.Cut(new Vector2(0.2f, 0.51f), new Vector2(0.8f, 0.51f), TissueDepth.Skin);
        sim.Grip(1, new Vector2(0.5f, 0.4f));
        var held = sim.Pos[sim.Nearest(new Vector2(0.5f, 0.4f))];
        var region = new byte[sim.Rest.Length];
        var lift = new float[sim.Rest.Length];
        var step = 0;
        AllocationBudget.Check("tissue step with a grip beside a cut", () => {
            var target = held + Vector3.Up * (0.004f * Mathf.Sin(step++ * 0.3f));
            sim.MoveGrip(1, sim.Restrain(1, target));
            sim.Advance(TissueSim.Step);
            sim.Region(region);
            sim.CaptureGripLift(lift);
        });
        AssertBool(region.Contains((byte)1)).OverrideFailureMessage("the cut and the grip are in the region").IsTrue();
    }

    /// <summary>Blood strokes, a wipe painting several channels at once, and a single disk.</summary>
    [TestCase]
    public void WoundMapPaint() {
        var map = new WoundMap(WoundMap.MinSize);
        var seed = 0ul;
        AllocationBudget.Check("wound map strokes and disks", () => {
            map.Stroke(WoundMap.Layer.Fluids, WoundMap.Blood, Mid, Mid + new Vector2(0.1f, 0.05f), 0.01f, 0.5f,
                WoundMap.Mode.Max, jitter: 0.002f, seed: seed++);
            map.DiskOps(WoundMap.Layer.Fluids, Mid, 0.05f, [
                new(WoundMap.Blood, 0.1f, WoundMap.Mode.Sub),
                new(WoundMap.Grime, 0.1f, WoundMap.Mode.Sub),
            ]);
            map.Disk(WoundMap.Layer.Wounds, WoundMap.Bruise, Mid, 0.02f, 0.3f, WoundMap.Mode.Max);
        });
    }

    // Measured per frame in the appendectomy: 7 KB idle, 40 KB while the cut is made (its meshes rebuilt at new sizes)
    // and 32 KB while it bleeds (most of it the blood field growing once with the pool). The blood field is built on a
    // worker, so how many builds land in a window varies a little with the machine.
    private const long IdleBudget = 10 << 10, CuttingBudget = 52 << 10, BleedingBudget = 44 << 10;

    /// <summary>A surgery's frames: the patient asleep before the first cut, a skin cut across the site being made
    /// with the scalpel already in hand, then the cut bleeding.</summary>
    [TestCase]
    public async Task SurgeryFrames() {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        SurgeryState.PatientIsAsleep(driver.Patient);
        await Frames.Seconds(2f);
        await AllocationBudget.CheckFrames("idle surgery", () => Frames.Seconds(5f), IdleBudget);
        await driver.PlayerRequestsItem("scalpel");
        await AllocationBudget.CheckFrames("cutting the skin",
            () => driver.PlayerCutsSkin(new Vector2(0.3f, 0.45f), new Vector2(0.7f, 0.45f), 1), CuttingBudget);
        AssertBool(driver.Patient.Wounds.Any(wound => wound.MadeBySurgeon)).OverrideFailureMessage("the cut was made")
            .IsTrue();
        await AllocationBudget.CheckFrames("the cut bleeding", () => Frames.Seconds(5f), BleedingBudget);
        AssertFloat(driver.Patient.Vitals.BleedRate).OverrideFailureMessage("the cut was still bleeding").IsGreater(0f);
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }

    // Measured per frame in the thigh: 28 KB while a lip is drawn aside with forceps, the layers planned and filled
    // again as the skin moves (51 KB before the layer surfaces kept their buffers).
    private const long DrawnLipBudget = 36 << 10;

    /// <summary>The widened thigh wound's lip taken in forceps and drawn aside a centimeter: the layers are planned,
    /// filled and published again as the skin moves, into kept buffers.</summary>
    [TestCase]
    public async Task ThighLipDrawnAside() {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        await driver.Start("bullet_muscle");
        SurgeryState.PatientIsAsleep(driver.Patient);
        await driver.PlayerIncises(0.05f);
        var cut = driver.Patient.Wounds.Where(wound => wound.Kind == WoundKind.Cut).MaxBy(wound => wound.LengthUv)!;
        await driver.PlayerRequestsItem("forceps");
        await driver.PlayerWalksTo(driver.SitePoint(cut.Midpoint));
        var lip = driver.LipOf(cut, 1);
        AssertObject(await driver.PlayerTakesHoldOfLip(driver.Me.Active, lip)).OverrideFailureMessage("the forceps take the lip")
            .IsNotNull();
        var away = (driver.SitePoint(lip) - driver.SitePoint(cut.ClosestPoint(lip))).Slide(Vector3.Up).Normalized();
        await AllocationBudget.CheckFrames("a thigh lip drawn aside",
            () => driver.PlayerDrawsLip(driver.Me.Active, away, 0.01f), DrawnLipBudget);
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }

    /// <summary>Two drugs soaking in, crossing into working during the warm-up, then held at their level.</summary>
    [TestCase]
    public void DrugsInTheBody() {
        var drugs = new DrugLevels();
        foreach (var id in (string[])["lidocaine", "propofol"]) {
            drugs.Give(Db.Drug(id)!, 1f, 2f);
        }
        AllocationBudget.Check("drug levels tick", () => drugs.Update(0.1f, static (_, _) => 0f));
        AssertBool(drugs.Entries.Count == 2).OverrideFailureMessage("both drugs are still in the body").IsTrue();
    }
}
