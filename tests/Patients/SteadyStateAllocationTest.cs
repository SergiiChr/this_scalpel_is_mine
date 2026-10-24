namespace Scalpel.Tests.Patients;

/// <summary>
/// The patient's per-frame and per-tick simulation allocates nothing once warmed up (<see cref="AllocationBudget"/>):
/// skin held and pulled beside a cut, paint on the wound map, and drugs wearing off.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("allocation_budget")]
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
        var step = 0;
        AllocationBudget.Check("tissue step with a grip beside a cut", () => {
            sim.MoveGrip(1, held + Vector3.Up * (0.004f * Mathf.Sin(step++ * 0.3f)));
            sim.Advance(TissueSim.Step);
            sim.Region(region);
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
