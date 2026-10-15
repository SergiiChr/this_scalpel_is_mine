namespace Scalpel.Patients;

public partial class PatientBody {
    /// <summary>Material coordinates at a physical cutting contact, accounting for skin retraction.</summary>
    internal Vector2 CutUv(Vector3 p) {
        var uv = LocalToUv(p);
        var nearest = float.PositiveInfinity;
        if (!_skinBuckets.TryGetValue(SkinCell(p), out var bucket)) { return uv; }
        foreach (var t in bucket) {
            var ia = _skinSheetIndex[t];
            var ib = _skinSheetIndex[t + 1];
            var ic = _skinSheetIndex[t + 2];
            var a = _skinSheet[ia];
            var b = _skinSheet[ib];
            var c = _skinSheet[ic];
            var w = Barycentric(new Vector2(p.X, p.Z), new Vector2(a.X, a.Z), new Vector2(b.X, b.Z), new Vector2(c.X, c.Z));
            if (!w.IsFinite() || Mathf.Min(w.X, Mathf.Min(w.Y, w.Z)) < -0.00001f) { continue; }
            var distance = Mathf.Abs(p.Y - (a.Y * w.X + b.Y * w.Y + c.Y * w.Z));
            if (distance >= nearest) { continue; }
            nearest = distance;
            uv = _skinSheetUv[ia] * w.X + _skinSheetUv[ib] * w.Y + _skinSheetUv[ic] * w.Z;
        }
        // An aperture has no skin material to invert: keep its anatomical coordinate for deeper work.
        return uv;
    }

    /// <summary>The current material sheet continued smoothly across its opening, for finding the blade section.
    /// Sampling separate lips would jump between banks as the incision retracts.</summary>
    internal float CutSurfaceHeight(Vector3 p) {
        var uv = LocalToUv(p);
        var cell = (Vector2I)(uv * new Vector2(Tissue.ResX, Tissue.ResY)).Floor();
        var best = float.PositiveInfinity;
        var height = SurfaceHeight(uv);
        // Skin around a working incision moves less than two cells. Search neighboring material triangles,
        // never sample across a detached drawn lip or infer coordinates from a flat world projection.
        for (var ring = 0; ring <= 2; ring++) {
            for (var j = Math.Max(0, cell.Y - ring); j <= Math.Min(Tissue.ResY - 1, cell.Y + ring); j++) {
                for (var i = Math.Max(0, cell.X - ring); i <= Math.Min(Tissue.ResX - 1, cell.X + ring); i++) {
                    if (ring > 0 && Math.Abs(i - cell.X) < ring && Math.Abs(j - cell.Y) < ring) { continue; }
                    var a = Tissue.Index(i, j);
                    var c = a + Tissue.ResX + 1;
                    Sample(a, a + 1, c);
                    Sample(a + 1, c + 1, c);
                }
            }
            if (float.IsFinite(best)) { break; }
        }
        void Sample(int ia, int ib, int ic) {
            var a = LayerPoint(0, ia);
            var b = LayerPoint(0, ib);
            var c = LayerPoint(0, ic);
            var w = Barycentric(new Vector2(p.X, p.Z), new Vector2(a.X, a.Z), new Vector2(b.X, b.Z), new Vector2(c.X, c.Z));
            if (!w.IsFinite() || Mathf.Min(w.X, Mathf.Min(w.Y, w.Z)) < -0.00001f) { return; }
            var y = a.Y * w.X + b.Y * w.Y + c.Y * w.Z;
            var off = Mathf.Abs(p.Y - y);
            if (off >= best) { return; }
            best = off;
            height = y;
        }
        return height;
    }
}
