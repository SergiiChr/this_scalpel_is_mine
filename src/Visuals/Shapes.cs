using System.Runtime.InteropServices;

namespace Scalpel.Visuals;

/// <summary>
/// Room architecture (walls, floors), invisible colliders, 3D labels and tubes along a path (bones, veins, tubing).
/// Everything else is a model.
/// </summary>
public static class Shapes {
    public static MeshInstance3D Slab(Node3D parent, Vector3 size, Color color, Vector3 position, float grime = 0.35f,
        Pattern pattern = Pattern.None) =>
        Slab(parent, size, Materials.RoomSurface(color, grime, pattern), position);

    public static MeshInstance3D Slab(Node3D parent, Vector3 size, Material material, Vector3 position) {
        var instance = new MeshInstance3D {
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = material,
            Position = position,
        };
        parent.AddChild(instance);
        return instance;
    }

    public static StaticBody3D StaticBox(Node3D parent, Vector3 size, Vector3 position, uint layer = 1) {
        var body = new StaticBody3D { CollisionLayer = layer, CollisionMask = 0, Position = position };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        parent.AddChild(body);
        return body;
    }

    public static Label3D Label(Node3D parent, string text, Vector3 position, int size = 32) {
        var label = new Label3D {
            Text = text,
            FontSize = size,
            PixelSize = 0.001f,
            Position = position,
            Modulate = new Color(0.75f, 0.9f, 0.78f),
        };
        parent.AddChild(label);
        return label;
    }

    /// <summary>
    /// A closed tube with an elliptic cross section (width across the path, height up) along a path (local space; the
    /// cross section keeps +Y as up where it can). Fine tubes can use fewer sides.
    /// </summary>
    public static ArrayMesh Tube(IReadOnlyList<Vector3> path, float width, float height, int sides = 12,
        ArrayMesh? into = null) =>
        Tubes([path], width, height, sides, into);

    /// <summary>
    /// Several disconnected tubes in one mesh. A routed suture has multiple visible spans, but rebuilding and
    /// submitting one fine mesh is substantially cheaper than a mesh and MeshInstance3D for every span.
    /// <paramref name="into"/>: a mesh to refill instead of making a new one, for tubes rebuilt while the game runs.
    /// A replaced mesh keeps its GPU buffers until the garbage collector gets to its wrapper.
    /// Staples, sutures and tape are redrawn this way on every frame the skin moves, so it's built in buffers reused
    /// from call to call: fresh ones came to about 40 KB per tube, enough garbage for a long collection every few
    /// seconds.
    /// </summary>
    public static ArrayMesh Tubes(IEnumerable<IReadOnlyList<Vector3>> paths, float width, float height, int sides = 12,
        ArrayMesh? into = null) {
        var built = _tubes ??= new TubeBuffers();
        built.Vertices.Clear();
        built.Normals.Clear();
        foreach (var path in paths) {
            AppendTube(built, path, width, height, sides);
        }
        var mesh = into ?? new ArrayMesh();
        mesh.ClearSurfaces();
        if (built.Vertices.Count == 0) {
            return mesh;
        }
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        SetPacked(arrays, Mesh.ArrayType.Vertex, CollectionsMarshal.AsSpan(built.Vertices));
        SetPacked(arrays, Mesh.ArrayType.Normal, CollectionsMarshal.AsSpan(built.Normals));
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>What Tubes() builds a mesh in, kept for the next call. Per thread, should one build off the main
    /// thread.</summary>
    [ThreadStatic] private static TubeBuffers? _tubes;

    private sealed class TubeBuffers {
        public readonly List<Vector3> Vertices = [];
        public readonly List<Vector3> Normals = [];
        public readonly List<int> Triangles = [];
        public Vector3[] Points = [];
        public Vector3[] Sums = [];
    }

    /// <summary>Puts <paramref name="values"/> in a mesh's arrays as a packed array. The engine copies it in through
    /// a Variant held by a tracked wrapper with a finalizer, disposed here at once: a mesh rebuilt every frame (a
    /// thread being sewn, tissue being cut) would otherwise leave thousands of them for one long collection. The
    /// arrays themselves are the same kind of wrapper: dispose them once the surface is added.</summary>
    public static void SetPacked<[MustBeVariant] T>(Godot.Collections.Array arrays, Mesh.ArrayType slot, T[] values) {
        using var packed = Variant.From(values);
        arrays[(int)slot] = packed;
    }

    /// <summary>SetPacked() for part of a reused buffer.</summary>
    internal static void SetPacked(Godot.Collections.Array arrays, Mesh.ArrayType slot, Span<Vector3> values) {
        using var packed = Variant.CreateFrom(values);
        arrays[(int)slot] = packed;
    }

    /// <inheritdoc cref="SetPacked(Godot.Collections.Array, Mesh.ArrayType, Span{Vector3})"/>
    internal static void SetPacked(Godot.Collections.Array arrays, Mesh.ArrayType slot, Span<Vector2> values) {
        using var packed = Variant.CreateFrom(values);
        arrays[(int)slot] = packed;
    }

    /// <inheritdoc cref="SetPacked(Godot.Collections.Array, Mesh.ArrayType, Span{Vector3})"/>
    internal static void SetPacked(Godot.Collections.Array arrays, Mesh.ArrayType slot, Span<int> values) {
        using var packed = Variant.CreateFrom(values);
        arrays[(int)slot] = packed;
    }

    /// <inheritdoc cref="SetPacked(Godot.Collections.Array, Mesh.ArrayType, Span{Vector3})"/>
    internal static void SetPacked(Godot.Collections.Array arrays, Mesh.ArrayType slot, Span<Color> values) {
        using var packed = Variant.CreateFrom(values);
        arrays[(int)slot] = packed;
    }

    /// <summary>The tube's triangles, each vertex's normal the average of the faces meeting at its point, as
    /// SurfaceTool.GenerateNormals() gives: summed by the ring vertex's index, which the point's position would
    /// need a dictionary for.</summary>
    private static void AppendTube(TubeBuffers built, IReadOnlyList<Vector3> path, float width, float height, int sides) {
        var count = path.Count;
        // Points: the rings' vertices, then the two end caps' centers.
        var pointCount = count * sides + 2;
        if (built.Points.Length < pointCount) {
            built.Points = new Vector3[pointCount];
            built.Sums = new Vector3[pointCount];
        }
        var points = built.Points;
        for (var i = 0; i < count; i++) {
            var along = (path[Math.Min(i + 1, count - 1)] - path[Math.Max(i - 1, 0)]).Normalized();
            var side = along.Cross(Vector3.Up).Normalized();
            if (side.LengthSquared() < 0.5f) {
                side = Vector3.Right;
            }
            var up = side.Cross(along).Normalized();
            for (var k = 0; k < sides; k++) {
                var angle = Mathf.Tau * k / sides;
                points[i * sides + k] = path[i] + side * Mathf.Cos(angle) * width + up * Mathf.Sin(angle) * height;
            }
        }
        var first = count * sides;
        var last = first + 1;
        points[first] = path[0];
        points[last] = path[count - 1];
        var triangles = built.Triangles;
        triangles.Clear();
        void Add(int a, int b, int c) {
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
        }
        for (var i = 1; i < count; i++) {
            var previous = (i - 1) * sides;
            var ring = i * sides;
            for (var k = 0; k < sides; k++) {
                var n = (k + 1) % sides;
                // Clockwise seen from outside: Godot's front faces.
                Add(previous + k, ring + n, ring + k);
                Add(previous + k, previous + n, ring + n);
            }
        }
        for (var k = 0; k < sides; k++) {
            var n = (k + 1) % sides;
            Add(first, n, k);
            Add((count - 1) * sides + k, (count - 1) * sides + n, last);
        }
        var sums = built.Sums;
        Array.Clear(sums, 0, pointCount);
        for (var t = 0; t < triangles.Count; t += 3) {
            var normal = new Plane(points[triangles[t]], points[triangles[t + 1]], points[triangles[t + 2]]).Normal;
            sums[triangles[t]] += normal;
            sums[triangles[t + 1]] += normal;
            sums[triangles[t + 2]] += normal;
        }
        foreach (var v in triangles) {
            built.Vertices.Add(points[v]);
            built.Normals.Add(sums[v].Normalized());
        }
    }
}
