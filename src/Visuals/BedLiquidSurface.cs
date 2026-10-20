namespace Scalpel.Visuals;

/// <summary>The actual upper mattress triangles, shared by liquid contact, wet meshes and runoff outlets.
/// Using the imported mesh keeps rounded corners and the gaps between separate pads out of the footprint.</summary>
internal sealed class BedLiquidSurface {
    private readonly record struct Face(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal, Rect2 Bounds, Vector3 Na, Vector3 Nb, Vector3 Nc);
    private readonly List<Face> _faces = [];
    private readonly List<(Vector3 A, Vector3 B)> _edges = [];
    private readonly List<(Vector2[] Outline, Rect2 Bounds)> _supports = [];
    private const float Lift = 0.00025f;
    private readonly List<Face>[] _shellRows = Enumerable.Range(0, 64).Select(_ => new List<Face>()).ToArray();
    private readonly float _bottom;
    private readonly float _padBottom;
    private readonly float _padTop;
    private readonly Rect2 _padBounds;
    private readonly float _shoulder;
    private readonly List<Face> _frameTop = [];
    private readonly List<Face> _shell = [];
    private readonly Dictionary<Vector3, Coating> _coatings = [];
    internal float DripHeight => _bottom + 0.003f;
    internal readonly record struct Flow(Vector3 Start, Vector3 Direction, Vector3 End, float Width, float Phase = 0f, float Progress = 0f, bool Gap = false, Vector3 Entry = default, float Length = 0.08f, Vector3 Exit = default);


    internal BedLiquidSurface(MeshInstance3D pads, MeshInstance3D frame) {
        var frameVertices = frame.Mesh.GetFaces().Select(p => frame.GlobalTransform * p).ToArray();
        _bottom = frameVertices.Min(p => p.Y);
        var vertices = pads.Mesh.GetFaces().Select(p => pads.GlobalTransform * p).ToArray();
        _padBottom = vertices.Min(p => p.Y);
        _padTop = vertices.Max(p => p.Y);
        _padBounds = new Rect2(Xz(vertices[0]), Vector2.Zero);
        foreach (var vertex in vertices) { _padBounds = _padBounds.Expand(Xz(vertex)); }
        var normals = CacheShell(vertices);
        CacheShell(frameVertices, frame: true);
        _shoulder = _frameTop.Min(face => Mathf.Min(face.A.Y, Mathf.Min(face.B.Y, face.C.Y))) + 0.0001f;
        foreach (var direction in new[] { Vector3.Left, Vector3.Right, Vector3.Forward, Vector3.Back }) {
            _coatings.Add(direction, new Coating(_shell, Vector3.Up.Cross(direction), Vector3.Up, direction));
        }
        _coatings.Add(Vector3.Up, new Coating(_frameTop, Vector3.Right, Vector3.Back, Vector3.Up));
        var edges = new Dictionary<(Vector3, Vector3), int>();
        for (var i = 0; i < vertices.Length; i += 3) {
            var a = vertices[i];
            var b = vertices[i + 1];
            var c = vertices[i + 2];
            var normal = (c - a).Cross(b - a).Normalized();
            if (normal.Y <= 0.001f) { continue; }
            var bounds = new Rect2(Xz(a), Vector2.Zero).Expand(Xz(b)).Expand(Xz(c));
            _faces.Add(new Face(a, b, c, normal, bounds, Vector3.Zero, Vector3.Zero, Vector3.Zero));
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

    private int Row(float y) => Math.Clamp((int)((y - _bottom) / (Room.TableHeight - _bottom) * _shellRows.Length), 0, _shellRows.Length - 1);

    private Dictionary<Vector3, Vector3> CacheShell(Vector3[] vertices, bool frame = false) {
        var normals = new Dictionary<Vector3, Vector3>();
        for (var i = 0; i < vertices.Length; i += 3) {
            var weighted = (vertices[i + 2] - vertices[i]).Cross(vertices[i + 1] - vertices[i]);
            for (var j = 0; j < 3; j++) { normals[vertices[i + j]] = normals.GetValueOrDefault(vertices[i + j]) + weighted; }
        }
        for (var i = 0; i < vertices.Length; i += 3) {
            var a = vertices[i]; var b = vertices[i + 1]; var c = vertices[i + 2];
            var normal = (c - a).Cross(b - a).Normalized();
            if (normal.IsZeroApprox()) { continue; }
            var face = new Face(a, b, c, normal, new Rect2(Xz(a), Vector2.Zero).Expand(Xz(b)).Expand(Xz(c)),
                normals[a].Normalized(), normals[b].Normalized(), normals[c].Normalized());
            _shell.Add(face);
            if (frame && normal.Y > 0.001f) { _frameTop.Add(face); }
            var first = Row(Mathf.Min(a.Y, Mathf.Min(b.Y, c.Y)));
            var last = Row(Mathf.Max(a.Y, Mathf.Max(b.Y, c.Y)));
            for (var row = first; row <= last; row++) { _shellRows[row].Add(face); }
        }
        return normals;
    }

    private (Vector3 Position, Vector3 Normal)? SideAt(Vector3 start, Vector3 direction, float y) {
        var from = new Vector3(start.X, y, start.Z);
        var best = float.NegativeInfinity;
        (Vector3 Position, Vector3 Normal)? result = null;
        foreach (var face in _shellRows[Row(y)]) {
            if (direction.X != 0f && (start.Z < face.Bounds.Position.Y - 0.000001f || start.Z > face.Bounds.End.Y + 0.000001f)) { continue; }
            if (direction.Z != 0f && (start.X < face.Bounds.Position.X - 0.000001f || start.X > face.Bounds.End.X + 0.000001f)) { continue; }
            var denominator = face.Normal.Dot(direction);
            if (denominator <= 0.0001f) { continue; }
            var distance = face.Normal.Dot(face.A - from) / denominator;
            if (distance < -0.08f || distance > 0.04f || distance <= best) { continue; }
            var at = from + direction * distance;
            var b = face.B - face.A; var c = face.C - face.A; var offset = at - face.A;
            var bb = b.Dot(b); var bc = b.Dot(c); var cc = c.Dot(c);
            var det = bb * cc - bc * bc;
            if (Mathf.Abs(det) < 1e-16f) { continue; }
            var u = (cc * offset.Dot(b) - bc * offset.Dot(c)) / det;
            var v = (bb * offset.Dot(c) - bc * offset.Dot(b)) / det;
            if (u < -0.00001f || v < -0.00001f || u + v > 1.00001f) { continue; }
            best = distance;
            var normal = (face.Na * (1f - u - v) + face.Nb * u + face.Nc * v).Normalized();
            result = (at + normal * Lift, normal);
        }
        return result;
    }

    private static Vector3 Outward(Vector3 at) {
        var endDistance = Mathf.Min(Mathf.Abs(at.X - Room.TableFoot), Mathf.Abs(at.X - Room.TableHead));
        var sideDistance = Room.TableHalfWidth - Mathf.Abs(at.Z);
        if (endDistance < sideDistance) { return at.X > 0f ? Vector3.Right : Vector3.Left; }
        return at.Z > 0f ? Vector3.Back : Vector3.Forward;
    }

    private (Vector3 Position, Vector3 Normal)? FrameTopAt(Vector2 point) {
        foreach (var face in _frameTop) {
            if (!face.Bounds.Grow(0.000003f).HasPoint(point)) { continue; }
            var a = Xz(face.A); var b = Xz(face.B); var c = Xz(face.C);
            if ((b - a).Cross(point - a) < -1e-8f || (c - b).Cross(point - b) < -1e-8f || (a - c).Cross(point - c) < -1e-8f) { continue; }
            var normal = Normal(face, point);
            return (new Vector3(point.X, Height(face, point), point.Y) + normal * Lift, normal);
        }
        return null;
    }

    private static Vector3 FrameEdge(Vector3 start, Vector3 direction) => direction.X != 0f
        ? new Vector3(direction.X > 0f ? Room.TableHead - 0.0002f : Room.TableFoot + 0.0002f, start.Y, start.Z)
        : new Vector3(start.X, start.Y, direction.Z * (Room.TableHalfWidth - 0.0002f));

    private Vector3 PadOutward(Vector3 start) {
        var nearest = float.PositiveInfinity;
        var direction = Vector3.Right;
        foreach (var (_, bounds) in _supports) {
            var center = bounds.GetCenter();
            if (!bounds.Grow(0.00001f).HasPoint(Xz(start))) { continue; }
            var dx = Mathf.Min(Mathf.Abs(start.X - bounds.Position.X), Mathf.Abs(start.X - bounds.End.X));
            var dz = Mathf.Min(Mathf.Abs(start.Z - bounds.Position.Y), Mathf.Abs(start.Z - bounds.End.Y));
            if (Mathf.Min(dx, dz) >= nearest) { continue; }
            nearest = Mathf.Min(dx, dz);
            direction = dx < dz ? (start.X > center.X ? Vector3.Right : Vector3.Left)
                : (start.Z > center.Y ? Vector3.Back : Vector3.Forward);
        }
        return direction;
    }

    internal sealed class RivuletScratch { internal readonly List<StripTriangle> Triangles = []; }
    internal readonly record struct StripTriangle(Vector2 A, Vector2 B, Vector2 C, Rect2 Bounds);

    private static float FlowWidth(Flow flow, float t) {
        var width = (0.002f + 0.0006f * Mathf.Sin(flow.Phase)) + flow.Width * (0.8f + 0.25f * Mathf.Sin(flow.Phase * 1.7f)) * Mathf.Exp(-t * (19f + 8f * Mathf.Sin(flow.Phase)));
        width *= 1f + 0.15f * Mathf.Sin(t * 11f + flow.Phase);
        if (flow.Progress < 1f) { width *= Mathf.Clamp((flow.Progress - t) * flow.Length / 0.0015f, 0f, 1f); }
        return width;
    }

    /// <summary>Clip narrow wet masks to actual side triangles rather than stretching a ribbon over their corners.
    /// This avoids floating fins and visible gaps where a rounded pad meets the supporting frame.</summary>
    internal void Rivulets(LiquidSurface surface, IReadOnlyList<Flow> flows, Vector3 center, RivuletScratch scratch) {
        foreach (var flow in flows) {
            if (flow.Progress <= 0f) { continue; }
            if (!flow.Gap) {
                Vertical(flow, flow.Start, flow.Direction, flow.Start.Y + Lift, DripHeight, 0f, 1f);
                continue;
            }
            var frameStart = FrameTopAt(Xz(flow.Start));
            var frameExit = FrameTopAt(Xz(flow.Exit));
            if (frameStart is null || frameExit is null) { continue; }
            var entry = SideAt(flow.Start, flow.Entry, frameStart.Value.Position.Y)?.Position ?? flow.Start;
            Vertical(flow, flow.Start, flow.Entry, flow.Start.Y + Lift, frameStart.Value.Position.Y, 0f, 0.25f);
            var from = Xz(entry);
            var to = Xz(flow.Exit);
            var direction = (to - from).Normalized();
            var across = new Vector2(-direction.Y, direction.X);
            var middle = MakeStrip(flow, 0.25f, 0.8f, from.DistanceTo(to), from, to, across, false, scratch.Triangles);
            Coat(surface, middle, Vector3.Up, entry, center);
            Vertical(flow, flow.Exit, flow.Direction, frameExit.Value.Position.Y, DripHeight, 0.8f, 1f);
        }

        void Vertical(Flow flow, Vector3 start, Vector3 outward, float top, float bottom, float begin, float end) {
            var across = Vector3.Up.Cross(outward);
            var origin = start.Dot(across);
            var strips = MakeStrip(flow, begin, end, top - bottom,
                new Vector2(origin, top), new Vector2(origin, bottom), Vector2.Right, true, scratch.Triangles);
            Coat(surface, strips, outward, start, center);
        }
    }

    private static List<StripTriangle> MakeStrip(Flow flow, float begin, float end, float length, Vector2 from, Vector2 to, Vector2 across, bool vertical, List<StripTriangle> triangles) {
        triangles.Clear();
        var nominalEnd = end;
        end = Mathf.Min(end, flow.Progress);
        if (end <= begin) { return triangles; }
        var steps = Math.Max(2, Mathf.CeilToInt(length * (end - begin) / (nominalEnd - begin) / 0.002f));
        var previous = Position(begin);
        var previousWidth = FlowWidth(flow, begin) * 0.5f;
        for (var i = 1; i <= steps; i++) {
            var t = Mathf.Lerp(begin, end, (float)i / steps);
            var at = Position(t);
            var width = FlowWidth(flow, t) * 0.5f;
            var a = previous - across * previousWidth; var b = previous + across * previousWidth;
            var c = at - across * width; var d = at + across * width;
            Add(a, b, c); Add(b, d, c);
            previous = at; previousWidth = width;
        }
        return triangles;

        Vector2 Position(float t) {
            var at = from.Lerp(to, (t - begin) / (nominalEnd - begin));
            if (vertical) { at.X += Mathf.Sin(t * 7f + flow.Phase) * 0.001f * t; }
            return at;
        }
        void Add(Vector2 a, Vector2 b, Vector2 c) => triangles.Add(new StripTriangle(a, b, c, new Rect2(a, Vector2.Zero).Expand(b).Expand(c)));
    }

    private readonly record struct ProjectedFace(Face Face, Vector2 A, Vector2 B, Vector2 C, Rect2 Bounds, float Determinant, float Near, float Far, int FirstBin = 0);

    /// <summary>Fixed cardinal projections and interval bins shared by all rivulet workers. A narrow streak visits
    /// only overlapping faces, rather than projecting every mattress and frame triangle for every stream.</summary>
    private sealed class Coating {
        internal readonly ProjectedFace[] Faces;
        internal readonly List<int>[] Bins = Enumerable.Range(0, 64).Select(_ => new List<int>()).ToArray();
        private readonly float _left;
        private readonly float _width;
        internal int Bin(float u) => Math.Clamp((int)((u - _left) / _width * Bins.Length), 0, Bins.Length - 1);

        internal Coating(List<Face> faces, Vector3 uAxis, Vector3 vAxis, Vector3 outward) {
            var projected = new List<ProjectedFace>();
            foreach (var face in faces) {
                if (face.Normal.Dot(outward) <= 0.0001f) { continue; }
                Vector2 Project(Vector3 p) => new(p.Dot(uAxis), p.Dot(vAxis));
                var a = Project(face.A); var b = Project(face.B); var c = Project(face.C);
                var determinant = (b - a).Cross(c - a);
                if (Mathf.Abs(determinant) < 1e-12f) { continue; }
                var depths = new Vector3(face.A.Dot(outward), face.B.Dot(outward), face.C.Dot(outward));
                projected.Add(new ProjectedFace(face, a, b, c, new Rect2(a, Vector2.Zero).Expand(b).Expand(c), determinant,
                    Mathf.Min(depths.X, Mathf.Min(depths.Y, depths.Z)), Mathf.Max(depths.X, Mathf.Max(depths.Y, depths.Z))));
            }
            Faces = [.. projected];
            _left = Faces.Min(face => face.Bounds.Position.X);
            _width = Mathf.Max(Faces.Max(face => face.Bounds.End.X) - _left, 0.0001f);
            for (var i = 0; i < Faces.Length; i++) {
                var first = Bin(Faces[i].Bounds.Position.X);
                Faces[i] = Faces[i] with { FirstBin = first };
                for (var bin = first; bin <= Bin(Faces[i].Bounds.End.X); bin++) { Bins[bin].Add(i); }
            }
        }
    }

    private void Coat(LiquidSurface surface, List<StripTriangle> strips, Vector3 outward, Vector3 origin, Vector3 center) {
        if (strips.Count == 0) { return; }
        var coating = _coatings[outward];
        var bounds = strips[0].Bounds;
        foreach (var strip in strips) { bounds = bounds.Merge(strip.Bounds); }
        Span<Vector2> first = stackalloc Vector2[8];
        Span<Vector2> second = stackalloc Vector2[8];
        var firstBin = coating.Bin(bounds.Position.X);
        var lastBin = coating.Bin(bounds.End.X);
        var originDepth = origin.Dot(outward);
        for (var bin = firstBin; bin <= lastBin; bin++) {
            foreach (var index in coating.Bins[bin]) {
                var projected = coating.Faces[index];
                // Faces spanning several bins are visited once without a per-build hash set.
                if (bin != Math.Max(firstBin, projected.FirstBin)) { continue; }
                if (outward.Y == 0f && (projected.Near - originDepth > 0.04f || projected.Far - originDepth < -0.08f)) { continue; }
                var face = projected.Face;
                var a = projected.A; var b = projected.B; var c = projected.C;
                var determinant = projected.Determinant;
                var faceBounds = projected.Bounds;
                if (!faceBounds.Intersects(bounds)) { continue; }
                foreach (var strip in strips) {
                    if (!faceBounds.Intersects(strip.Bounds)) { continue; }
                    first[0] = strip.A; first[1] = strip.B; first[2] = strip.C;
                    var count = determinant > 0f ? Clip(first[..3], second, a, b) : Clip(first[..3], second, b, a);
                    count = determinant > 0f ? Clip(second[..count], first, b, c) : Clip(second[..count], first, c, b);
                    count = determinant > 0f ? Clip(first[..count], second, c, a) : Clip(first[..count], second, a, c);
                    for (var i = 1; i + 1 < count; i++) {
                        var p = Vertex(second[0]); var q = Vertex(second[i]); var t = Vertex(second[i + 1]);
                        if ((t.Position - p.Position).Cross(q.Position - p.Position).Dot(face.Normal) < 0f) { (q, t) = (t, q); }
                        surface.Facet(p.Position - center, q.Position - center, t.Position - center, p.Normal, q.Normal, t.Normal);
                    }
                }
                (Vector3 Position, Vector3 Normal) Vertex(Vector2 at) {
                    var u = (at - a).Cross(c - a) / determinant;
                    var v = (b - a).Cross(at - a) / determinant;
                    var normal = (face.Na * (1f - u - v) + face.Nb * u + face.Nc * v).Normalized();
                    return (face.A + (face.B - face.A) * u + (face.C - face.A) * v + normal * Lift, normal);
                }
            }
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
        if (motion.Y >= 0f || to.Y > _padTop || from.Y < _padBottom) { return null; }
        var bounds = new Rect2(Xz(from), Vector2.Zero).Expand(Xz(to)).Grow(0.000001f);
        if (!_padBounds.Intersects(bounds, includeBorders: true)) { return null; }
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

    internal Flow[] Outlets(IReadOnlyList<Vector2> outline, Vector3 center) {
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
        var result = new List<Flow>(count);
        for (var i = 0; i < count; i++) {
            var remaining = length * (i + 0.5f + 0.4f * Mathf.Sin(i * 12.9898f + center.X * 17f + center.Z * 11f)) / count;
            foreach (var (a, b) in wet) {
                var span = a.DistanceTo(b);
                if (remaining <= span) {
                    var start = a.Lerp(b, remaining / span);
                    var direction = Outward(start);
                    var edge = FrameEdge(start, direction);
                    var gap = Xz(start).DistanceTo(Xz(edge)) > 0.035f;
                    if (SideAt(gap ? edge : start, direction, DripHeight) is { } bottom
                        && SideAt(edge, direction, _shoulder) is { } top) {
                        var exit = top.Position - top.Normal * Lift;
                        result.Add(new Flow(start, direction, bottom.Position, Mathf.Min(length / count * 1.1f, 0.032f),
                            Gap: gap, Entry: PadOutward(start), Length: start.Y - DripHeight + (gap ? Xz(start).DistanceTo(Xz(exit)) : 0f), Exit: exit));
                    }
                    break;
                }
                remaining -= span;
            }
        }
        return [.. result];
    }
}
