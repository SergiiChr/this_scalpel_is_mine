namespace Scalpel.Patients;

/// <summary>Reusable spatial index and exact triangle contact against a sloping surface, its faces bucketed in cells
/// <paramref name="cell"/> (meters) wide.</summary>
internal sealed class ContactSurface(float cell = 0.01f) {
    // A compact grid avoids hashing neighboring cells for every rendered tissue triangle.
    // Buckets survive reindexing as the skin moves; only their contents and the grid origin change.
    private List<int>?[] _faces = [];
    private Vector2I _firstCell;
    private int _width;
    private int _height;
    private readonly record struct ContactFace(Vector3 A, Vector3 B, Vector3 C,
        Vector3 Low, Vector3 High, float SlopeX, float SlopeZ, float OriginY, float Winding);

    private ContactFace[] _triangles = [];
    private int[] _seen = [];
    private int _query;

    internal void Index(Vector3[] vertices, IReadOnlyList<int> indices) {
        var count = indices.Count / 3;
        if (_triangles.Length < count) {
            // Cutting adds a few triangles at a time. Keep spare capacity instead of replacing every contact
            // face and query stamp on each cut step; the indexes are reused for sheets and cut walls alike.
            var capacity = Math.Max(count, Math.Max(_triangles.Length * 2, 16));
            Array.Resize(ref _triangles, capacity);
            Array.Resize(ref _seen, capacity);
        }
        foreach (var bucket in _faces) { bucket?.Clear(); }
        var firstCell = new Vector2I(int.MaxValue, int.MaxValue);
        var lastCell = new Vector2I(int.MinValue, int.MinValue);
        for (var t = 0; t < indices.Count; t += 3) {
            var a = vertices[indices[t]];
            var b = vertices[indices[t + 1]];
            var c = vertices[indices[t + 2]];
            var low = a.Min(b).Min(c);
            var high = a.Max(b).Max(c);
            var ab = b - a;
            var ac = c - a;
            var det = ab.X * ac.Z - ab.Z * ac.X;
            if (Mathf.Abs(det) < 1e-10f) {
                _triangles[t / 3] = default;
                continue;
            }
            var sx = (ab.Y * ac.Z - ac.Y * ab.Z) / det;
            var sz = (ac.Y * ab.X - ab.Y * ac.X) / det;
            var index = t / 3;
            _triangles[index] = new ContactFace(a, b, c, low, high, sx, sz, a.Y - sx * a.X - sz * a.Z, MathF.Sign(det));
            var first = (Vector2I)(new Vector2(low.X, low.Z) / cell).Floor();
            var last = (Vector2I)(new Vector2(high.X, high.Z) / cell).Floor();
            firstCell = firstCell.Min(first);
            lastCell = lastCell.Max(last);
        }
        _firstCell = firstCell;
        _width = count == 0 || firstCell.X == int.MaxValue ? 0 : lastCell.X - firstCell.X + 1;
        _height = _width == 0 ? 0 : lastCell.Y - firstCell.Y + 1;
        if (_faces.Length < _width * _height) {
            Array.Resize(ref _faces, Math.Max(_width * _height, Math.Max(_faces.Length * 2, 16)));
        }
        for (var index = 0; index < count; index++) {
            ref readonly var face = ref _triangles[index];
            if (face.Winding == 0f) { continue; }
            var first = (Vector2I)(new Vector2(face.Low.X, face.Low.Z) / cell).Floor() - _firstCell;
            var last = (Vector2I)(new Vector2(face.High.X, face.High.Z) / cell).Floor() - _firstCell;
            for (var j = first.Y; j <= last.Y; j++) {
                for (var i = first.X; i <= last.X; i++) {
                    var near = _faces[j * _width + i] ??= [];
                    near.Add(index);
                }
            }
        }
    }

    private readonly List<int> _contactFaces = [];
    private readonly Vector3[] _contactPolygon = new Vector3[8];
    private readonly Vector3[] _contactClipped = new Vector3[8];

    /// <summary>Highest face over this point, or NaN outside the indexed surface.</summary>
    internal float HeightAt(float x, float z) {
        var key = (Vector2I)(new Vector2(x, z) / cell).Floor() - _firstCell;
        if (key.X < 0 || key.Y < 0 || key.X >= _width || key.Y >= _height) { return float.NaN; }
        var faces = _faces[key.Y * _width + key.X];
        if (faces is null) { return float.NaN; }
        var height = float.NegativeInfinity;
        foreach (var index in faces) {
            ref readonly var face = ref _triangles[index];
            var ab = face.B - face.A;
            var ac = face.C - face.A;
            var dx = x - face.A.X;
            var dz = z - face.A.Z;
            var determinant = ab.X * ac.Z - ab.Z * ac.X;
            var u = (dx * ac.Z - dz * ac.X) / determinant;
            var v = (dz * ab.X - dx * ab.Z) / determinant;
            if (u >= -0.00001f && v >= -0.00001f && u + v <= 1.00001f) {
                height = Mathf.Max(height, x * face.SlopeX + z * face.SlopeZ + face.OriginY);
            }
        }
        return float.IsFinite(height) ? height : float.NaN;
    }

    internal void PushTriangle(Vector3[] vertices, int a, int b, int c, float gap, bool above, bool crossingOnly = false) {
        var ab = vertices[b] - vertices[a];
        var ac = vertices[c] - vertices[a];
        var determinant = ab.X * ac.Z - ab.Z * ac.X;
        if (Mathf.Abs(determinant) < 1e-10f) { return; }
        var low = vertices[a].Min(vertices[b]).Min(vertices[c]);
        var high = vertices[a].Max(vertices[b]).Max(vertices[c]);
        var first = ((Vector2I)(new Vector2(low.X, low.Z) / cell).Floor() - _firstCell).Max(Vector2I.Zero);
        var last = ((Vector2I)(new Vector2(high.X, high.Z) / cell).Floor() - _firstCell).Min(new Vector2I(_width - 1, _height - 1));
        _contactFaces.Clear();
        // Store each face once; buckets and query scratch hold compact indices rather than face copies.
        // A generation stamp deduplicates neighboring buckets without hashing every candidate again.
        if (_query == int.MaxValue) {
            Array.Clear(_seen);
            _query = 0;
        }
        _query++;
        for (var j = first.Y; j <= last.Y; j++) {
            for (var i = first.X; i <= last.X; i++) {
                if (_faces[j * _width + i] is { } near) {
                    foreach (var index in near) {
                        if (_seen[index] == _query) { continue; }
                        _seen[index] = _query;
                        ref readonly var face = ref _triangles[index];
                        // Cull unchanged bounds before copying candidates into the contact work list.
                        // Most tethered-bed faces are already safely below their nearby skin.
                        if (low.X > face.High.X || high.X < face.Low.X || low.Z > face.High.Z || high.Z < face.Low.Z
                            || (above ? low.Y >= face.High.Y + gap : high.Y <= face.Low.Y - gap)) { continue; }
                        _contactFaces.Add(index);
                    }
                }
            }
        }
        foreach (var index in _contactFaces) {
            ref readonly var face = ref _triangles[index];
            var (x, y, z) = (face.A, face.B, face.C);
            var slopeX = face.SlopeX;
            var slopeZ = face.SlopeZ;
            var originY = face.OriginY + (above ? gap : -gap);
            var sideA = vertices[a].Y - slopeX * vertices[a].X - slopeZ * vertices[a].Z - originY;
            var sideB = vertices[b].Y - slopeX * vertices[b].X - slopeZ * vertices[b].Z - originY;
            var sideC = vertices[c].Y - slopeX * vertices[c].X - slopeZ * vertices[c].Z - originY;
            if (above ? sideA >= 0f && sideB >= 0f && sideC >= 0f : sideA <= 0f && sideB <= 0f && sideC <= 0f) { continue; }
            _contactPolygon[0] = vertices[a];
            _contactPolygon[1] = vertices[b];
            _contactPolygon[2] = vertices[c];
            var count = 3;
            Clip(x, y, face.Winding, ref count);
            Clip(y, z, face.Winding, ref count);
            Clip(z, x, face.Winding, ref count);
            if (crossingOnly) {
                // A lifted cut wall can lie wholly above another skin facet. Keep that separation; only a wall
                // reaching through the facet from its protected side needs pushing away. Check the overlap itself,
                // not vertices outside this face's footprint.
                var protectedSide = false;
                for (var n = 0; n < count; n++) {
                    var p = _contactPolygon[n];
                    var side = p.Y - (slopeX * p.X + slopeZ * p.Z + face.OriginY);
                    protectedSide |= above ? side > 0.000001f : side < -0.000001f;
                }
                if (!protectedSide) { continue; }
            }
            for (var n = 0; n < count; n++) {
                var p = _contactPolygon[n];
                var roof = slopeX * p.X + slopeZ * p.Z + originY;
                var offset = p - vertices[a];
                var wb = (offset.X * ac.Z - offset.Z * ac.X) / determinant;
                var wc = (offset.Z * ab.X - offset.X * ab.Z) / determinant;
                var wa = 1f - wb - wc;
                var height = vertices[a].Y * wa + vertices[b].Y * wb + vertices[c].Y * wc;
                if (above ? roof <= height : roof >= height) { continue; }
                var correction = (height - roof) / (wa * wa + wb * wb + wc * wc);
                vertices[a].Y -= correction * wa;
                vertices[b].Y -= correction * wb;
                vertices[c].Y -= correction * wc;
            }
        }
    }

    private void Clip(Vector3 a, Vector3 b, float sign, ref int count) {
        var edge = b - a;
        var result = 0;
        for (var n = 0; n < count; n++) {
            var p = _contactPolygon[n];
            var q = _contactPolygon[(n + 1) % count];
            var dp = ((p.Z - a.Z) * edge.X - (p.X - a.X) * edge.Z) * sign;
            var dq = ((q.Z - a.Z) * edge.X - (q.X - a.X) * edge.Z) * sign;
            if (dp >= 0f) { _contactClipped[result++] = p; }
            if ((dp >= 0f) != (dq >= 0f)) { _contactClipped[result++] = p.Lerp(q, dp / (dp - dq)); }
        }
        Array.Copy(_contactClipped, _contactPolygon, result);
        count = result;
    }

}
