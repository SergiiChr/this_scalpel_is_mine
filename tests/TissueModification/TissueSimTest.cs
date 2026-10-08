namespace Scalpel.Tests.TissueModification;

/// <summary>Programmatic soft-tissue geometry and physics checks.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("tissue_modification")]
public class TissueSimTest
{
    private static readonly Vector2 Size = new(0.3f, 0.25f);
    private static readonly Vector2 Mid = new(0.5f, 0.5f);
    /// <summary>The line most cases cut along: across the middle of the site.</summary>
    private static readonly Vector2 CutFrom = new(0.2f, 0.51f);
    private static readonly Vector2 CutTo = new(0.8f, 0.51f);

    [TestCase]
    public void TissueGeometryAndPhysicsContracts()
    {
        CutGapes();
        DepthLayers();
        RetractionAndTears();
        ThinSkinHoldsAtRest();
        StitchesClose();
        Sleeps();
        Elastic();
        MuscleFirst();
        LooseStitchGapes();
        RunningThreadTightensTogether();
        ExactSnaps();
        DeformedSurface();
        RestsOnCurvedBody();
        StaysOnBody();
        FoldsOntoDrape();
        PieceComesOff();
    }

    /// <summary>A flat site that tears on its own (as the host's does).</summary>
    private static TissueSim Sim()
    {
        var sim = new TissueSim { Tearing = true };
        sim.Build(Size, _ => 0f);
        return sim;
    }

    private static void Settle(TissueSim sim, int steps = 60)
    {
        for (var i = 0; i < steps; i++)
        {
            sim.Substep();
        }
    }

    private static void Check(bool ok, string what) => AssertBool(ok).OverrideFailureMessage(what).IsTrue();

    private static void CutGapes()
    {
        var sim = Sim();
        Settle(sim);
        Check(!sim.AnySevered && sim.GapAt(Mid) == 0f, "intact skin has no gap");
        sim.Cut(CutFrom, CutTo, TissueDepth.Muscle);
        Settle(sim);
        Check(sim.GapAt(Mid) > TissueSim.OpenGap, $"a full depth cut gapes on its own (gap {sim.GapAt(Mid):0.0000})");
        Check(sim.IsOpen(Mid), "a full depth cut opens into the cavity");
        Check(sim.Snapped.Count == 0, "skin tension alone doesn't tear anything");
    }

    private static void DepthLayers()
    {
        var sim = Sim();
        sim.Cut(CutFrom, CutTo, TissueDepth.Fat);
        Settle(sim);
        var full = sim.TriangleCount * 3;
        Check(sim.Triangles(TissueDepth.Skin).Length < full, "skin layer has a hole over a fat deep cut");
        Check(sim.Triangles(TissueDepth.Fat).Length < full, "fat layer has a hole over a fat deep cut");
        Check(sim.Triangles(TissueDepth.Muscle).Length == full, "muscle layer stays whole under a fat deep cut");
        Check(!sim.IsOpen(Mid), "a fat deep cut doesn't open into the cavity");
    }

    private static void RetractionAndTears()
    {
        var sim = Sim();
        sim.Cut(CutFrom, CutTo, TissueDepth.Muscle);
        Settle(sim);
        var resting = sim.GapAt(Mid);
        var edge = new Vector2(0.5f, 0.46f);
        sim.Grip(1, edge);
        sim.MoveGrip(1, sim.Rest[sim.Nearest(edge)] + new Vector3(0f, 0.005f, -0.02f));
        Settle(sim);
        Check(sim.GapAt(Mid) > resting + 0.005f, "pulling an edge 2 cm widens the gap");
        Check(sim.Snapped.Count == 0, "a 2 cm pull doesn't tear");
        sim.MoveGrip(1, sim.Rest[sim.Nearest(edge)] + new Vector3(0f, 0.005f, -0.08f));
        Settle(sim);
        Check(sim.Snapped.Count > 0, "an 8 cm pull tears");
        sim.Release(1);
        var tissue = Sim();
        tissue.Tearing = false;
        tissue.Grip(1, edge);
        tissue.MoveGrip(1, tissue.Rest[tissue.Nearest(edge)] + new Vector3(0f, 0.005f, -0.08f));
        Settle(tissue);
        Check(tissue.Snapped.Count == 0, "clients (tearing off) never snap springs themselves");
    }

    private static void ThinSkinHoldsAtRest()
    {
        var sim = Sim();
        sim.BreakMult = 0.5f;
        sim.Cut(CutFrom, CutTo, TissueDepth.Muscle);
        Settle(sim, 120);
        Check(sim.Snapped.Count == 0, "thin skin (tear threshold x0.5) doesn't tear from its own tension");
    }

    /// <summary>Stitches every centimeter along the cut across the middle, at <paramref name="tension"/>.</summary>
    private static void StitchAlong(TissueSim sim, float tension)
    {
        for (var u = 0.2f; u <= 0.8f; u += 0.01f)
        {
            sim.Stitch(new Vector2(u, 0.51f), tension, 2.2f);
        }
    }

    /// <summary>Into the fat: skin can't be closed over open muscle (see <see cref="MuscleFirst"/>).</summary>
    private static void StitchesClose()
    {
        var sim = Sim();
        sim.Cut(CutFrom, CutTo, TissueDepth.Fat);
        Settle(sim);
        StitchAlong(sim, 0.95f);
        Settle(sim);
        Check(sim.GapAt(Mid) == 0f, "stitching along the whole cut closes it");
        Check(sim.Triangles(TissueDepth.Skin).Length == sim.TriangleCount * 3, "a stitched cut shows no hole");
        sim.Burst(Mid, 0.5f);
        Settle(sim);
        Check(sim.GapAt(Mid) > TissueSim.OpenGap, "a burst closure gapes again");
    }

    private static void Sleeps()
    {
        var sim = Sim();
        sim.Cut(CutFrom, CutTo, TissueDepth.Skin);
        Settle(sim, 200);
        Check(sim.IsSleeping, "the sim sleeps once nothing moves");
        var before = sim.StepsDone;
        sim.Advance(1f);
        Check(sim.StepsDone == before, "a sleeping sim does no work");
        sim.Shake(0.003f);
        Check(!sim.IsSleeping, "a jolt wakes the sim");
    }

    /// <summary>Skin is elastic: a slow 5 cm pull on intact skin drags the skin around it along without tearing, and
    /// all of the moved skin is shown simulated (inside the region).</summary>
    private static void Elastic()
    {
        var sim = Sim();
        Settle(sim);
        var k = sim.Nearest(Mid);
        sim.Grip(1, Mid);
        for (var i = 0; i < 50; i++)
        {
            sim.MoveGrip(1, sim.Rest[k] + new Vector3(0.001f * (i + 1), 0f, 0f));
            sim.Substep();
        }
        Settle(sim);
        Check(sim.Snapped.Count == 0, "a slow 5 cm pull on intact skin doesn't tear it");
        // 4 cm behind the grip, against the pull.
        var behind = k - Mathf.RoundToInt(0.04f / (Size.X / sim.ResX));
        var moved = sim.Pos[behind].DistanceTo(sim.Rest[behind]);
        Check(moved > 0.008f, $"skin 4 cm behind a 5 cm pull follows it by more than 8 mm ({moved * 1000f:0.0} mm)");
        var region = sim.Region();
        var hidden = Enumerable.Range(0, sim.Pos.Length)
            .Count(p => sim.Pos[p].DistanceTo(sim.Settled[p]) > 0.002f && region[p] == 0);
        Check(hidden == 0, $"all visibly moved skin is inside the simulated region ({hidden} points outside)");
    }

    /// <summary>A cut through the muscle retracts and keeps the cavity open until the muscle is sewn. Sewn muscle
    /// closes the muscle layer and the cavity; the skin still gapes on its own until it's stitched too.</summary>
    private static void MuscleFirst()
    {
        var fat = Sim();
        fat.Cut(CutFrom, CutTo, TissueDepth.Fat);
        Settle(fat, 120);
        var sim = Sim();
        sim.Cut(CutFrom, CutTo, TissueDepth.Muscle);
        Settle(sim, 120);
        Check(sim.GapAt(Mid) > fat.GapAt(Mid) + 0.002f, "cut muscle retracts: its gap is wider than a cut into the fat "
            + $"({sim.GapAt(Mid) * 1000f:0.0} vs {fat.GapAt(Mid) * 1000f:0.0} mm)");
        Check(sim.MuscleOpenNear(Mid, 0.03f), "a cut through the muscle leaves the muscle open");
        Check(sim.CloseLayer([Mid], 0.5f, TissueDepth.Fat) == 0, "the fat can't be closed over open muscle");
        for (var u = 0.2f; u <= 0.8f; u += 0.015f)
        {
            sim.MuscleStitch(new Vector2(u, 0.51f), 0.03f);
        }
        Settle(sim, 120);
        Check(!sim.MuscleOpenNear(Mid, 0.03f), "sewing along the muscle closes it");
        Check(!sim.IsOpen(Mid), "sewn muscle closes the cavity");
        var full = sim.TriangleCount * 3;
        Check(sim.Triangles(TissueDepth.Muscle).Length == full, "sewn muscle shows no hole in the muscle layer");
        Check(sim.Triangles(TissueDepth.Fat).Length < full, "the fat over sewn muscle still shows its opening");
        sim.CloseLayer([CutFrom, CutTo], 0.03f, TissueDepth.Fat);
        Check(sim.Triangles(TissueDepth.Fat).Length == full,
            "closed subcutaneous tissue shows no hole in the fat layer");
        Check(sim.GapAt(Mid) > TissueSim.OpenGap, "the skin over sewn muscle still gapes until it's stitched");
    }

    /// <summary>A stitch closes the cut only where it pulls the edges together: a loose one leaves the rest of the gap
    /// open.</summary>
    private static void LooseStitchGapes()
    {
        var sims = new List<TissueSim>();
        foreach (var tension in (float[])[0.95f, 1.6f])
        {
            var sim = Sim();
            sim.Cut(CutFrom, CutTo, TissueDepth.Fat);
            Settle(sim);
            StitchAlong(sim, tension);
            Settle(sim);
            sims.Add(sim);
        }
        Check(sims[0].GapAt(Mid) == 0f, "a tight stitch closes the gap");
        Check(sims[1].GapAt(Mid) > TissueSim.OpenGap * 0.5f,
            $"a loose stitch leaves the gap open ({sims[1].GapAt(Mid) * 1000f:0.0} mm)");
        Check(sims[1].Triangles(TissueDepth.Skin).Length < sims[1].TriangleCount * 3,
            "a loosely stitched cut still shows its opening");
    }

    /// <summary>A running suture is one routed thread: the first hole makes no span, every later one a span from the
    /// last, the wheel changes every span at once, and torn through, every span lets go.</summary>
    private static void RunningThreadTightensTogether()
    {
        var sim = Sim();
        sim.Cut(CutFrom, CutTo, TissueDepth.Fat);
        Settle(sim);
        const int id = 77;
        var springsBefore = sim.SpringCount;
        Check(sim.ThreadAnchor(id, new Vector2(0.35f, 0.46f), TissueDepth.Skin, 1.25f, 1.8f, 1f),
            "the first click creates a running-thread anchor");
        Check(sim.SpringCount == springsBefore, "the first anchor has no disconnected stitch bar");
        Check(sim.ThreadAnchor(id, new Vector2(0.42f, 0.56f), TissueDepth.Skin, 1.25f, 1.8f, 1f),
            "the second click routes thread to the next hole");
        Check(sim.ThreadAnchor(id, new Vector2(0.50f, 0.46f), TissueDepth.Skin, 1.25f, 1.8f, 1f),
            "a third click continues the same thread");
        Settle(sim);
        var looseGap = sim.GapAt(new Vector2(0.42f, 0.51f));
        var info = sim.Thread(id)!;
        Check(info.Anchors.Count == 3 && info.Springs.Count == 2, "three holes are joined by one two-span thread");
        var oldLengths = info.Springs.Select(s => sim.SpringAt(s).Rest).ToList();
        sim.ThreadTension(id, 0.85f);
        for (var i = 0; i < oldLengths.Count; i++)
        {
            Check(sim.SpringAt(info.Springs[i]).Rest < oldLengths[i], $"tightening from the end shortens span {i}");
        }
        Settle(sim);
        Check(sim.GapAt(new Vector2(0.42f, 0.51f)) < looseGap, "the tightened running thread draws the skin together");
        sim.SnapThread(id);
        Check(info.Springs.Count == 2 && info.Springs.All(s => !sim.SpringAt(s).Active),
            "a thread torn through lets go of every span");
        Check(!sim.ThreadAnchor(id, new Vector2(0.58f, 0.56f), TissueDepth.Skin, 1.25f, 1.8f, 1f),
            "a torn thread takes no more holes");
    }

    /// <summary>A client mirrors the host's tears spring by spring, diagonals included, and ends with identical
    /// topology.</summary>
    private static void ExactSnaps()
    {
        var host = Sim();
        var client = Sim();
        client.Tearing = false;
        foreach (var sim in (TissueSim[])[host, client])
        {
            sim.Cut(new Vector2(0.2f, 0.3f), new Vector2(0.8f, 0.7f), TissueDepth.Fat);
            sim.Stitch(new Vector2(0.5f, 0.5f), 1f, 1.05f);
        }
        var edge = new Vector2(0.45f, 0.55f);
        host.Grip(1, edge);
        host.MoveGrip(1, host.Rest[host.Nearest(edge)] + new Vector3(-0.03f, 0.01f, 0.06f));
        Settle(host);
        Check(host.Snapped.Count > 0, "a hard diagonal pull tears springs");
        foreach (var entry in host.Snapped)
        {
            client.SnapSpring(entry.Spring);
        }
        Check(client.TopologyHash() == host.TopologyHash(), "the client's torn springs match the host's exactly");
    }

    /// <summary>Contact follows the skin as it's deformed: lifted by a grip it's higher, over an opening there's no
    /// skin.</summary>
    private static void DeformedSurface()
    {
        var sim = Sim();
        var spot = new Vector2(0.5f, 0.3f);
        Check(Mathf.Abs(sim.SkinHeight(spot)) < 0.0001f, "resting skin lies on the body");
        sim.Grip(1, spot);
        sim.MoveGrip(1, sim.Rest[sim.Nearest(spot)] + new Vector3(0f, 0.012f, 0f));
        Settle(sim);
        Check(sim.SkinHeight(spot) > 0.008f,
            $"skin lifted by a grip is higher where it's lifted ({sim.SkinHeight(spot) * 1000f:0.0} mm)");
        Check(Mathf.Abs(sim.SkinHeight(new Vector2(0.1f, 0.9f))) < 0.001f, "skin far from the grip stays put");
        sim.Release(1);
        sim.Cut(CutFrom, CutTo, TissueDepth.Muscle);
        Settle(sim, 120);
        Check(float.IsNaN(sim.SkinHeight(new Vector2(0.5f, 0.51f))), "no skin over an open incision");
    }

    /// <summary>Skin under tension over a round body (a 25 cm radius, like a torso) settles once when it's built and
    /// then rests: not shown simulated, asleep.</summary>
    private static void RestsOnCurvedBody()
    {
        var sim = new TissueSim();
        sim.Build(Size, uv =>
        {
            var x = (uv.X - 0.5f) * Size.X;
            return Mathf.Sqrt((0.25f * 0.25f) - (x * x)) - 0.25f;
        });
        Settle(sim);
        var moved = Enumerable.Range(0, sim.Pos.Length).Max(k => sim.Pos[k].DistanceTo(sim.Settled[k]));
        Check(moved < TissueSim.RegionMove,
            $"skin over a round body stays where it settled (moved {moved * 1000f:0.0} mm)");
        Check(!sim.Region().Contains((byte)1), "untouched skin over a round body isn't shown simulated");
        Check(sim.IsSleeping, "untouched skin over a round body sleeps");
    }

    /// <summary>Where the site hangs off the body (here past uv.x 0.8), the skin is never drawn and never shown
    /// simulated, whatever is cut or pulled next to it.</summary>
    private static void StaysOnBody()
    {
        var sim = new TissueSim { Tearing = true };
        sim.Build(Size, _ => 0f, uv => uv.X <= 0.8f);
        sim.Cut(new Vector2(0.1f, 0.51f), new Vector2(0.95f, 0.51f), TissueDepth.Muscle);
        var edge = new Vector2(0.75f, 0.46f);
        sim.Grip(1, edge);
        sim.MoveGrip(1, sim.Rest[sim.Nearest(edge)] + new Vector3(0.01f, 0.01f, -0.02f));
        Settle(sim);
        var region = sim.Region();
        var drawnOff = sim.Triangles(TissueDepth.Skin).Count(k => sim.Off[k]);
        var shownOff = Enumerable.Range(0, sim.Pos.Length).Count(k => sim.Off[k] && region[k] == 1);
        Check(sim.Off.Contains(true), "the test site has skin off the body");
        Check(drawnOff == 0, $"no skin is drawn off the body ({drawnOff} triangle corners)");
        Check(shownOff == 0, $"no skin off the body is shown simulated ({shownOff} points)");
    }

    /// <summary>Skin folded out of the drape's opening (here the middle of the site) lies on the drape (a floor 1 cm up
    /// outside the opening), while skin that starts under the drape isn't pushed through it.</summary>
    private static void FoldsOntoDrape()
    {
        var sim = Sim();
        var opening = Size.X * 0.25f;
        sim.FloorAt = (x, _) => Mathf.Abs(x) > opening ? 0.01f : float.NaN;
        sim.Exposed = [.. sim.Rest.Select(rest => Mathf.Abs(rest.X) < opening)];
        sim.Cut(new Vector2(0.4f, 0.2f), new Vector2(0.4f, 0.8f), TissueDepth.Muscle);
        var edge = new Vector2(0.45f, 0.5f);
        sim.Grip(1, edge);
        sim.MoveGrip(1, sim.Rest[sim.Nearest(edge)] + new Vector3(0.06f, -0.01f, 0f));
        Settle(sim, 120);
        var gripped = sim.Nearest(edge);
        var through = Enumerable.Range(0, sim.Pos.Length).Count(k =>
            sim.Exposed[k] && sim.Pos[k].X > opening && sim.Pos[k].Y < 0.01f - 0.0001f && k != gripped);
        var lifted = Enumerable.Range(0, sim.Pos.Length).Count(k => !sim.Exposed[k] && sim.Pos[k].Y > 0.005f);
        Check(through == 0, $"skin folded out over the drape stays on it ({through} points under)");
        Check(lifted == 0, $"skin under the drape isn't pushed up through it ({lifted} points)");
    }

    /// <summary>A circle cut through the skin all round frees a piece; until it closes there's none. Taken off, the
    /// skin layer has a hole there and only there, the layers under it stay whole, and peers that take the same piece
    /// off agree.</summary>
    private static void PieceComesOff()
    {
        TissueSim[] sims = [Sim(), Sim()];
        var middle = sims[0].Nearest(Mid);
        foreach (var sim in sims)
        {
            const int points = 24;
            for (var n = 0; n < points; n++)
            {
                sim.Cut(CirclePoint(Mathf.Tau * n / points), CirclePoint(Mathf.Tau * (n + 1) / points),
                    TissueDepth.Skin);
                if (n == points / 2 && sim == sims[0])
                {
                    Check(sim.PieceOf(middle).Count == 0, "skin cut halfway round is still joined");
                }
            }
            Settle(sim);
        }
        var piece = sims[0].PieceOf(middle);
        Check(piece.Count > 0, $"a circle cut through the skin frees a piece ({piece.Count} grid points)");
        Check(sims[0].PieceOf(sims[0].Nearest(new Vector2(0.2f, 0.2f))).Count == 0,
            "skin outside the circle isn't a piece");
        var full = sims[0].TriangleCount * 3;
        var skinBefore = sims[0].Triangles(TissueDepth.Skin).Length;
        foreach (var sim in sims)
        {
            sim.Excise(middle);
        }
        Check(sims[0].Excised.Count(taken => taken) == piece.Count, "taking it off takes the whole piece");
        Check(sims[0].Triangles(TissueDepth.Skin).Length < skinBefore, "the skin layer has a hole where it was");
        Check(sims[0].Triangles(TissueDepth.Fat).Length == full, "the fat layer under it stays whole");
        Check(sims[0].IsOpen(Mid, TissueDepth.Skin) && !sims[0].IsOpen(Mid, TissueDepth.Fat),
            "the hole is open through the skin only");
        Check(!sims[0].IsOpen(new Vector2(0.2f, 0.2f), TissueDepth.Skin), "skin away from it isn't open");
        Check(sims[0].Excise(middle) == 0, "a piece already taken can't be taken again");
        Check(sims[0].TopologyHash() == sims[1].TopologyHash(), "peers that take the same piece off agree");
    }

    /// <summary>The point <paramref name="angle"/> round a 1.5 cm circle about the middle of the site (uv).</summary>
    private static Vector2 CirclePoint(float angle) =>
        Mid + (new Vector2(Mathf.Cos(angle) / Size.X, Mathf.Sin(angle) / Size.Y) * 0.015f);
}
