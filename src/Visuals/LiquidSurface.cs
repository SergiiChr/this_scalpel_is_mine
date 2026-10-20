namespace Scalpel.Visuals;

/// <summary>Continuous liquid footprints. A boundary cuts the free outline instead of squeezing the whole pool;
/// sampled wound boundaries interpolate their crossings rather than dropping complete grid squares.</summary>
internal sealed class LiquidSurface(bool withUv = false) {
    // Integral of Patch's .88 inner ring, .65 central height and .15 rim, divided by footprint area.
    internal const float PatchMeanDepth = 0.5924f;
    private readonly List<Vector3> _vertices = [];
    private readonly List<Vector3> _normals = [];
    private readonly List<Vector2>? _uv = withUv ? [] : null;
    private Vector3[] _packedVertices = [];
    private Vector3[] _packedNormals = [];
    private Vector2[] _packedUv = [];
    private bool _prepared;

    internal void Clear() {
        _vertices.Clear();
        _normals.Clear();
        _uv?.Clear();
        _prepared = false;
    }
    private readonly record struct Sample(Vector2 At, float Inside, Vector3 Vertex);
    private Sample[] _fieldSamples = [];

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
        var count = (columns + 1) * (rows + 1);
        if (_fieldSamples.Length != count) { _fieldSamples = new Sample[count]; }
        var samples = _fieldSamples;
        for (var y = 0; y <= rows; y++) {
            for (var x = 0; x <= columns; x++) {
                var at = bounds.Position + bounds.Size * new Vector2((float)x / columns, (float)y / rows);
                var clearance = inside(at);
                samples[y * (columns + 1) + x] = new Sample(at, clearance, clearance >= 0f ? position(at, clearance) : default);
            }
        }
        for (var y = 0; y < rows; y++) {
            for (var x = 0; x < columns; x++) {
                var i = y * (columns + 1) + x;
                var a = samples[i];
                var b = samples[i + 1];
                var c = samples[i + columns + 2];
                var d = samples[i + columns + 1];
                ClippedTriangle(a, b, c, inside, position);
                ClippedTriangle(a, c, d, inside, position);
            }
        }
    }

    /// <summary>Sample an incision along its own centreline. Even a narrow diagonal opening keeps an inside row;
    /// a world-aligned coarse grid could miss the liquid entirely between its vertices.</summary>
    internal sealed record RibbonGrid(Vector2[] Centers, Vector2[] Normals, float Reach, int Rows) {
        internal int Columns => Centers.Length - 1;
        internal Vector2 At(float column, float offset) {
            column = Mathf.Clamp(column, 0f, Columns);
            var left = Math.Min((int)column, Columns - 1);
            var fraction = column - left;
            return Centers[left].Lerp(Centers[left + 1], fraction)
                + Normals[left].Lerp(Normals[left + 1], fraction) * offset;
        }
        internal float RowOffset(int row) => -Reach + Reach * 2f * ((float)row / Rows);
    }

    internal static RibbonGrid PlanRibbon(IReadOnlyList<Vector2> path, float reach) {
        var points = Simplify(path);
        if (points.Length < 2) { return new RibbonGrid([], [], reach, 0); }
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
        return new RibbonGrid(centers.ToArray(), normals, reach, rows);
    }

    internal void Ribbon(IReadOnlyList<Vector2> path, float reach, Func<Vector2, float> inside, Func<Vector2, float, Vector3> position) {
        var grid = PlanRibbon(path, reach);
        if (grid.Columns < 1) { return; }
        Vector2 At(Vector2 p) => grid.At(p.X, p.Y);
        Field(new Rect2(0f, -reach, grid.Columns, reach * 2f), grid.Columns, grid.Rows,
            p => inside(At(p)), (p, clearance) => position(At(p), clearance));
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

    private void ClippedTriangle(Sample a, Sample b, Sample c, Func<Vector2, float> inside, Func<Vector2, float, Vector3> position) {
        Span<Sample> input = stackalloc Sample[] { a, b, c };
        Span<Vector3> output = stackalloc Vector3[4];
        var count = 0;
        for (var i = 0; i < 3; i++) {
            var from = input[i];
            var to = input[(i + 1) % 3];
            if (from.Inside >= 0f) { output[count++] = from.Vertex; }
            if ((from.Inside >= 0f) != (to.Inside >= 0f)) {
                var fraction = from.Inside / (from.Inside - to.Inside);
                var at = from.At.Lerp(to.At, fraction);
                var clearance = inside(at);
                // Refine nonlinear crossings only to the visible contour tolerance. A safeguarded
                // secant step usually converges immediately; bisection bounds difficult corners.
                var lower = 0f;
                var upper = 1f;
                var lowerValue = from.Inside;
                var upperValue = to.Inside;
                for (var step = 0; step < 8 && Mathf.Abs(clearance) > 0.000025f; step++) {
                    if ((clearance >= 0f) == (lowerValue >= 0f)) {
                        lower = fraction;
                        lowerValue = clearance;
                    } else {
                        upper = fraction;
                        upperValue = clearance;
                    }
                    var next = lower + (upper - lower) * lowerValue / (lowerValue - upperValue);
                    fraction = Mathf.Clamp(next, lower + (upper - lower) * 0.1f, upper - (upper - lower) * 0.1f);
                    at = from.At.Lerp(to.At, fraction);
                    clearance = inside(at);
                }
                if (Mathf.Abs(clearance) > 0.000025f) {
                    at = from.At.Lerp(to.At, lowerValue >= 0f ? lower : upper);
                    clearance = inside(at);
                }
                output[count++] = position(at, clearance);
            }
        }
        for (var i = 1; i + 1 < count; i++) {
            Triangle(output[0], output[i], output[i + 1]);
        }
    }

    internal void Facet(Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc,
        Vector2 ua = default, Vector2 ub = default, Vector2 uc = default) {
        Triangle(a, b, c, na, nb, nc, ua, ub, uc);
    }

    private void Triangle(Vector3 a, Vector3 b, Vector3 c) => Triangle(a, b, c, Vector3.Up, Vector3.Up, Vector3.Up);

    private void Triangle(Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc,
        Vector2 ua = default, Vector2 ub = default, Vector2 uc = default) {
        _prepared = false;
        _vertices.Add(a);
        _vertices.Add(b);
        _vertices.Add(c);
        _normals.Add(na);
        _normals.Add(nb);
        _normals.Add(nc);
        if (_uv is not null) { _uv.Add(ua); _uv.Add(ub); _uv.Add(uc); }
    }

    /// <summary>Submit the real vertex format during scene setup, before a dynamic film first becomes visible.
    /// A microscopic seed is below pixel size and replaced by the first worker upload; an empty mesh would defer
    /// shader pipeline compilation until blood starts flowing.</summary>
    internal static ArrayMesh Primed(bool withUv = false) {
        var surface = new LiquidSurface(withUv);
        surface.Facet(Vector3.Zero, Vector3.Right * 0.000001f, Vector3.Back * 0.000001f,
            Vector3.Up, Vector3.Up, Vector3.Up, Vector2.Zero, Vector2.Right, Vector2.Zero);
        return surface.Upload();
    }

    internal static ArrayMesh Drop(out float volume) {
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
        var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        var signedVolume = 0f;
        for (var i = 0; i + 2 < indices.Length; i += 3) {
            signedVolume += vertices[indices[i]].Dot(vertices[indices[i + 1]].Cross(vertices[indices[i + 2]])) / 6f;
        }
        volume = Mathf.Abs(signedVolume);
        Shapes.SetPacked(arrays, Mesh.ArrayType.Vertex, vertices);
        Shapes.SetPacked(arrays, Mesh.ArrayType.Normal, normals);
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>Freeze managed geometry on its build worker, before the main-thread engine upload.</summary>
    internal void PrepareUpload() {
        if (_prepared) { return; }
        if (_packedVertices.Length != _vertices.Count) {
            _packedVertices = new Vector3[_vertices.Count];
            _packedNormals = new Vector3[_vertices.Count];
        }
        _vertices.CopyTo(_packedVertices);
        _normals.CopyTo(_packedNormals);
        if (_uv is not null) {
            if (_packedUv.Length != _uv.Count) { _packedUv = new Vector2[_uv.Count]; }
            _uv.CopyTo(_packedUv);
        }
        _prepared = true;
    }

    internal ArrayMesh Upload(ArrayMesh? into = null) {
        var mesh = into ?? new ArrayMesh();
        mesh.ClearSurfaces();
        if (_vertices.Count == 0) { return mesh; }
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        PrepareUpload();
        Shapes.SetPacked(arrays, Mesh.ArrayType.Vertex, _packedVertices);
        Shapes.SetPacked(arrays, Mesh.ArrayType.Normal, _packedNormals);
        if (_uv is not null) { Shapes.SetPacked(arrays, Mesh.ArrayType.TexUV, _packedUv); }
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
