namespace Scalpel.Tests.Patients;

[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("liquids")]
public class BedLiquidSupportTest {
    [TestCase]
    public void StackedFrameShelvesAreHitTopFirstAcrossSeamsCornersAndSweptCells() {
        var frame = Surface(Plane(-1f, 1f, -1f, 1f, 0.5f).Concat(Plane(-1f, 1f, -1f, 1f, 0.8f)).ToArray());
        var pads = Surface(Plane(2f, 3f, -1f, 1f, 1f));
        Frames.Tree.Root.AddChild(frame);
        Frames.Tree.Root.AddChild(pads);
        try {
            var bed = new BedLiquidSurface(pads, frame);
            var upperHit = bed.Landing(new Vector3(0f, 1.1f, 0f), new Vector3(0f, 0.4f, 0f));
            var lowerHit = bed.Landing(new Vector3(0f, 0.7f, 0f), new Vector3(0f, 0.4f, 0f));
            AssertBool(upperHit.HasValue && lowerHit.HasValue).OverrideFailureMessage("a drop over the frame's shelves lands on them").IsTrue();
            AssertFloat(upperHit!.Value.Y).OverrideFailureMessage("the upper overlapping shelf is hit first").IsEqualApprox(0.8f, 0.00001f);
            AssertFloat(lowerHit!.Value.Y).OverrideFailureMessage("a ray starting below the upper shelf reaches the lower shelf").IsEqualApprox(0.5f, 0.00001f);

            foreach (var (x, z) in new[] { (-0.5f, -0.25f), (0f, 0f), (1f, 1f) }) {
                var hit = bed.Landing(new Vector3(x, 1.1f, z), new Vector3(x, 0.4f, z));
                AssertBool(hit.HasValue).OverrideFailureMessage($"analytic upper-plane intersection exists at seam/corner ({x}, {z})").IsTrue();
                AssertFloat(hit!.Value.Y).OverrideFailureMessage("seams and corners keep the exact upper support height").IsEqualApprox(0.8f, 0.00001f);
            }
            var swept = bed.Landing(new Vector3(-1.5f, 1.1f, 0f), new Vector3(1.5f, 0.4f, 0f));
            AssertBool(swept.HasValue).OverrideFailureMessage("a long descending ray crossing many contact-grid cells still hits the shelf").IsTrue();
            var crossing = (1.1f - 0.8f) / (1.1f - 0.4f);
            AssertFloat(swept!.Value.X).OverrideFailureMessage("the swept hit follows the analytic segment-plane crossing").IsEqualApprox(-1.5f + 3f * crossing, 0.00002f);
            AssertFloat(swept.Value.Y).IsEqualApprox(0.8f, 0.00001f);

            AssertInt(bed.Triangles.Length).OverrideFailureMessage("the bed's film covers every pad and frame triangle")
                .IsEqual(18);
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
