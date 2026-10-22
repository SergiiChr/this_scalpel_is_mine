namespace Scalpel.Tests.Patients;

[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("liquids")]
public class LiquidShapeTest {
    [TestCase]
    public void LiquidGridInterpolatesAnObliqueBoundary() {
        var surface = new LiquidSurface();
        surface.Field(new Rect2(Vector2.Zero, Vector2.One), 8, 8, p => 0.813f - p.X - p.Y, (p, _) => new Vector3(p.X, 0f, p.Y));
        using var mesh = surface.Upload();
        var points = mesh.GetFaces();
        AssertInt(points.Length).IsGreater(0);
        AssertBool(points.All(p => p.X + p.Z <= 0.81301f)).OverrideFailureMessage("no fluid triangle crosses its supporting boundary").IsTrue();
        AssertBool(points.Any(p => Mathf.Abs(p.X + p.Z - 0.813f) < 0.00001f))
            .OverrideFailureMessage("the contour reaches the boundary instead of stopping at stair steps").IsTrue();

        surface.Clear();
        surface.Field(new Rect2(-0.1f, -0.1f, 0.2f, 0.2f), 8, 8,
            p => 0.025f - p.X, (p, _) => new Vector3(p.X, 0f, p.Y));
        using var clippedMesh = surface.Upload();
        var clippedPoints = clippedMesh.GetFaces();
        AssertFloat(clippedPoints.Max(p => p.X)).OverrideFailureMessage("the supported edge is interpolated").IsEqualApprox(0.025f, 0.000001f);
        AssertFloat(clippedPoints.Min(p => p.X)).OverrideFailureMessage("contact does not squeeze the opposite free edge")
            .IsLess(-0.09f);
    }

    [TestCase]
    public void LiquidContoursFollowCurvedInnerAndOuterBoundaries() {
        const float Inner = 0.037f, Outer = 0.083f;
        var surface = new LiquidSurface();
        surface.Field(new Rect2(-0.1f, -0.1f, 0.2f, 0.2f), 16, 16,
            point => Mathf.Min(point.Length() - Inner, Outer - point.Length()),
            (point, _) => new Vector3(point.X, 0f, point.Y));
        using var mesh = surface.Upload();
        var radii = mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array()
            .Select(point => new Vector2(point.X, point.Z).Length()).ToArray();
        AssertInt(radii.Length).IsGreater(0);
        AssertFloat(radii.Min()).OverrideFailureMessage("liquid does not cross a curved inner obstacle").IsGreaterEqual(Inner - 0.000025f);
        AssertFloat(radii.Max()).OverrideFailureMessage($"liquid stays inside the curved outer support (maximum radius {radii.Max()})").IsLessEqual(Outer + 0.000025f);
        AssertBool(radii.Any(radius => Mathf.Abs(radius - Inner) < 0.0001f)).IsTrue();
        AssertBool(radii.Any(radius => Mathf.Abs(radius - Outer) < 0.0001f)).IsTrue();
    }

    [TestCase]
    public void LiquidRibbonKeepsANarrowBentOpeningConnected() {
        var surface = new LiquidSurface();
        var a = new Vector2(0.011f, 0.007f);
        var corner = new Vector2(0.035f, 0.029f);
        var b = new Vector2(0.017f, 0.054f);
        float Inside(Vector2 p) => 0.0006f - Mathf.Min(p.DistanceTo(Geometry2D.GetClosestPointToSegment(p, a, corner)),
            p.DistanceTo(Geometry2D.GetClosestPointToSegment(p, corner, b)));
        var recordedPath = Enumerable.Range(0, 51).Select(i => a.Lerp(corner, i / 50f))
            .Concat(Enumerable.Range(1, 50).Select(i => corner.Lerp(b, i / 50f))).ToArray();
        surface.Ribbon(recordedPath, 0.005f, Inside, (p, _) => new Vector3(p.X, 0f, p.Y));
        using var mesh = surface.Upload();
        var vertices = mesh.GetFaces();
        AssertInt(vertices.Length).IsGreater(30);
        AssertInt(vertices.Length).OverrideFailureMessage("dense input samples do not produce excessive planar geometry").IsLess(2000);
        bool Covered(Vector2 at) => Enumerable.Range(0, vertices.Length / 3).Any(t => Contains(at,
            new Vector2(vertices[t * 3].X, vertices[t * 3].Z), new Vector2(vertices[t * 3 + 1].X, vertices[t * 3 + 1].Z),
            new Vector2(vertices[t * 3 + 2].X, vertices[t * 3 + 2].Z)));
        foreach (var (from, to) in new[] { (a, corner), (corner, b) }) {
            for (var i = 1; i < 20; i++) {
                AssertBool(Covered(from.Lerp(to, i / 20f)))
                    .OverrideFailureMessage($"the narrow bent pool covers {from.Lerp(to, i / 20f)} on {from} to {to}, step {i}").IsTrue();
            }
        }
        AssertBool(Covered(corner)).OverrideFailureMessage("the original turn remains covered rather than cut off by a chord").IsTrue();
    }

    private static bool Contains(Vector2 p, Vector2 a, Vector2 b, Vector2 c) {
        var area = (b - a).Cross(c - a);
        if (Mathf.Abs(area) < 1e-12f) { return false; }
        var ab = (b - a).Cross(p - a);
        var bc = (c - b).Cross(p - b);
        var ca = (a - c).Cross(p - c);
        return area > 0f ? Mathf.Min(ab, Mathf.Min(bc, ca)) >= -1e-10f : Mathf.Max(ab, Mathf.Max(bc, ca)) <= 1e-10f;
    }

}
