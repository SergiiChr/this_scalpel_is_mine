namespace Scalpel.Tests.Patients;

[TestSuite, RequireGodotRuntime]
[TestCategory("liquids")]
public class BedLiquidSupportTest {
    [TestCase]
    public void StackedFrameSupportsClipIndependentlyAcrossSeamsCornersAndSweptCells() {
        var frame = Surface(Plane(-1f, 1f, -1f, 1f, 0.5f).Concat(Plane(-1f, 1f, -1f, 1f, 0.8f)).ToArray());
        var pads = Surface(Plane(2f, 3f, -1f, 1f, 1f));
        Frames.Tree.Root.AddChild(frame);
        Frames.Tree.Root.AddChild(pads);
        try {
            var bed = new BedLiquidSurface(pads, frame);
            var upperHit = bed.Contact(new Vector3(0f, 1.1f, 0f), new Vector3(0f, 0.4f, 0f));
            var lowerHit = bed.Contact(new Vector3(0f, 0.7f, 0f), new Vector3(0f, 0.4f, 0f));
            AssertBool(upperHit.HasValue && upperHit.Value.Frame).IsTrue();
            AssertBool(lowerHit.HasValue && lowerHit.Value.Frame).IsTrue();
            AssertFloat(upperHit!.Value.At.Y).OverrideFailureMessage("the upper overlapping shelf is hit first").IsEqualApprox(0.8f, 0.00001f);
            AssertFloat(lowerHit!.Value.At.Y).OverrideFailureMessage("a ray starting below the upper shelf reaches the lower shelf").IsEqualApprox(0.5f, 0.00001f);
            AssertBool(upperHit.Value.Support != lowerHit.Value.Support)
                .OverrideFailureMessage("overlapping shelves at the same XZ coordinates remain distinct supports").IsTrue();

            foreach (var (x, z) in new[] { (-0.5f, -0.25f), (0f, 0f), (1f, 1f) }) {
                var hit = bed.Contact(new Vector3(x, 1.1f, z), new Vector3(x, 0.4f, z));
                AssertBool(hit.HasValue).OverrideFailureMessage($"analytic upper-plane intersection exists at seam/corner ({x}, {z})").IsTrue();
                AssertFloat(hit!.Value.At.Y).OverrideFailureMessage("seams and corners keep the exact upper support height").IsEqualApprox(0.8f, 0.00001f);
            }
            var swept = bed.Contact(new Vector3(-1.5f, 1.1f, 0f), new Vector3(1.5f, 0.4f, 0f));
            AssertBool(swept.HasValue).OverrideFailureMessage("a long descending ray crossing many contact-grid cells still hits the shelf").IsTrue();
            var crossing = (1.1f - 0.8f) / (1.1f - 0.4f);
            AssertFloat(swept!.Value.At.X).OverrideFailureMessage("the swept hit follows the analytic segment-plane crossing").IsEqualApprox(-1.5f + 3f * crossing, 0.00002f);
            AssertFloat(swept.Value.At.Y).IsEqualApprox(0.8f, 0.00001f);

            var outline = LiquidSurface.Outline(0.3f, 0.37f);
            var expectedArea = LiquidSurface.Area(outline);
            var upper = new LiquidSurface();
            var lower = new LiquidSurface();
            var upperArea = bed.CoverFrame(outline, new Vector3(0f, 0.8f, 0f), upper, upperHit.Value.Support);
            var lowerArea = bed.CoverFrame(outline, new Vector3(0f, 0.5f, 0f), lower, lowerHit.Value.Support);
            AssertFloat(upperArea).OverrideFailureMessage("the upper pool covers its own projected outline once").IsEqualApprox(expectedArea, 0.00002f);
            AssertFloat(lowerArea).OverrideFailureMessage("the lower pool covers its own projected outline once").IsEqualApprox(expectedArea, 0.00002f);
            AssertBool(upper.Vertices.All(p => Mathf.Abs(p.Y - BedLiquidSurface.FilmDepth) < 0.00001f))
                .OverrideFailureMessage("upper vertices stay at the upper support plus film lift").IsTrue();
            AssertBool(lower.Vertices.All(p => Mathf.Abs(p.Y - BedLiquidSurface.FilmDepth) < 0.00001f))
                .OverrideFailureMessage("lower vertices stay at the lower support plus film lift").IsTrue();
        } finally {
            frame.Free();
            pads.Free();
        }
    }

    private static Vector3[] Plane(float left, float right, float near, float far, float y) {
        var a = new Vector3(left, y, near);
        var b = new Vector3(right, y, near);
        var c = new Vector3(right, y, far);
        var d = new Vector3(left, y, far);
        return [a, b, c, a, c, d];
    }

    private static MeshInstance3D Surface(Vector3[] vertices) {
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        Shapes.SetPacked(arrays, Mesh.ArrayType.Vertex, vertices);
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return new MeshInstance3D { Mesh = mesh };
    }
}
