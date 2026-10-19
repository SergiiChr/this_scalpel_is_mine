namespace Scalpel.Visuals;

/// <summary>Continuous liquid footprints. A boundary cuts the free outline instead of squeezing the whole pool;
/// sampled wound boundaries interpolate their crossings rather than dropping complete grid squares.</summary>
internal sealed class LiquidSurface {
    private readonly List<Vector3> _vertices = [];
    private readonly List<Vector3> _normals = [];
    private readonly record struct Sample(Vector2 At, float Inside, Vector3 Vertex);

    internal static Vector2[] Outline(float radius, float phase) {
        const int Count = 64;
        var points = new Vector2[Count];
        for (var i = 0; i < Count; i++) {
            var angle = Mathf.Tau * i / Count;
            var edge = 1f + 0.065f * Mathf.Sin(angle * 3f + phase) + 0.045f * Mathf.Sin(angle * 5f - phase * 1.7f)
                + 0.02f * Mathf.Sin(angle * 9f + phase * 0.7f);
            points[i] = Vector2.FromAngle(angle) * edge;
        }
        // Area is the volume proxy used by BloodFlow. Shape variation must not create or erase blood.
        var scale = radius / Mathf.Sqrt(Area(points));
        for (var i = 0; i < Count; i++) { points[i] *= scale; }
        return points;
    }

    /// <summary>Footprint area divided by pi, matching the former radius-squared volume unit.</summary>
    internal static float Area(IReadOnlyList<Vector2> points) {
        var twice = 0f;
        for (var i = 0; i < points.Count; i++) {
            twice += points[i].Cross(points[(i + 1) % points.Count]);
        }
        return Mathf.Abs(twice) / (2f * Mathf.Pi);
    }

    internal static Vector2[] Clip(IReadOnlyList<Vector2> outline, Rect2 support) {
        var points = outline.ToList();
        for (var edge = 0; edge < 4; edge++) {
            var next = new List<Vector2>(points.Count + 2);
            for (var i = 0; i < points.Count; i++) {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                var da = Margin(a, edge, support);
                var db = Margin(b, edge, support);
                if (da >= 0f) { next.Add(a); }
                if ((da >= 0f) != (db >= 0f)) { next.Add(a.Lerp(b, da / (da - db))); }
            }
            points = next;
        }
        return [.. points];
    }

    private static float Margin(Vector2 p, int edge, Rect2 box) => edge switch {
        0 => p.X - box.Position.X,
        1 => box.End.X - p.X,
        2 => p.Y - box.Position.Y,
        _ => box.End.Y - p.Y,
    };

    internal void Patch(IReadOnlyList<Vector2> outline, float depth) {
        if (outline.Count < 3) { return; }
        var center = new Vector3(0f, depth * 0.65f, 0f);
        for (var i = 0; i < outline.Count; i++) {
            var a = outline[i];
            var b = outline[(i + 1) % outline.Count];
            var innerA = new Vector3(a.X * 0.88f, depth * 0.65f, a.Y * 0.88f);
            var innerB = new Vector3(b.X * 0.88f, depth * 0.65f, b.Y * 0.88f);
            var outerA = new Vector3(a.X, depth * 0.15f, a.Y);
            var outerB = new Vector3(b.X, depth * 0.15f, b.Y);
            Triangle(center, innerA, innerB);
            var na = new Vector3(a.X, 0f, a.Y).Normalized() * Mathf.Clamp(depth * 0.5f / Mathf.Max(a.Length() * 0.12f, 0.0001f), 0.08f, 0.8f);
            var nb = new Vector3(b.X, 0f, b.Y).Normalized() * Mathf.Clamp(depth * 0.5f / Mathf.Max(b.Length() * 0.12f, 0.0001f), 0.08f, 0.8f);
            na = (na + Vector3.Up).Normalized();
            nb = (nb + Vector3.Up).Normalized();
            Triangle(innerA, outerA, outerB, Vector3.Up, na, nb);
            Triangle(innerA, outerB, innerB, Vector3.Up, nb, Vector3.Up);
        }
    }

    internal void Field(Rect2 bounds, int columns, int rows, Func<Vector2, float> inside, Func<Vector2, float, Vector3> position) {
        var samples = new Sample[(columns + 1) * (rows + 1)];
        for (var y = 0; y <= rows; y++) {
            for (var x = 0; x <= columns; x++) {
                var at = bounds.Position + bounds.Size * new Vector2((float)x / columns, (float)y / rows);
                var clearance = inside(at);
                samples[y * (columns + 1) + x] = new Sample(at, clearance, position(at, clearance));
            }
        }
        for (var y = 0; y < rows; y++) {
            for (var x = 0; x < columns; x++) {
                var i = y * (columns + 1) + x;
                var a = samples[i];
                var b = samples[i + 1];
                var c = samples[i + columns + 2];
                var d = samples[i + columns + 1];
                ClippedTriangle(a, b, c);
                ClippedTriangle(a, c, d);
            }
        }
    }

    /// <summary>Sample an incision along its own centreline. Even a narrow diagonal opening keeps an inside row;
    /// a world-aligned coarse grid could miss the liquid entirely between its vertices.</summary>
    internal void Ribbon(IReadOnlyList<Vector2> path, float reach, Func<Vector2, float> inside, Func<Vector2, float, Vector3> position) {
        var points = Simplify(path);
        if (points.Length < 2) { return; }
        points[0] -= (points[1] - points[0]).Normalized() * 0.004f;
        points[^1] += (points[^1] - points[^2]).Normalized() * 0.004f;
        var centers = new List<Vector2>();
        for (var i = 1; i < points.Length; i++) {
            var steps = Math.Clamp(Mathf.CeilToInt(points[i].DistanceTo(points[i - 1]) / 0.004f), 1, 256);
            for (var step = 0; step < steps; step++) { centers.Add(points[i - 1].Lerp(points[i], (float)step / steps)); }
        }
        centers.Add(points[^1]);
        var columns = centers.Count - 1;
        var rows = Math.Clamp(Mathf.CeilToInt(reach * 2f / 0.004f), 8, 32);
        rows += rows % 2;
        var normals = new Vector2[centers.Count];
        for (var x = 0; x <= columns; x++) {
            var before = (centers[x] - centers[Math.Max(x - 1, 0)]).Normalized();
            var after = (centers[Math.Min(x + 1, columns)] - centers[x]).Normalized();
            if (x == 0) { before = after; }
            if (x == columns) { after = before; }
            var outgoing = new Vector2(-after.Y, after.X);
            var miter = new Vector2(-before.Y - after.Y, before.X + after.X).Normalized();
            // Both adjoining strips share this cross-section. Limit an almost-reversing turn's miter.
            normals[x] = miter / Mathf.Max(miter.Dot(outgoing), 0.3f);
        }
        Vector2 At(Vector2 p) {
            var column = Math.Clamp(Mathf.RoundToInt(p.X), 0, columns);
            return centers[column] + normals[column] * p.Y;
        }
        Field(new Rect2(0f, -reach, columns, reach * 2f), columns, rows, p => inside(At(p)), (p, clearance) => position(At(p), clearance));
    }

    private static Vector2[] Simplify(IReadOnlyList<Vector2> path) {
        if (path.Count < 3) { return path.ToArray(); }
        // Cuts record many almost-collinear input samples. Keep actual turns while discarding points whose
        // omission changes the path by less than a tenth of a millimetre.
        var keep = new bool[path.Count];
        keep[0] = keep[^1] = true;
        var pending = new Stack<(int First, int Last)>();
        pending.Push((0, path.Count - 1));
        while (pending.TryPop(out var span)) {
            var a = path[span.First];
            var direction = path[span.Last] - a;
            var lengthSquared = direction.LengthSquared();
            var farthest = -1;
            var distanceSquared = 0.0001f * 0.0001f;
            for (var i = span.First + 1; i < span.Last; i++) {
                var fraction = lengthSquared > 0f ? Mathf.Clamp((path[i] - a).Dot(direction) / lengthSquared, 0f, 1f) : 0f;
                var distance = path[i].DistanceSquaredTo(a + direction * fraction);
                if (distance <= distanceSquared) { continue; }
                distanceSquared = distance;
                farthest = i;
            }
            if (farthest < 0) { continue; }
            keep[farthest] = true;
            pending.Push((span.First, farthest));
            pending.Push((farthest, span.Last));
        }
        return path.Where((_, i) => keep[i]).ToArray();
    }

    private void ClippedTriangle(Sample a, Sample b, Sample c) {
        Span<Sample> input = stackalloc Sample[] { a, b, c };
        Span<Vector3> output = stackalloc Vector3[4];
        var count = 0;
        for (var i = 0; i < 3; i++) {
            var from = input[i];
            var to = input[(i + 1) % 3];
            if (from.Inside >= 0f) { output[count++] = from.Vertex; }
            if ((from.Inside >= 0f) != (to.Inside >= 0f)) {
                output[count++] = from.Vertex.Lerp(to.Vertex, from.Inside / (from.Inside - to.Inside));
            }
        }
        for (var i = 1; i + 1 < count; i++) {
            Triangle(output[0], output[i], output[i + 1]);
        }
    }

    internal void Facet(Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc) => Triangle(a, b, c, na, nb, nc);

    private void Triangle(Vector3 a, Vector3 b, Vector3 c) => Triangle(a, b, c, Vector3.Up, Vector3.Up, Vector3.Up);

    private void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc) {
        _vertices.Add(a);
        _vertices.Add(b);
        _vertices.Add(c);
        _normals.Add(na);
        _normals.Add(nb);
        _normals.Add(nc);
    }

    internal static ArrayMesh Drop() {
        using var sphere = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 8, Rings = 6 };
        using var arrays = sphere.GetMeshArrays();
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var normals = new Vector3[vertices.Length];
        for (var i = 0; i < vertices.Length; i++) {
            var p = vertices[i];
            var width = 0.78f - 0.28f * p.Y;
            p.X *= width;
            p.Z *= width;
            vertices[i] = p;
            normals[i] = new Vector3(p.X / (width * width), p.Y + 0.28f * (p.X * p.X + p.Z * p.Z) / (width * width * width),
                p.Z / (width * width)).Normalized();
        }
        Shapes.SetPacked(arrays, Mesh.ArrayType.Vertex, vertices);
        Shapes.SetPacked(arrays, Mesh.ArrayType.Normal, normals);
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    internal ArrayMesh Upload(ArrayMesh? into = null) {
        var mesh = into ?? new ArrayMesh();
        mesh.ClearSurfaces();
        if (_vertices.Count == 0) { return mesh; }
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        Shapes.SetPacked(arrays, Mesh.ArrayType.Vertex, _vertices.ToArray());
        Shapes.SetPacked(arrays, Mesh.ArrayType.Normal, _normals.ToArray());
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
