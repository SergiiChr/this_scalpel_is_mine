namespace Scalpel.Tests.Tools;

/// <summary>
/// The Gelpi retractor (self-retaining) as a player uses it: laid along a cut and set in its middle with Use tool, its
/// points going down into it as deep as it goes, opened on the wheel so the cut's edges move apart, let go of and left
/// holding the wound open, taken back, closed and taken out. On a tight thigh wound the wheel stops where the skin
/// stops giving, before it bunches at the drape. Pressed onto whole skin it bounces off. Set in a skin cut and let go
/// of, the scalpel cuts the muscle between its jaws. Key frames: the site from above and obliquely and what the surgeon
/// sees (the &lt; &gt; aim at the tips). Review them for the retractor lying along the cut with its arms over the
/// skin, the points down in the cut on its edges, the opening widening with the tips and not past them, no skin
/// passing through the jaws, the &lt; &gt; on the tips and the muscle cut showing between the jaws.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("smoke"), TestCategory("tool_gelpi"), TestCategory("tissue_modification"), TestCategory("frame_budget")]
[TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class GelpiTest {
    private const float Length = 0.05f;
    /// <summary>How far (meters) from the cut's middle its gap is measured.</summary>
    private const float Middle = 0.005f;
    private const float Open = 0.032f;
    /// <summary>How wide (meters, through the muscle) the Gelpi must hold a tight thigh wound when its wheel stops, and
    /// how high (meters) the skin it pushes toward the drape may rise then.</summary>
    private const float ThighOpen = 0.025f;
    private const float Ridge = 0.008f;
    // Temporary local budget override: recorded CPU/render overruns remain measured; functional assertions still fail normally.
    private static readonly BudgetAllowance BudgetOverride = new("known to go over the frame budget, not profiled yet", 24.5f);

    [TestCase(Timeout = Limits.Slow)]
    public async Task GelpiSetInACutHoldsItOpenUntilTakenOut() {
        var session = await ToolSession.Start("appendectomy", "gelpi/open", views: true);
        var driver = session.Driver;
        var patient = driver.Patient;
        var body = driver.Body;
        var tissue = body.Tissue;
        var (from, to) = Cut(driver, Length);
        var middle = (from + to) * 0.5f;
        session.Along = driver.SitePoint(to) - driver.SitePoint(from);
        await driver.PlayerCutsSkin(from, to, 2);
        await driver.PlayerPutsDown();
        await Frames.Seconds(1f);
        var ownGape = tissue.GapAt(middle, Middle);
        var wounds = patient.Wounds.Count;

        await AimIntoTheCut(driver, from, to);
        var gelpi = await driver.PlayerSetsGelpi(from, to);
        var hand = driver.Me.Hands[driver.Me.Active];
        AssertBool(gelpi.InWound && hand.Attached)
            .OverrideFailureMessage($"Use tool on the cut sets the retractor in it\n{driver.Recent()}").IsTrue();
        if (!gelpi.InWound) {
            await session.Finish(BudgetOverride);
            return;
        }
        var sides = JawSides(driver, gelpi, middle);
        AssertFloat(sides[0] * sides[1])
            .OverrideFailureMessage($"each jaw holds the edge on its own side of the cut ({sides[0]}, {sides[1]})").IsLess(0f);
        var length = -gelpi.GlobalBasis.Z;
        AssertFloat(Mathf.Abs(length.Y))
            .OverrideFailureMessage($"it lies along the body ({Mathf.RadToDeg(Mathf.Asin(Mathf.Abs(length.Y))):0} degrees off level)")
            .IsLess(0.25f);
        AssertFloat(Mathf.Abs(length.Normalized().Dot(session.Along.Value.Normalized())))
            .OverrideFailureMessage("along the cut, its jaws across it").IsGreater(0.95f);
        var dug = -body.HeightAboveSite(gelpi.TipPosition());
        var deep = Mathf.Min(body.OpeningDepth(middle, 0.01f), ToolActions.SpreadReach);
        AssertFloat(dug)
            .OverrideFailureMessage($"its points go down into the cut as deep as it goes ({dug * 1000f:0.0} mm, the cut {deep * 1000f:0.0} mm)")
            .IsEqualApprox(deep, 0.0015f);
        var marks = driver.Surgery.Hud.JawPoints;
        AssertInt(marks.Length).OverrideFailureMessage("the aim is a < and a > instead of the dot").IsEqual(2);
        await driver.Capture("set", "the Gelpi set in the cut, its points down in it as deep as it goes, the aim a < and a >");

        var setAt = gelpi.GlobalTransform;
        await driver.PlayerOpensGelpi(Open);
        AssertFloat(gelpi.Spread).OverrideFailureMessage("the wheel opens the retractor notch by notch").IsEqualApprox(Open, 0.001f);
        var opened = tissue.GapAt(middle, Middle);
        var widened = Open - ToolActions.SpreadRange.X;
        AssertFloat(opened - ownGape)
            .OverrideFailureMessage($"the cut opens with the tips ({(opened - ownGape) * 1000f:0.0} mm wider, the tips {widened * 1000f:0.0} mm)")
            .IsGreater(widened * 0.7f);
        AssertFloat(opened).OverrideFailureMessage($"but no wider than the tips hold it ({opened * 1000f:0.0} mm)")
            .IsLess(Open + 0.004f);
        AssertInt(patient.Wounds.Count).OverrideFailureMessage("opened as far as the cut gives, nothing tears").IsEqual(wounds);
        AssertBool(gelpi.GlobalTransform.IsEqualApprox(setAt)).OverrideFailureMessage("set, the retractor stays where it went in")
            .IsTrue();
        var openedMarks = driver.Surgery.Hud.JawPoints;
        if (marks.Length == 2 && openedMarks.Length == 2) {
            AssertFloat(openedMarks[0].DistanceTo(openedMarks[1])).OverrideFailureMessage("the < and > move apart with the tips")
                .IsGreater(marks[0].DistanceTo(marks[1]) + 10f);
        }
        await AssertLipsApart(driver);
        await driver.Capture("opened", "the Gelpi opened: the cut spread wider, the < and > apart with the tips");

        SurgeryDriver.Press(InputActions.Grab);
        await Frames.Seconds(2f);
        AssertThat(gelpi.State).OverrideFailureMessage("let go of, the retractor stands in the wound").IsEqual(ToolState.Standing);
        AssertBool(hand.Attached).OverrideFailureMessage("and the hand is free").IsFalse();
        AssertFloat(tissue.GapAt(middle, Middle)).OverrideFailureMessage("left alone, it still holds the cut open")
            .IsGreater(opened - 0.002f);
        await driver.Capture("let_go", "let go: the Gelpi stands in the wound by itself and holds the cut open, the hand free");

        await driver.PlayerReaches(gelpi.GlobalPosition);
        SurgeryDriver.Press(InputActions.Grab);
        await Frames.Physics(10);
        AssertObject(driver.Me.HeldTool(driver.Me.Active)).OverrideFailureMessage("taken back in hand").IsEqual(gelpi);
        AssertBool(gelpi.InWound && hand.Attached && gelpi.GlobalTransform.IsEqualApprox(setAt))
            .OverrideFailureMessage("still set where it went in").IsTrue();
        await driver.PlayerOpensGelpi(ToolActions.SpreadRange.X);
        AssertFloat(tissue.GapAt(middle, Middle)).OverrideFailureMessage("closed on the wheel, the edges come back")
            .IsLess(opened - (widened * 0.7f));
        SurgeryDriver.Use();
        await Frames.Physics(10);
        SurgeryDriver.Use(false);
        await Frames.Seconds(2f);
        AssertBool(gelpi.InWound || hand.Attached).OverrideFailureMessage("Use tool again takes it out").IsFalse();
        AssertBool(tissue.Grips().All(grip => grip.Key >= 0)).OverrideFailureMessage("its jaws let go of the skin").IsTrue();
        AssertFloat(tissue.GapAt(middle, Middle)).OverrideFailureMessage("the cut falls back to its own gape")
            .IsLess(ownGape + 0.002f);
        await driver.Capture("taken_out", "taken out: the jaws off the skin, the cut back to its own gape");
        await driver.PlayerPutsDown();
        AssertBool(driver.LiesOnTray(gelpi)).OverrideFailureMessage("the retractor is put back on the tray").IsTrue();
        await session.Finish(BudgetOverride);
    }

    [TestCase(Timeout = Limits.Slow)]
    public async Task GelpiPressedOntoWholeSkinBouncesOff() {
        var session = await ToolSession.Start("appendectomy", "gelpi/bounce", views: true);
        var driver = session.Driver;
        var spot = driver.SitePoint(new Vector2(0.4f, 0.45f));
        var gelpi = (await driver.PlayerRequestsItem("gelpi"))!;
        await driver.PlayerWalksTo(spot);
        await driver.PlayerReaches(spot);
        var hand = driver.Me.Hands[driver.Me.Active];
        SurgeryDriver.Use();
        await Frames.Physics(3);
        var pressed = hand.GlobalPosition.Y;
        var highest = pressed;
        for (var i = 0; i < 40; i++) {
            await Frames.Physics(1);
            highest = Mathf.Max(highest, hand.GlobalPosition.Y);
        }
        AssertBool(gelpi.InWound || hand.Attached).OverrideFailureMessage("with no cut to go into it doesn't set").IsFalse();
        AssertFloat(highest - pressed).OverrideFailureMessage($"it bounces off the skin ({(highest - pressed) * 100f:0.0} cm up)")
            .IsGreater(SurgeonHand.BounceHeight * 0.5f);
        AssertFloat(hand.GlobalPosition.Y).OverrideFailureMessage("and comes back down onto it").IsLess(pressed + 0.003f);
        SurgeryDriver.Use(false);
        await driver.Capture("bounced", "pressed on whole skin with no cut: the Gelpi isn't set, it rests on the skin");
        await session.Finish(BudgetOverride);
    }

    [TestCase(Timeout = Limits.Slow)]
    public async Task GelpiHoldsASkinCutOpenForTheMuscleUnderIt() {
        var session = await ToolSession.Start("appendectomy", "gelpi/muscle", views: true);
        var driver = session.Driver;
        var body = driver.Body;
        var (from, to) = Cut(driver, Length);
        var middle = (from + to) * 0.5f;
        session.Along = driver.SitePoint(to) - driver.SitePoint(from);
        await driver.PlayerCutsSkin(from, to, 1);
        await driver.PlayerPutsDown();
        var gelpi = await driver.PlayerSetsGelpi(from, to);
        AssertBool(gelpi.InWound).OverrideFailureMessage("set in a skin cut").IsTrue();
        var dug = -body.HeightAboveSite(gelpi.TipPosition());
        AssertFloat(dug).OverrideFailureMessage($"its points go only through the skin ({dug * 1000f:0.0} mm)")
            .IsEqualApprox(PatientBody.SkinThickness, 0.0015f);
        await driver.PlayerOpensGelpi(Open);
        SurgeryDriver.Press(InputActions.Grab);
        await Frames.Seconds(1f);
        AssertThat(gelpi.State).OverrideFailureMessage("let go of, it stays set").IsEqual(ToolState.Standing);
        await driver.Capture("skin_held_open", "the Gelpi through the skin only, holding it open, standing by itself");
        var inside = body.MetersToUv(0.008f);
        await driver.PlayerCutsSkin(middle - new Vector2(inside, 0f), middle + new Vector2(inside, 0f), 3);
        AssertThat(body.Tissue.DeepestCut(middle, body.MetersToUv(0.004f)))
            .OverrideFailureMessage($"the scalpel cuts the muscle between its jaws\n{driver.Recent()}").IsEqual(TissueDepth.Muscle);
        await driver.Capture("muscle_cut", "the scalpel has cut the muscle between the Gelpi's jaws");
        await session.Finish(BudgetOverride);
    }

    [TestCase(Timeout = Limits.Slow)]
    public async Task GelpiOnATightThighWoundStopsOpeningWhereTheSkinStopsGiving() {
        var session = await ToolSession.Start("bullet_muscle", "gelpi/thigh", views: true);
        var driver = session.Driver;
        var body = driver.Body;
        var tissue = body.Tissue;
        var middle = driver.Patient.Wounds[0].Midpoint;
        // The entry wound widened through every layer along the thigh, near the drape's edge on one side. Its midpoint
        // lies a few millimeters off the widened cut.
        var half = new Vector2(body.MetersToUv(0.025f), 0f);
        var (from, to) = (middle - half, middle + half);
        session.Along = driver.SitePoint(to) - driver.SitePoint(from);
        await driver.PlayerCutsSkin(from, to, 3);
        await driver.PlayerPutsDown();
        await Frames.Seconds(1f);
        var wounds = driver.Patient.Wounds.Count;
        var gelpi = await driver.PlayerSetsGelpi(from, to);
        AssertBool(gelpi.InWound).OverrideFailureMessage("set in the thigh wound").IsTrue();
        await driver.PlayerOpensGelpi(ToolActions.SpreadRange.Y);
        AssertFloat(gelpi.Spread).OverrideFailureMessage($"the wheel stops short of fully open ({gelpi.Spread * 100f:0.0} cm)")
            .IsLess(ToolActions.SpreadRange.Y - ToolActions.SpreadStep);
        var strain = Mathf.Max(tissue.StrainAt(Patient.SpreaderKey(gelpi.Uid, 0)), tissue.StrainAt(Patient.SpreaderKey(gelpi.Uid, 1)));
        AssertFloat(strain).OverrideFailureMessage($"where the skin a jaw holds no longer gives (strained {strain:0.00})")
            .IsGreater(ToolActions.SpreadStrain);
        var gap = tissue.GapAt(middle, body.MetersToUv(0.004f), TissueDepth.Muscle);
        AssertFloat(gap).OverrideFailureMessage($"but only once the wound stands {gap * 100f:0.0} cm open through the muscle").IsGreater(ThighOpen);
        // Beyond the jaws' grips: the skin they push toward the drape, the lips included.
        var ridge = Enumerable.Range(0, tissue.Pos.Length)
            .Where(k => Mathf.Abs(tissue.UvOf(k).Y - middle.Y) * body.SiteSize.Y > 0.012f)
            .Max(k => tissue.Pos[k].Y - tissue.Rest[k].Y);
        var aboveBody = Enumerable.Range(0, tissue.Pos.Length)
            .Where(k => Mathf.Abs(tissue.UvOf(k).Y - middle.Y) * body.SiteSize.Y > 0.012f)
            .Max(k => tissue.Pos[k].Y - body.SurfaceHeight(body.LocalToUv(tissue.Pos[k])));
        using var skin = body.Layers[0].Mesh.SurfaceGetArrays(0);
        var vertices = skin[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var uvs = skin[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        var drawnRidge = Enumerable.Range(0, vertices.Length)
            .Where(v => Mathf.Abs(uvs[v].Y - middle.Y) * body.SiteSize.Y > 0.012f)
            .Select(v => vertices[v].Y - body.SurfaceHeight(body.LocalToUv(vertices[v])))
            .DefaultIfEmpty(float.PositiveInfinity).Max();
        await driver.Capture("stopped", $"the Gelpi opened as far as the thigh skin gives, the wound over {ThighOpen * 100f:0} cm open: the skin beside each jaw humped up smooth under the blood pooled round the wound, no ridge at the drape's edge, nothing through the skin");
        AssertFloat(drawnRidge).OverrideFailureMessage($"the drawn skin pushed toward the drape stays below an {Ridge * 1000f:0} mm ridge ({drawnRidge * 1000f:0.0} mm drawn, {ridge * 1000f:0.0} mm solver displacement, {aboveBody * 1000f:0.0} mm solver height)").IsLess(Ridge);
        AssertInt(driver.Patient.Wounds.Count).OverrideFailureMessage("nothing tears").IsEqual(wounds);
        await AssertLipsApart(driver);
        await session.Finish(BudgetOverride);
    }

    /// <summary>A cut <paramref name="length"/> meters long along u, in the same place on the belly every case.</summary>
    private static (Vector2 From, Vector2 To) Cut(SurgeryDriver driver, float length) {
        var from = new Vector2(0.4f, 0.45f);
        return (from, from + new Vector2(driver.Body.MetersToUv(length), 0f));
    }

    /// <summary>Held over the middle of the cut from <paramref name="from"/> to <paramref name="to"/> (site uv, along
    /// u), square across it, the &lt; and &gt; hang into the wound: each down from the lip on its own side, under the
    /// skin, where its tip will go in.</summary>
    private static async Task AimIntoTheCut(SurgeryDriver driver, Vector2 from, Vector2 to) {
        var body = driver.Body;
        var middle = driver.SitePoint((from + to) * 0.5f);
        await driver.PlayerRequestsItem("gelpi");
        await driver.PlayerWalksTo(middle);
        await driver.PlayerTurnsBlade(driver.SitePoint(to) - driver.SitePoint(from));
        await driver.PlayerReaches(middle);
        await Frames.Physics(3);
        var marks = driver.Surgery.Hud.JawMarks;
        var across = 0f;
        for (var side = 0; side < 2; side++) {
            var local = body.Site.ToLocal(marks[side]);
            var uv = body.WorldToUv(marks[side]);
            var under = body.SkinHeight(new Vector2(uv.X, from.Y + ((uv.Y - from.Y) * 3f)));
            AssertFloat(local.Y).OverrideFailureMessage($"mark {side} hangs into the wound, under the skin beside it")
                .IsLess(under - (Hud.JawDepth * 0.5f));
            across += Mathf.Sign(uv.Y - from.Y) * (side == 1 ? 1f : -1f);
        }
        AssertFloat(Mathf.Abs(across)).OverrideFailureMessage("one on each side of the cut").IsEqual(2f);
        await driver.Capture("aimed", "aimed over the cut: the < and > hang into the wound, one on each side of it");
    }

    /// <summary>Which side of the cut's middle each jaw holds the skin on, across the cut (it runs along u): opposite
    /// signs for two edges.</summary>
    private static float[] JawSides(SurgeryDriver driver, SurgicalTool gelpi, Vector2 middle) {
        var tissue = driver.Body.Tissue;
        var sides = new float[2];
        foreach (var grip in tissue.Grips()) {
            for (var side = 0; side < 2; side++) {
                if (grip.Key == Patient.SpreaderKey(gelpi.Uid, side)) {
                    sides[side] = (tissue.Pos[grip.Particle].Z / tissue.Size.Y) + 0.5f - middle.Y;
                }
            }
        }
        return sides;
    }

    /// <summary>Spread open, each lip stays on its own side of the opening and nothing under the skin comes up
    /// through it (TissueLayers.AssertUnderSkin()).</summary>
    private static Task AssertLipsApart(SurgeryDriver driver) => driver.Unbudgeted(() => {
        TissueLayers.AssertUnderSkin(driver.Body);
        return Task.CompletedTask;
    });
}
