namespace Scalpel.Core;

/// <summary>Where a ray met a <see cref="FineTriangleMesh"/>: the point, the face's own normal and the face's number.
/// </summary>
public readonly record struct MeshHit(Vector3 Position, Vector3 Normal, int Face);

/// <summary>
/// A TriangleMesh that rays don't slip through small faces of.
/// Godot takes a face for parallel to a ray when twice its area across the ray is under 1e-5, so in meters it misses
/// every face under about 3 mm a side: most of the body model's round the lips, nose and fingers.
/// This one holds the faces <see cref="Scale"/> times bigger and scales rays and hits to match.
/// </summary>
public sealed class FineTriangleMesh {
    private const float Scale = 1000f;
    private readonly TriangleMesh _mesh = new();

    /// <summary>Builds the mesh from faces given as three corners each.</summary>
    public void CreateFromFaces(IEnumerable<Vector3> faces) => _mesh.CreateFromFaces([.. faces.Select(p => p * Scale)]);

    /// <summary>The nearest face along <paramref name="direction"/> from <paramref name="from"/>, null for none.</summary>
    public MeshHit? IntersectRay(Vector3 from, Vector3 direction) {
        using var hit = _mesh.IntersectRay(from * Scale, direction);
        return hit.Count == 0
            ? null
            : new MeshHit(hit["position"].AsVector3() / Scale, hit["normal"].AsVector3(), hit["face_index"].AsInt32());
    }
}
