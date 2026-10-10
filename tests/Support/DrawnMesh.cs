namespace Scalpel.Tests.Support;

/// <summary>Where a mesh's vertices are drawn (world): its rest positions moved by its blend shapes' weights, a film bag
/// lying flat say. Bones are taken at rest.</summary>
public static class DrawnMesh
{
    public static IEnumerable<Vector3> Vertices(MeshInstance3D instance)
    {
        var transform = instance.GlobalTransform;
        if (instance.Mesh is not ArrayMesh mesh || mesh.GetBlendShapeCount() == 0)
        {
            return instance.Mesh.GetFaces().Select(vertex => transform * vertex);
        }
        var weights = Enumerable.Range(0, mesh.GetBlendShapeCount()).Select(instance.GetBlendShapeValue).ToArray();
        return Enumerable.Range(0, mesh.GetSurfaceCount()).SelectMany(surface =>
        {
            var rest = mesh.SurfaceGetArrays(surface)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var shapes = mesh.SurfaceGetBlendShapeArrays(surface)
                .Select(shape => shape[(int)Mesh.ArrayType.Vertex].AsVector3Array()).ToArray();
            // Each shape holds where every vertex goes at full weight.
            return rest.Select((vertex, i) =>
                transform * shapes.Select((shape, s) => (shape[i] - vertex) * weights[s]).Aggregate(vertex, (at, move) => at + move));
        });
    }
}
