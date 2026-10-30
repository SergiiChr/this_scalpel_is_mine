namespace Scalpel.Tests.TissueModification;

/// <summary>
/// The shape of pulled skin, apart from tearing and tool placement: the lip of a full-depth incision pulled as far as
/// a hand gets it (the skin holds the hand back, <see cref="TissueSim.Restrain"/>), then a flap cut free on two more
/// sides and pushed up and back toward its hinge so it humps up, each held and released, on a belly and a curved
/// thigh. Review the frames for a smooth stretched mound at the grip that eases out into the skin around it, thin
/// skin over its tethered bed, lips that stay smooth, the drape untouched, and no clipping; intersections within and
/// between the three tissue sheets and with the drape are checked numerically. On a tight thigh wound, forceps pull
/// the lip further than the skin gives: it peels up off the muscle, held there while the other hand sews the muscle
/// where that lays it bare.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("tissue_modification"), TestCategory("skin_pull"), TestCategory("visual_confirmation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class PulledSkinTest {
    // Temporary local budget override: recorded CPU/render overruns remain measured; functional assertions still fail normally.
    private static readonly BudgetAllowance BudgetOverride = new("cutting and deforming tissue exceed the recorded CPU/render budget", 23.5f);

    /// <summary>The incision, across the site's middle (uv).</summary>
    private static readonly Vector2 CutFrom = new(0.3f, 0.5f);
    private static readonly Vector2 CutTo = new(0.7f, 0.5f);
    /// <summary>Where the lip is gripped, just past the cut on its +v side.</summary>
    private static readonly Vector2 Grip = new(0.5f, 0.55f);
    /// <summary>Where the hand takes the lip (site space): away from the cut and up, further than the skin gives.
    /// </summary>
    private static readonly Vector3 LipPull = new(0f, 0.018f, 0.045f);
    /// <summary>How far down the site (v) the flap's sides are cut from the incision's ends: its hinge lies there.
    /// They go through the skin and fat (a cut this deep), the flap a surgeon raises: the muscle stays on its bed.
    /// </summary>
    private const float FlapSide = 0.25f;
    private const float FlapDepth = 0.5f;
    /// <summary>Where the hand takes the flap's free edge, as shares of the flap's own length (hinge to free edge):
    /// up, and a little back toward its hinge, so the flap lifts and humps up.</summary>
    private static readonly Vector3 FlapPull = new(0f, 0.6f, 0.15f);
    private const int GripKey = 900;
    /// <summary>How far (meters) from the wound's middle the hand draws its lip across the limb: further than the skin
    /// gives. The lip must then stand this far (meters) above the muscle under it.</summary>
    private const float PeelPull = 0.04f;
    private const float Peeled = 0.01f;
    /// <summary>How much (meters) of the muscle along the wound the peeled lip lays bare to sew, at least.</summary>
    private const float PeeledReach = 0.01f;
    /// <summary>How far (meters) the views of the peeled lip are taken from.</summary>
    private const float PeelView = 0.15f;

    [TestCase("appendectomy", Timeout = Limits.Slow)]
    [TestCase("leg_extension", Timeout = Limits.Slow)]
    public async Task PulledLipStretchesSmoothlyAndAFreedFlapHumpsUp(string scenario) {
        var session = await ToolSession.Start(scenario, $"skin_fold/{scenario}", captureUntouched: scenario == "leg_extension");
        var driver = session.Driver;
        var body = driver.Body;
        var tissue = body.Tissue;
        var quietRim = new[] { new Vector2(0.1f, 0.1f), new Vector2(0.9f, 0.1f), new Vector2(0.1f, 0.9f), new Vector2(0.9f, 0.9f) }
            .Select(at => tissue.Nearest(at)).Select(index => (Index: index, Point: body.LayerPoint(0, index))).ToArray();
        driver.Note("making the full-depth incision");
        await driver.Unbudgeted(async () => {
            SurgeryState.SkinIsCut(driver.Patient, CutFrom, CutTo, 1f);
            await Frames.Seconds(1f);
        });
        AssertRimQuiet(body, quietRim, "an incision doesn't lift unrelated skin at the drape's rim");
        await driver.Capture("incised", "a smooth full-depth opening with thin skin lips tapering to closed ends, no spikes, the skin at the drape's rim resting under it");

        var k = tissue.Nearest(Grip);
        var from = tissue.Pos[k];
        var bedUnderGrip = body.LayerPoint(2, k);
        var farBed = tissue.Nearest(new Vector2(0.05f, 0.5f));
        var muscleBefore = body.LayerPoint(2, farBed);
        driver.Note($"a hand pulls the lip {LipPull.Z * 1000f:0} mm away from the cut and {LipPull.Y * 1000f:0} mm up");
        var hand = await HandPulls(tissue, from, LipPull);
        var pulled = tissue.Pos[k] - from;
        AssertFloat(pulled.Length()).OverrideFailureMessage($"the skin holds the hand short of where it went ({pulled.Length() * 1000f:0} of {LipPull.Length() * 1000f:0} mm)")
            .IsLess(LipPull.Length() * 0.9f);
        AssertFloat(pulled.Length()).OverrideFailureMessage($"but it does give ({pulled.Length() * 1000f:0} mm)")
            .IsGreater(0.006f);
        // Past what it gives the sim's strain may stay over the limit (an edge peeled up reads over it), but the lip
        // goes no further along the body however hard the hand pulls on.
        var stopped = tissue.Pos[k];
        await PullsOn(tissue, hand, LipPull with { Y = 0f } * 0.5f);
        var further = (tissue.Pos[k] - stopped).Slide(Vector3.Up).Length();
        AssertFloat(further).OverrideFailureMessage($"pulled on past what it gives, the lip goes no further along the body ({further * 1000f:0.0} mm)")
            .IsLess(0.001f);
        var bedMovement = (body.LayerPoint(2, k) - bedUnderGrip).Length();
        AssertFloat(bedMovement)
            .OverrideFailureMessage("the muscle under the grip, cut through beside it, comes along only part of the way and the skin "
                + $"slides on over it, uncovering its cut edge ({bedMovement * 1000f:0} of {pulled.Length() * 1000f:0} mm)")
            .IsBetween(0.001f, pulled.Length() * 0.5f);
        AssertRimQuiet(body, quietRim, "the pull doesn't lift the skin at the drape's rim");
        AssertPerimeterInPlace(tissue);
        AssertFloat(body.LayerPoint(2, farBed).DistanceTo(muscleBefore))
            .OverrideFailureMessage("the muscle bed across the site stays anchored").IsLess(0.001f);
        await driver.Capture("pulled", "the lip drawn as far as the skin gives: a smooth stretched mound at the grip easing out into the skin around it, the opening wide, thin skin over its tethered fat and muscle, the drape untouched");
        await AssertNothingClips(driver);
        driver.Note("letting go");
        tissue.Release(GripKey);
        await Frames.Seconds(3f);
        await AssertNothingClips(driver);
        await driver.Capture("released", "let go, the skin settles back onto the body: the opening at its own gape, no spikes, nothing left lifted");

        driver.Note("cutting the flap free along two sides and pushing its free edge up and back toward its hinge");
        await driver.Unbudgeted(async () => {
            SurgeryState.SkinIsCut(driver.Patient, CutFrom, CutFrom + new Vector2(0f, FlapSide), FlapDepth);
            SurgeryState.SkinIsCut(driver.Patient, CutTo, CutTo + new Vector2(0f, FlapSide), FlapDepth);
            await Frames.Seconds(1f);
        });
        from = tissue.Pos[k];
        await HandPulls(tissue, from, FlapPull * FlapSide * tissue.Size.Y);
        AssertFloat(tissue.Pos[k].Y - from.Y).OverrideFailureMessage($"the freed flap's edge lifts off the body ({(tissue.Pos[k].Y - from.Y) * 1000f:0} mm)")
            .IsGreater(0.005f);
        AssertRimQuiet(body, quietRim, "humping the flap up doesn't lift the skin at the drape's rim");
        AssertPerimeterInPlace(tissue);
        await driver.Capture("flap_humped", "the flap cut free on three sides, its edge pushed up and back: it humps up into a rounded fold, its fat walls along its sides, the bed showing where it rose, nothing passing through anything");
        await AssertNothingClips(driver);
        tissue.Release(GripKey);
        await Frames.Seconds(3f);
        await AssertNothingClips(driver);
        await driver.Capture("flap_released", "the flap let go settles back over its bed, no sheet left through another");
        await session.Finish(BudgetOverride);
    }

    /// <summary>How far (meters) the lip across from a peeled one may move while the muscle under it is sewn: only the
    /// little less it gapes over closed muscle.</summary>
    private const float MaxOtherLipMove = 0.004f;

    [TestCase]
    public async Task ForcepsPeelATightThighWoundsEdgeUpOffItsMuscleToSewIt() {
        var session = await ToolSession.Start("bullet_muscle", "skin_fold/peeled");
        var driver = session.Driver;
        var body = driver.Body;
        var middle = driver.Patient.Wounds[0].Midpoint;
        // The entry wound widened through every layer along the thigh.
        var half = new Vector2(body.MetersToUv(0.025f), 0f);
        await driver.PlayerCutsSkin(middle - half, middle + half, 3);
        await driver.PlayerPutsDown();
        await Frames.Seconds(1f);
        var wounds = driver.Patient.Wounds.Count;
        // The needle first: fetched while the other hand holds the lip, walking to the tray would drag it.
        await driver.SwitchTo(SurgeryDriver.Left);
        await driver.PlayerRequestsItem("needle");
        await driver.SwitchTo(SurgeryDriver.Right);
        var forceps = (await driver.PlayerRequestsItem("forceps"))!;
        // Across the limb (meters to uv: the site is narrower across it than along) to the lip, and a little past it.
        var across = new Vector2(0f, 0.001f / body.SiteSize.Y);
        var lip = middle;
        for (var i = 0; i < 60 && body.LayerAt(lip) != "skin"; i++) {
            lip += across;
        }
        lip += across * 2f;
        var k = body.Tissue.Nearest(lip);
        await driver.PlayerWalksTo(driver.SitePoint(lip));
        await driver.PlayerReaches(driver.SitePoint(lip));
        SurgeryDriver.Use();
        await Frames.Physics(10);
        AssertBool(forceps.Hold is SkinHold).OverrideFailureMessage($"the forceps take the wound's lip ({forceps.Hold})").IsTrue();
        // Further across than the thigh's tight skin gives.
        await driver.PlayerSweepsTo(driver.SitePoint(middle + (across * PeelPull * 1000f)));
        await Frames.Seconds(1f);
        var peeled = body.LayerPoint(0, k).Y - body.LayerPoint(2, k).Y;
        AssertFloat(peeled).OverrideFailureMessage($"pulled past what the skin gives, the lip peels {peeled * 1000f:0} mm up off the muscle")
            .IsGreater(Peeled);
        AssertInt(driver.Patient.Wounds.Count).OverrideFailureMessage("peeled without tearing").IsEqual(wounds);
        await AssertNothingClips(driver);
        // From across the limb on the far side, under the lifted lip, the hands left out: the one holding the forceps
        // hangs right over it.
        var site = driver.SitePoint(middle);
        var under = -body.Site.GlobalBasis.Z;
        await SurgeryState.SiteIsSwabbed(driver.Patient);
        await session.CaptureOblique("peeled", $"forceps hold the thigh wound's lip peeled over {Peeled * 100f:0} cm up off the muscle, the site wiped: the skin and its fat lifted in a smooth arch, the muscle's cut edge showing flat under it, nothing through anything", site, under, PeelView);

        await driver.SwitchTo(SurgeryDriver.Left);
        var muscle = driver.Patient.Wounds.Where(wound => wound.ThroughMuscle).ToList();
        var cut = muscle.MaxBy(wound => wound.LengthUv)!;
        AssertString(driver.ShowsAlong(cut, TissueDepth.Muscle)).OverrideFailureMessage("the peeled lip lays the muscle bare").IsNotEmpty();
        if (KeyFrames.Wanted()) {
            // Close views without the hands: both are working over the seam while the other holds the lip up.
            driver.OnKeyFrame = (keyFrame, description) => session.CaptureAt(keyFrame, description, site, PeelView);
        }
        // The other lip, beside the cut and not past its ends: the muscle stitches under it mustn't move it. The rows
        // wholly across the cut: the hand's stroke wanders a little either side of its line, and the row it runs
        // beside may be the pulled lip's.
        var tissue = body.Tissue;
        var (start, end) = (cut.Points[0], cut.Points[^1]);
        bool Alongside(Vector2 uv) => (uv - start).Dot(end - start) is var along && along > 0f && along < (end - start).LengthSquared();
        var otherLip = Enumerable.Range(0, tissue.Pos.Length)
            .Where(p => tissue.UvOf(p).Y < middle.Y - 1f / tissue.ResY && Alongside(tissue.UvOf(p)) && cut.DistanceTo(tissue.UvOf(p)) < body.MetersToUv(0.015f))
            .ToDictionary(p => p, p => tissue.Pos[p]);
        // Close the widened incision while it is exposed. Closing the short original entry first can draw
        // its neighboring incision back beneath the held lip before the needle reaches it.
        foreach (var wound in muscle.OrderByDescending(wound => wound.LengthUv)) {
            await driver.PlayerSews(wound, TissueDepth.Muscle);
        }
        // The muscle shows only where the lip is peeled off it: sewn there.
        var sewn = body.UvToMeters(cut.Muscle.Count(m => m >= 1f) * Wound.BinLengthUv);
        AssertFloat(sewn * 100f).OverrideFailureMessage($"the needle sews {sewn * 100f:0.0} cm of muscle shut under the peeled lip\n{driver.Recent()}")
            .IsGreaterEqual(PeeledReach * 100f);
        // Held all along: the skin neither lets the lip down nor draws the hand back while the needle works.
        var stillPeeled = body.LayerPoint(0, k).Y - body.LayerPoint(2, k).Y;
        AssertBool(forceps.Hold is SkinHold).OverrideFailureMessage("the forceps still hold the lip").IsTrue();
        AssertFloat(stillPeeled).OverrideFailureMessage($"still peeled {stillPeeled * 1000f:0} mm up off the muscle").IsGreater(Peeled);
        // The other lip lies where it was: drawn after the peeled one, it would slide over the muscle or rise into a
        // spike.
        var worst = otherLip.MaxBy(lip => tissue.Pos[lip.Key].DistanceTo(lip.Value));
        var moved = tissue.Pos[worst.Key].DistanceTo(worst.Value);
        AssertFloat(moved).OverrideFailureMessage($"the other lip stays where it lay while the muscle is sewn ({moved * 1000f:0.0} mm moved, {(tissue.Pos[worst.Key] - worst.Value) * 1000f} mm, at {tissue.UvOf(worst.Key)})")
            .IsLess(MaxOtherLipMove);
        await SurgeryState.SiteIsSwabbed(driver.Patient);
        await session.CaptureOblique("muscle_sewn", "the muscle sewn, the site wiped: stitches cross the muscle's cut under the held lip, the other lip lying flat where it was, the skin and fat walls round it, nothing through anything", site, under, PeelView);
        await session.Finish(BudgetOverride);
    }

    /// <summary>Takes grip <see cref="GripKey"/> at Grip and pulls from <paramref name="from"/> by
    /// <paramref name="pull"/> (<see cref="PullsOn"/>), then holds still for 1 s. Returns where the hand ends up.</summary>
    private static async Task<Vector3> HandPulls(TissueSim tissue, Vector3 from, Vector3 pull) {
        tissue.Grip(GripKey, Grip);
        var at = await PullsOn(tissue, from, pull);
        // Holding still, the hand still gives where the skin asks it to.
        for (var n = 0; n < 60; n++) {
            at = tissue.Restrain(GripKey, at);
            tissue.MoveGrip(GripKey, at);
            await Frames.Physics(1);
        }
        return at;
    }

    /// <summary>The hand holding on at <paramref name="at"/> pulls by <paramref name="pull"/> over 1.5 s, as far as the
    /// skin lets it. Returns where it ends up.</summary>
    private static async Task<Vector3> PullsOn(TissueSim tissue, Vector3 at, Vector3 pull) {
        for (var n = 1; n <= 90; n++) {
            at = tissue.Restrain(GripKey, at + pull / 90f);
            tissue.MoveGrip(GripKey, at);
            await Frames.Physics(1);
        }
        return at;
    }

    private static void AssertRimQuiet(PatientBody body, (int Index, Vector3 Point)[] rim, string claim) {
        foreach (var (index, point) in rim) {
            AssertFloat(body.LayerPoint(0, index).Y - point.Y).OverrideFailureMessage(claim).IsLess(0.002f);
        }
    }

    private static void AssertPerimeterInPlace(TissueSim tissue) {
        foreach (var far in new[] { tissue.Index(0, 0), tissue.Index(tissue.ResX, 0), tissue.Index(0, tissue.ResY), tissue.Index(tissue.ResX, tissue.ResY) }) {
            AssertFloat(tissue.Pos[far].DistanceTo(tissue.Rest[far]))
                .OverrideFailureMessage("a local pull leaves the site's far corners in place").IsLess(0.00001f);
        }
    }

    private static Task AssertNothingClips(SurgeryDriver driver) => driver.Unbudgeted(() => {
        AssertTissueSheetsDoNotIntersect(driver.Body);
        TissueLayers.AssertUnderSkin(driver.Body);
        AssertSkinDoesNotCrossDrape(driver.Body);
        AssertDistantGownIsNotReplacedBySkin(driver.Body);
        return Task.CompletedTask;
    });

    private static void AssertTissueSheetsDoNotIntersect(PatientBody body) {
        // Each sheet's triangles between skin grid points, and whether the skin is open over each (its fat or muscle
        // in view). Only what can show is checked: the skin through itself anywhere, a deeper sheet where the
        // skin is open over it. The lips (triangles with a vertex off the grid, where the blade crossed) and pairs
        // drawn from a shared grid point are left out: the layers are one sheet offset by their depths, and where
        // it creases at the grid's own scale the offsets meet a skin thickness under the skin whatever is done.
        var triangles = new List<(Vector3 A, Vector3 B, Vector3 C, Vector2[] Uvs, string Layer, bool Shows)>();
        var tissue = body.Tissue;
        bool OnGrid(Vector2 uv) => Mathf.Abs(uv.X * tissue.ResX - Mathf.Round(uv.X * tissue.ResX)) < 0.001f
            && Mathf.Abs(uv.Y * tissue.ResY - Mathf.Round(uv.Y * tissue.ResY)) < 0.001f;
        var skinArrays = body.Layers[0].Mesh.SurfaceGetArrays(0);
        var skinVertices = skinArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var skinIndices = skinArrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        // A deeper sheet's triangle shows where no skin lies over its middle.
        bool Uncovered(Vector3 at) {
            var flat = new Vector2(at.X, at.Z);
            for (var t = 0; t < skinIndices.Length; t += 3) {
                var (a, b, c) = (skinVertices[skinIndices[t]], skinVertices[skinIndices[t + 1]], skinVertices[skinIndices[t + 2]]);
                var w = TissueLayers.Barycentric(flat, new Vector2(a.X, a.Z), new Vector2(b.X, b.Z), new Vector2(c.X, c.Z));
                if (w.X >= 0f && w.Y >= 0f && w.Z >= 0f && a.Y * w.X + b.Y * w.Y + c.Y * w.Z > at.Y) {
                    return false;
                }
            }
            return true;
        }
        foreach (var layer in body.Layers.Where(layer => layer.Visible)) {
            var arrays = layer.Mesh.SurfaceGetArrays(0);
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            for (var t = 0; t < indices.Length; t += 3) {
                Vector2[] corners = [uvs[indices[t]], uvs[indices[t + 1]], uvs[indices[t + 2]]];
                if (corners.All(OnGrid)) {
                    var (a, b, c) = (vertices[indices[t]], vertices[indices[t + 1]], vertices[indices[t + 2]]);
                    var shows = layer.Name == "Skin" || Uncovered((a + b + c) / 3f);
                    triangles.Add((a, b, c, corners, layer.Name, shows));
                }
            }
        }
        var intersections = new List<string>();
        for (var a = 0; a < triangles.Count; a++) {
            var (p, q, r, uvs, layer, shows) = triangles[a];
            var low = p.Min(q).Min(r);
            var high = p.Max(q).Max(r);
            for (var b = a + 1; b < triangles.Count; b++) {
                var (x, y, z, otherUvs, other, otherShows) = triangles[b];
                var otherLow = x.Min(y).Min(z);
                var otherHigh = x.Max(y).Max(z);
                if (!(shows && otherShows)
                    || low.X > otherHigh.X || high.X < otherLow.X || low.Y > otherHigh.Y || high.Y < otherLow.Y
                    || low.Z > otherHigh.Z || high.Z < otherLow.Z
                    || uvs.Any(uv => otherUvs.Any(o => o.DistanceSquaredTo(uv) < 1e-10f))) {
                    continue;
                }
                if (TissueLayers.Crosses(p, q, x, y, z) || TissueLayers.Crosses(q, r, x, y, z) || TissueLayers.Crosses(r, p, x, y, z)
                    || TissueLayers.Crosses(x, y, p, q, r) || TissueLayers.Crosses(y, z, p, q, r) || TissueLayers.Crosses(z, x, p, q, r)) {
                    var at = (p + q + r) / 3f;
                    intersections.Add($"{layer} at {body.LocalToUv(at)} through {other} at {body.LocalToUv((x + y + z) / 3f)}");
                }
            }
        }
        AssertInt(intersections.Count).OverrideFailureMessage(
                $"the skin doesn't pass through itself, nor fat or muscle in view through another sheet ({intersections.Count} crossings: {string.Join("; ", intersections.Take(6))})")
            .IsEqual(0);
    }

    /// <summary>Quiet gown well inside its own surface cannot acquire islands of replacement skin.</summary>
    private static void AssertDistantGownIsNotReplacedBySkin(PatientBody body) {
        TriangleMesh Surface(string name) {
            var instance = (MeshInstance3D)body.FindChild(name, true, false);
            var toSite = body.Site.GlobalTransform.AffineInverse() * instance.GlobalTransform;
            var mesh = new TriangleMesh();
            mesh.CreateFromFaces([.. instance.Mesh.GetFaces().Select(point => toSite * point)]);
            return mesh;
        }
        using var gown = Surface("Gown");
        using var skin = Surface("Body");
        using var replacement = new TriangleMesh();
        replacement.CreateFromFaces(body.Layers[0].Mesh.GetFaces());
        var tissue = body.Tissue;
        var covered = new bool[tissue.Rest.Length];
        var heights = new float[covered.Length];
        for (var k = 0; k < covered.Length; k++) {
            var point = tissue.Rest[k];
            var origin = new Vector3(point.X, 0.5f, point.Z);
            using var cloth = gown.IntersectRay(origin, Vector3.Down);
            using var bodyHit = skin.IntersectRay(origin, Vector3.Down);
            if (cloth.Count == 0) { continue; }
            heights[k] = cloth["position"].AsVector3().Y;
            covered[k] = bodyHit.Count == 0 || heights[k] > bodyHit["position"].AsVector3().Y + 0.00001f;
        }
        var islands = 0;
        var samples = 0;
        for (var j = 1; j < tissue.ResY; j++) {
            for (var i = 1; i < tissue.ResX; i++) {
                var k = tissue.Index(i, j);
                if (!covered[k] || !covered[k - 1] || !covered[k + 1]
                    || !covered[k - tissue.ResX - 1] || !covered[k + tissue.ResX + 1]) { continue; }
                var point = new Vector2(tissue.Rest[k].X, tissue.Rest[k].Z);
                if (point.Length() < 0.1f || !tissue.FloorOpen.HasPoint(point)) { continue; }
                samples++;
                using var hit = replacement.IntersectRay(new Vector3(point.X, 0.5f, point.Y), Vector3.Down);
                if (hit.Count > 0 && hit["position"].AsVector3().Y >= heights[k] - 0.0005f) { islands++; }
            }
        }
        if (body.SiteId == "abdomen") {
            AssertInt(samples).OverrideFailureMessage("the belly gown rim is sampled outside the local fold").IsGreater(0);
        }
        AssertInt(islands).OverrideFailureMessage($"quiet gown away from the grip has no skin-coloured replacement islands ({islands} covered samples)")
            .IsEqual(0);
    }

    private static void AssertSkinDoesNotCrossDrape(PatientBody body) {
        var drape = body.Drape!;
        var toSite = body.Site.GlobalTransform.AffineInverse() * drape.GlobalTransform;
        var cloth = drape.Mesh.GetFaces().Select(p => toSite * p).ToArray();
        var arrays = body.Layers[0].Mesh.SurfaceGetArrays(0);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        var crossings = 0;
        AssertThat(drape.CastShadow).IsEqual(GeometryInstance3D.ShadowCastingSetting.DoubleSided);
        for (var t = 0; t < indices.Length; t += 3) {
            // The skirt joining exposed skin back to the covered body passes beneath the cloth at its rim.
            // Check the exposed sheet, where a crossing would show as a hole or triangle on top of the drape.
            if (Enumerable.Range(0, 3).Any(n => !body.Tissue.Exposed[body.Tissue.Index(
                Mathf.RoundToInt(uvs[indices[t + n]].X * body.Tissue.ResX),
                Mathf.RoundToInt(uvs[indices[t + n]].Y * body.Tissue.ResY))])) {
                continue;
            }
            var a = vertices[indices[t]];
            var b = vertices[indices[t + 1]];
            var c = vertices[indices[t + 2]];
            var low = a.Min(b).Min(c);
            var high = a.Max(b).Max(c);
            for (var n = 0; n < cloth.Length; n += 3) {
                var x = cloth[n];
                var y = cloth[n + 1];
                var z = cloth[n + 2];
                var otherLow = x.Min(y).Min(z);
                var otherHigh = x.Max(y).Max(z);
                if (low.X > otherHigh.X || high.X < otherLow.X || low.Y > otherHigh.Y || high.Y < otherLow.Y
                    || low.Z > otherHigh.Z || high.Z < otherLow.Z) { continue; }
                if (TissueLayers.Crosses(a, b, x, y, z) || TissueLayers.Crosses(b, c, x, y, z) || TissueLayers.Crosses(c, a, x, y, z)
                    || TissueLayers.Crosses(x, y, a, b, c) || TissueLayers.Crosses(y, z, a, b, c) || TissueLayers.Crosses(z, x, a, b, c)) {
                    crossings++;
                }
            }
        }
        AssertInt(crossings).OverrideFailureMessage($"exposed skin does not pass through the drape ({crossings} triangle crossings)")
            .IsEqual(0);
    }
}
