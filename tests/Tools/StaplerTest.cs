namespace Scalpel.Tests.Tools;

/// <summary>
/// The staplers as a player uses them: the aim shows two rings lying on the skin where the legs go in, and a click with
/// them on both edges of an open cut puts one staple in there, joining the edges. Through a belly cut into the muscle
/// the first staples go into the muscle, then the skin closes over it. The office stapler works the same but now and
/// then tears out of the skin or catches a vessel. Key frames: the site untouched, after the first staple and stapled
/// shut, and what the surgeon sees (the two rings). Review them for steel staples bridging the cut square to it with
/// their legs in the skin on both sides, the edges meeting between them and the rings on the cut's edges.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("tool_skin_stapler"), TestCategory("tool_office_stapler")]
[TestCategory("tissue_modification"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class StaplerTest
{
    private const string BudgetBroken = "known to go over the frame budget: each staple changes the tissue, and "
        + "rebuilding its layers takes about 6 ms (PatientBody.RebuildLayers()); not optimized yet";

    [TestCase]
    public async Task SkinStaplerClosesAForearmCut()
    {
        var session = await ToolSession.Start("hand_stitch", "stapler/forearm", views: true);
        var driver = session.Driver;
        var patient = driver.Patient;
        var body = driver.Body;
        var tissue = body.Tissue;
        var wound = patient.Wounds[0];
        session.Along = driver.SitePoint(wound.Points[^1]) - driver.SitePoint(wound.Points[0]);
        var stapler = (await driver.PlayerRequestsItem("skin_stapler"))!;
        AssertBool(driver.Me.UsesLevel(driver.Me.Active)).OverrideFailureMessage("the stapler takes no level from the wheel")
            .IsFalse();
        var middle = driver.SitePoint(wound.Midpoint);
        await driver.PlayerWalksTo(middle);
        await driver.PlayerTurnsBlade(session.Along.Value);
        await AimOverTheArmsEdge(driver, stapler, middle);
        await driver.PlayerReaches(middle + new Vector3(0f, 0f, 0.03f));
        await Frames.Physics(2);
        var hud = driver.Surgery.Hud;
        AssertBool(hud.LegRingsShown).OverrideFailureMessage("the aim is two rings, one per leg").IsTrue();
        AssertBool(hud.DotShown).OverrideFailureMessage("instead of the dot").IsFalse();
        var charges = stapler.Charges;
        var toasts = new List<string>();
        hud.Toasted += toasts.Add;
        await Click();
        hud.Toasted -= toasts.Add;
        AssertInt(stapler.Charges).OverrideFailureMessage("a click beside the cut puts no staple in").IsEqual(charges);
        var why = ToolActions.StapleMiss(tissue, body.WorldToUv(stapler.Use.StapleAim[0]), body.WorldToUv(stapler.Use.StapleAim[1]));
        AssertBool(toasts.Contains(why) && toasts[^1].Length > 0)
            .OverrideFailureMessage($"and says why ({string.Join(", ", toasts)})").IsTrue();

        await driver.PlayerReaches(middle);
        await Frames.Physics(2);
        AssertRingsOnSkin(driver, stapler);
        // A pair of forceps left lying under a leg: the ring stays on the skin, not up on them.
        var forceps = SurgeryState.ToolLiesAt(driver.Surgery, "forceps",
            ToolActions.StapleLegs(stapler, body)[0] + (Vector3.Up * 0.01f));
        await Frames.Seconds(1f);
        AssertRingsOnSkin(driver, stapler);
        driver.Surgery.Tools.Consume(forceps);
        await Frames.Physics(2);
        await driver.Capture("ready", "the stapler over the cut, its rings on the skin");
        var gap = tissue.GapAt(wound.Midpoint, 0.02f);
        var aimed = RingMiddles(driver);
        SurgeryDriver.Use();
        await Frames.Physics(4);
        SurgeryDriver.Use(false);
        AssertInt(stapler.Charges).OverrideFailureMessage("a click puts one staple in").IsEqual(charges - 1);
        for (var side = 0; side < 2; side++)
        {
            var miss = (stapler.Use.StapleAim[side] - aimed[side]).Slide(Vector3.Up).Length();
            AssertFloat(miss).OverrideFailureMessage($"leg {side} goes in where its ring showed it ({miss * 1000f:0.0} mm off)")
                .IsLess(0.0005f);
        }
        await Frames.Seconds(0.5f);
        var closedGap = tissue.GapAt(wound.Midpoint, 0.02f);
        AssertFloat(closedGap)
            .OverrideFailureMessage($"and its edges meet under it ({closedGap * 1000f:0.0} mm, were {gap * 1000f:0.0} mm)")
            .IsLess(gap * 0.5f);
        AssertFloat(wound.Closure).OverrideFailureMessage("one staple closes a bit of the cut").IsBetween(0.01f, 0.5f);
        AssertBool(body.StapleWire is not null && body.DrawnStaples.Count == 1).OverrideFailureMessage("the staple is drawn")
            .IsTrue();

        var placed = 1 + await driver.PlayerStaples(wound);
        await Frames.Seconds(1f);
        AssertFloat(wound.Closure).OverrideFailureMessage($"stapled along its length the cut is closed\n{driver.Recent()}")
            .IsGreater(0.99f);
        AssertFloat(tissue.GapAlong(wound.Points, 0.03f, TissueDepth.Skin))
            .OverrideFailureMessage("the staples hold the edges together").IsLess(TissueSim.OpenGap);
        AssertInt(stapler.Charges).OverrideFailureMessage("one charge per staple").IsEqual(charges - placed);
        AssertInt(body.DrawnStaples.Count).OverrideFailureMessage("every staple is drawn").IsEqual(placed);
        AssertStaplesAcross(driver, body.DrawnStaples, wound);
        AssertFloat(patient.Vitals.BleedRate).OverrideFailureMessage("the stapled cut stops bleeding").IsLess(0.2f);
        await driver.PlayerReaches(middle);
        charges = stapler.Charges;
        await Click();
        AssertInt(stapler.Charges).OverrideFailureMessage("over the stapled cut a click puts no staple in").IsEqual(charges);
        await driver.PlayerPutsDown();
        // Back at the patient, to see the result from where the surgeon works.
        await driver.PlayerWalksTo(driver.SitePoint(wound.Midpoint));
        await driver.Capture("stapled", "the cut stapled shut, staples square across it, seen from where the surgeon works");
        await session.Finish(BudgetBroken);
    }

    [TestCase]
    public async Task SkinStaplerClosesABellyCutMuscleFirst()
    {
        var session = await ToolSession.Start("appendectomy", "stapler/belly_muscle", views: true);
        var driver = session.Driver;
        var body = driver.Body;
        var tissue = body.Tissue;
        var from = new Vector2(0.35f, 0.45f);
        var to = from + new Vector2(body.MetersToUv(0.05f), 0f);
        session.Along = driver.SitePoint(to) - driver.SitePoint(from);
        await driver.PlayerCutsSkin(from, to, 3);
        await driver.PlayerPutsDown();
        await Frames.Seconds(1f);
        var wound = driver.Patient.Wounds[^1];
        var middle = wound.Midpoint;
        AssertBool(wound.ThroughMuscle && tissue.MuscleOpenNear(middle, Patient.MuscleReach))
            .OverrideFailureMessage("the cut goes through the muscle").IsTrue();
        await driver.PlayerRequestsItem("skin_stapler");
        await driver.PlayerWalksTo(driver.SitePoint(middle));
        await driver.PlayerTurnsBlade(session.Along.Value);
        await driver.PlayerReaches(driver.SitePoint(middle));
        await Click();
        AssertBool(tissue.MuscleOpenNear(middle, Patient.MuscleReach))
            .OverrideFailureMessage("the first staple goes into the open muscle").IsFalse();
        AssertFloat(wound.Closure).OverrideFailureMessage("and leaves the skin over it open").IsLess(0.01f);
        var muscleStaple = body.DrawnStaples[0].Path;
        AssertFloat(muscleStaple[1].Y).OverrideFailureMessage("it's drawn down in the muscle, under the skin")
            .IsLess(body.SkinHeight(SiteUv(body, muscleStaple[1])) - PatientBody.SkinThickness);
        await driver.Capture("muscle_staple", "the first staple drawn down in the open muscle, the skin over it still open");
        await driver.PlayerStaples(wound);
        await Frames.Seconds(1f);
        AssertBool(tissue.Severed.Any(s => tissue.DepthOf(s) == TissueDepth.Muscle))
            .OverrideFailureMessage("no muscle is left open").IsFalse();
        AssertFloat(wound.Closure).OverrideFailureMessage($"the skin is stapled shut over it\n{driver.Recent()}").IsGreater(0.99f);
        AssertFloat(tissue.GapAlong(wound.Points, 0.03f, TissueDepth.Skin)).OverrideFailureMessage("and its edges meet")
            .IsLess(TissueSim.OpenGap);
        var layers = body.DrawnStaples.Select(staple => staple.Layer).ToHashSet();
        AssertBool(layers.Contains(TissueDepth.Muscle) && layers.Contains(TissueDepth.Skin))
            .OverrideFailureMessage("staples went into both the muscle and the skin").IsTrue();
        AssertStaplesAcross(driver, [.. body.DrawnStaples.Where(staple => staple.Layer == TissueDepth.Skin)], wound);
        await driver.PlayerPutsDown();
        // Back at the patient, to see the result from where the surgeon works.
        await driver.PlayerWalksTo(driver.SitePoint(wound.Midpoint));
        await driver.Capture("stapled", "staples in both the muscle and the skin, the skin ones square across the cut");
        await session.Finish(BudgetBroken);
    }

    [TestCase]
    public async Task OfficeStaplerCanTearOrBleed()
    {
        var session = await ToolSession.Start("appendectomy", "stapler/office", views: true);
        var driver = session.Driver;
        var patient = driver.Patient;
        var body = driver.Body;
        var from = new Vector2(0.4f, 0.45f);
        var to = from + new Vector2(body.MetersToUv(0.05f), 0f);
        session.Along = driver.SitePoint(to) - driver.SitePoint(from);
        await driver.PlayerCutsSkin(from, to, 1);
        await driver.PlayerPutsDown();
        await Frames.Seconds(1f);
        var wound = patient.Wounds[^1];
        SurgeryState.ToolIsOnTray(driver.Surgery, "office_stapler");
        var stapler = (await driver.PlayerRequestsItem("office_stapler"))!;
        AssertFloat(stapler.Def.TearChance + stapler.Def.BleedChance)
            .OverrideFailureMessage("an office staple only now and then goes wrong").IsBetween(0.01f, 0.25f);
        SurgeryState.ToolGoesWrong(stapler, 1f, 0f);
        var at = driver.SitePoint(wound.Points[0].Lerp(wound.Points[^1], 0.25f));
        await driver.PlayerWalksTo(at);
        await driver.PlayerTurnsBlade(session.Along.Value);
        await driver.PlayerReaches(at);
        var wounds = patient.Wounds.Count;
        await Click();
        AssertBool(patient.Flags.ContainsKey("tears")).OverrideFailureMessage("an office staple that tears out tears the skin")
            .IsTrue();
        AssertInt(patient.Wounds.Count).OverrideFailureMessage("a tear is a new wound").IsEqual(wounds + 1);
        AssertFloat(wound.Closure).OverrideFailureMessage("and the staple holds nothing").IsLess(0.1f);
        await driver.Capture("torn", "an office staple torn out: a new tear in the skin, the staple holding nothing");

        SurgeryState.ToolGoesWrong(stapler, 0f, 1f);
        at = driver.SitePoint(wound.Points[0].Lerp(wound.Points[^1], 0.7f));
        await driver.PlayerReaches(at);
        wounds = patient.Wounds.Count;
        await Click();
        await Frames.Seconds(1f);
        var siteM = body.UvToMeters(1f);
        AssertInt(patient.Wounds.Count).OverrideFailureMessage("a staple through a vessel makes no new wound to close")
            .IsEqual(wounds);
        AssertFloat(wound.BleedRate(siteM, 1f)).OverrideFailureMessage("but the cut bleeds through it")
            .IsGreater(Patient.StapleNick * 0.5f);
        AssertInt(body.DrawnStaples.Count).OverrideFailureMessage("the staple still goes in").IsEqual(1);
        AssertBool(patient.Flags.ContainsKey("office_staples")).OverrideFailureMessage("the report hears about the office staples")
            .IsTrue();
        await driver.Capture("bleeding", "an office staple in, the cut bleeding through it");
        await driver.PlayerPutsDown();
        await driver.PlayerStopsBleeding(0.05f);
        AssertFloat(wound.BleedRate(siteM, 1f)).OverrideFailureMessage("cauterized, the nicked vessel stops bleeding")
            .IsLess(0.05f);
        await session.Finish(BudgetBroken);
    }

    private static async Task Click()
    {
        await Frames.Physics(2);
        SurgeryDriver.Use();
        await Frames.Physics(4);
        SurgeryDriver.Use(false);
        await Frames.Physics(3);
    }

    /// <summary>The stapler hovers over the skin, and each ring of its aim lies on the skin under where its leg comes
    /// down.</summary>
    private static void AssertRingsOnSkin(SurgeryDriver driver, SurgicalTool stapler)
    {
        var legs = ToolActions.StapleLegs(stapler, driver.Body);
        var above = legs[0].Y - SkinY(driver.Body, legs[0]);
        AssertFloat(above).OverrideFailureMessage($"the stapler hovers {above * 1000f:0.0} mm over the skin").IsGreater(0.003f);
        var middles = RingMiddles(driver);
        for (var side = 0; side < 2; side++)
        {
            foreach (var p in driver.Surgery.Hud.LegRings[side])
            {
                AssertFloat(Mathf.Abs(p.Y - SkinY(driver.Body, p))).OverrideFailureMessage($"ring {side} lies on the skin")
                    .IsLess(0.0005f);
            }
            AssertFloat((middles[side] - legs[side]).Slide(Vector3.Up).Length())
                .OverrideFailureMessage($"right under leg {side}").IsLess(0.0005f);
        }
    }

    /// <summary>Holds the stapler square across the arm with one leg just past its side, where the surface drops away to
    /// the drape or the table, and checks the rings there.</summary>
    private static async Task AimOverTheArmsEdge(SurgeryDriver driver, SurgicalTool stapler, Vector3 middle)
    {
        var me = driver.Me;
        var top = me.SurfaceBelow(middle, false).Y;
        var outside = middle;
        for (var i = 0; i < 40; i++)
        {
            outside += new Vector3(0f, 0f, 0.003f);
            if (me.SurfaceBelow(outside, false).Y < top - 0.02f)
            {
                break;
            }
        }
        await driver.PlayerReaches(outside - new Vector3(0f, 0f, (stapler.Def.StapleSpan * 0.5f) - 0.002f));
        await Frames.Physics(2);
        var drops = ToolActions.StapleLegs(stapler, driver.Body).Select(leg => top - me.SurfaceBelow(leg, false).Y).ToList();
        AssertFloat(drops.Max())
            .OverrideFailureMessage($"one leg is past the side of the arm ({string.Join(", ", drops)} m below its top)")
            .IsGreater(0.02f);
        // Wherever the legs are, each ring lies on what's under its middle, no steeper than 45 degrees, not floating in
        // the air or standing on end.
        var middles = RingMiddles(driver);
        for (var side = 0; side < 2; side++)
        {
            var off = middles[side].Y - me.SurfaceBelow(middles[side], false).Y;
            AssertFloat(Mathf.Abs(off)).OverrideFailureMessage($"ring {side} lies on what's under it ({off * 1000f:0.0} mm off)")
                .IsLess(0.002f);
            foreach (var p in driver.Surgery.Hud.LegRings[side])
            {
                AssertFloat(Mathf.Abs(p.Y - middles[side].Y)).OverrideFailureMessage($"ring {side} doesn't stand on end")
                    .IsLessEqual(Hud.LegRing * 1.5f);
            }
        }
    }

    /// <summary>The middles of the stapler's two rings (world), where the aim shows its legs going in.</summary>
    private static Vector3[] RingMiddles(SurgeryDriver driver) =>
        [.. driver.Surgery.Hud.LegRings.Select(ring => ring.Aggregate(Vector3.Zero, (sum, p) => sum + p) / ring.Length)];

    /// <summary>The skin's height (world) as drawn under <paramref name="p"/>.</summary>
    private static float SkinY(PatientBody body, Vector3 p)
    {
        var local = body.Site.ToLocal(p);
        return body.Site.ToGlobal(new Vector3(local.X, body.SkinHeight(body.WorldToUv(p)), local.Z)).Y;
    }

    /// <summary>Where a point drawn on the site (site-local) lies in site uv.</summary>
    private static Vector2 SiteUv(PatientBody body, Vector3 local) =>
        new((local.X / body.SiteSize.X) + 0.5f, (local.Z / body.SiteSize.Y) + 0.5f);

    /// <summary>Every staple bridges the cut: its legs on both sides of the wound's line, each within the staple's span
    /// of it, square across it, its crown riding on the skin.</summary>
    private static void AssertStaplesAcross(SurgeryDriver driver, IReadOnlyList<DrawnStaple> staples, Wound wound)
    {
        var body = driver.Body;
        var def = Db.Tool("skin_stapler")!;
        var widest = def.StapleSpan + (def.StapleGive * 2f);
        var cut = (wound.Points[^1] - wound.Points[0]).Normalized();
        foreach (var path in staples.Select(staple => staple.Path))
        {
            var a = SiteUv(body, path[1]);
            var b = SiteUv(body, path[^2]);
            var line = wound.ClosestPoint((a + b) * 0.5f);
            AssertFloat((a - line).Dot(b - a) * (b - line).Dot(b - a))
                .OverrideFailureMessage("a staple has a leg on each side of the cut").IsLess(0f);
            AssertFloat(path[1].DistanceTo(path[^2])).OverrideFailureMessage("and is no wider than the stapler reaches")
                .IsLess(widest * 1.1f);
            AssertFloat(Mathf.Abs((b - a).Orthogonal().Normalized().Dot(cut))).OverrideFailureMessage("lying square across the cut")
                .IsGreater(0.8f);
            for (var i = 1; i < path.Length - 1; i++)
            {
                AssertFloat(path[i].Y).OverrideFailureMessage("its crown rides on the skin, not in it")
                    .IsGreaterEqual(body.SkinHeight(SiteUv(body, path[i])) + PatientBody.StapleRadius);
            }
        }
    }
}
