namespace Scalpel.Tests.Models;

/// <summary>Tubes (sutures, staples, IV tubing) are built from plain arrays, without a call into the engine per vertex.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
public partial class TubeTest
{
    private static readonly Vector3[] Bent = [new(0f, 0f, 0f), new(0.02f, 0.005f, 0f), new(0.03f, 0.01f, 0.02f)];

    [TestCase]
    public void NormalsMatchTheEngines()
    {
        var tube = Shapes.Tube(Bent, 0.002f, 0.001f, 6);
        var arrays = tube.SurfaceGetArrays(0);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var vertex in vertices)
        {
            surface.AddVertex(vertex);
        }
        surface.GenerateNormals();
        var expected = surface.Commit().SurfaceGetArrays(0)[(int)Mesh.ArrayType.Normal].AsVector3Array();
        AssertInt(normals.Length).OverrideFailureMessage("a normal per vertex").IsEqual(expected.Length);
        for (var i = 0; i < normals.Length; i++)
        {
            AssertFloat(normals[i].DistanceTo(expected[i]))
                .OverrideFailureMessage($"vertex {i}: normal {normals[i]}, SurfaceTool's {expected[i]}").IsLess(1e-5f);
        }
    }

    [TestCase]
    public void RebuildRefillsTheSameMesh()
    {
        var tube = Shapes.Tube(Bent, 0.002f, 0.002f, 6);
        var refilled = Shapes.Tube([.. Bent.Select(point => point + Vector3.Up)], 0.002f, 0.002f, 6, tube);
        AssertObject(refilled).OverrideFailureMessage("the mesh passed in is refilled").IsSame(tube);
        AssertInt(refilled.GetSurfaceCount()).OverrideFailureMessage("the old surface is replaced").IsEqual(1);
        AssertFloat(refilled.GetAabb().Position.Y).OverrideFailureMessage("it holds the new path").IsGreater(0.9f);
    }
}
