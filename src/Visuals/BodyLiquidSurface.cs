namespace Scalpel.Visuals;

/// <summary>Immutable model triangles for attached runoff. Paths are traced on workers; no physics queries or
/// model reads occur during gameplay. Shared vertices let each short downhill step visit only neighbouring faces.</summary>
internal sealed class BodyLiquidSurface {
    internal const float FilmDepth = 0.00012f;
    private const float Lift = FilmDepth;
    private readonly record struct Face(Vector3 A, Vector3 B, Vector3 C, Vector3 Na, Vector3 Nb, Vector3 Nc, int Ia, int Ib, int Ic);
    private readonly Face[] _faces;
    private readonly List<int>[] _adjacent;
    internal readonly record struct Sample(Vector3 At, Vector3 Normal, Vector3 Left, Vector3 Right, Vector3 LeftNormal,
        Vector3 RightNormal, float Distance, float Ml, float Width);

    internal sealed record Path(Sample[] Samples, bool OnBed, float Scale = 1f) {
        internal float InletWidth => Samples.Max(s => s.Width);
        internal float Length => Samples.Length == 0 ? 0f : Samples[^1].Distance;
        internal float CapacityMl => Samples.Length == 0 ? 0f : Samples[^1].Ml;
        internal Vector3 End => Samples.Length == 0 ? Vector3.Zero : Samples[^1].At;
        internal float DistanceFor(float ml) {
            if (ml >= CapacityMl) { return Length; }
            for (var i = 1; i < Samples.Length; i++) {
                if (ml >= Samples[i].Ml) { continue; }
                var a = Samples[i - 1]; var b = Samples[i];
                var low = 0f; var high = 1f;
                for (var n = 0; n < 14; n++) {
                    var f = (low + high) * 0.5f;
                    if (a.Ml + SegmentMl(a, b, f) <= ml) { low = f; } else { high = f; }
                }
                return Mathf.Lerp(a.Distance, b.Distance, low);
            }
            return Length;
        }
        internal float VolumeAt(float distance) {
            for (var i = 1; i < Samples.Length; i++) {
                if (distance >= Samples[i].Distance) { continue; }
                var a = Samples[i - 1]; var b = Samples[i];
                return a.Ml + SegmentMl(a, b, Mathf.Clamp((distance - a.Distance) / (b.Distance - a.Distance), 0f, 1f));
            }
            return CapacityMl;
        }
        private static (Vector3 Left, Vector3 Right) Front(Sample a, Sample b, float f) {
            var left = a.Left.Lerp(b.Left, f); var right = a.Right.Lerp(b.Right, f);
            var middle = (left + right) * 0.5f;
            // Widen continuously as the segment fills; no half-strip volume jump at a sample boundary.
            var width = Mathf.SmoothStep(0f, 1f, f);
            return (middle.Lerp(left, width), middle.Lerp(right, width));
        }
        private float SegmentMl(Sample a, Sample b, float f) {
            var (left, right) = Front(a, b, f);
            return ((a.Right - a.Left).Cross(left - a.Left).Length()
                + (right - a.Right).Cross(left - a.Right).Length()) * 0.5f * Scale * Scale * FilmDepth * 1000000f;
        }
        internal void Draw(LiquidSurface surface, float distance) {
            for (var i = 1; i < Samples.Length && Samples[i - 1].Distance < distance; i++) {
                var a = Samples[i - 1]; var b = Samples[i];
                var f = Mathf.Clamp((distance - a.Distance) / (b.Distance - a.Distance), 0f, 1f);
                var (left, right) = Front(a, b, f);
                var nl = a.LeftNormal.Lerp(b.LeftNormal, f).Normalized();
                var nr = a.RightNormal.Lerp(b.RightNormal, f).Normalized();
                surface.Facet(a.Left, a.Right, left, a.LeftNormal, a.RightNormal, nl);
                surface.Facet(a.Right, right, left, a.RightNormal, nr, nl);
            }
        }
    }

    internal BodyLiquidSurface(IReadOnlyList<Vector3> vertices, IReadOnlyList<Vector3> normals) {
        var weld = new Dictionary<Vector3, int>();
        var adjacent = new List<List<int>>();
        int Vertex(Vector3 p, int face) {
            var key = p.Snapped(Vector3.One * 0.00001f);
            if (!weld.TryGetValue(key, out var index)) { index = adjacent.Count; weld.Add(key, index); adjacent.Add([]); }
            adjacent[index].Add(face);
            return index;
        }
        _faces = new Face[vertices.Count / 3];
        for (var i = 0; i < _faces.Length; i++) {
            var n = i * 3;
            _faces[i] = new Face(vertices[n], vertices[n + 1], vertices[n + 2], normals[n], normals[n + 1], normals[n + 2],
                Vertex(vertices[n], i), Vertex(vertices[n + 1], i), Vertex(vertices[n + 2], i));
        }
        _adjacent = adjacent.ToArray();
    }

    internal Path Trace(Vector3 source, Vector3 gravity, int seed, Transform3D toWorld, BedLiquidSurface bed) {
        var first = Closest(source, -1);
        if (first.At.DistanceTo(source) > 0.03f) { return new Path([], false); }
        var scale = toWorld.Basis.Scale.X;
        var tangent = gravity.Slide(first.Normal);
        var outward = (seed / 8) switch { 0 => Vector3.Left, 1 => Vector3.Right, 2 => Vector3.Forward, _ => Vector3.Back };
        if (tangent.LengthSquared() < 0.001f) { tangent = outward.Slide(first.Normal); }
        // Overlap the broad film's lip so contact cannot show a pale seam between the pool and its narrower outlet.
        var inlet = Closest(first.At - tangent.Normalized() * 0.008f / scale, first.Face);
        var points = new List<(Vector3 At, Vector3 Normal, int Face)>(96) { inlet };
        var onBed = false;
        for (var step = 0; step < 160; step++) {
            var current = points[^1];
            if (current.Normal.Dot(gravity) > 0.92f) { break; }
            var downhill = gravity.Slide(current.Normal);
            if (downhill.LengthSquared() < 0.001f) { downhill = outward.Slide(current.Normal); }
            if (downhill.LengthSquared() < 0.001f) { break; }
            var acrossFlow = current.Normal.Cross(downhill).Normalized();
            downhill = (downhill.Normalized() + acrossFlow * (0.06f * Mathf.Sin(step * 0.16f + seed * 2.13f))).Normalized();
            var next = Closest(current.At + downhill * (0.0025f / scale), current.Face);
            if (next.At.DistanceTo(current.At) < 0.0002f || (next.At - current.At).Dot(gravity) < -0.0002f) { break; }
            if (bed.Landing(toWorld * current.At, toWorld * next.At) is { } contact) {
                points.Add((toWorld.AffineInverse() * contact, next.Normal, next.Face));
                onBed = true;
                break;
            }
            points.Add(next);
        }
        var samples = new Sample[points.Count];
        var length = 0f;
        for (var i = 1; i < points.Count; i++) { length += points[i].At.DistanceTo(points[i - 1].At); }
        var distance = 0f; var ml = 0f;
        for (var i = 0; i < points.Count; i++) {
            var p = points[i];
            var segment = i == 0 ? 0f : p.At.DistanceTo(points[i - 1].At);
            distance += segment;
            var t = distance / Mathf.Max(length, 0.001f);
            var width = Mathf.Lerp(0.009f + 0.003f * Mathf.Sin(seed * 1.7f), 0.0012f + 0.0003f * Mathf.Sin(seed * 0.7f + 1f), Mathf.Pow(t, 0.65f))
                * (0.9f + 0.1f * Mathf.Sin(seed * 1.37f + t * 9f)) / scale;
            width *= Mathf.SmoothStep(0f, 0.008f / scale, distance);
            var along = points[Math.Min(i + 1, points.Count - 1)].At - points[Math.Max(i - 1, 0)].At;
            var across = p.Normal.Cross(along).Normalized();
            var left = Closest(p.At - across * width * 0.5f, p.Face);
            var right = Closest(p.At + across * width * 0.5f, p.Face);
            if (i > 0) {
                var previous = samples[i - 1];
                var a = toWorld * previous.Left; var b = toWorld * previous.Right;
                var c = toWorld * (left.At + left.Normal * (Lift / scale)); var d = toWorld * (right.At + right.Normal * (Lift / scale));
                var area = ((b - a).Cross(c - a).Length() + (d - b).Cross(c - b).Length()) * 0.5f;
                ml += area * FilmDepth * 1000000f;
            }
            samples[i] = new Sample(p.At + p.Normal * (Lift / scale), p.Normal, left.At + left.Normal * (Lift / scale),
                right.At + right.Normal * (Lift / scale), left.Normal, right.Normal, distance, ml, width);
        }
        return new Path(samples, onBed, scale);
    }

    internal float DistanceTo(Vector3 point) => Closest(point, -1).At.DistanceTo(point);

    private (Vector3 At, Vector3 Normal, int Face) Closest(Vector3 point, int face) {
        var best = (At: Vector3.Zero, Normal: Vector3.Up, Face: -1);
        var distance = float.PositiveInfinity;
        void Visit(int index) {
            var f = _faces[index];
            var at = ClosestPoint(point, f.A, f.B, f.C);
            var d = point.DistanceSquaredTo(at);
            if (d >= distance) { return; }
            var w = Geometry3D.GetTriangleBarycentricCoords(at, f.A, f.B, f.C);
            var normal = (f.Na * w.X + f.Nb * w.Y + f.Nc * w.Z).Normalized();
            best = (at, normal, index); distance = d;
        }
        if (face < 0) { for (var i = 0; i < _faces.Length; i++) { Visit(i); } } else {
            Visit(face);
            var f = _faces[face];
            foreach (var index in _adjacent[f.Ia]) { Visit(index); }
            foreach (var index in _adjacent[f.Ib]) { Visit(index); }
            foreach (var index in _adjacent[f.Ic]) { Visit(index); }
        }
        return best;
    }

    private static Vector3 ClosestPoint(Vector3 p, Vector3 a, Vector3 b, Vector3 c) {
        var ab = b - a; var ac = c - a; var ap = p - a;
        var d1 = ab.Dot(ap); var d2 = ac.Dot(ap);
        if (d1 <= 0f && d2 <= 0f) { return a; }
        var bp = p - b; var d3 = ab.Dot(bp); var d4 = ac.Dot(bp);
        if (d3 >= 0f && d4 <= d3) { return b; }
        var vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f) { return a + ab * (d1 / (d1 - d3)); }
        var cp = p - c; var d5 = ab.Dot(cp); var d6 = ac.Dot(cp);
        if (d6 >= 0f && d5 <= d6) { return c; }
        var vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f) { return a + ac * (d2 / (d2 - d6)); }
        var va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) { return b + (c - b) * ((d4 - d3) / (d4 - d3 + d5 - d6)); }
        var inverse = 1f / (va + vb + vc);
        return a + ab * (vb * inverse) + ac * (vc * inverse);
    }
}
