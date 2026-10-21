namespace Scalpel.Tests.Patients;

[TestSuite, RequireGodotRuntime]
[TestCategory("liquid_contours")]
[TestCategory("smoke"), TestCategory("liquids")]
public class LiquidShapeTest {
    [TestCase]
    public void ADelayedBedUploadDoesNotRewindLiveFlow() {
        var outlet = new BloodFlow.Outlet(1) { Progress = 0.8f, AllowedProgress = 0.9f, CoatingMl = 0.2f };
        outlet.Uploaded(new BedLiquidSurface.RivuletState(0.6f, 0.4f, 0.1f));
        AssertFloat(outlet.Progress).OverrideFailureMessage("time advanced while the worker built its older snapshot").IsEqual(0.8f);
        AssertFloat(outlet.DrawnProgress).OverrideFailureMessage("release still waits for the actually uploaded front").IsEqual(0.4f);
        AssertFloat(outlet.AllowedProgress).OverrideFailureMessage("an old snapshot cannot reduce an already affordable front").IsEqual(0.9f);
        AssertFloat(outlet.CoatingMl).OverrideFailureMessage("uploading a snapshot does not consume the current dose").IsEqual(0.2f);
    }

    [TestCase]
    public void WorkerFailuresAreConsumedOnceAndAllowLaterWork() {
        var source = new TaskCompletionSource<int>();
        Task<int>? pending = source.Task;
        AssertBool(WorkerTask.TryTakeResult(ref pending, out _, out _))
            .OverrideFailureMessage("unfinished work keeps ownership of its captured buffers").IsFalse();
        AssertObject(pending).IsSame(source.Task);
        var failure = new InvalidOperationException("failed liquid build");
        source.SetException(failure);
        AssertBool(WorkerTask.TryTakeResult(ref pending, out var failedValue, out var caught)).IsTrue();
        AssertObject(caught).IsSame(failure);
        AssertInt(failedValue).OverrideFailureMessage("a failed value task returns the type default")
            .IsEqual(0);
        AssertObject(pending).OverrideFailureMessage("the failed handle is detached after one observation").IsNull();
        AssertBool(WorkerTask.TryTakeResult(ref pending, out _, out _))
            .OverrideFailureMessage("a fault cannot be consumed a second time").IsFalse();
        pending = Task.FromResult(7);
        AssertBool(WorkerTask.TryTakeResult(ref pending, out var value, out var successFailure)).IsTrue();
        AssertInt(value!).IsEqual(7);
        AssertObject(successFailure).IsNull();
        Task? draw = Task.FromException(failure);
        AssertBool(WorkerTask.TryTakeTaskResult(ref draw, out var drawFailure)).IsTrue();
        AssertObject(drawFailure).OverrideFailureMessage("non-generic draw faults are observed without throwing")
            .IsSame(failure);
        AssertObject(draw).OverrideFailureMessage("mesh-only worker handles are also detached").IsNull();
    }

    [TestCase]
    public void LiquidGridInterpolatesAnObliqueBoundary() {
        var surface = new LiquidSurface();
        surface.Field(new Rect2(Vector2.Zero, Vector2.One), 8, 8, p => 0.813f - p.X - p.Y, (p, _) => new Vector3(p.X, 0f, p.Y));
        using var mesh = surface.Upload();
        var points = mesh.GetFaces();
        AssertInt(points.Length).IsGreater(0);
        AssertBool(points.All(p => p.X + p.Z <= 0.81301f)).OverrideFailureMessage("no fluid triangle crosses its supporting boundary").IsTrue();
        AssertBool(points.Any(p => Mathf.Abs(p.X + p.Z - 0.813f) < 0.00001f))
            .OverrideFailureMessage("the contour reaches the boundary instead of stopping at stair steps").IsTrue();

        surface.Clear();
        surface.Field(new Rect2(-0.1f, -0.1f, 0.2f, 0.2f), 8, 8,
            p => 0.025f - p.X, (p, _) => new Vector3(p.X, 0f, p.Y));
        using var clippedMesh = surface.Upload();
        var clippedPoints = clippedMesh.GetFaces();
        AssertFloat(clippedPoints.Max(p => p.X)).OverrideFailureMessage("the supported edge is interpolated").IsEqualApprox(0.025f, 0.000001f);
        AssertFloat(clippedPoints.Min(p => p.X)).OverrideFailureMessage("contact does not squeeze the opposite free edge")
            .IsLess(-0.09f);
    }

    [TestCase]
    public void LiquidContoursFollowCurvedInnerAndOuterBoundaries() {
        const float Inner = 0.037f, Outer = 0.083f;
        var surface = new LiquidSurface();
        surface.Field(new Rect2(-0.1f, -0.1f, 0.2f, 0.2f), 16, 16,
            point => Mathf.Min(point.Length() - Inner, Outer - point.Length()),
            (point, _) => new Vector3(point.X, 0f, point.Y));
        using var mesh = surface.Upload();
        var radii = mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array()
            .Select(point => new Vector2(point.X, point.Z).Length()).ToArray();
        AssertInt(radii.Length).IsGreater(0);
        AssertFloat(radii.Min()).OverrideFailureMessage("liquid does not cross a curved inner obstacle").IsGreaterEqual(Inner - 0.000025f);
        AssertFloat(radii.Max()).OverrideFailureMessage($"liquid stays inside the curved outer support (maximum radius {radii.Max()})").IsLessEqual(Outer + 0.000025f);
        AssertBool(radii.Any(radius => Mathf.Abs(radius - Inner) < 0.0001f)).IsTrue();
        AssertBool(radii.Any(radius => Mathf.Abs(radius - Outer) < 0.0001f)).IsTrue();
    }

    [TestCase]
    public void LiquidRibbonKeepsANarrowBentOpeningConnected() {
        var surface = new LiquidSurface();
        var a = new Vector2(0.011f, 0.007f);
        var corner = new Vector2(0.035f, 0.029f);
        var b = new Vector2(0.017f, 0.054f);
        float Inside(Vector2 p) => 0.0006f - Mathf.Min(p.DistanceTo(Geometry2D.GetClosestPointToSegment(p, a, corner)),
            p.DistanceTo(Geometry2D.GetClosestPointToSegment(p, corner, b)));
        var recordedPath = Enumerable.Range(0, 51).Select(i => a.Lerp(corner, i / 50f))
            .Concat(Enumerable.Range(1, 50).Select(i => corner.Lerp(b, i / 50f))).ToArray();
        surface.Ribbon(recordedPath, 0.005f, Inside, (p, _) => new Vector3(p.X, 0f, p.Y));
        using var mesh = surface.Upload();
        var vertices = mesh.GetFaces();
        AssertInt(vertices.Length).IsGreater(30);
        AssertInt(vertices.Length).OverrideFailureMessage("dense input samples do not produce excessive planar geometry").IsLess(2000);
        bool Covered(Vector2 at) => Enumerable.Range(0, vertices.Length / 3).Any(t => Contains(at,
            new Vector2(vertices[t * 3].X, vertices[t * 3].Z), new Vector2(vertices[t * 3 + 1].X, vertices[t * 3 + 1].Z),
            new Vector2(vertices[t * 3 + 2].X, vertices[t * 3 + 2].Z)));
        foreach (var (from, to) in new[] { (a, corner), (corner, b) }) {
            for (var i = 1; i < 20; i++) {
                AssertBool(Covered(from.Lerp(to, i / 20f)))
                    .OverrideFailureMessage($"the narrow bent pool covers {from.Lerp(to, i / 20f)} on {from} to {to}, step {i}").IsTrue();
            }
        }
        AssertBool(Covered(corner)).OverrideFailureMessage("the original turn remains covered rather than cut off by a chord").IsTrue();
    }

    [TestCase]
    public void LiquidOutletsVaryTheirTimingAndRetainQueuedVolume() {
        var pulse = new LiquidPulse(123);
        for (var i = 0; i < 60; i++) { pulse.Add(0.1f); pulse.Advance(1f / 60f, false); }
        AssertFloat(pulse.Pending).IsEqualApprox(6f, 0.00001f);
        var released = pulse.Advance(1f / 60f, true);
        AssertFloat(released).IsEqualApprox(6f, 0.00001f);
        var intervals = new HashSet<int>();
        var widths = new HashSet<float>();
        var elapsed = 0;
        var extra = 0f;
        for (var i = 0; i < 240; i++) {
            pulse.Add(0.01f);
            elapsed++;
            var amount = pulse.Advance(1f / 60f, true);
            if (amount <= 0f) { continue; }
            extra += amount;
            intervals.Add(elapsed);
            widths.Add(pulse.Width);
            elapsed = 0;
        }
        AssertInt(intervals.Count).OverrideFailureMessage("drops do not detach at metronomic intervals").IsGreater(3);
        AssertInt(widths.Count).OverrideFailureMessage("necks change thickness between releases").IsGreater(3);
        AssertFloat(extra + pulse.Pending).IsEqualApprox(2.4f, 0.00001f);
    }
    [TestCase(1)]
    [TestCase(7)]
    public void FloorPoolMeshContainsTheDepositedMilliliters(int drops) {
        var surface = new LiquidSurface();
        surface.Patch(LiquidSurface.Outline(Mathf.Sqrt(drops * BloodFlow.PoolAreaPerDrop), 1.3f), BloodFlow.PoolThickness);
        using var mesh = surface.Upload();
        using var arrays = mesh.SurfaceGetArrays(0);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var cubicMeters = 0f;
        for (var i = 0; i < vertices.Length; i += 3) {
            var a = vertices[i]; var b = vertices[i + 1]; var c = vertices[i + 2];
            var area = Mathf.Abs(new Vector2(b.X - a.X, b.Z - a.Z).Cross(new Vector2(c.X - a.X, c.Z - a.Z))) * 0.5f;
            cubicMeters += area * (a.Y + b.Y + c.Y) / 3f;
        }
        AssertFloat(cubicMeters * 1000000f).OverrideFailureMessage($"the rendered floor-pool profile integrates to the actual deposited dose: {cubicMeters * 1000000f} ml for {drops * BloodFlow.DropMl} ml")
            .IsEqualApprox(drops * BloodFlow.DropMl, 0.0002f);
    }

    [TestCase(1f, false)]
    [TestCase(2f, true)]
    public void GrowingStreakMeshUsesItsActualDose(float scale, bool curved) {
        const float Width = 0.006f, Length = 0.0025f;
        var edge = curved ? -0.0003f : 0f;
        var perimeter = 2f * new Vector2(Width * 0.5f, edge).Length();
        var capacity = perimeter * Length * scale * scale * BodyLiquidSurface.FilmDepth * 1000000f;
        BodyLiquidSurface.SkinWeights Bone(int index) => new(new Vector4I(index, 0, 0, 0), new Vector4(1f, 0f, 0f, 0f));
        BodyLiquidSurface.Sample Sample(float z, float ml) => new(new Vector3(0f, 0f, z), Vector3.Up,
            new Vector3(-Width * 0.5f, edge, z), new Vector3(Width * 0.5f, edge, z), Vector3.Up, Vector3.Up,
            Bone(0), Bone(1), Bone(2), z, ml, Width);
        var start = Sample(0f, 0f);
        var middle = Sample(Length * 0.5f, 0f);
        var end = Sample(Length, 0f);
        middle = middle with { Ml = BodyLiquidSurface.Path.SegmentMl(start, middle, 1f, scale) };
        end = end with { Ml = middle.Ml + BodyLiquidSurface.Path.SegmentMl(middle, end, 1f, scale) };
        var path = new BodyLiquidSurface.Path([start, middle, end], false, scale);
        AssertFloat(path.VolumeAt(0f)).OverrideFailureMessage("a curved cross-section does not invent volume before the front advances").IsEqual(0f);
        foreach (var fraction in new[] { 0.1f, 0.5f, 0.75f, 1f }) {
            var distance = Length * fraction;
            var flat = new LiquidSurface(withUv: true);
            var skinned = new LiquidSurface(withUv: true, withSkin: true, withUv2: true);
            path.Draw(flat, distance);
            path.Draw(skinned, distance, Transform3D.Identity, Vector2.One);
            using var flatMesh = flat.Upload();
            using var indexedMesh = skinned.Upload();
            using var arrays = flatMesh.SurfaceGetArrays(0);
            using var indexedArrays = indexedMesh.SurfaceGetArrays(0);
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var uv = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var flatNormals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var flatFaces = flatMesh.GetFaces();
            var indexedVertices = indexedArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var indexedNormals = indexedArrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var indexedUv = indexedArrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            var indexedUv2 = indexedArrays[(int)Mesh.ArrayType.TexUV2].AsVector2Array();
            var indices = indexedArrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            var indexedFaces = indexedMesh.GetFaces();
            var bones = indexedArrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
            var skinWeights = indexedArrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
            AssertInt(uv.Length).IsEqual(vertices.Length);
            AssertInt(indices.Length).OverrideFailureMessage("the indexed runoff strip expands to the same triangle count as flat facets")
                .IsEqual(flatFaces.Length);
            AssertInt(indexedFaces.Length).OverrideFailureMessage("both meshes expose the same actual triangle-expanded face vertices")
                .IsEqual(flatFaces.Length);
            AssertInt(indexedVertices.Length).OverrideFailureMessage("shared rows use at most half as many uploaded vertices as flat facets")
                .IsLessEqual(vertices.Length / 2);
            AssertInt(indexedUv2.Length).OverrideFailureMessage("indexed runoff retains the site-map UV2 at every shared row vertex")
                .IsEqual(indexedVertices.Length);
            AssertInt(indexedArrays[(int)Mesh.ArrayType.Bones].AsInt32Array().Length)
                .OverrideFailureMessage("each indexed runoff vertex carries four skin bind indices").IsEqual(indexedVertices.Length * 4);
            AssertInt(skinWeights.Length)
                .OverrideFailureMessage("each indexed runoff vertex carries four skin weights").IsEqual(indexedVertices.Length * 4);
            var segmentFraction = fraction <= 0.5f ? fraction * 2f : (fraction - 0.5f) * 2f;
            var frontWidth = Mathf.SmoothStep(0f, 1f, segmentFraction);
            var frontLeft = indexedVertices.Length - 3;
            var frontRight = indexedVertices.Length - 1;
            float Influence(int vertex, int bone) {
                var value = 0f;
                for (var slot = 0; slot < 4; slot++) {
                    if (bones[vertex * 4 + slot] == bone) { value += skinWeights[vertex * 4 + slot]; }
                }
                return value;
            }
            AssertFloat(Influence(frontLeft, 0)).OverrideFailureMessage("the partial left front blends from centre skin weights toward the full edge by the geometric smoothstep")
                .IsEqualApprox(frontWidth, 0.00001f);
            AssertFloat(Influence(frontLeft, 1)).OverrideFailureMessage("the partial left front retains the complementary centre skin weight")
                .IsEqualApprox(1f - frontWidth, 0.00001f);
            AssertFloat(Influence(frontRight, 2)).OverrideFailureMessage("the partial right front blends from centre skin weights toward its full edge by the geometric smoothstep")
                .IsEqualApprox(frontWidth, 0.00001f);
            AssertFloat(Influence(frontRight, 1)).OverrideFailureMessage("the partial right front retains the complementary centre skin weight")
                .IsEqualApprox(1f - frontWidth, 0.00001f);
            for (var i = 0; i < flatFaces.Length; i++) {
                var flatVertex = flatFaces[i];
                AssertFloat(indexedFaces[i].DistanceTo(flatVertex))
                    .OverrideFailureMessage($"indexed and flat meshes expose identical actual face positions at vertex {i}")
                    .IsLess(0.000001f);
                var indexedVertexIndex = indices[i];
                AssertFloat(indexedVertices[indexedVertexIndex].DistanceTo(vertices[i]))
                    .OverrideFailureMessage($"indexed vertex expansion matches flat surface-array order at vertex {i}")
                    .IsLess(0.000001f);
                AssertFloat(indexedNormals[indexedVertexIndex].DistanceTo(flatNormals[i]))
                    .OverrideFailureMessage($"indexed and flat runoff triangles have identical normals at expanded vertex {i}")
                    .IsLess(0.000001f);
                AssertFloat(indexedUv[indexedVertexIndex].DistanceTo(uv[i]))
                    .OverrideFailureMessage($"indexed and flat runoff triangles have identical cross-section UVs at expanded vertex {i}")
                    .IsLess(0.000001f);
            }
            var area = 0f;
            for (var i = 0; i < vertices.Length; i += 3) {
                var triangle = (vertices[i + 1] - vertices[i]).Cross(vertices[i + 2] - vertices[i]).Length() * 0.5f;
                var a = 2f * uv[i].X - 1f; var b = 2f * uv[i + 1].X - 1f; var c = 2f * uv[i + 2].X - 1f;
                // Exact moments of the shader's rounded depth over each actual rendered triangle.
                var meanSquare = (a * a + b * b + c * c + a * b + b * c + c * a) / 6f;
                var fourth = 0f;
                for (var n = 0; n <= 4; n++) {
                    for (var m = 0; m <= 4 - n; m++) { fourth += Mathf.Pow(a, n) * Mathf.Pow(b, m) * Mathf.Pow(c, 4 - n - m); }
                }
                area += triangle * 1.875f * (1f - 2f * meanSquare + fourth / 15f);
            }
            var renderedMl = area * scale * scale * BodyLiquidSurface.FilmDepth * 1000000f;
            AssertFloat(path.VolumeAt(distance)).OverrideFailureMessage("the growing tip reports the volume of its actual triangles")
                .IsEqualApprox(renderedMl, 0.000001f);
            AssertFloat(path.DistanceFor(renderedMl)).OverrideFailureMessage("the supplied dose limits the drawn front")
                .IsEqualApprox(distance, Length * 0.0001f);
        }
        AssertFloat(path.VolumeAt(Length) - path.VolumeAt(Length * 0.999f))
            .OverrideFailureMessage("finishing a segment has no abrupt volume jump").IsLess(capacity * 0.003f);
    }

    // Centreline samples lie on shared triangle edges. Permit floating point round-off at those edges.
    private static bool Contains(Vector2 p, Vector2 a, Vector2 b, Vector2 c) {
        var area = (b - a).Cross(c - a);
        if (Mathf.Abs(area) < 1e-12f) { return false; }
        var ab = (b - a).Cross(p - a);
        var bc = (c - b).Cross(p - b);
        var ca = (a - c).Cross(p - c);
        return area > 0f ? Mathf.Min(ab, Mathf.Min(bc, ca)) >= -1e-10f : Mathf.Max(ab, Mathf.Max(bc, ca)) <= 1e-10f;
    }

}
