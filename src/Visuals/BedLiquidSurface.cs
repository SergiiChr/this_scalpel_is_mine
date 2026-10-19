namespace Scalpel.Visuals;

/// <summary>The actual upper mattress triangles, shared by liquid contact, wet meshes and runoff outlets.
/// Using the imported mesh keeps rounded corners and the gaps between separate pads out of the footprint.</summary>
internal sealed class BedLiquidSurface {
    private readonly record struct Face(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal, Rect2 Bounds, Vector3 Na, Vector3 Nb, Vector3 Nc);
    private readonly List<Face> _faces = [];
    private readonly List<(Vector3 A, Vector3 B)> _edges = [];
    private readonly List<(Vector2[] Outline, Rect2 Bounds)> _supports = [];
    private const float Lift = 0.0007f;

    internal BedLiquidSurface(MeshInstance3D pads) {
        var edges = new Dictionary<(Vector3, Vector3), int>();
        var normals = new Dictionary<Vector3, Vector3>();
        var vertices = pads.Mesh.GetFaces();
        for (var i = 0; i < vertices.Length; i += 3) {
            var a = pads.GlobalTransform * vertices[i];
            var b = pads.GlobalTransform * vertices[i + 1];
            var c = pads.GlobalTransform * vertices[i + 2];
            var normal = (c - a).Cross(b - a).Normalized();
            if (normal.Y <= 0.001f) { continue; }
            var bounds = new Rect2(Xz(a), Vector2.Zero).Expand(Xz(b)).Expand(Xz(c));
            _faces.Add(new Face(a, b, c, normal, bounds, Vector3.Zero, Vector3.Zero, Vector3.Zero));
            var weighted = (c - a).Cross(b - a);
            normals[a] = normals.GetValueOrDefault(a) + weighted;
            normals[b] = normals.GetValueOrDefault(b) + weighted;
            normals[c] = normals.GetValueOrDefault(c) + weighted;
            Edge(a, b); Edge(b, c); Edge(c, a);
        }
        for (var i = 0; i < _faces.Count; i++) {
            var face = _faces[i];
            _faces[i] = face with { Na = normals[face.A].Normalized(), Nb = normals[face.B].Normalized(), Nc = normals[face.C].Normalized() };
        }
        foreach (var (edge, count) in edges) { if (count == 1) { _edges.Add(edge); } }
        var remaining = _edges.SelectMany(edge => new[] { edge.A, edge.B }).ToHashSet();
        while (remaining.Count > 0) {
            var group = new List<Vector2>();
            var pending = new Queue<Vector3>();
            pending.Enqueue(remaining.First());
            while (pending.TryDequeue(out var vertex)) {
                if (!remaining.Remove(vertex)) { continue; }
                group.Add(Xz(vertex));
                foreach (var (a, b) in _edges) {
                    if (a == vertex) { pending.Enqueue(b); }
                    if (b == vertex) { pending.Enqueue(a); }
                }
            }
            var hull = Geometry2D.ConvexHull(group.ToArray());
            if (hull.Length > 1 && hull[0] == hull[^1]) { hull = hull[..^1]; }
            var winding = 0f;
            for (var i = 1; i + 1 < hull.Length; i++) { winding += (hull[i] - hull[0]).Cross(hull[i + 1] - hull[0]); }
            if (winding < 0f) { Array.Reverse(hull); }
            var bounds = new Rect2(hull[0], Vector2.Zero);
            foreach (var point in hull) { bounds = bounds.Expand(point); }
            _supports.Add((hull, bounds));
        }



        void Edge(Vector3 a, Vector3 b) {
            // Imported vertices at a material seam can differ only by floating-point roundoff.
            a = a.Snapped(Vector3.One * 0.000001f);
            b = b.Snapped(Vector3.One * 0.000001f);
            var swap = a.X > b.X || (a.X == b.X && (a.Z > b.Z || (a.Z == b.Z && a.Y > b.Y)));
            var key = swap ? (b, a) : (a, b);
            edges[key] = edges.GetValueOrDefault(key) + 1;
        }
    }

    private static Vector2 Xz(Vector3 p) => new(p.X, p.Z);
    private static float Height(Face face, Vector2 at) => face.A.Y
        - (face.Normal.X * (at.X - face.A.X) + face.Normal.Z * (at.Y - face.A.Z)) / face.Normal.Y;

    internal float? HeightAt(Vector2 at) {
        foreach (var face in _faces) {
            if (!face.Bounds.Grow(0.000003f).HasPoint(at)) { continue; }
            var a = Xz(face.A); var b = Xz(face.B); var c = Xz(face.C);
            if ((b - a).Cross(at - a) >= -0.000003f * (b - a).Length() && (c - b).Cross(at - b) >= -0.000003f * (c - b).Length()
                && (a - c).Cross(at - c) >= -0.000003f * (a - c).Length()) { return Height(face, at); }
        }
        return null;
    }

    internal Vector3? Landing(Vector3 from, Vector3 to) {
        var motion = to - from;
        if (motion.Y >= 0f) { return null; }
        var bounds = new Rect2(Xz(from), Vector2.Zero).Expand(Xz(to)).Grow(0.000001f);
        foreach (var face in _faces) {
            if (!face.Bounds.Intersects(bounds)) { continue; }
            var start = face.Normal.Dot(from - face.A);
            var end = face.Normal.Dot(to - face.A);
            if (start < 0f || end > 0f) { continue; }
            var hit = from.Lerp(to, start / (start - end));
            var p = Xz(hit); var a = Xz(face.A); var b = Xz(face.B); var c = Xz(face.C);
            if ((b - a).Cross(p - a) >= -0.00000001f && (c - b).Cross(p - b) >= -0.00000001f
                && (a - c).Cross(p - c) >= -0.00000001f) { return hit; }
        }
        return null;
    }

    internal float Area(IReadOnlyList<Vector2> outline, Vector3 center) {
        Span<Vector2> first = stackalloc Vector2[256];
        Span<Vector2> second = stackalloc Vector2[256];
        var area = 0f;
        var bounds = new Rect2(outline[0] + Xz(center), Vector2.Zero);
        foreach (var point in outline) { bounds = bounds.Expand(point + Xz(center)); }
        foreach (var (support, supportBounds) in _supports) {
            if (!supportBounds.Intersects(bounds)) { continue; }
            for (var i = 0; i < outline.Count; i++) { first[i] = outline[i] + Xz(center); }
            var count = outline.Count;
            for (var i = 0; i < support.Length && count > 0; i++) {
                count = Clip(first[..count], second, support[i], support[(i + 1) % support.Length]);
                second[..count].CopyTo(first);
            }
            var signed = 0f;
            for (var i = 1; i + 1 < count; i++) { signed += (first[i] - first[0]).Cross(first[i + 1] - first[0]); }
            area += Mathf.Abs(signed) / (2f * Mathf.Pi);
        }
        return area;
    }

    internal float Cover(IReadOnlyList<Vector2> outline, Vector3 center, LiquidSurface? surface = null) {
        var world = outline.Select(p => p + Xz(center)).ToArray();
        var bounds = new Rect2(world[0], Vector2.Zero);
        foreach (var point in world) { bounds = bounds.Expand(point); }
        // Every radial triangle is convex even though the complete organic footprint is concave.
        // Stack buffers keep its exact triangle intersections allocation-free, without native clipper snapping.
        var origin = Xz(center);
        var sources = new (Vector2 A, Vector2 B, Rect2 Bounds)[world.Length];
        for (var i = 0; i < world.Length; i++) {
            var a = world[i]; var b = world[(i + 1) % world.Length];
            sources[i] = (a, b, new Rect2(origin, Vector2.Zero).Expand(a).Expand(b));
        }
        Span<Vector2> first = stackalloc Vector2[8];
        Span<Vector2> second = stackalloc Vector2[8];
        var area = 0f;
        foreach (var face in _faces) {
            if (!face.Bounds.Intersects(bounds)) { continue; }
            foreach (var (a, b, sourceBounds) in sources) {
                if (!face.Bounds.Intersects(sourceBounds)) { continue; }
                first[0] = origin; first[1] = a; first[2] = b;
                var count = Clip(first[..3], second, Xz(face.A), Xz(face.B));
                count = Clip(second[..count], first, Xz(face.B), Xz(face.C));
                count = Clip(first[..count], second, Xz(face.C), Xz(face.A));
                for (var i = 1; i + 1 < count; i++) {
                    var p = second[0]; var q = second[i]; var t = second[i + 1];
                    area += Mathf.Abs((q - p).Cross(t - p)) / (2f * Mathf.Pi);
                    if (surface is null) { continue; }
                    Vector3 Position(Vector2 point) => new Vector3(point.X, Height(face, point) + Lift, point.Y) - center;
                    surface.Facet(Position(p), Position(q), Position(t), Normal(face, p), Normal(face, q), Normal(face, t));
                }
            }
        }
        return area;
    }

    private static Vector3 Normal(Face face, Vector2 at) {
        var b = Xz(face.B - face.A); var c = Xz(face.C - face.A); var offset = at - Xz(face.A);
        var denominator = b.Cross(c);
        var u = offset.Cross(c) / denominator;
        var v = b.Cross(offset) / denominator;
        return (face.Na * (1f - u - v) + face.Nb * u + face.Nc * v).Normalized();
    }

    private static int Clip(ReadOnlySpan<Vector2> input, Span<Vector2> output, Vector2 a, Vector2 b) {
        var count = 0;
        var edge = b - a;
        for (var i = 0; i < input.Length; i++) {
            var p = input[i]; var q = input[(i + 1) % input.Length];
            var dp = edge.Cross(p - a); var dq = edge.Cross(q - a);
            if (dp >= 0f) { output[count++] = p; }
            if ((dp >= 0f) != (dq >= 0f)) { output[count++] = p.Lerp(q, dp / (dp - dq)); }
        }
        return count;
    }

    private static bool Inside(Vector2 p, Vector2[] polygon) {
        var inside = false;
        for (var i = 0; i < polygon.Length; i++) {
            var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) { inside = !inside; }
        }
        return inside;
    }

    internal Vector3[] Outlets(IReadOnlyList<Vector2> outline, Vector3 center) {
        var polygon = outline.ToArray();
        var bounds = new Rect2(polygon[0] + Xz(center), Vector2.Zero);
        foreach (var point in polygon) { bounds = bounds.Expand(point + Xz(center)); }
        var wet = new List<(Vector3 A, Vector3 B)>();
        var length = 0f;
        foreach (var (a, b) in _edges) {
            if (Mathf.Max(a.X, b.X) < bounds.Position.X || Mathf.Min(a.X, b.X) > bounds.End.X
                || Mathf.Max(a.Z, b.Z) < bounds.Position.Y || Mathf.Min(a.Z, b.Z) > bounds.End.Y) { continue; }
            var crossings = new List<float> { 0f, 1f };
            var direction = Xz(b - a);
            for (var i = 0; i < outline.Count; i++) {
                var p = outline[i] + Xz(center); var q = outline[(i + 1) % outline.Count] + Xz(center);
                var edge = q - p;
                var denominator = direction.Cross(edge);
                if (Mathf.Abs(denominator) < 0.000000001f) { continue; }
                var t = (p - Xz(a)).Cross(edge) / denominator;
                var u = (p - Xz(a)).Cross(direction) / denominator;
                if (t > 0f && t < 1f && u >= 0f && u <= 1f) { crossings.Add(t); }
            }
            crossings.Sort();
            for (var i = 1; i < crossings.Count; i++) {
                var from = crossings[i - 1]; var to = crossings[i];
                var middle = Xz(a.Lerp(b, (from + to) * 0.5f)) - Xz(center);
                if (!Inside(middle, polygon)) { continue; }
                var start = a.Lerp(b, from); var end = a.Lerp(b, to);
                wet.Add((start, end)); length += start.DistanceTo(end);
            }
        }
        if (length < 0.00001f) { return []; }
        var count = Math.Clamp(Mathf.CeilToInt(length / 0.035f), 1, 12);
        var result = new Vector3[count];
        for (var i = 0; i < count; i++) {
            var remaining = length * (i + 0.35f + 0.15f * Mathf.Sin(i * 2.3f)) / count;
            foreach (var (a, b) in wet) {
                var span = a.DistanceTo(b);
                if (remaining <= span) { result[i] = a.Lerp(b, remaining / span); break; }
                remaining -= span;
            }
        }
        return result;
    }
}
