namespace Scalpel.Tools;

/// <summary>The actual blade's intersection with the skin. The buried tip is not where a tilted blade cuts its
/// surface: using that point leaves intact skin between the visible blade and the incision.</summary>
internal sealed class BladeContact {
    private readonly Vector3[] _points;
    private readonly (int A, int B)[] _edges;
    private readonly Vector3[] _placed;
    private readonly float[] _heights;
    private readonly float _heelZ;

    internal BladeContact(Vector3[] faces, Transform3D toTool) {
        var points = new List<Vector3>();
        var edges = new HashSet<(int, int)>();
        var byPoint = new Dictionary<Vector3, int>();
        int Vertex(Vector3 p) {
            if (byPoint.TryGetValue(p, out var v)) { return v; }
            v = points.Count;
            byPoint[p] = v;
            points.Add(toTool * p);
            return v;
        }
        for (var t = 0; t < faces.Length; t += 3) {
            for (var e = 0; e < 3; e++) {
                var a = Vertex(faces[t + e]);
                var b = Vertex(faces[t + (e + 1) % 3]);
                edges.Add((Math.Min(a, b), Math.Max(a, b)));
            }
        }
        _points = [.. points];
        _edges = [.. edges];
        _placed = new Vector3[_points.Length];
        _heights = new float[_points.Length];
        _heelZ = _points.Max(p => p.Z);
    }

    /// <summary>Keep the blade's heel from passing wholly under intact skin. An opening can admit it,
    /// but intact skin at the handle end supports the knife while its cutting edge enters the body wall.</summary>
    internal float HeelLift(PatientBody body, Transform3D pose) {
        var highest = float.NegativeInfinity;
        var toSite = body.Site.GlobalTransform.AffineInverse() * pose;
        foreach (var point in _points) {
            if (Mathf.Abs(point.Z - _heelZ) > 0.000001f) { continue; }
            var local = toSite * point;
            var uv = body.CutUv(local);
            if (!new Rect2(0f, 0f, 1f, 1f).HasPoint(uv) || !body.OnBody(uv) || body.Tissue.IsOpen(uv, TissueDepth.Skin)) { continue; }
            var skin = body.Site.ToGlobal(local with { Y = body.CutSurfaceHeight(local) });
            highest = Mathf.Max(highest, (pose * point).Y - skin.Y);
        }
        // Half a millimeter leaves a genuine section through the blade instead of a grazing endpoint.
        return float.IsFinite(highest) && highest < 0f ? -highest + 0.0005f : 0f;
    }

    /// <summary>Ends of the blade's footprint on the current skin, in site space, ordered along the blade.
    /// Scratch arrays and model edges are reused; no mesh readback or allocation occurs while cutting.</summary>
    internal (Vector3 From, Vector3 To)? SkinSpan(PatientBody body, Transform3D pose, Vector3 direction) {
        var toSite = body.Site.GlobalTransform.AffineInverse() * pose;
        var toTool = pose.AffineInverse() * body.Site.GlobalTransform;
        var along = body.Site.GlobalBasis.Inverse() * direction;
        var low = float.PositiveInfinity;
        var high = float.NegativeInfinity;
        var first = Vector3.Zero;
        var last = Vector3.Zero;
        for (var v = 0; v < _points.Length; v++) {
            var p = toSite * _points[v];
            _placed[v] = p;
            _heights[v] = p.Y - body.CutSurfaceHeight(p);
        }
        foreach (var (a, b) in _edges) {
            if ((_heights[a] > 0f) == (_heights[b] > 0f)) { continue; }
            var left = _placed[a];
            var right = _placed[b];
            var above = _heights[a] > 0f;
            // Resolve the curved surface along this model edge, rather than cutting against a flat height limit.
            for (var n = 0; n < 10; n++) {
                var mid = (left + right) * 0.5f;
                if ((mid.Y > body.CutSurfaceHeight(mid)) == above) { left = mid; } else { right = mid; }
            }
            var hit = (left + right) * 0.5f;
            // The blade is extruded across local X: use its center plane for the incision's centerline.
            var local = toTool * hit;
            local.X = 0f;
            hit = toSite * local;
            var uv = body.CutUv(hit);
            // The continued surface supplies a height across openings, but never creates skin past the body/site.
            if (!new Rect2(0f, 0f, 1f, 1f).HasPoint(uv) || !body.OnBody(uv)) { continue; }
            var at = hit.Dot(along);
            if (at < low) { low = at; first = hit; }
            if (at > high) { high = at; last = hit; }
        }
        if (!float.IsFinite(low) || high - low <= 0.00001f) { return null; }
        // The continued sheet gives a stable height through a hole, but there is no skin there to stab.
        // Sample at material-cell spacing so a narrow intact bridge between two openings still counts.
        var firstUv = body.CutUv(first);
        var lastUv = body.CutUv(last);
        var cells = (lastUv - firstUv) * new Vector2(body.Tissue.ResX, body.Tissue.ResY);
        var samples = Math.Max(1, Mathf.CeilToInt(cells.Length() * 2f));
        for (var i = 0; i <= samples; i++) {
            if (!body.Tissue.IsOpen(body.CutUv(first.Lerp(last, (float)i / samples)), TissueDepth.Skin)) {
                return (first, last);
            }
        }
        return null;
    }
}
