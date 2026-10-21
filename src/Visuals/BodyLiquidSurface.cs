namespace Scalpel.Visuals;

/// <summary>Immutable model triangles for attached runoff. Paths are traced on workers; no physics queries or
/// model reads occur during gameplay. Shared vertices let each short downhill step visit only neighbouring faces.</summary>
internal sealed class BodyLiquidSurface {
    internal const float FilmDepth = 0.00012f;
    private const float Lift = FilmDepth;
    internal readonly record struct SkinWeights(Vector4I Bones, Vector4 Weights);
    private readonly record struct Face(Vector3 A, Vector3 B, Vector3 C, Vector3 Na, Vector3 Nb, Vector3 Nc,
        SkinWeights Wa, SkinWeights Wb, SkinWeights Wc, int Ia, int Ib, int Ic);
    private readonly Face[] _faces;
    private readonly List<int>[] _adjacent;
    internal Transform3D SiteToMesh { get; }
    internal sealed record SkinPose(Transform3D MeshToWorld, Transform3D[] BoneToWorld, bool[] ValidBind) {
        internal Vector3 Deform(Vector3 meshPoint, SkinWeights influence) {
            var result = Vector3.Zero;
            var total = 0f;
            void Add(int bind, float weight) {
                if (weight > 0f && bind >= 0 && bind < BoneToWorld.Length && ValidBind[bind]) {
                    result += (BoneToWorld[bind] * meshPoint) * weight;
                    total += weight;
                }
            }
            Add(influence.Bones.X, influence.Weights.X); Add(influence.Bones.Y, influence.Weights.Y);
            Add(influence.Bones.Z, influence.Weights.Z); Add(influence.Bones.W, influence.Weights.W);
            return total > 0f ? result / total : MeshToWorld * meshPoint;
        }
    }
    internal readonly record struct Sample(Vector3 At, Vector3 Normal, Vector3 Left, Vector3 Right, Vector3 LeftNormal,
        Vector3 RightNormal, SkinWeights AtSkin, SkinWeights LeftSkin, SkinWeights RightSkin,
        float Distance, float Ml, float Width);

    internal sealed record Path(Sample[] Samples, bool OnBed, float Scale = 1f) {
        internal float InletWidth => Samples.Max(s => s.Width);
        internal float Length => Samples.Length == 0 ? 0f : Samples[^1].Distance;
        internal float CapacityMl => Samples.Length == 0 ? 0f : Samples[^1].Ml;
        internal Vector3 End => Samples.Length == 0 ? Vector3.Zero : Samples[^1].At;
        internal Sample EndSample => Samples.Length == 0 ? default : Samples[^1];
        internal float DistanceFor(float ml) {
            if (ml >= CapacityMl) { return Length; }
            for (var i = 1; i < Samples.Length; i++) {
                if (ml >= Samples[i].Ml) { continue; }
                var a = Samples[i - 1]; var b = Samples[i];
                var low = 0f; var high = 1f;
                for (var n = 0; n < 14; n++) {
                    var f = (low + high) * 0.5f;
                    if (a.Ml + SegmentMl(a, b, f, Scale) <= ml) { low = f; } else { high = f; }
                }
                return Mathf.Lerp(a.Distance, b.Distance, low);
            }
            return Length;
        }
        internal float VolumeAt(float distance) {
            for (var i = 1; i < Samples.Length; i++) {
                if (distance >= Samples[i].Distance) { continue; }
                var a = Samples[i - 1]; var b = Samples[i];
                return a.Ml + SegmentMl(a, b, Mathf.Clamp((distance - a.Distance) / (b.Distance - a.Distance), 0f, 1f), Scale);
            }
            return CapacityMl;
        }
        private static (Vector3 Left, Vector3 Right) Front(Sample a, Sample b, float f) {
            var left = a.Left.Lerp(b.Left, f); var right = a.Right.Lerp(b.Right, f);
            var middle = a.At.Lerp(b.At, f);
            // Widen continuously as the segment fills; no half-strip volume jump at a sample boundary.
            var width = Mathf.SmoothStep(0f, 1f, f);
            return (middle.Lerp(left, width), middle.Lerp(right, width));
        }
        internal static float SegmentMl(Sample a, Sample b, float f, float scale) {
            var (left, right) = Front(a, b, f);
            var middle = a.At.Lerp(b.At, f);
            static float TwiceArea(Vector3 p, Vector3 q, Vector3 r) => (q - p).Cross(r - p).Length();
            // Exact quartic-profile means for UV triangles (0,.5,0), (.5,.5,0), (.5,1,.5), (1,1,.5).
            // A centre contact prevents a narrow ribbon cutting a chord through rounded skin.
            var area = TwiceArea(a.Left, a.At, left) * 0.625f + TwiceArea(a.At, middle, left) * 1.375f
                + TwiceArea(a.At, a.Right, middle) * 1.375f + TwiceArea(a.Right, right, middle) * 0.625f;
            return area * 0.5f * scale * scale * FilmDepth * 1000000f;
        }
        internal void Draw(LiquidSurface surface, float distance, Transform3D siteToMesh, Vector2 siteSize) {
            surface.BeginRibbon();
            for (var i = 1; i < Samples.Length && Samples[i - 1].Distance < distance; i++) {
                var a = Samples[i - 1]; var b = Samples[i];
                var f = Mathf.Clamp((distance - a.Distance) / (b.Distance - a.Distance), 0f, 1f);
                var (left, right) = Front(a, b, f);
                var nl = a.LeftNormal.Lerp(b.LeftNormal, f).Normalized();
                var nr = a.RightNormal.Lerp(b.RightNormal, f).Normalized();
                var middle = a.At.Lerp(b.At, f);
                var nm = a.Normal.Lerp(b.Normal, f).Normalized();
                var nla = (siteToMesh.Basis * a.LeftNormal).Normalized();
                var na = (siteToMesh.Basis * a.Normal).Normalized();
                var nra = (siteToMesh.Basis * a.RightNormal).Normalized();
                var nlb = (siteToMesh.Basis * nl).Normalized();
                var nrb = (siteToMesh.Basis * nr).Normalized();
                var nmb = (siteToMesh.Basis * nm).Normalized();
                if (!surface.SupportsSkin) {
                    var uvMiddle = Vector2.Right * 0.5f;
                    surface.Facet(siteToMesh * a.Left, siteToMesh * a.At, siteToMesh * left, nla, na, nlb,
                        Vector2.Zero, uvMiddle, Vector2.Zero);
                    surface.Facet(siteToMesh * a.At, siteToMesh * middle, siteToMesh * left, na, nmb, nlb,
                        uvMiddle, uvMiddle, Vector2.Zero);
                    surface.Facet(siteToMesh * a.At, siteToMesh * a.Right, siteToMesh * middle, na, nra, nmb,
                        uvMiddle, Vector2.Right, uvMiddle);
                    surface.Facet(siteToMesh * a.Right, siteToMesh * right, siteToMesh * middle, nra, nrb, nmb,
                        Vector2.Right, Vector2.Right, uvMiddle);
                    continue;
                }
                var mapLeft = MapUv(left, siteSize); var mapMiddle = MapUv(middle, siteSize);
                var skinMiddle = BlendSkin(a.AtSkin, b.AtSkin, f);
                var edgeLeftSkin = BlendSkin(a.LeftSkin, b.LeftSkin, f);
                var skinLeft = BlendSkin(skinMiddle, edgeLeftSkin, Mathf.SmoothStep(0f, 1f, f));
                var mapRight = MapUv(right, siteSize);
                var edgeRightSkin = BlendSkin(a.RightSkin, b.RightSkin, f);
                var skinRight = BlendSkin(skinMiddle, edgeRightSkin, Mathf.SmoothStep(0f, 1f, f));
                if (i == 1) {
                    surface.RibbonRow(siteToMesh * a.Left, siteToMesh * a.At, siteToMesh * a.Right,
                        nla, na, nra, MapUv(a.Left, siteSize), MapUv(a.At, siteSize), MapUv(a.Right, siteSize),
                        a.LeftSkin, a.AtSkin, a.RightSkin);
                }
                surface.RibbonRow(siteToMesh * left, siteToMesh * middle, siteToMesh * right,
                    nlb, nmb, nrb, mapLeft, mapMiddle, mapRight, skinLeft, skinMiddle, skinRight);
            }
        }
        internal void Draw(LiquidSurface surface, float distance) => Draw(surface, distance, Transform3D.Identity, Vector2.One);
    }

    internal BodyLiquidSurface(IReadOnlyList<Vector3> vertices, IReadOnlyList<Vector3> normals)
        : this(vertices, normals, Enumerable.Repeat(new SkinWeights(Vector4I.Zero, new Vector4(1f, 0f, 0f, 0f)),
            vertices.Count).ToArray(), Vector2.One, Transform3D.Identity) { }

    internal BodyLiquidSurface(IReadOnlyList<Vector3> vertices, IReadOnlyList<Vector3> normals,
        IReadOnlyList<SkinWeights> skin, Vector2 siteSize, Transform3D siteToMesh) {
        SiteToMesh = siteToMesh;
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
                skin[n], skin[n + 1], skin[n + 2], Vertex(vertices[n], i), Vertex(vertices[n + 1], i), Vertex(vertices[n + 2], i));
        }
        _adjacent = adjacent.ToArray();
    }

    internal SkinPose CaptureSkinPose(MeshInstance3D skinMesh, Skeleton3D skeleton) {
        if (skinMesh.Skin is not { } skin) { return new SkinPose(skinMesh.GlobalTransform, [], []); }
        var bind = new Transform3D[skin.GetBindCount()];
        var valid = new bool[bind.Length];
        for (var i = 0; i < bind.Length; i++) {
            var bone = skin.GetBindBone(i);
            if (bone < 0 && skin.GetBindName(i).ToString().Length > 0) { bone = skeleton.FindBone(skin.GetBindName(i)); }
            if (bone >= 0 && bone < skeleton.GetBoneCount()) {
                bind[i] = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone) * skin.GetBindPose(i);
                valid[i] = true;
            }
        }
        return new SkinPose(skinMesh.GlobalTransform, bind, valid);
    }

    internal Path Trace(Vector3 source, Vector3 gravity, int seed, Transform3D toWorld, BedLiquidSurface bed,
        SkinPose? skinPose = null) {
        var first = Closest(source, -1);
        if (first.At.DistanceTo(source) > 0.03f) { return new Path([], false); }
        var scale = toWorld.Basis.Scale.X;
        var tangent = gravity.Slide(first.Normal);
        var outward = (seed / 8) switch { 0 => Vector3.Left, 1 => Vector3.Right, 2 => Vector3.Forward, _ => Vector3.Back };
        if (tangent.LengthSquared() < 0.001f) { tangent = outward.Slide(first.Normal); }
        // Overlap the broad film's lip so contact cannot show a pale seam between the pool and its narrower outlet.
        var inlet = Closest(first.At - tangent.Normalized() * 0.008f / scale, first.Face);
        var points = new List<(Vector3 At, Vector3 Normal, SkinWeights Skin, int Face)>(96) { inlet };
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
            var currentWorld = skinPose?.Deform(SiteToMesh * current.At, current.Skin) ?? toWorld * current.At;
            var nextWorld = skinPose?.Deform(SiteToMesh * next.At, next.Skin) ?? toWorld * next.At;
            if (bed.Landing(currentWorld, nextWorld) is { } contact) {
                var segment = nextWorld - currentWorld;
                var fraction = segment.LengthSquared() > 0f
                    ? Mathf.Clamp((contact - currentWorld).Dot(segment) / segment.LengthSquared(), 0f, 1f)
                    : 0f;
                points.Add((current.At.Lerp(next.At, fraction), current.Normal.Lerp(next.Normal, fraction).Normalized(),
                    BlendSkin(current.Skin, next.Skin, fraction), next.Face));
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
            // The broad film already follows the flank; this ribbon is only its final attached tail.
            var width = Mathf.Lerp(0.0045f + 0.0015f * Mathf.Sin(seed * 1.7f), 0.0006f + 0.00015f * Mathf.Sin(seed * 0.7f + 1f), Mathf.Pow(t, 0.65f))
                * (0.9f + 0.1f * Mathf.Sin(seed * 1.37f + t * 9f)) / scale;
            width *= Mathf.SmoothStep(0f, 0.008f / scale, distance);
            var along = points[Math.Min(i + 1, points.Count - 1)].At - points[Math.Max(i - 1, 0)].At;
            var across = p.Normal.Cross(along).Normalized();
            var left = Closest(p.At - across * width * 0.5f, p.Face);
            var right = Closest(p.At + across * width * 0.5f, p.Face);
            var sample = new Sample(p.At + p.Normal * (Lift / scale), p.Normal, left.At + left.Normal * (Lift / scale),
                right.At + right.Normal * (Lift / scale), left.Normal, right.Normal, p.Skin, left.Skin, right.Skin, distance, 0f, width);
            if (i > 0) { ml += Path.SegmentMl(samples[i - 1], sample, 1f, scale); }
            samples[i] = sample with { Ml = ml };
        }
        return new Path(samples, onBed, scale);
    }

    internal float DistanceTo(Vector3 point) => Closest(point, -1).At.DistanceTo(point);
    internal SkinWeights SkinAt(Vector3 point) => Closest(point, -1).Skin;

    internal Vector3 Deform(Vector3 sitePoint, SkinWeights influence, MeshInstance3D skinMesh, Skeleton3D skeleton) {
        var meshPoint = SiteToMesh * sitePoint;
        if (skinMesh.Skin is not { } skin || influence.Weights.LengthSquared() <= 0f) {
            return skinMesh.GlobalTransform * meshPoint;
        }
        var posed = Vector3.Zero;
        var total = 0f;
        void Add(int bind, float weight) {
            if (weight <= 0f || bind < 0 || bind >= skin.GetBindCount()) { return; }
            var bone = skin.GetBindBone(bind);
            if (bone < 0 && skin.GetBindName(bind).ToString().Length > 0) { bone = skeleton.FindBone(skin.GetBindName(bind)); }
            if (bone < 0 || bone >= skeleton.GetBoneCount()) { return; }
            var current = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone);
            posed += (skinMesh.GlobalTransform.AffineInverse() * current * skin.GetBindPose(bind) * meshPoint) * weight;
            total += weight;
        }
        Add(influence.Bones.X, influence.Weights.X); Add(influence.Bones.Y, influence.Weights.Y);
        Add(influence.Bones.Z, influence.Weights.Z); Add(influence.Bones.W, influence.Weights.W);
        return total > 0f ? skinMesh.GlobalTransform * (posed / total) : skinMesh.GlobalTransform * meshPoint;
    }

    private (Vector3 At, Vector3 Normal, SkinWeights Skin, int Face) Closest(Vector3 point, int face) {
        var best = (At: Vector3.Zero, Normal: Vector3.Up, Skin: default(SkinWeights), Face: -1);
        var distance = float.PositiveInfinity;
        void Visit(int index) {
            var f = _faces[index];
            var at = ClosestPoint(point, f.A, f.B, f.C);
            var d = point.DistanceSquaredTo(at);
            if (d >= distance) { return; }
            var w = Geometry3D.GetTriangleBarycentricCoords(at, f.A, f.B, f.C);
            var normal = (f.Na * w.X + f.Nb * w.Y + f.Nc * w.Z).Normalized();
            best = (at, normal, BlendSkin(f.Wa, f.Wb, f.Wc, w), index); distance = d;
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

    private static Vector2 MapUv(Vector3 at, Vector2 size) => new(at.X / size.X + 0.5f, at.Z / size.Y + 0.5f);

    private static SkinWeights BlendSkin(SkinWeights a, SkinWeights b, float f) {
        if (f <= 0f) { return a; }
        if (f >= 1f) { return b; }
        return BlendSkin(a, b, a, new Vector3(1f - f, f, 0f));
    }

    private static SkinWeights BlendSkin(SkinWeights a, SkinWeights b, SkinWeights c, Vector3 bary) {
        Span<int> bones = stackalloc int[12];
        Span<float> weights = stackalloc float[12];
        var count = 0;
        Accumulate(a, bary.X, bones, weights, ref count);
        Accumulate(b, bary.Y, bones, weights, ref count);
        Accumulate(c, bary.Z, bones, weights, ref count);
        Span<int> topBones = stackalloc int[4];
        Span<float> topWeights = stackalloc float[4];
        var topCount = 0;
        var sum = 0f;
        for (var i = 0; i < count; i++) {
            var value = weights[i];
            var position = topCount;
            while (position > 0 && value > topWeights[position - 1]) { position--; }
            if (position >= 4) { continue; }
            if (topCount < 4) { topCount++; }
            for (var j = topCount - 1; j > position; j--) {
                topWeights[j] = topWeights[j - 1]; topBones[j] = topBones[j - 1];
            }
            topWeights[position] = value; topBones[position] = bones[i];
        }
        for (var i = 0; i < topCount; i++) { sum += topWeights[i]; }
        if (sum <= 0f) { return new SkinWeights(Vector4I.Zero, new Vector4(1f, 0f, 0f, 0f)); }
        var resultBones = new Vector4I(); var resultWeights = Vector4.Zero;
        for (var i = 0; i < topCount; i++) {
            var value = topWeights[i] / sum;
            switch (i) {
                case 0: resultBones.X = topBones[i]; resultWeights.X = value; break;
                case 1: resultBones.Y = topBones[i]; resultWeights.Y = value; break;
                case 2: resultBones.Z = topBones[i]; resultWeights.Z = value; break;
                case 3: resultBones.W = topBones[i]; resultWeights.W = value; break;
            }
        }
        return new SkinWeights(resultBones, resultWeights);
    }

    private static void Accumulate(SkinWeights value, float scale, Span<int> bones, Span<float> weights, ref int count) {
        if (scale <= 0f) { return; }
        AccumulateOne(value.Bones.X, value.Weights.X * scale, bones, weights, ref count);
        AccumulateOne(value.Bones.Y, value.Weights.Y * scale, bones, weights, ref count);
        AccumulateOne(value.Bones.Z, value.Weights.Z * scale, bones, weights, ref count);
        AccumulateOne(value.Bones.W, value.Weights.W * scale, bones, weights, ref count);
    }

    private static void AccumulateOne(int bone, float weight, Span<int> bones, Span<float> weights, ref int count) {
        if (weight <= 0f || bone < 0) { return; }
        for (var i = 0; i < count; i++) {
            if (bones[i] == bone) { weights[i] += weight; return; }
        }
        bones[count] = bone;
        weights[count] = weight;
        count++;
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
