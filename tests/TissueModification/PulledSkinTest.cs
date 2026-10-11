namespace Scalpel.Tests.TissueModification;

/// <summary>
/// The shape of pulled skin, apart from tearing and tool placement: the lip of a full-depth incision pulled as far as
/// a hand gets it (the skin holds the hand back, <see cref="TissueSim.Restrain"/>), then a flap cut free on two more
/// sides and pushed up and back toward its hinge so it humps up, each held and released, on a belly and a curved
/// thigh. Review the frames for a smooth stretched mound at the grip that eases out into the skin around it, thin
/// skin over its tethered bed, lips that stay smooth, the drape untouched, and no clipping; intersections within and
/// between the three tissue sheets and with the drape are checked numerically.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tissue_modification"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class PulledSkinTest
{
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

    [TestCase("appendectomy")]
    [TestCase("leg_extension")]
    public async Task PulledLipStretchesSmoothlyAndAFreedFlapHumpsUp(string scenario)
    {
        var session = await ToolSession.Start(scenario, $"skin_fold/{scenario}");
        var driver = session.Driver;
        var body = driver.Body;
        var tissue = body.Tissue;
        // Tearing is covered in TissueSimTest. Here an intact sheet must stay a valid sheet.
        tissue.Tearing = false;
        var quietRim = new[] { new Vector2(0.1f, 0.1f), new Vector2(0.9f, 0.1f), new Vector2(0.1f, 0.9f), new Vector2(0.9f, 0.9f) }
            .Select(at => tissue.Nearest(at)).Select(index => (Index: index, Point: body.LayerPoint(0, index))).ToArray();
        driver.Note("making the full-depth incision");
        await driver.Unbudgeted(async () =>
        {
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
        await HandPulls(tissue, from, LipPull);
        var pulled = tissue.Pos[k] - from;
        AssertFloat(pulled.Length()).OverrideFailureMessage($"the skin holds the hand short of where it went ({pulled.Length() * 1000f:0} of {LipPull.Length() * 1000f:0} mm)")
            .IsLess(LipPull.Length() * 0.9f);
        AssertFloat(pulled.Length()).OverrideFailureMessage($"but it does give ({pulled.Length() * 1000f:0} mm)")
            .IsGreater(0.006f);
        AssertFloat(tissue.StrainAt(GripKey)).OverrideFailureMessage($"stretched no further than it gives ({tissue.StrainAt(GripKey):0.00} of {TissueSim.GripStrain})")
            .IsLess(TissueSim.GripStrain + 0.3f);
        var bedMovement = (body.LayerPoint(2, k) - bedUnderGrip).Length();
        AssertFloat(bedMovement)
            .OverrideFailureMessage($"the wound's edge comes along as one block: the muscle under the grip, cut through beside it, moves with the skin ({bedMovement * 1000f:0} of {pulled.Length() * 1000f:0} mm)")
            .IsBetween(pulled.Length() * 0.3f, pulled.Length() * 1.001f);
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
        await driver.Unbudgeted(async () =>
        {
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
        await session.Finish();
    }

    /// <summary>Takes grip <see cref="GripKey"/> from <paramref name="from"/> by <paramref name="pull"/> over 90
    /// physics frames the way a hand does: a step at a time from wherever it got to, held back where the skin stops
    /// giving (the hand comes back to the grip, Patient.UpdateGrip()).</summary>
    private static async Task HandPulls(TissueSim tissue, Vector3 from, Vector3 pull)
    {
        tissue.Grip(GripKey, Grip);
        var at = from;
        for (var n = 1; n <= 90; n++)
        {
            at = tissue.Restrain(GripKey, at + pull / 90f);
            tissue.MoveGrip(GripKey, at);
            await Frames.Physics(1);
        }
        // Holding still, the hand still gives where the skin asks it to.
        for (var n = 0; n < 60; n++)
        {
            at = tissue.Restrain(GripKey, at);
            tissue.MoveGrip(GripKey, at);
            await Frames.Physics(1);
        }
    }

    private static void AssertRimQuiet(PatientBody body, (int Index, Vector3 Point)[] rim, string claim)
    {
        foreach (var (index, point) in rim)
        {
            AssertFloat(body.LayerPoint(0, index).Y - point.Y).OverrideFailureMessage(claim).IsLess(0.002f);
        }
    }

    private static void AssertPerimeterInPlace(TissueSim tissue)
    {
        foreach (var far in new[] { tissue.Index(0, 0), tissue.Index(tissue.ResX, 0), tissue.Index(0, tissue.ResY), tissue.Index(tissue.ResX, tissue.ResY) })
        {
            AssertFloat(tissue.Pos[far].DistanceTo(tissue.Rest[far]))
                .OverrideFailureMessage("a local pull leaves the site's far corners in place").IsLess(0.00001f);
        }
    }

    private static Task AssertNothingClips(SurgeryDriver driver) => driver.Unbudgeted(() =>
    {
        AssertTissueSheetsDoNotIntersect(driver.Body);
        TissueLayers.AssertUnderSkin(driver.Body);
        AssertSkinDoesNotCrossDrape(driver.Body);
        return Task.CompletedTask;
    });

    private static void AssertTissueSheetsDoNotIntersect(PatientBody body)
    {
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
        bool Uncovered(Vector3 at)
        {
            var flat = new Vector2(at.X, at.Z);
            for (var t = 0; t < skinIndices.Length; t += 3)
            {
                var (a, b, c) = (skinVertices[skinIndices[t]], skinVertices[skinIndices[t + 1]], skinVertices[skinIndices[t + 2]]);
                var w = TissueLayers.Barycentric(flat, new Vector2(a.X, a.Z), new Vector2(b.X, b.Z), new Vector2(c.X, c.Z));
                if (w.X >= 0f && w.Y >= 0f && w.Z >= 0f && a.Y * w.X + b.Y * w.Y + c.Y * w.Z > at.Y)
                {
                    return false;
                }
            }
            return true;
        }
        foreach (var layer in body.Layers.Where(layer => layer.Visible))
        {
            var arrays = layer.Mesh.SurfaceGetArrays(0);
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            for (var t = 0; t < indices.Length; t += 3)
            {
                Vector2[] corners = [uvs[indices[t]], uvs[indices[t + 1]], uvs[indices[t + 2]]];
                if (corners.All(OnGrid))
                {
                    var (a, b, c) = (vertices[indices[t]], vertices[indices[t + 1]], vertices[indices[t + 2]]);
                    var shows = layer.Name == "Skin" || Uncovered((a + b + c) / 3f);
                    triangles.Add((a, b, c, corners, layer.Name, shows));
                }
            }
        }
        var intersections = new List<string>();
        for (var a = 0; a < triangles.Count; a++)
        {
            var (p, q, r, uvs, layer, shows) = triangles[a];
            var low = p.Min(q).Min(r);
            var high = p.Max(q).Max(r);
            for (var b = a + 1; b < triangles.Count; b++)
            {
                var (x, y, z, otherUvs, other, otherShows) = triangles[b];
                var otherLow = x.Min(y).Min(z);
                var otherHigh = x.Max(y).Max(z);
                if (!(shows && otherShows)
                    || low.X > otherHigh.X || high.X < otherLow.X || low.Y > otherHigh.Y || high.Y < otherLow.Y
                    || low.Z > otherHigh.Z || high.Z < otherLow.Z
                    || uvs.Any(uv => otherUvs.Any(o => o.DistanceSquaredTo(uv) < 1e-10f)))
                {
                    continue;
                }
                if (TissueLayers.Crosses(p, q, x, y, z) || TissueLayers.Crosses(q, r, x, y, z) || TissueLayers.Crosses(r, p, x, y, z)
                    || TissueLayers.Crosses(x, y, p, q, r) || TissueLayers.Crosses(y, z, p, q, r) || TissueLayers.Crosses(z, x, p, q, r))
                {
                    var at = (p + q + r) / 3f;
                    intersections.Add($"{layer} at {body.LocalToUv(at)} through {other} at {body.LocalToUv((x + y + z) / 3f)}");
                }
            }
        }
        AssertInt(intersections.Count).OverrideFailureMessage(
                $"the skin doesn't pass through itself, nor fat or muscle in view through another sheet ({intersections.Count} crossings: {string.Join("; ", intersections.Take(6))})")
            .IsEqual(0);
    }

    private static void AssertSkinDoesNotCrossDrape(PatientBody body)
    {
        var drape = body.Drape!;
        var toSite = body.Site.GlobalTransform.AffineInverse() * drape.GlobalTransform;
        var cloth = drape.Mesh.GetFaces().Select(p => toSite * p).ToArray();
        var arrays = body.Layers[0].Mesh.SurfaceGetArrays(0);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        var crossings = 0;
        AssertThat(drape.CastShadow).IsEqual(GeometryInstance3D.ShadowCastingSetting.DoubleSided);
        for (var t = 0; t < indices.Length; t += 3)
        {
            // The skirt joining exposed skin back to the covered body passes beneath the cloth at its rim.
            // Check the exposed sheet, where a crossing would show as a hole or triangle on top of the drape.
            if (Enumerable.Range(0, 3).Any(n => !body.Tissue.Exposed[body.Tissue.Index(
                Mathf.RoundToInt(uvs[indices[t + n]].X * body.Tissue.ResX),
                Mathf.RoundToInt(uvs[indices[t + n]].Y * body.Tissue.ResY))]))
            {
                continue;
            }
            var a = vertices[indices[t]];
            var b = vertices[indices[t + 1]];
            var c = vertices[indices[t + 2]];
            var low = a.Min(b).Min(c);
            var high = a.Max(b).Max(c);
            for (var n = 0; n < cloth.Length; n += 3)
            {
                var x = cloth[n];
                var y = cloth[n + 1];
                var z = cloth[n + 2];
                var otherLow = x.Min(y).Min(z);
                var otherHigh = x.Max(y).Max(z);
                if (low.X > otherHigh.X || high.X < otherLow.X || low.Y > otherHigh.Y || high.Y < otherLow.Y
                    || low.Z > otherHigh.Z || high.Z < otherLow.Z) { continue; }
                if (TissueLayers.Crosses(a, b, x, y, z) || TissueLayers.Crosses(b, c, x, y, z) || TissueLayers.Crosses(c, a, x, y, z)
                    || TissueLayers.Crosses(x, y, a, b, c) || TissueLayers.Crosses(y, z, a, b, c) || TissueLayers.Crosses(z, x, a, b, c))
                {
                    crossings++;
                }
            }
        }
        AssertInt(crossings).OverrideFailureMessage($"exposed skin does not pass through the drape ({crossings} triangle crossings)")
            .IsEqual(0);
    }
}
