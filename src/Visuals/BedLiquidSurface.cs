namespace Scalpel.Visuals;

/// <summary>The actual upper mattress triangles, shared by liquid contact, wet meshes and runoff outlets.
/// Using the imported mesh keeps rounded corners and the gaps between separate pads out of the footprint.</summary>
internal sealed class BedLiquidSurface {
    private readonly record struct Face(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal, Rect2 Bounds, Vector3 Na, Vector3 Nb, Vector3 Nc);
    private readonly List<Face> _faces = [];
    private readonly List<(Vector3 A, Vector3 B)> _edges = [];
    private readonly List<(Vector3 A, Vector3 B)> _frameEdges = [];
    private readonly List<(Vector2[] Outline, Rect2 Bounds)> _supports = [];
    internal const float FilmDepth = 0.00025f;
    private const float Lift = FilmDepth;
    private readonly List<Face>[] _shellRows = Enumerable.Range(0, 64).Select(_ => new List<Face>()).ToArray();
    private readonly float _bottom;
    private readonly float _landingBottom;
    private readonly float _landingTop;
    private readonly Rect2 _landingBounds;
    private readonly List<Face> _landingFaces = [];
    private readonly float _shoulder;
    private readonly List<Face> _frameTop = [];
    private readonly List<Face> _visibleFrameTop = [];
    private readonly List<Face> _shell = [];
    private readonly Dictionary<Vector3, Coating> _coatings = [];
    internal float DripHeight => _bottom + 0.003f;
    internal readonly record struct Flow(Vector3 Start, Vector3 Direction, Vector3 End, float Width, float Phase = 0f, float Progress = 0f, bool Gap = false, Vector3 Entry = default, float Length = 0.08f, Vector3 Exit = default);


    internal BedLiquidSurface(MeshInstance3D pads, MeshInstance3D frame) {
        var frameVertices = frame.Mesh.GetFaces().Select(p => frame.GlobalTransform * p).ToArray();
        _bottom = frameVertices.Min(p => p.Y);
        var vertices = pads.Mesh.GetFaces().Select(p => pads.GlobalTransform * p).ToArray();
        var normals = CacheShell(vertices);
        CacheShell(frameVertices, frame: true);
        CacheEdges(_frameTop, _frameEdges);
        _shoulder = _frameTop.Min(face => Mathf.Min(face.A.Y, Mathf.Min(face.B.Y, face.C.Y))) + 0.0001f;
        foreach (var direction in new[] { Vector3.Left, Vector3.Right, Vector3.Forward, Vector3.Back }) {
            _coatings.Add(direction, new Coating(_shell, Vector3.Up.Cross(direction), Vector3.Up, direction));
        }
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
        _landingFaces.AddRange(_faces);
        _landingFaces.AddRange(_frameTop);
        var landingVertices = _landingFaces.SelectMany(face => new[] { face.A, face.B, face.C }).ToArray();
        _landingBottom = landingVertices.Min(p => p.Y);
        _landingTop = landingVertices.Max(p => p.Y);
        _landingBounds = new Rect2(Xz(landingVertices[0]), Vector2.Zero);
        foreach (var vertex in landingVertices) { _landingBounds = _landingBounds.Expand(Xz(vertex)); }
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
        CacheExposedFrame();
        _coatings.Add(Vector3.Up, new Coating(_visibleFrameTop, Vector3.Right, Vector3.Back, Vector3.Up));

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

    private static void CacheEdges(IReadOnlyList<Face> faces, List<(Vector3 A, Vector3 B)> output) {
        var counts = new Dictionary<(Vector3, Vector3), int>();
        foreach (var face in faces) {
            Edge(face.A, face.B); Edge(face.B, face.C); Edge(face.C, face.A);
        }
        foreach (var (edge, count) in counts) { if (count == 1) { output.Add(edge); } }

        void Edge(Vector3 a, Vector3 b) {
            a = a.Snapped(Vector3.One * 0.000001f);
            b = b.Snapped(Vector3.One * 0.000001f);
            var swap = a.X > b.X || (a.X == b.X && (a.Z > b.Z || (a.Z == b.Z && a.Y > b.Y)));
            var key = swap ? (b, a) : (a, b);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
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

    internal sealed class RivuletScratch {
        internal readonly List<StripTriangle> Triangles = [];
        internal readonly CoatingPath?[] Paths = new CoatingPath?[12];
        internal readonly float[] AvailableMl = new float[12];
        internal readonly RivuletState[] States = new RivuletState[12];
    }
    internal readonly record struct RivuletState(float AllowedProgress, float DrawnProgress, float CapacityMl);
    internal readonly record struct StripTriangle(Vector2 A, Vector2 B, Vector2 C, Rect2 Bounds, float Ta, float Tb, float Tc);
    internal readonly record struct CoatingVertex(Vector3 Position, Vector3 Normal, float Progress) {
        internal CoatingVertex Lerp(CoatingVertex other, float f) => new(Position.Lerp(other.Position, f), Normal.Lerp(other.Normal, f).Normalized(), Mathf.Lerp(Progress, other.Progress, f));
    }

    /// <summary>Cache the actual clipped triangles once per outlet geometry. Progress clips that same geometry,
    /// so its capacity and visible coating use identical surface area without repeating mattress intersections.</summary>
    internal sealed class CoatingPath(Flow flow) {
        internal readonly Flow Flow = flow;
        internal readonly List<(CoatingVertex A, CoatingVertex B, CoatingVertex C)> Triangles = [];
        internal float CapacityMl;
        internal float LastMl = float.NaN;
        internal float LastProgress;
        internal void Add(CoatingVertex a, CoatingVertex b, CoatingVertex c) {
            Triangles.Add((a, b, c));
            CapacityMl += (b.Position - a.Position).Cross(c.Position - a.Position).Length() * 0.5f * FilmDepth * 1000000f;
        }
        internal float Paint(float progress, LiquidSurface? surface = null, Vector3 center = default) {
            if (progress <= 0f) { return 0f; }
            var ml = 0f;
            Span<CoatingVertex> input = stackalloc CoatingVertex[3];
            Span<CoatingVertex> output = stackalloc CoatingVertex[4];
            foreach (var (a, b, c) in Triangles) {
                input[0] = a; input[1] = b; input[2] = c;
                var count = 0;
                for (var i = 0; i < 3; i++) {
                    var from = input[i]; var to = input[(i + 1) % 3];
                    if (from.Progress <= progress) { output[count++] = from; }
                    if ((from.Progress <= progress) != (to.Progress <= progress)) {
                        output[count++] = from.Lerp(to, (progress - from.Progress) / (to.Progress - from.Progress));
                    }
                }
                for (var i = 1; i + 1 < count; i++) {
                    var p = output[0]; var q = output[i]; var r = output[i + 1];
                    ml += (q.Position - p.Position).Cross(r.Position - p.Position).Length() * 0.5f * FilmDepth * 1000000f;
                    surface?.Facet(p.Position - center, q.Position - center, r.Position - center, p.Normal, q.Normal, r.Normal);
                }
            }
            return ml;
        }
        internal float ProgressFor(float ml) {
            if (ml >= CapacityMl) { return 1f; }
            if (ml <= 0f) { return 0f; }
            var low = 0f; var high = 1f;
            // Always retain the affordable lower bound; a finite dose cannot overdraw its visible coating.
            for (var i = 0; i < 14; i++) {
                var middle = (low + high) * 0.5f;
                if (Paint(middle) <= ml) { low = middle; } else { high = middle; }
            }
            return low;
        }
    }

    private static float FlowWidth(Flow flow, float t) {
        var width = (0.002f + 0.0006f * Mathf.Sin(flow.Phase)) + flow.Width * (0.8f + 0.25f * Mathf.Sin(flow.Phase * 1.7f)) * Mathf.Exp(-t * (19f + 8f * Mathf.Sin(flow.Phase)));
        width *= 1f + 0.15f * Mathf.Sin(t * 11f + flow.Phase);
        return width;
    }

    /// <summary>Clip narrow wet masks to actual side triangles rather than stretching a ribbon over their corners.
    /// This avoids floating fins and visible gaps where a rounded pad meets the supporting frame.</summary>
    internal void Rivulets(LiquidSurface surface, IReadOnlyList<Flow> flows, Vector3 center, RivuletScratch scratch) {
        for (var i = 0; i < flows.Count; i++) {
            var flow = flows[i];
            if (flow.Length <= 0f) { scratch.States[i] = default; continue; }
            var geometry = flow with { Progress = 1f };
            var path = scratch.Paths[i];
            if (path is null || path.Flow != geometry) { scratch.Paths[i] = path = TraceRivulet(geometry, scratch); }
            var available = scratch.AvailableMl[i];
            if (available != path.LastMl) {
                path.LastProgress = path.ProgressFor(available);
                path.LastMl = available;
            }
            var allowed = path.LastProgress;
            var drawn = Mathf.Min(flow.Progress, allowed);
            path.Paint(drawn, surface, center);
            scratch.States[i] = new RivuletState(allowed, drawn, path.CapacityMl);
        }
    }

    private CoatingPath TraceRivulet(Flow flow, RivuletScratch scratch) {
        var path = new CoatingPath(flow);
        if (!flow.Gap) {
            Vertical(flow, flow.Start, flow.Direction, flow.Start.Y + Lift, DripHeight, 0f, 1f);
            return path;
        }
        var frameStart = FrameTopAt(Xz(flow.Start));
        var frameExit = FrameTopAt(Xz(flow.Exit));
        if (frameStart is null || frameExit is null) { return path; }
        var entry = SideAt(flow.Start, flow.Entry, frameStart.Value.Position.Y)?.Position ?? flow.Start;
        Vertical(flow, flow.Start, flow.Entry, flow.Start.Y + Lift, frameStart.Value.Position.Y, 0f, 0.25f);
        var from = Xz(entry);
        var to = Xz(flow.Exit);
        var direction = (to - from).Normalized();
        var across = new Vector2(-direction.Y, direction.X);
        var middle = MakeStrip(flow, 0.25f, 0.8f, from.DistanceTo(to), from, to, across, false, scratch.Triangles);
        Coat(path, middle, Vector3.Up, entry);
        Vertical(flow, flow.Exit, flow.Direction, frameExit.Value.Position.Y, DripHeight, 0.8f, 1f);
        return path;

        void Vertical(Flow flow, Vector3 start, Vector3 outward, float top, float bottom, float begin, float end) {
            var across = Vector3.Up.Cross(outward);
            var origin = start.Dot(across);
            var strips = MakeStrip(flow, begin, end, top - bottom,
                new Vector2(origin, top), new Vector2(origin, bottom), Vector2.Right, true, scratch.Triangles);
            Coat(path, strips, outward, start);
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
        var previousT = begin;
        for (var i = 1; i <= steps; i++) {
            var t = Mathf.Lerp(begin, end, (float)i / steps);
            var at = Position(t);
            var width = FlowWidth(flow, t) * 0.5f;
            var a = previous - across * previousWidth; var b = previous + across * previousWidth;
            var c = at - across * width; var d = at + across * width;
            Add(a, b, c, previousT, previousT, t); Add(b, d, c, previousT, t, t);
            previous = at; previousWidth = width; previousT = t;
        }
        return triangles;

        Vector2 Position(float t) {
            var at = from.Lerp(to, (t - begin) / (nominalEnd - begin));
            if (vertical) { at.X += Mathf.Sin(t * 7f + flow.Phase) * 0.001f * t; }
            return at;
        }
        void Add(Vector2 a, Vector2 b, Vector2 c, float ta, float tb, float tc) => triangles.Add(new StripTriangle(a, b, c, new Rect2(a, Vector2.Zero).Expand(b).Expand(c), ta, tb, tc));
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

    private void Coat(CoatingPath path, List<StripTriangle> strips, Vector3 outward, Vector3 origin) {
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
                    var stripDet = (strip.B - strip.A).Cross(strip.C - strip.A);
                    if (Mathf.Abs(stripDet) < 1e-15f) { continue; }
                    first[0] = strip.A; first[1] = strip.B; first[2] = strip.C;
                    var count = determinant > 0f ? Clip(first[..3], second, a, b) : Clip(first[..3], second, b, a);
                    count = determinant > 0f ? Clip(second[..count], first, b, c) : Clip(second[..count], first, c, b);
                    count = determinant > 0f ? Clip(first[..count], second, c, a) : Clip(first[..count], second, a, c);
                    for (var i = 1; i + 1 < count; i++) {
                        var p = Vertex(second[0]); var q = Vertex(second[i]); var t = Vertex(second[i + 1]);
                        if ((t.Position - p.Position).Cross(q.Position - p.Position).Dot(face.Normal) < 0f) { (q, t) = (t, q); }
                        path.Add(p, q, t);
                    }
                    CoatingVertex Vertex(Vector2 at) {
                        var u = (at - a).Cross(c - a) / determinant;
                        var v = (b - a).Cross(at - a) / determinant;
                        var normal = (face.Na * (1f - u - v) + face.Nb * u + face.Nc * v).Normalized();
                        var su = (at - strip.A).Cross(strip.C - strip.A) / stripDet;
                        var sv = (strip.B - strip.A).Cross(at - strip.A) / stripDet;
                        var progress = strip.Ta * (1f - su - sv) + strip.Tb * su + strip.Tc * sv;
                        return new CoatingVertex(face.A + (face.B - face.A) * u + (face.C - face.A) * v + normal * Lift, normal, Mathf.Clamp(progress, 0f, 1f));
                    }
                }

            }
        }
    }

    private static Vector2 Xz(Vector3 p) => new(p.X, p.Z);
    private static float Height(Face face, Vector2 at) => face.A.Y
        - (face.Normal.X * (at.X - face.A.X) + face.Normal.Z * (at.Y - face.A.Z)) / face.Normal.Y;

    internal float? HeightAt(Vector2 at) {
        float? highest = null;
        foreach (var face in _faces) {
            if (!face.Bounds.Grow(0.000003f).HasPoint(at)) { continue; }
            var a = Xz(face.A); var b = Xz(face.B); var c = Xz(face.C);
            if ((b - a).Cross(at - a) >= -0.000003f * (b - a).Length() && (c - b).Cross(at - b) >= -0.000003f * (c - b).Length()
                && (a - c).Cross(at - c) >= -0.000003f * (a - c).Length()) {
                var height = Height(face, at);
                if (!highest.HasValue || height > highest.Value) { highest = height; }
            }
        }
        return highest;
    }

    // Compute the shelf's exposed triangles once during loading. A frame pool must not grow beneath
    // a pad, and the same clipped support is used for its footprint, volume and attached coating.
    private void CacheExposedFrame() {
        foreach (var frame in _frameTop) {
            List<Vector2[]> pieces = [[Xz(frame.A), Xz(frame.B), Xz(frame.C)]];
            // The pads sit above the frame. Reuse their existing convex projected outlines instead
            // of cutting the shelf against every interior pad triangle and generating redundant fragments.
            foreach (var (outline, bounds) in _supports) {
                if (!frame.Bounds.Intersects(bounds)) { continue; }
                var exposed = new List<Vector2[]>();
                foreach (var piece in pieces) {
                    var remaining = piece;
                    for (var edge = 0; edge < outline.Length; edge++) {
                        var a = outline[edge]; var b = outline[(edge + 1) % outline.Length];
                        var outside = ClipHalfPlane(remaining, a, b, -1f);
                        if (outside.Length >= 3) { exposed.Add(outside); }
                        remaining = ClipHalfPlane(remaining, a, b, 1f);
                        if (remaining.Length < 3) { break; }
                    }
                }
                pieces = exposed;
                if (pieces.Count == 0) { break; }
            }
            foreach (var piece in pieces) {
                for (var i = 1; i + 1 < piece.Length; i++) {
                    var a = piece[0]; var b = piece[i]; var c = piece[i + 1];
                    // Repeated pad intersections can leave nearly collinear fan triangles. Exclude
                    // sub-micron slivers: their ill-conditioned edges can extrapolate a later pool clip.
                    var longest = Mathf.Max(a.DistanceTo(b), Mathf.Max(b.DistanceTo(c), c.DistanceTo(a)));
                    if (Mathf.Abs((b - a).Cross(c - a)) < longest * 0.000001f) { continue; }
                    Vector3 At(Vector2 p) => new(p.X, Height(frame, p), p.Y);
                    _visibleFrameTop.Add(new Face(At(a), At(b), At(c), frame.Normal,
                        new Rect2(a, Vector2.Zero).Expand(b).Expand(c), Normal(frame, a), Normal(frame, b), Normal(frame, c)));
                }
            }
        }
    }

    private static Vector2[] ClipHalfPlane(Vector2[] input, Vector2 a, Vector2 b, float sign) {
        var output = new List<Vector2>(input.Length + 1);
        var x = (double)b.X - a.X; var y = (double)b.Y - a.Y;
        // A five-micron overlap avoids ambiguous ownership of an edge after float reconstruction.
        var margin = 0.000005 * Math.Sqrt(x * x + y * y);
        double Distance(Vector2 p) => (x * ((double)p.Y - a.Y) - y * ((double)p.X - a.X) + margin) * sign;
        for (var i = 0; i < input.Length; i++) {
            var from = input[i]; var to = input[(i + 1) % input.Length];
            var first = Distance(from); var second = Distance(to);
            if (first >= 0f) { output.Add(from); }
            if ((first >= 0.0) != (second >= 0.0)) {
                var fraction = first / (first - second);
                output.Add(new Vector2((float)(from.X + ((double)to.X - from.X) * fraction),
                    (float)(from.Y + ((double)to.Y - from.Y) * fraction)));
            }
        }
        var area = 0f;
        for (var i = 1; i + 1 < output.Count; i++) { area += (output[i] - output[0]).Cross(output[i + 1] - output[0]); }
        return Mathf.Abs(area) > 1e-12f ? output.ToArray() : [];
    }

    internal readonly record struct Hit(Vector3 At, bool Frame);

    internal bool FrameSupports(Vector2 at) => Contact(new Vector3(at.X, _landingTop + 0.001f, at.Y),
        new Vector3(at.X, _landingBottom - 0.001f, at.Y)) is { Frame: true };

    internal Vector3? Landing(Vector3 from, Vector3 to) => Contact(from, to)?.At;

    internal Hit? Contact(Vector3 from, Vector3 to) {
        var motion = to - from;
        if (motion.Y >= 0f || to.Y > _landingTop || from.Y < _landingBottom) { return null; }
        var bounds = new Rect2(Xz(from), Vector2.Zero).Expand(Xz(to)).Grow(0.000001f);
        if (!_landingBounds.Intersects(bounds, includeBorders: true)) { return null; }
        Hit? closest = null;
        var closestProgress = float.PositiveInfinity;
        for (var index = 0; index < _landingFaces.Count; index++) {
            var face = _landingFaces[index];
            if (!face.Bounds.Grow(0.000003f).Intersects(bounds)) { continue; }
            var start = face.Normal.Dot(from - face.A);
            var end = face.Normal.Dot(to - face.A);
            if (start < 0f || end > 0f) { continue; }
            var progress = start / (start - end);
            if (progress >= closestProgress) { continue; }
            var hit = from.Lerp(to, progress);
            var p = Xz(hit); var a = Xz(face.A); var b = Xz(face.B); var c = Xz(face.C);
            if ((b - a).Cross(p - a) >= -0.000003f * (b - a).Length()
                && (c - b).Cross(p - b) >= -0.000003f * (c - b).Length()
                && (a - c).Cross(p - c) >= -0.000003f * (a - c).Length()) {
                closest = new Hit(hit, index >= _faces.Count);
                closestProgress = progress;
            }
        }
        return closest;
    }

    internal float Cover(IReadOnlyList<Vector2> outline, Vector3 center, LiquidSurface? surface = null) {
        return CoverFaces(outline, center, surface, _faces);
    }

    internal float CoverFrame(IReadOnlyList<Vector2> outline, Vector3 center, LiquidSurface? surface = null) {
        return CoverFaces(outline, center, surface, _visibleFrameTop);
    }

    private static float CoverFaces(IReadOnlyList<Vector2> outline, Vector3 center, LiquidSurface? surface, IReadOnlyList<Face> faces) {
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
        foreach (var face in faces) {
            if (!face.Bounds.Intersects(bounds)) { continue; }
            foreach (var (a, b, sourceBounds) in sources) {
                if (!face.Bounds.Intersects(sourceBounds)) { continue; }
                first[0] = origin; first[1] = a; first[2] = b;
                var count = Clip(first[..3], second, Xz(face.A), Xz(face.B));
                count = Clip(second[..count], first, Xz(face.B), Xz(face.C));
                count = Clip(first[..count], second, Xz(face.C), Xz(face.A));
                for (var i = 1; i + 1 < count; i++) {
                    var p = Constrain(face, second[0]); var q = Constrain(face, second[i]); var t = Constrain(face, second[i + 1]);
                    area += Mathf.Abs((q - p).Cross(t - p)) / (2f * Mathf.Pi);
                    if (surface is null) { continue; }
                    Vector3 Position(Vector2 point) => new Vector3(point.X, Height(face, point) + Lift, point.Y) - center;
                    surface.Facet(Position(p), Position(q), Position(t), Normal(face, p), Normal(face, q), Normal(face, t));
                }
            }
        }
        return area;
    }

    private static Vector2 Constrain(Face face, Vector2 at) {
        var a = Xz(face.A); var b = Xz(face.B); var c = Xz(face.C);
        double Cross(Vector2 p, Vector2 q, Vector2 r) => ((double)q.X - p.X) * ((double)r.Y - p.Y)
            - ((double)q.Y - p.Y) * ((double)r.X - p.X);
        if (Cross(a, b, at) >= 0.0 && Cross(b, c, at) >= 0.0 && Cross(c, a, at) >= 0.0) { return at; }
        // Clipping an almost collinear wedge can amplify float roundoff along its long edge. Keep
        // the final point on the finite supporting triangle, including its endpoints.
        var nearest = a;
        var distance = float.PositiveInfinity;
        ReadOnlySpan<(Vector2 From, Vector2 To)> edges = stackalloc[] { (a, b), (b, c), (c, a) };
        foreach (var (from, to) in edges) {
            var point = Geometry2D.GetClosestPointToSegment(at, from, to);
            var candidate = point.DistanceSquaredTo(at);
            if (candidate < distance) { distance = candidate; nearest = point; }
        }
        return nearest;
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
        // Thin shelf fragments can be almost parallel to a pool edge. Compute crossings in double
        // precision so cancellation cannot move the wet film outside its supporting triangle.
        var x = (double)b.X - a.X; var y = (double)b.Y - a.Y;
        for (var i = 0; i < input.Length; i++) {
            var p = input[i]; var q = input[(i + 1) % input.Length];
            var dp = x * ((double)p.Y - a.Y) - y * ((double)p.X - a.X);
            var dq = x * ((double)q.Y - a.Y) - y * ((double)q.X - a.X);
            if (dp >= 0.0) { output[count++] = p; }
            if ((dp >= 0.0) != (dq >= 0.0)) {
                var fraction = dp / (dp - dq);
                output[count++] = new Vector2((float)(p.X + ((double)q.X - p.X) * fraction),
                    (float)(p.Y + ((double)q.Y - p.Y) * fraction));
            }
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
        return Outlets(outline, center, _edges, frameOnly: false);
    }

    internal Flow[] FrameOutlets(IReadOnlyList<Vector2> outline, Vector3 center) {
        return Outlets(outline, center, _frameEdges, frameOnly: true);
    }

    private Flow[] Outlets(IReadOnlyList<Vector2> outline, Vector3 center, IReadOnlyList<(Vector3 A, Vector3 B)> edges, bool frameOnly) {
        var polygon = outline.ToArray();
        var bounds = new Rect2(polygon[0] + Xz(center), Vector2.Zero);
        foreach (var point in polygon) { bounds = bounds.Expand(point + Xz(center)); }
        var wet = new List<(Vector3 A, Vector3 B)>();
        var length = 0f;
        foreach (var (a, b) in edges) {
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
                    var gap = !frameOnly && Xz(start).DistanceTo(Xz(edge)) > 0.035f;
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
