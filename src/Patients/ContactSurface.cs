namespace Scalpel.Patients;

/// <summary>Reusable spatial index and exact triangle contact against a sloping surface.</summary>
internal sealed class ContactSurface
{
    private readonly Dictionary<Vector2I, List<ContactFace>> _faces = [];
    private const float ContactCell = 0.01f;
    private readonly record struct ContactFace(int Id, Vector3 A, Vector3 B, Vector3 C,
        Vector3 Low, Vector3 High, float SlopeX, float SlopeZ, float OriginY, float Winding);

    internal void Index(Vector3[] vertices, IReadOnlyList<int> indices)
    {
        foreach (var bucket in _faces.Values) { bucket.Clear(); }
        for (var t = 0; t < indices.Count; t += 3)
        {
            var a = vertices[indices[t]];
            var b = vertices[indices[t + 1]];
            var c = vertices[indices[t + 2]];
            var low = a.Min(b).Min(c);
            var high = a.Max(b).Max(c);
            var ab = b - a;
            var ac = c - a;
            var det = ab.X * ac.Z - ab.Z * ac.X;
            if (Mathf.Abs(det) < 1e-10f) { continue; }
            var sx = (ab.Y * ac.Z - ac.Y * ab.Z) / det;
            var sz = (ac.Y * ab.X - ab.Y * ac.X) / det;
            var face = new ContactFace(t, a, b, c, low, high, sx, sz, a.Y - sx * a.X - sz * a.Z, MathF.Sign(det));
            var first = (Vector2I)(new Vector2(low.X, low.Z) / ContactCell).Floor();
            var last = (Vector2I)(new Vector2(high.X, high.Z) / ContactCell).Floor();
            for (var j = first.Y; j <= last.Y; j++)
            {
                for (var i = first.X; i <= last.X; i++)
                {
                    var key = new Vector2I(i, j);
                    if (!_faces.TryGetValue(key, out var near)) { _faces[key] = near = []; }
                    near.Add(face);
                }
            }
        }
    }

    private readonly Dictionary<int, ContactFace> _contactFaces = [];
    private readonly Vector3[] _contactPolygon = new Vector3[8];
    private readonly Vector3[] _contactClipped = new Vector3[8];

    /// <summary>Highest face over this point, or NaN outside the indexed surface.</summary>
    internal float HeightAt(float x, float z)
    {
        var key = (Vector2I)(new Vector2(x, z) / ContactCell).Floor();
        if (!_faces.TryGetValue(key, out var faces)) { return float.NaN; }
        var height = float.NegativeInfinity;
        foreach (var face in faces)
        {
            var ab = face.B - face.A;
            var ac = face.C - face.A;
            var dx = x - face.A.X;
            var dz = z - face.A.Z;
            var determinant = ab.X * ac.Z - ab.Z * ac.X;
            var u = (dx * ac.Z - dz * ac.X) / determinant;
            var v = (dz * ab.X - dx * ab.Z) / determinant;
            if (u >= -0.00001f && v >= -0.00001f && u + v <= 1.00001f)
            {
                height = Mathf.Max(height, x * face.SlopeX + z * face.SlopeZ + face.OriginY);
            }
        }
        return float.IsFinite(height) ? height : float.NaN;
    }

    internal void PushTriangle(Vector3[] vertices, int a, int b, int c, float gap, bool above)
    {
        var low = vertices[a].Min(vertices[b]).Min(vertices[c]);
        var high = vertices[a].Max(vertices[b]).Max(vertices[c]);
        var first = (Vector2I)(new Vector2(low.X, low.Z) / ContactCell).Floor();
        var last = (Vector2I)(new Vector2(high.X, high.Z) / ContactCell).Floor();
        _contactFaces.Clear();
        for (var j = first.Y; j <= last.Y; j++)
        {
            for (var i = first.X; i <= last.X; i++)
            {
                if (_faces.TryGetValue(new Vector2I(i, j), out var near))
                {
                    foreach (var face in near) { _contactFaces.TryAdd(face.Id, face); }
                }
            }
        }
        var ab = vertices[b] - vertices[a];
        var ac = vertices[c] - vertices[a];
        var determinant = ab.X * ac.Z - ab.Z * ac.X;
        if (Mathf.Abs(determinant) < 1e-10f) { return; }
        foreach (var face in _contactFaces.Values)
        {
            var (x, y, z) = (face.A, face.B, face.C);
            var skinLow = face.Low;
            var skinHigh = face.High;
            if (low.X > skinHigh.X || high.X < skinLow.X || low.Z > skinHigh.Z || high.Z < skinLow.Z
                || (above ? low.Y >= skinHigh.Y + gap : high.Y <= skinLow.Y - gap)) { continue; }
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
            for (var n = 0; n < count; n++)
            {
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

    private void Clip(Vector3 a, Vector3 b, float sign, ref int count)
    {
        var edge = b - a;
        var result = 0;
        for (var n = 0; n < count; n++)
        {
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
