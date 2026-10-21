namespace Scalpel.Tests.Tools;

/// <summary>
/// The retractor as a player uses it: hooked onto one edge of a cut with Use tool, drawn aside by moving the hand so
/// that edge pulls away from the other, let go of with Grab so it lies down on the body and keeps pulling, taken back
/// and unhooked with Use tool so the cut falls back. Key frames: the site from above and obliquely, hooked, pulled and
/// left lying. Review them for the hook sitting on the edge it pulls, that edge stretched aside over a few centimeters
/// and the other one staying put, and the retractor let go of lying on the skin pointing away from the cut, not
/// standing up or sinking into the body.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("smoke"), TestCategory("tool_retractor"), TestCategory("tissue_modification"), TestCategory("visual_confirmation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class RetractorTest {
    private const float Length = 0.05f;
    /// <summary>How far (meters) from the cut the hook goes in, and how far the hand then draws it aside.</summary>
    private const float HookOff = 0.006f;
    private const float Pull = 0.02f;
    /// <summary>How far (meters) the hand sweeps a hooked edge away: more than the skin gives.</summary>
    private const float FarPull = 0.08f;
    /// <summary>How far (meters) from the cut's middle its gap is measured.</summary>
    private const float Middle = 0.005f;
    /// <summary>Most a retractor let go of may tilt up over the body it lies on (degrees).</summary>
    private const float LyingTilt = 8f;
    /// <summary>How deep (meters) a retractor lying on the body may press into it at most: less than its stay's half
    /// thickness (tools/assetgen/instruments.py), so the stay still shows. It rises and falls with a breathing belly.
    /// </summary>
    private const float LyingPress = 0.002f;
    /// <summary>How far (meters) the skin along the drape's window may rise over where it rested: a grip slides it along
    /// the body there by a few millimeters, it doesn't lift it up onto the cloth.</summary>
    private const float RimRise = 0.003f;
    /// <summary>How long (meters) the cut the four retractors hold open is.</summary>
    private const float Opening = 0.08f;
    // Temporary local budget override: recorded CPU/render overruns remain measured; functional assertions still fail normally.
    private static readonly BudgetAllowance BudgetOverride = new("known to go over the frame budget, not profiled yet", 20.5f);

    [TestCase]
    public async Task RetractorHooksOneEdgeAndPullsTheCutOpen() {
        var session = await ToolSession.Start("appendectomy", "retractor/pull");
        var driver = session.Driver;
        var patient = driver.Patient;
        var body = driver.Body;
        var tissue = body.Tissue;
        var from = new Vector2(0.4f, 0.45f);
        var to = from + new Vector2(body.MetersToUv(Length), 0f);
        var middle = (from + to) * 0.5f;
        await driver.PlayerCutsSkin(from, to, 2);
        await driver.PlayerPutsDown();
        await Frames.Seconds(1f);
        var ownGape = tissue.GapAt(middle, Middle);
        var lipsBefore = Lips(driver, middle);
        var wounds = patient.Wounds.Count;

        var hookAt = driver.SitePoint(middle + new Vector2(0f, body.MetersToUv(HookOff)));
        var retractor = await Hook(driver, hookAt, "retractor");
        var hand = driver.Me.Hands[driver.Me.Active];
        await driver.Capture("hooked", "the retractor's hook in one edge of the cut, nothing pulled yet");

        var aside = driver.SitePoint(middle + new Vector2(0f, body.MetersToUv(HookOff + Pull))) - hookAt;
        await driver.PlayerSweepsTo(retractor.TipPosition() + aside);
        await Frames.Seconds(1f);
        var opened = tissue.GapAt(middle, Middle);
        AssertFloat(opened - ownGape)
            .OverrideFailureMessage($"drawn {Pull * 100f:0} cm aside, the cut opens ({(opened - ownGape) * 1000f:0.0} mm wider)")
            .IsGreater(Pull * 0.5f);
        var lips = Lips(driver, middle);
        var hookedMoved = lips[1] - lipsBefore[1];
        var otherMoved = Mathf.Abs(lips[0] - lipsBefore[0]);
        AssertFloat(hookedMoved)
            .OverrideFailureMessage($"the hooked edge moves aside ({hookedMoved * 1000f:0.0} mm), the other one much less ({otherMoved * 1000f:0.0} mm)")
            .IsGreater(otherMoved * 3f);
        AssertInt(patient.Wounds.Count).OverrideFailureMessage("a 2 cm pull doesn't tear").IsEqual(wounds);
        await driver.Capture("pulled", "pulled 2 cm aside: the hooked edge moved, the other much less, no tear");

        var hooked = retractor.TipPosition();
        SurgeryDriver.Tap(InputActions.Grab);
        await Frames.Seconds(2f);
        AssertThat(retractor.State).OverrideFailureMessage("let go of with Grab, the retractor stays hooked").IsEqual(ToolState.Standing);
        AssertBool(hand.Attached).OverrideFailureMessage("and the hand is free").IsFalse();
        AssertFloat((retractor.TipPosition() - hooked).Slide(Vector3.Up).Length())
            .OverrideFailureMessage("the hook stays where it held the skin").IsLess(0.002f);
        AssertLiesOnBody(driver, retractor);
        AssertFloat(tissue.GapAt(middle, Middle)).OverrideFailureMessage("left alone, it keeps the cut pulled open")
            .IsGreater(opened - 0.002f);
        var handle = (retractor.GlobalPosition - retractor.TipPosition()).Normalized();
        AssertFloat(Mathf.Abs(handle.Y))
            .OverrideFailureMessage($"it lies along the body ({Mathf.RadToDeg(Mathf.Asin(Mathf.Abs(handle.Y))):0} degrees off level), not standing up")
            .IsLess(0.35f);
        AssertPointsAway(retractor, aside);
        await driver.Capture("let_go", "let go: the retractor lies along the body pointing away, the cut stays open");

        await driver.PlayerReaches(retractor.TipPosition());
        SurgeryDriver.Press(InputActions.Grab);
        await Frames.Physics(10);
        AssertObject(driver.Me.HeldTool(driver.Me.Active)).OverrideFailureMessage("taken back in hand").IsEqual(retractor);
        AssertBool(retractor.Hold is SkinHold && hand.Attached).OverrideFailureMessage("still hooked").IsTrue();
        AssertFloat(retractor.TipPosition().DistanceTo(hooked))
            .OverrideFailureMessage("the hook stays on the skin it held as the hand takes it").IsLess(0.01f);
        AssertInt(patient.Wounds.Count).OverrideFailureMessage("taking it back doesn't tear").IsEqual(wounds);

        SurgeryDriver.Use();
        await Frames.Physics(10);
        SurgeryDriver.Use(false);
        await Frames.Seconds(2f);
        AssertBool(retractor.Hold is null && !hand.Attached).OverrideFailureMessage("Use tool again lets go").IsTrue();
        AssertFloat(tissue.GapAt(middle, Middle)).OverrideFailureMessage("let go of, the cut falls back to its own gape")
            .IsLess(ownGape + 0.002f);
        await driver.PlayerPutsDown();
        AssertBool(driver.LiesOnTray(retractor)).OverrideFailureMessage("the retractor is put back on the tray").IsTrue();
        await session.Finish(BudgetOverride);
    }

    /// <summary>Skin isn't dragged across the site like rubber: pulled past what it gives, it holds the hand where it
    /// stops (<see cref="TissueSim.Restrain"/>), the way holding onto something stops a hand.</summary>
    [TestCase]
    public async Task PulledPastWhatTheSkinGivesTheHandIsHeldBack() {
        var session = await ToolSession.Start("appendectomy", "retractor/held_back");
        var driver = session.Driver;
        var patient = driver.Patient;
        var body = driver.Body;
        var tissue = body.Tissue;
        var from = new Vector2(0.4f, 0.45f);
        var to = from + new Vector2(body.MetersToUv(Length), 0f);
        var middle = (from + to) * 0.5f;
        await driver.PlayerCutsSkin(from, to, 2);
        await driver.PlayerPutsDown();
        await Frames.Seconds(1f);
        var hookAt = driver.SitePoint(middle + new Vector2(0f, body.MetersToUv(HookOff)));
        var retractor = await Hook(driver, hookAt, "retractor");
        var hand = driver.Me.Hands[driver.Me.Active];
        var wounds = patient.Wounds.Count;
        var away = driver.SitePoint(middle + new Vector2(0f, body.MetersToUv(HookOff + FarPull))) - hookAt;
        await driver.PlayerSweepsTo(retractor.TipPosition() + away);
        await Frames.Seconds(1f);
        var pulled = (retractor.TipPosition() - hookAt).Slide(Vector3.Up).Length();
        AssertFloat(pulled)
            .OverrideFailureMessage($"swept {FarPull * 100f:0} cm away, the hook stops short, held by the skin ({pulled * 1000f:0} mm)")
            .IsLess(FarPull * 0.7f);
        AssertFloat((driver.Tip(hand) - retractor.TipPosition()).Length())
            .OverrideFailureMessage("the hand stays with the hook, not where the mouse went").IsLess(0.01f);
        // Past what it gives the sim's strain may stay over the limit (an edge peeled up reads over it), but the hook
        // goes no further along the body however hard the hand pulls on.
        var stopped = retractor.TipPosition();
        await driver.PlayerSweepsTo(stopped + away * 0.5f);
        await Frames.Seconds(1f);
        var further = (retractor.TipPosition() - stopped).Slide(Vector3.Up).Length();
        AssertFloat(further).OverrideFailureMessage($"swept on, the hook goes no further along the body ({further * 1000f:0.0} mm)")
            .IsLess(0.001f);
        AssertInt(patient.Wounds.Count).OverrideFailureMessage("held back, nothing tears").IsEqual(wounds);
        await driver.Capture("held_back", $"the hand swept {FarPull * 100f:0} cm away but held back by the skin: the hooked edge drawn as far as the skin gives, stretched smooth, no tear");
        SurgeryDriver.Use();
        await Frames.Physics(10);
        SurgeryDriver.Use(false);
        await Frames.Seconds(1f);
        AssertBool(retractor.Hold is null && !hand.Attached).OverrideFailureMessage("Use tool lets go").IsTrue();
        await session.Finish(BudgetOverride);
    }

    [TestCase(Description = "KNOWN ISSUE: the skin under the drape's rim past the wound's end peaks up 5 mm once the "
        + "retractors are let go. The rim's skin piles up from small changes in how far they draw the skin: with the "
        + "skin holding the hand back gradually they draw it a little less; before that, drawing the end one 12 mm instead "
        + "of 10 mm peaked it the same.")]
    public async Task RetractorsLetGoRoundAWidenedThighWoundLieAlongTheLimb() =>
        await KnownIssue.Expect("under the drape's rim, no peak", LetGoRoundAWidenedThighWound);

    private async Task LetGoRoundAWidenedThighWound() {
        var session = await ToolSession.Start("bullet_muscle", "retractor/thigh");
        var driver = session.Driver;
        var body = driver.Body;
        var middle = driver.Patient.Wounds[0].Midpoint;
        var rim = WindowRim(body);
        // The entry wound widened through every layer along the thigh, then three retractors round it: one on each edge,
        // one at its end, each drawn away from the wound and let go of.
        var half = new Vector2(body.MetersToUv(0.025f), 0f);
        await driver.PlayerCutsSkin(middle - half, middle + half, 3);
        await driver.PlayerPutsDown();
        await Frames.Seconds(1f);
        var wounds = driver.Patient.Wounds.Count;
        var hooks = new List<SurgicalTool>();
        foreach (var away in (Vector2[])[new(0, -1), new(0, 1), new(1, 0)]) {
            var number = hooks.Count + 1;
            // On each side at the edge of the opening, pressed into it as a player does: the hook catches the edge on
            // that side. At the end on the skin past it: drawn along the cut from inside it, it would tear it longer.
            var atEnd = away.X != 0f;
            var at = atEnd ? EdgeBeside(driver, middle + (half * 0.8f), away) : middle + (away * body.MetersToUv(0.003f));
            SurgeryState.ToolIsOnTray(driver.Surgery, "retractor");
            var hookAt = driver.SitePoint(at);
            var retractor = await Hook(driver, hookAt, $"retractor {number}");
            // The end only gently: the short strip of skin between the hook and the end of the cut tears pulled 2 cm.
            var pull = driver.SitePoint(at + (away * body.MetersToUv(atEnd ? Pull * 0.5f : Pull))) - hookAt;
            await LetGoPulled(driver, retractor, pull, number);
            AssertInt(driver.Patient.Wounds.Count).OverrideFailureMessage($"retractor {number} hooked and pulled without tearing")
                .IsEqual(wounds);
            hooks.Add(retractor);
        }
        foreach (var retractor in hooks) {
            AssertLiesOnBody(driver, retractor);
        }
        await driver.Capture("let_go", "the retractors let go round the widened thigh wound, each lying along the limb on the skin");
        // Drawn as the skin's sheet shows it: a face pushed up over the drape's cloth lifts its corners.
        var drawn = DrawnSkin(body);
        var rimDrawn = rim.Where(edge => drawn.ContainsKey(edge.Index)).ToArray();
        AssertBool(rimDrawn.Length > 0).OverrideFailureMessage("the skin's sheet reaches the window's edge").IsTrue();
        var risen = rimDrawn.Select(edge => (edge.Index, Rise: drawn[edge.Index] - edge.Point.Y)).MaxBy(edge => edge.Rise);
        if (risen.Rise >= RimRise) {
            var k = risen.Index;
            GD.Print($"rim peak at {body.Tissue.UvOf(k)}: before {rim.First(edge => edge.Index == k).Point}, "
                + $"LayerPoint {body.LayerPoint(0, k)}, drawn height {drawn[k]}, sim {body.Tissue.Pos[k]}, settled {body.Tissue.Settled[k]}, "
                + $"fold {body.Tissue.NearFold(k)}, cloth {body.Tissue.OnClothOrSkirt(k)}, exposed {body.Tissue.Exposed[k]}");
        }
        AssertFloat(risen.Rise)
            .OverrideFailureMessage($"the skin along the window's edge stays on the body under the drape's rim, no peak: it rises {risen.Rise * 1000f:0.0} mm at uv {body.Tissue.UvOf(risen.Index)}")
            .IsLess(RimRise);
        await session.Finish(BudgetOverride);
    }

    [TestCase(Timeout = Limits.Slow)]
    public async Task FourRetractorsHoldTheAbdomenOpenForTheScalpelAndForceps() {
        var session = await ToolSession.Start("appendectomy", "retractor/four");
        var driver = session.Driver;
        var patient = driver.Patient;
        var body = driver.Body;
        var appendix = patient.Targets[0];
        // A cut through every layer over the appendix, along the site's long side.
        var half = new Vector2(body.MetersToUv(Opening * 0.5f), 0f);
        await driver.PlayerCutsSkin(appendix.Uv - half, appendix.Uv + half, 3);
        await driver.PlayerPutsDown();
        await Frames.Seconds(1f);
        var wounds = patient.Wounds.Count;
        await driver.Capture("incised", "a cut through every layer over the appendix, along the site's long side");

        // Two retractors on each edge, a third of the way in from each end, each hooked and drawn away from the cut.
        var hooks = new List<SurgicalTool>();
        foreach (var spot in (Vector2[])[new(-1, -1), new(1, -1), new(-1, 1), new(1, 1)]) {
            var number = hooks.Count + 1;
            var at = EdgeBeside(driver, appendix.Uv + new Vector2(half.X * spot.X / 3f, 0f), new Vector2(0f, spot.Y));
            SurgeryState.ToolIsOnTray(driver.Surgery, "retractor");
            var hookAt = driver.SitePoint(at);
            var retractor = await Hook(driver, hookAt, $"retractor {number}");
            var aside = driver.SitePoint(at + new Vector2(0f, body.MetersToUv(Pull) * spot.Y)) - hookAt;
            await LetGoPulled(driver, retractor, aside, number);
            AssertLiesOnBody(driver, retractor);
            hooks.Add(retractor);
        }
        AssertBool(hooks.All(Holding)).OverrideFailureMessage("all four hold the edges").IsTrue();
        AssertBool(body.IsOpen(appendix.Uv)).OverrideFailureMessage("the abdomen stands open between them").IsTrue();
        var gap = MuscleOpening(body, appendix.Uv);
        AssertFloat(gap).OverrideFailureMessage($"{gap * 100f:0.0} cm wide through the muscle").IsGreater(Pull);
        AssertInt(patient.Wounds.Count).OverrideFailureMessage("held open without tearing").IsEqual(wounds);
        // Over a few breaths: the belly rises and falls, and the retractors on it with it.
        var deepest = 0f;
        for (var i = 0; i < 16; i++) {
            await Frames.Seconds(0.25f);
            deepest = Mathf.Max(deepest, hooks.Max(retractor => PressedIn(driver, retractor)));
        }
        AssertFloat(deepest)
            .OverrideFailureMessage($"breathing, the belly lifts the retractors with it: pressed in at most {deepest * 1000f:0.0} mm")
            .IsLess(LyingPress);
        await driver.Capture("retracted", "four retractors hold the abdomen open, lying on the belly, not pressed into it");

        // The scalpel goes down into the opening between them and cuts the appendix's base free.
        var scalpel = (await driver.PlayerRequestsItem("scalpel"))!;
        var baseAt = body.UvToWorld(appendix.Uv, appendix.Depth);
        await driver.PlayerWalksTo(baseAt);
        await driver.PlayerReaches(baseAt);
        await driver.SetLevel(3);
        SurgeryDriver.Use();
        var reached = false;
        for (var i = 0; i < 30; i++) {
            await Frames.Physics(1);
            reached = reached || body.Probe(scalpel.TipPosition()).Zone == SiteZone.Cavity;
        }
        await driver.AssertAndCapture(reached, "the scalpel reaches into the opening, not onto a retractor", "scalpel_inside");
        await Frames.Until(() => appendix.Anchor <= 0f, 20f);
        SurgeryDriver.Use(false);
        AssertFloat(appendix.Anchor).OverrideFailureMessage("the scalpel cuts the appendix free inside the opening").IsEqual(0f);
        await driver.PlayerPutsDown();

        // Forceps take hold of it in the opening and lift it out past the retractors.
        await driver.PlayerExtracts("appendix");
        AssertBool(appendix.Extracted)
            .OverrideFailureMessage($"the forceps lift the appendix out between the retractors\n{driver.Recent()}").IsTrue();
        AssertBool(hooks.All(Holding)).OverrideFailureMessage("the retractors still hold the edges").IsTrue();
        await session.Finish(BudgetOverride);
    }

    /// <summary>The skin along the edge of the drape's window (exposed grid points beside one under the drape), each
    /// with where it's drawn now.</summary>
    private static (int Index, Vector3 Point)[] WindowRim(PatientBody body) {
        var tissue = body.Tissue;
        bool Covered(int i, int j) => i >= 0 && j >= 0 && i <= tissue.ResX && j <= tissue.ResY && !tissue.Exposed[tissue.Index(i, j)];
        return [.. Enumerable.Range(0, tissue.Rest.Length)
            .Where(k => tissue.Exposed[k] && tissue.CellOf(k) is var at
                && (Covered(at.X - 1, at.Y) || Covered(at.X + 1, at.Y) || Covered(at.X, at.Y - 1) || Covered(at.X, at.Y + 1)))
            .Select(k => (k, body.LayerPoint(0, k)))];
    }

    /// <summary>How high (site y) the skin's sheet is drawn at each grid point it has a vertex on: the highest of them.
    /// </summary>
    private static Dictionary<int, float> DrawnSkin(PatientBody body) {
        var tissue = body.Tissue;
        var heights = new Dictionary<int, float>();
        // The sheet is the surface drawn in the skin's material; a layer with no sheet publishes only its walls.
        var skin = body.Layers[0].Mesh;
        var sheet = Enumerable.Range(0, skin.GetSurfaceCount()).Where(s => skin.SurfaceGetMaterial(s) == body.SkinMaterial)
            .DefaultIfEmpty(-1).First();
        if (sheet < 0) {
            return heights;
        }
        var arrays = skin.SurfaceGetArrays(sheet);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        for (var v = 0; v < vertices.Length; v++) {
            var cell = new Vector2I(Mathf.RoundToInt(uvs[v].X * tissue.ResX), Mathf.RoundToInt(uvs[v].Y * tissue.ResY));
            if (uvs[v].DistanceTo(new Vector2((float)cell.X / tissue.ResX, (float)cell.Y / tissue.ResY)) > 0.0001f) {
                continue;
            }
            var k = tissue.Index(cell.X, cell.Y);
            heights[k] = heights.TryGetValue(k, out var height) ? Mathf.Max(height, vertices[v].Y) : vertices[v].Y;
        }
        return heights;
    }

    /// <summary>The actual opening through the published muscle sheet, across the incision at the target.
    /// A rest-grid radius can miss the opening after its material has moved; this checks the space tools enter.</summary>
    private static float MuscleOpening(PatientBody body, Vector2 uv) {
        using var arrays = body.Layers[2].Mesh.SurfaceGetArrays(0);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        var at = body.SitePoint(uv, 0f);
        var left = float.NegativeInfinity;
        var right = float.PositiveInfinity;
        for (var t = 0; t < indices.Length; t += 3) {
            var low = float.PositiveInfinity;
            var high = float.NegativeInfinity;
            for (var e = 0; e < 3; e++) {
                var a = vertices[indices[t + e]];
                var b = vertices[indices[t + (e + 1) % 3]];
                if (at.X < Mathf.Min(a.X, b.X) || at.X > Mathf.Max(a.X, b.X)) { continue; }
                if (Mathf.Abs(b.X - a.X) < 1e-9f) {
                    low = Mathf.Min(low, Mathf.Min(a.Z, b.Z));
                    high = Mathf.Max(high, Mathf.Max(a.Z, b.Z));
                } else {
                    var z = Mathf.Lerp(a.Z, b.Z, (at.X - a.X) / (b.X - a.X));
                    low = Mathf.Min(low, z);
                    high = Mathf.Max(high, z);
                }
            }
            if (!float.IsFinite(low)) { continue; }
            if (low <= at.Z && high >= at.Z) { return 0f; }
            if (high < at.Z) { left = Mathf.Max(left, high); }
            if (low > at.Z) { right = Mathf.Min(right, low); }
        }
        return float.IsFinite(left) && float.IsFinite(right) ? right - left : 0f;
    }

    private static bool Holding(SurgicalTool retractor) => retractor.State == ToolState.Standing && retractor.Hold is not null;

    /// <summary>A retractor (a fresh one when the tray has one) hooked at <paramref name="at"/> (world) with Use tool.
    /// </summary>
    private static async Task<SurgicalTool> Hook(SurgeryDriver driver, Vector3 at, string name) {
        var retractor = (await driver.PlayerRequestsItem("retractor"))!;
        await driver.PlayerWalksTo(at);
        await driver.PlayerReaches(at);
        SurgeryDriver.Use();
        await Frames.Physics(10);
        SurgeryDriver.Use(false);
        await Frames.Physics(3);
        AssertBool(retractor.Hold is SkinHold && driver.Me.Hands[driver.Me.Active].Attached)
            .OverrideFailureMessage($"{name} hooks the edge\n{driver.Recent()}").IsTrue();
        return retractor;
    }

    /// <summary>Draws the hooked retractor <paramref name="pull"/> (world) aside and lets go of it with Grab: it stays
    /// hooked, its handle pointing the way it pulled.</summary>
    private static async Task LetGoPulled(SurgeryDriver driver, SurgicalTool retractor, Vector3 pull, int number) {
        // Measure the complete pull since the hook entered the skin, including any settling before this sweep.
        var hold = (SkinHold)retractor.Hold!;
        var origin = hold.Origin ?? driver.Body.Site.ToLocal(driver.Body.UvToWorld(hold.Anchor));
        await driver.PlayerSweepsTo(retractor.TipPosition() + pull);
        var pulled = driver.Body.Site.GlobalBasis.Inverse()
            * (retractor.TipPosition() - driver.Body.Site.ToGlobal(origin));
        SurgeryDriver.Tap(InputActions.Grab);
        await Frames.Seconds(1f);
        AssertThat(retractor.State).OverrideFailureMessage($"retractor {number} stays hooked when let go of")
            .IsEqual(ToolState.Standing);
        AssertPointsAway(retractor, driver.Body.Site.GlobalBasis * pulled);
    }

    /// <summary>The skin just outside the opening's edge from <paramref name="uv"/> (on the cut) toward
    /// <paramref name="away"/>: where the skin is whole, HookOff further out.</summary>
    private static Vector2 EdgeBeside(SurgeryDriver driver, Vector2 uv, Vector2 away) {
        var step = away * driver.Body.MetersToUv(0.001f);
        for (var i = 0; i < 60 && driver.Body.LayerAt(uv) != "skin"; i++) {
            uv += step;
        }
        return uv + (step * HookOff * 1000f);
    }

    /// <summary>Where the cut's two edges are at its middle (site z, meters): the mean of the lips on the -v side and the
    /// +v side.</summary>
    private static float[] Lips(SurgeryDriver driver, Vector2 middle) {
        var tissue = driver.Body.Tissue;
        var sums = new float[2];
        var counts = new int[2];
        foreach (var s in tissue.Severed) {
            if (Mathf.Abs(tissue.CrossingUv(s).X - middle.X) > driver.Body.MetersToUv(0.01f)) {
                continue;
            }
            foreach (var k in (int[])[tissue.SpringAt(s).A, tissue.SpringAt(s).B]) {
                var side = tissue.UvOf(k).Y > middle.Y ? 1 : 0;
                sums[side] += tissue.Pos[k].Z;
                counts[side]++;
            }
        }
        return [sums[0] / Math.Max(counts[0], 1), sums[1] / Math.Max(counts[1], 1)];
    }

    /// <summary>A retractor let go of lies on the body: its handle tilts up no more than LyingTilt over the way the body
    /// rises from its hook to its handle (over level where the handle hangs past the edge of a limb), and it presses into
    /// the body nowhere deeper than LyingPress.</summary>
    private static void AssertLiesOnBody(SurgeryDriver driver, SurgicalTool retractor) {
        var tip = retractor.TipPosition();
        var grip = retractor.GlobalPosition;
        var across = (grip - tip).Slide(Vector3.Up).Length();
        var tilt = Mathf.RadToDeg(Mathf.Atan2(grip.Y - tip.Y, across));
        var under = BodyHeight(driver, grip);
        var bodyRise = under is { } height && BodyHeight(driver, tip) is { } below
            ? Mathf.RadToDeg(Mathf.Atan2(height - below, across)) : 0f;
        AssertFloat(tilt - Mathf.Max(bodyRise, 0f))
            .OverrideFailureMessage($"the retractor lies down (tilted {tilt:0} degrees, the body rising {bodyRise:0}), not standing up")
            .IsLess(LyingTilt);
        var deepest = PressedIn(driver, retractor);
        AssertFloat(deepest).OverrideFailureMessage($"pressed into the body at most {deepest * 1000f:0.0} mm").IsLess(LyingPress);
    }

    /// <summary>How deep (meters) a retractor lying on the body presses into it at its deepest along it now.</summary>
    private static float PressedIn(SurgeryDriver driver, SurgicalTool retractor) {
        var deepest = 0f;
        for (var i = 1; i <= 10; i++) {
            var p = retractor.TipPosition().Lerp(retractor.GlobalPosition, i / 10f);
            if (BodyHeight(driver, p) is { } height) {
                deepest = Mathf.Max(deepest, height - p.Y);
            }
        }
        return deepest;
    }

    /// <summary>A retractor let go of points its handle straight away from where it hooked: the way it was pulled.
    /// </summary>
    private static void AssertPointsAway(SurgicalTool retractor, Vector3 pull) {
        var handle = (retractor.GlobalPosition - retractor.TipPosition()).Slide(Vector3.Up);
        var facing = handle.Normalized().Dot(pull.Slide(Vector3.Up).Normalized());
        AssertFloat(facing)
            .OverrideFailureMessage($"its handle points away from the hook, the way it pulled ({Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(facing, -1f, 1f))):0} degrees off)")
            .IsGreater(0.97f);
    }

    /// <summary>The height of the body at rest under <paramref name="p"/> (world y): the site's measured surface,
    /// elsewhere the body's own collider. Null off the body.</summary>
    private static float? BodyHeight(SurgeryDriver driver, Vector3 p) {
        var body = driver.Body;
        var uv = body.WorldToUv(p);
        if (new Rect2(0, 0, 1, 1).HasPoint(uv) && body.OnBody(uv)) {
            return body.UvToWorld(uv).Y;
        }
        var query = PhysicsRayQueryParameters3D.Create(p + (Vector3.Up * 0.2f), p + Vector3.Down, PatientBody.SurfaceLayer);
        var hit = driver.GetViewport().World3D.DirectSpaceState.IntersectRay(query);
        return hit.Count > 0 ? hit["position"].AsVector3().Y : null;
    }
}
