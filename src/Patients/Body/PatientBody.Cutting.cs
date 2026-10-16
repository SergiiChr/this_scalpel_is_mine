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
    /// <summary>The uppermost rendered skin at a physical point, or the quiet fitted body outside the sheet.
    /// A hole in the rendered sheet has no supporting surface, even where the continued solver sheet has a height.</summary>
    internal bool SkinTopAt(Vector3 point, out float height) {
        height = float.NegativeInfinity;
        if (_skinBuckets.TryGetValue(SkinCell(point), out var near)) {
            foreach (var t in near) {
                var a = _skinSheet[_skinSheetIndex[t]];
                var b = _skinSheet[_skinSheetIndex[t + 1]];
                var c = _skinSheet[_skinSheetIndex[t + 2]];
                var w = Barycentric(new Vector2(point.X, point.Z), new Vector2(a.X, a.Z),
                    new Vector2(b.X, b.Z), new Vector2(c.X, c.Z));
                if (w.IsFinite() && Mathf.Min(w.X, Mathf.Min(w.Y, w.Z)) >= -0.00001f) {
                    height = Mathf.Max(height, a.Y * w.X + b.Y * w.Y + c.Y * w.Z);
                }
            }
        }
        if (float.IsFinite(height)) { return true; }
        var uv = LocalToUv(point);
        if (UsesSkinSheet(point) || !new Rect2(0f, 0f, 1f, 1f).HasPoint(uv) || !OnBody(uv)) { return false; }
        height = SurfaceHeight(uv);
        return true;
    }

    private readonly HashSet<int> _bladeFacesSeen = new(256);

    /// <summary>Whether the imported body is hidden here by the simulated skin sheet or its aperture.</summary>
    internal bool UsesSkinSheet(Vector3 point) {
        if (_region.Length == 0) { return false; }
        var cell = (Vector2I)(LocalToUv(point) * new Vector2(Tissue.ResX, Tissue.ResY)).Floor();
        for (var y = Math.Max(0, cell.Y); y <= Math.Min(Tissue.ResY, cell.Y + 1); y++) {
            for (var x = Math.Max(0, cell.X); x <= Math.Min(Tissue.ResX, cell.X + 1); x++) {
                if (_region[Tissue.Index(x, y)] != 0) { return true; }
            }
        }
        return false;
    }

    /// <summary>Intersect the rendered skin with the imported blade's center section. Both contact positions
    /// and material coordinates come from the same skin triangles, including retracted lips.</summary>
    internal (Vector3 From, Vector3 To, Vector2 FromUv, Vector2 ToUv)? RenderedBladeSpan(
        Vector3[] blade, int[] faces, Vector3[] placed, Transform3D toTool, Vector3 along) {
        if (placed.Length == 0) { return null; }
        var low = placed[0];
        var high = low;
        foreach (var point in placed) { low = low.Min(point); high = high.Max(point); }
        var firstCell = SkinCell(low);
        var lastCell = SkinCell(high);
        var firstAt = float.PositiveInfinity;
        var lastAt = float.NegativeInfinity;
        var first = Vector3.Zero;
        var last = Vector3.Zero;
        var firstUv = Vector2.Zero;
        var lastUv = Vector2.Zero;
        _bladeFacesSeen.Clear();
        for (var z = firstCell.Y; z <= lastCell.Y; z++) {
            for (var x = firstCell.X; x <= lastCell.X; x++) {
                if (!_skinBuckets.TryGetValue(new Vector2I(x, z), out var near)) { continue; }
                foreach (var t in near) {
                    if (!_bladeFacesSeen.Add(t)) { continue; }
                    var ia = _skinSheetIndex[t];
                    var ib = _skinSheetIndex[t + 1];
                    var ic = _skinSheetIndex[t + 2];
                    var a = _skinSheet[ia];
                    var b = _skinSheet[ib];
                    var c = _skinSheet[ic];
                    if (Mathf.Min(a.Y, Mathf.Min(b.Y, c.Y)) > high.Y
                        || Mathf.Max(a.Y, Mathf.Max(b.Y, c.Y)) < low.Y) { continue; }
                    var ta = toTool * a;
                    var tb = toTool * b;
                    var tc = toTool * c;
                    var start = Vector3.Zero;
                    var end = Vector3.Zero;
                    var startUv = Vector2.Zero;
                    var endUv = Vector2.Zero;
                    var count = 0;
                    for (var e = 0; e < 3; e++) {
                        var (p, q, tp, tq, up, uq) = e switch {
                            0 => (a, b, ta, tb, _skinSheetUv[ia], _skinSheetUv[ib]),
                            1 => (b, c, tb, tc, _skinSheetUv[ib], _skinSheetUv[ic]),
                            _ => (c, a, tc, ta, _skinSheetUv[ic], _skinSheetUv[ia]),
                        };
                        if ((tp.X > 0f) == (tq.X > 0f)) { continue; }
                        var share = tp.X / (tp.X - tq.X);
                        var hit = p.Lerp(q, share);
                        var uv = up.Lerp(uq, share);
                        if (count == 0) { start = hit; startUv = uv; count = 1; }
                        else if (hit.DistanceSquaredTo(start) > 1e-16f) { end = hit; endUv = uv; count = 2; }
                    }
                    if (count < 2) { continue; }
                    var toolStart = toTool * start;
                    var toolEnd = toTool * end;
                    var lineStart = new Vector2(toolStart.Y, toolStart.Z);
                    var lineEnd = new Vector2(toolEnd.Y, toolEnd.Z);
                    for (var f = 0; f < faces.Length; f += 3) {
                        var p = new Vector2(blade[faces[f]].Y, blade[faces[f]].Z);
                        var q = new Vector2(blade[faces[f + 1]].Y, blade[faces[f + 1]].Z);
                        var r = new Vector2(blade[faces[f + 2]].Y, blade[faces[f + 2]].Z);
                        var area = (q - p).Cross(r - p);
                        if (Mathf.Abs(area) < 1e-12f) { continue; }
                        var winding = Mathf.Sign(area);
                        var begin = 0f;
                        var finish = 1f;
                        if (!ClipBladeEdge(p, q, lineStart, lineEnd, winding, ref begin, ref finish)
                            || !ClipBladeEdge(q, r, lineStart, lineEnd, winding, ref begin, ref finish)
                            || !ClipBladeEdge(r, p, lineStart, lineEnd, winding, ref begin, ref finish)) { continue; }
                        for (var side = 0; side < 2; side++) {
                            var share = side == 0 ? begin : finish;
                            var hit = start.Lerp(end, share);
                            var uv = startUv.Lerp(endUv, share);
                            var at = hit.Dot(along);
                            if (at < firstAt) { firstAt = at; first = hit; firstUv = uv; }
                            if (at > lastAt) { lastAt = at; last = hit; lastUv = uv; }
                        }
                    }
                }
            }
        }
        return float.IsFinite(firstAt) && lastAt - firstAt > 0.00001f ? (first, last, firstUv, lastUv) : null;
    }

    private static bool ClipBladeEdge(Vector2 a, Vector2 b, Vector2 from, Vector2 to, float winding,
        ref float begin, ref float end) {
        var edge = b - a;
        var at = winding * edge.Cross(from - a);
        var change = winding * edge.Cross(to - from);
        if (Mathf.Abs(change) < 1e-12f) { return at >= -1e-12f; }
        var crossing = -at / change;
        if (change > 0f) { begin = Mathf.Max(begin, crossing); }
        else { end = Mathf.Min(end, crossing); }
        return begin <= end;
    }

}
