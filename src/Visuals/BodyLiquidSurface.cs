namespace Scalpel.Visuals;

/// <summary>The body model's skin as it lies at rest, in site space: three corners per triangle with their outward
/// normals and bone weights. The body's <see cref="SurfaceFilm"/> spreads blood over it, and its points are placed on
/// the posed body through the skeleton.</summary>
internal sealed class BodyLiquidSurface {
    internal readonly record struct SkinWeights(Vector4I Bones, Vector4 Weights);
    internal IReadOnlyList<Vector3> Corners { get; }
    internal IReadOnlyList<Vector3> Normals { get; }
    internal IReadOnlyList<SkinWeights> Skin { get; }
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

    internal BodyLiquidSurface(IReadOnlyList<Vector3> corners, IReadOnlyList<Vector3> normals,
        IReadOnlyList<SkinWeights> skin, Transform3D siteToMesh) {
        Corners = corners;
        Normals = normals;
        Skin = skin;
        SiteToMesh = siteToMesh;
    }

    /// <summary>The skeleton's bone each of the skin's binds poses, -1 for one it doesn't have. Looked up once: by
    /// name where a bind names its bone, which costs a string per bind.</summary>
    internal static int[] ResolveBinds(MeshInstance3D skinMesh, Skeleton3D skeleton) {
        if (skinMesh.Skin is not { } skin) { return []; }
        var bones = new int[skin.GetBindCount()];
        for (var i = 0; i < bones.Length; i++) {
            var bone = skin.GetBindBone(i);
            if (bone < 0 && skin.GetBindName(i).ToString().Length > 0) { bone = skeleton.FindBone(skin.GetBindName(i)); }
            bones[i] = bone >= 0 && bone < skeleton.GetBoneCount() ? bone : -1;
        }
        return bones;
    }

    internal static SkinPose CaptureSkinPose(MeshInstance3D skinMesh, Skeleton3D skeleton) =>
        CaptureSkinPose(skinMesh, skeleton, ResolveBinds(skinMesh, skeleton));

    /// <summary>The skin's pose now, from <paramref name="bindBones"/> (<see cref="ResolveBinds"/>).</summary>
    internal static SkinPose CaptureSkinPose(MeshInstance3D skinMesh, Skeleton3D skeleton, int[] bindBones) {
        if (skinMesh.Skin is not { } skin) { return new SkinPose(skinMesh.GlobalTransform, [], []); }
        var bind = new Transform3D[bindBones.Length];
        var valid = new bool[bind.Length];
        var skeletonToWorld = skeleton.GlobalTransform;
        for (var i = 0; i < bind.Length; i++) {
            if (bindBones[i] < 0) { continue; }
            bind[i] = skeletonToWorld * skeleton.GetBoneGlobalPose(bindBones[i]) * skin.GetBindPose(i);
            valid[i] = true;
        }
        return new SkinPose(skinMesh.GlobalTransform, bind, valid);
    }

    internal static SkinWeights BlendSkin(SkinWeights a, SkinWeights b, SkinWeights c, Vector3 bary) {
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
}
