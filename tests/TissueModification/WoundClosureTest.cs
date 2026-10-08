namespace Scalpel.Tests.TissueModification;

/// <summary>
/// A wound counts as closed exactly where it looks closed, whatever closed it: staples or a running thread leaving gaps
/// between them leave it open and bleeding there, and once its edges meet along its length it's closed and stops
/// bleeding. The forearm cut of Hand Stitch, stapled and sewn like a player would. Headless assertions in smoke; with
/// key frames also the gaps left and the cut closed. Review them for gaps between the closures where the cut counts
/// open, and no gap left where it counts closed.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("tissue_modification"), TestCategory("tool_skin_stapler")]
[TestCategory("tool_needle"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class WoundClosureTest
{
    private const string BudgetBroken = "known to go over the frame budget: each staple changes the tissue, and "
        + "rebuilding its layers takes about 6 ms (PatientBody.RebuildLayers()); not optimized yet";
    /// <summary>Edges further apart than this (meters) show a gap; closer than <see cref="Meets"/> they meet. Between
    /// the two it's a judgement call the checks leave alone.</summary>
    private const float Shows = 0.002f;
    private const float Meets = TissueSim.ClosedGap;
    /// <summary>A gap this close (meters) to a point of the cut is at that point; edges meeting this far round it, it's
    /// closed there. The game looks in between (TissueSim.SeamReach): it may count a point either way where a gap
    /// starts just past it.</summary>
    private const float At = 0.003f;
    private const float Around = 0.008f;
    /// <summary>Blood (ml/s) a closed wound may still lose: none to speak of.</summary>
    private const float Dry = 0.02f;

    [TestCase]
    public async Task StaplesCountClosedOnlyWhereTheEdgesMeet()
    {
        var session = await Start("staples");
        var driver = session.Driver;
        var wound = driver.Patient.Wounds[0];
        await driver.PlayerRequestsItem("skin_stapler");
        await driver.PlayerStaples(wound, 0.025f, 1);
        await Frames.Seconds(1f);
        AssertFloat(WidestGap(driver, wound))
            .OverrideFailureMessage($"staples 2.5 cm apart leave gaps between them\n{driver.Recent()}")
            .IsGreater(Shows);
        AssertFloat(wound.Closure).OverrideFailureMessage("so the cut isn't closed").IsLess(0.99f);
        AssertFloat(Bleeding(driver, wound)).OverrideFailureMessage("and it still bleeds").IsGreater(Dry);
        AssertCountsAsSeen(driver, wound, "sparse staples");
        await driver.Capture("gaps");

        // Filled in a centimeter apart, then wherever the rings still show a gap, like a player looking it over.
        await driver.PlayerStaples(wound, 0.01f, 2);
        await Frames.Seconds(1f);
        AssertFloat(WidestGap(driver, wound))
            .OverrideFailureMessage($"filled in, no gap shows along the cut\n{driver.Recent()}").IsLess(Shows);
        AssertFloat(wound.Closure).OverrideFailureMessage($"so it's closed\n{OpenBins(driver, wound)}")
            .IsGreater(0.99f);
        AssertFloat(Bleeding(driver, wound)).OverrideFailureMessage("and doesn't bleed").IsLess(Dry);
        AssertCountsAsSeen(driver, wound, "filled in");
        await driver.PlayerPutsDown();
        await driver.Capture("closed");
        await session.Finish(BudgetBroken);
    }

    [TestCase]
    public async Task ThreadCountsClosedOnlyWhereTheEdgesMeet()
    {
        var session = await Start("thread");
        var driver = session.Driver;
        var wound = driver.Patient.Wounds[0];
        await driver.PlayerRequestsItem("needle");
        AssertBool(await driver.PlayerThreads(wound, TissueDepth.Skin, 5f))
            .OverrideFailureMessage("holes go in beside the cut, far apart").IsTrue();
        await driver.PlayerPullsThread("closed");
        await Frames.Seconds(1f);
        AssertFloat(WidestGap(driver, wound))
            .OverrideFailureMessage(
                $"pulled to closed, holes far apart still leave gaps between them\n{driver.Recent()}")
            .IsGreater(Shows);
        AssertFloat(wound.Closure).OverrideFailureMessage("so the cut isn't closed").IsLess(0.99f);
        AssertFloat(Bleeding(driver, wound)).OverrideFailureMessage("and it still bleeds").IsGreater(Dry);
        AssertCountsAsSeen(driver, wound, "wide thread");
        await driver.Capture("gaps");

        await driver.PlayerTiesOff("tied");
        await Frames.Seconds(1f);
        AssertFloat(WidestGap(driver, wound))
            .OverrideFailureMessage("tied off, the thread gathers the edges between its holes too").IsLess(Shows);
        AssertFloat(wound.Closure).OverrideFailureMessage("so it's closed").IsGreater(0.99f);
        AssertFloat(Bleeding(driver, wound)).OverrideFailureMessage("and doesn't bleed").IsLess(Dry);
        AssertCountsAsSeen(driver, wound, "tied thread");
        await session.Finish(BudgetBroken);
    }

    /// <summary>The office stapler as in a real run: every staple catches a vessel, the cut bleeds through the staples,
    /// cautery stops it. Stapled shut and dry, the cut counts closed, enough for Hand Stitch's "Close the cut".
    /// </summary>
    [TestCase]
    public async Task OfficeStaplesThroughVesselsCountClosedOnceDry()
    {
        var session = await Start("office");
        var driver = session.Driver;
        var patient = driver.Patient;
        var wound = patient.Wounds[0];
        SurgeryState.ToolIsOnTray(driver.Surgery, "office_stapler");
        var stapler = (await driver.PlayerRequestsItem("office_stapler"))!;
        var def = stapler.Def;
        SurgeryState.ToolGoesWrong(stapler, 0f, 1f);
        await driver.PlayerStaples(wound, 0.01f, 2);
        SurgeryState.ToolGoesWrong(stapler, def.TearChance, def.BleedChance);
        await Frames.Seconds(1f);
        AssertInt(patient.Wounds.Count).OverrideFailureMessage("the nicked vessels make no new wounds").IsEqual(1);
        AssertFloat(Bleeding(driver, wound)).OverrideFailureMessage("but the stapled cut bleeds through")
            .IsGreater(Dry);
        await driver.PlayerPutsDown();
        await driver.PlayerStopsBleeding(Dry);
        await Frames.Seconds(1f);
        AssertFloat(Bleeding(driver, wound)).OverrideFailureMessage("the cautery stops it").IsLess(Dry);
        AssertFloat(patient.SkinClosure())
            .OverrideFailureMessage(
                $"stapled and dry, the cut is closed enough for \"Close the cut\"\n{OpenBins(driver, wound)}")
            .IsGreaterEqual(Patient.ClosedEnough);
        AssertCountsAsSeen(driver, wound, "office staples");
        await driver.Capture("closed");
        await session.Finish(BudgetBroken);
    }

    /// <summary>Starts Hand Stitch with the patient asleep, and in a run with key frames saves the case's under
    /// wound_closure/<paramref name="caseName"/>, starting untouched, the oblique views along the cut.</summary>
    private static async Task<ToolSession> Start(string caseName)
    {
        var session = await ToolSession.Start("hand_stitch", $"wound_closure/{caseName}");
        var driver = session.Driver;
        var wound = driver.Patient.Wounds[0];
        session.Along = driver.SitePoint(wound.Points[^1]) - driver.SitePoint(wound.Points[0]);
        return session;
    }

    /// <summary>Every bin of <paramref name="wound"/> showing a gap counts open, every one whose edges meet next to a
    /// closure counts closed.</summary>
    private static void AssertCountsAsSeen(SurgeryDriver driver, Wound wound, string label)
    {
        var tissue = driver.Body.Tissue;
        var wrong = new List<string>();
        for (var i = 0; i < wound.Bins.Length; i++)
        {
            var at = wound.BinPosition(i);
            var gap = GapAt(tissue, at, At);
            if (gap > Shows && wound.Bins[i] >= 1f)
            {
                wrong.Add($"bin {i} counts closed with a {gap * 1000f:0.0} mm gap");
            }
            else if (GapAt(tissue, at, Around) < Meets && tissue.HeldNear([at])[0] && wound.Bins[i] < 1f)
            {
                wrong.Add($"bin {i} counts {wound.Bins[i]:0.00} closed with its edges meeting");
            }
        }
        AssertBool(wrong.Count == 0)
            .OverrideFailureMessage(
                $"{label}: the cut counts as closed where it looks closed\n{string.Join("\n", wrong)}")
            .IsTrue();
    }

    /// <summary>The bins of <paramref name="wound"/> that count open, with their gap, whether a closure holds them and
    /// whether a staple has an edge left to take there.</summary>
    private static string OpenBins(SurgeryDriver driver, Wound wound)
    {
        var tissue = driver.Body.Tissue;
        var lines = new List<string>();
        for (var i = 0; i < wound.Bins.Length; i++)
        {
            if (wound.Bins[i] < 1f)
            {
                var at = wound.BinPosition(i);
                lines.Add($"bin {i}: {wound.Bins[i]:0.00}, gap {GapAt(tissue, at, At) * 1000f:0.0} mm, "
                    + $"held {(tissue.HeldNear([at])[0] ? 1 : 0)}, "
                    + $"unstitched edge {tissue.SkinOpenNear(at, tissue.OpenReach)}");
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>How far apart the skin's edges are pulled across the cut within <paramref name="look"/> (meters) of
    /// <paramref name="uv"/>, measured on the springs the layers are drawn from.</summary>
    private static float GapAt(TissueSim tissue, Vector2 uv, float look)
    {
        var gap = 0f;
        foreach (var s in tissue.Severed)
        {
            if (((tissue.CrossingUv(s) - uv) * tissue.Size).Length() < look)
            {
                var spring = tissue.SpringAt(s);
                var a = spring.A;
                var b = spring.B;
                gap = Mathf.Max(gap,
                    tissue.Pos[a].DistanceTo(tissue.Pos[b]) - tissue.Rest[a].DistanceTo(tissue.Rest[b]));
            }
        }
        return gap;
    }

    private static float WidestGap(SurgeryDriver driver, Wound wound) =>
        Enumerable.Range(0, wound.Bins.Length)
            .Select(i => GapAt(driver.Body.Tissue, wound.BinPosition(i), At))
            .DefaultIfEmpty(0f)
            .Max();

    private static float Bleeding(SurgeryDriver driver, Wound wound) => wound.BleedRate(driver.Body.UvToMeters(1f), 1f);
}
