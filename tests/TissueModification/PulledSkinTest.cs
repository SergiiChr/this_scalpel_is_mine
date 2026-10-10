namespace Scalpel.Tests.TissueModification;

/// <summary>Isolates the shape of a large skin pull from tearing and tool placement. The same simulated grip is
/// drawn on a belly and a curved thigh, held, then released. Inspect the top and oblique frames for a broad fold,
/// continuous layer walls and no intersections; intersections within and between the three tissue sheets are also checked numerically.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("tissue_modification"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class PulledSkinTest
{
    [TestCase("appendectomy")]
    [TestCase("leg_extension")]
    public async Task LargePullFormsAContinuousSkinFold(string scenario)
    {
        var session = await ToolSession.Start(scenario, $"skin_fold/{scenario}");
        var driver = session.Driver;
        var tissue = driver.Body.Tissue;
        // The mechanics of tearing are covered in TissueSimTest. Here an intact flap must remain a valid sheet.
        tissue.Tearing = false;
        var quietRim = new[] { new Vector2(0.1f, 0.1f), new Vector2(0.9f, 0.1f),
            new Vector2(0.1f, 0.9f), new Vector2(0.9f, 0.9f) }
            .Select(at => tissue.Nearest(at)).Select(index => (Index: index, Point: driver.Body.LayerPoint(0, index))).ToArray();
        driver.Note("making the full-depth incision");
        await driver.Unbudgeted(async () =>
        {
            SurgeryState.SkinIsCut(driver.Patient, new Vector2(0.3f, 0.5f), new Vector2(0.7f, 0.5f), 1f);
            await Frames.Seconds(1f);
        });
        foreach (var (index, point) in quietRim)
        {
            AssertFloat(driver.Body.LayerPoint(0, index).Y - point.Y)
                .OverrideFailureMessage("an ordinary incision does not lift unrelated skin at the drape rim").IsLess(0.002f);
        }
        await driver.Capture("incised");
        var uv = new Vector2(0.5f, 0.55f);
        var k = tissue.Nearest(uv);
        var from = tissue.Pos[k];
        var bedUnderGrip = driver.Body.LayerPoint(2, k);
        var farBed = tissue.Nearest(new Vector2(0.05f, 0.5f));
        var muscleBefore = driver.Body.LayerPoint(2, farBed);
        driver.Note("pulling the incised skin 45 mm sideways and 18 mm upward");
        tissue.Grip(900, uv);
        var pull = new Vector3(0f, 0.018f, 0.045f);
        for (var n = 1; n <= 90; n++)
        {
            tissue.MoveGrip(900, from + pull * n / 90f);
            await Frames.Physics(1);
        }
        await Frames.Seconds(1f);
        AssertFloat(tissue.Pos[k].DistanceTo(from + pull)).OverrideFailureMessage("the fold follows the grip")
            .IsLess(0.0005f);
        AssertFloat(tissue.Pos[k].Y - from.Y).OverrideFailureMessage("the pulled edge rises into a fold")
            .IsGreater(0.015f);
        var bedMovement = driver.Body.LayerPoint(2, k) - bedUnderGrip;
        AssertFloat(new Vector2(bedMovement.X, bedMovement.Z).Length())
            .OverrideFailureMessage("the muscle beneath the grip remains tethered while the skin slides 45 mm")
            .IsLess(0.015f);
        foreach (var (index, point) in quietRim)
        {
            AssertFloat(driver.Body.LayerPoint(0, index).Y - point.Y)
                .OverrideFailureMessage("a local fold does not lift the distant skin rim").IsLess(0.002f);
        }
        foreach (var far in new[] { tissue.Index(0, 0), tissue.Index(tissue.ResX, 0),
            tissue.Index(0, tissue.ResY), tissue.Index(tissue.ResX, tissue.ResY) })
        {
            AssertFloat(tissue.Pos[far].DistanceTo(tissue.Rest[far]))
                .OverrideFailureMessage("a local pull leaves the distant site perimeter in place").IsLess(0.00001f);
        }
        // Compare against the layer's position before the grip; the muscle bed must stay mostly anchored.
        AssertFloat(driver.Body.LayerPoint(2, farBed).DistanceTo(muscleBefore))
            .OverrideFailureMessage("the distant muscle bed stays anchored during the local pull").IsLess(0.001f);
        await driver.Capture("pulled");
        await driver.Unbudgeted(() =>
        {
            AssertTissueSheetsDoNotIntersect(driver.Body);
            AssertSkinDoesNotCrossDrape(driver.Body);
            return Task.CompletedTask;
        });
        await Frames.Seconds(1f);
        await driver.Capture("held");
        await driver.Unbudgeted(() =>
        {
            AssertTissueSheetsDoNotIntersect(driver.Body);
            AssertSkinDoesNotCrossDrape(driver.Body);
            return Task.CompletedTask;
        });
        driver.Note("releasing the fold");
        tissue.Release(900);
        await Frames.Seconds(3f);
        await driver.Unbudgeted(() =>
        {
            AssertTissueSheetsDoNotIntersect(driver.Body);
            AssertSkinDoesNotCrossDrape(driver.Body);
            return Task.CompletedTask;
        });
        await driver.Capture("released");
        await session.Finish();
    }

    private static void AssertTissueSheetsDoNotIntersect(PatientBody body)
    {
        var triangles = new List<(Vector3 A, Vector3 B, Vector3 C)>();
        foreach (var layer in body.Layers.Where(layer => layer.Visible))
        {
            var arrays = layer.Mesh.SurfaceGetArrays(0);
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            for (var t = 0; t < indices.Length; t += 3)
            {
                triangles.Add((vertices[indices[t]], vertices[indices[t + 1]], vertices[indices[t + 2]]));
            }
        }
        var intersections = 0;
        for (var a = 0; a < triangles.Count; a++)
        {
            var (p, q, r) = triangles[a];
            var low = p.Min(q).Min(r);
            var high = p.Max(q).Max(r);
            for (var b = a + 1; b < triangles.Count; b++)
            {
                var (x, y, z) = triangles[b];
                var otherLow = x.Min(y).Min(z);
                var otherHigh = x.Max(y).Max(z);
                if (low.X > otherHigh.X || high.X < otherLow.X || low.Y > otherHigh.Y || high.Y < otherLow.Y
                    || low.Z > otherHigh.Z || high.Z < otherLow.Z)
                {
                    continue;
                }
                if (Crosses(p, q, x, y, z) || Crosses(q, r, x, y, z) || Crosses(r, p, x, y, z)
                    || Crosses(x, y, p, q, r) || Crosses(y, z, p, q, r) || Crosses(z, x, p, q, r))
                {
                    intersections++;
                }
            }
        }
        AssertInt(intersections).OverrideFailureMessage($"pulled, held and released skin, fat and muscle have no sheets passing through one another ({intersections} crossings)")
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
                if (Crosses(a, b, x, y, z) || Crosses(b, c, x, y, z) || Crosses(c, a, x, y, z)
                    || Crosses(x, y, a, b, c) || Crosses(y, z, a, b, c) || Crosses(z, x, a, b, c))
                {
                    crossings++;
                }
            }
        }
        AssertInt(crossings).OverrideFailureMessage($"exposed skin does not pass through the drape ({crossings} triangle crossings)")
            .IsEqual(0);
    }

    /// <summary>Strict interior intersection: adjacent triangles touching at shared vertices/edges don't count.</summary>
    private static bool Crosses(Vector3 from, Vector3 to, Vector3 a, Vector3 b, Vector3 c)
    {
        var direction = to - from;
        var ab = b - a;
        var ac = c - a;
        var normal = ab.Cross(ac).Normalized();
        var fromSide = (from - a).Dot(normal);
        var toSide = (to - a).Dot(normal);
        // Float roundoff on nearly coplanar, adjoining faces isn't penetration. Require the segment to pass
        // through both sides by 20 micrometres (far below a visible crease or the skin's thickness).
        const float ContactTolerance = 0.00002f;
        if (fromSide * toSide >= 0f || Mathf.Abs(fromSide) < ContactTolerance || Mathf.Abs(toSide) < ContactTolerance)
        {
            return false;
        }
        var h = direction.Cross(ac);
        var det = ab.Dot(h);
        if (Mathf.Abs(det) < 1e-12f) { return false; }
        var offset = from - a;
        var u = offset.Dot(h) / det;
        var q = offset.Cross(ab);
        var v = direction.Dot(q) / det;
        var t = ac.Dot(q) / det;
        const float Margin = 0.0001f;
        return u > Margin && v > Margin && u + v < 1f - Margin && t > Margin && t < 1f - Margin;
    }
}
