namespace Scalpel.Tools;

/// <summary>The actual blade's intersection with the skin. The buried tip is not where a tilted blade cuts its
/// surface: using that point leaves intact skin between the visible blade and the incision.</summary>
internal sealed class BladeContact {
    private readonly Vector3[] _points;
    private readonly (int A, int B)[] _edges;
    private readonly int[] _faces;
    private readonly int[][] _sections;
    private readonly float _heelZ;
    private readonly Vector3[] _placed;
    private readonly float[] _heights;

    internal BladeContact(Mesh mesh, Transform3D toTool) {
        // GetFaces() goes through Godot's rounded triangle index. A thin cutting edge needs the exact
        // positions sent to the renderer, transformed through the blade's own tool slot only once.
        var faces = new List<Vector3>();
        for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++) {
            using var arrays = mesh.SurfaceGetArrays(surface);
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            if (indices.Length == 0) { faces.AddRange(vertices); } else {
                foreach (var index in indices) { faces.Add(vertices[index]); }
            }
        }
        var points = new List<Vector3>();
        var edges = new HashSet<(int, int)>();
        var triangles = new List<int>();
        var byPoint = new Dictionary<Vector3, int>();
        int Vertex(Vector3 p) {
            if (byPoint.TryGetValue(p, out var v)) { return v; }
            v = points.Count;
            byPoint[p] = v;
            points.Add(toTool * p);
            return v;
        }
        for (var t = 0; t < faces.Count; t += 3) {
            for (var e = 0; e < 3; e++) {
                var a = Vertex(faces[t + e]);
                var b = Vertex(faces[t + (e + 1) % 3]);
                edges.Add((Math.Min(a, b), Math.Max(a, b)));
                triangles.Add(a);
            }
        }
        _points = [.. points];
        _edges = [.. edges];
        _faces = [.. triangles];
        _sections = _points.Select((point, index) => (point, index)).GroupBy(v => v.point.Z)
            .Select(group => group.Select(v => v.index).ToArray()).ToArray();
        _heelZ = _points.Max(point => point.Z);
        _placed = new Vector3[_points.Length];
        _heights = new float[_points.Length];
    }

    /// <summary>Keep the blade from passing wholly under intact skin beside an opening. A buried blade fits
    /// inside the aperture; advancing beyond it must bring its edge through the skin at the rim.</summary>
    internal float HeelLift(PatientBody body, Transform3D pose) {
        var highest = float.NegativeInfinity;
        var toSite = body.Site.GlobalTransform.AffineInverse() * pose;
        foreach (var point in _points) {
            if (Mathf.Abs(point.Z - _heelZ) > 0.000001f) { continue; }
            var local = toSite * point;
            if (!body.SkinTopAt(local, out var height)) { continue; }
            var skin = body.Site.ToGlobal(local with { Y = height });
            highest = Mathf.Max(highest, (pose * point).Y - skin.Y);
        }
        var lift = float.IsFinite(highest) && highest < 0f ? -highest + 0.0005f : 0f;
        foreach (var section in _sections) {
            var top = section[0];
            foreach (var v in section) {
                if ((pose * _points[v]).Y > (pose * _points[top]).Y) { top = v; }
            }
            var local = toSite * _points[top];
            if (!body.SkinTopAt(local, out var height)) { continue; }
            var skin = body.Site.ToGlobal(local with { Y = height });
            lift = Mathf.Max(lift, skin.Y - PatientBody.SkinThickness - (pose * _points[top]).Y);
        }
        return lift;
    }

    /// <summary>Ends of the blade's footprint on the current skin, in site space, ordered along the blade.
    /// Scratch arrays and model edges are reused; no mesh readback or allocation occurs while cutting.</summary>
    internal (Vector3 From, Vector3 To, Vector2 FromUv, Vector2 ToUv)? SkinSpan(PatientBody body, Transform3D pose, Vector3 direction) {
        var toSite = body.Site.GlobalTransform.AffineInverse() * pose;
        var toTool = pose.AffineInverse() * body.Site.GlobalTransform;
        var along = body.Site.GlobalBasis.Inverse() * direction;
        var low = float.PositiveInfinity;
        var high = float.NegativeInfinity;
        var first = Vector3.Zero;
        var last = Vector3.Zero;
        var firstUv = Vector2.Zero;
        var lastUv = Vector2.Zero;
        for (var v = 0; v < _points.Length; v++) {
            var p = toSite * _points[v];
            _placed[v] = p;
            _heights[v] = p.Y - body.RestingSkinHeight(p);
        }
        if (body.RenderedBladeSpan(_points, _faces, _placed, toTool, along) is { } rendered) {
            first = rendered.From;
            last = rendered.To;
            firstUv = rendered.FromUv;
            lastUv = rendered.ToUv;
            low = first.Dot(along);
            high = last.Dot(along);
        }
        foreach (var (a, b) in _edges) {
            if ((_heights[a] > 0f) == (_heights[b] > 0f)) { continue; }
            var left = _placed[a];
            var right = _placed[b];
            var above = _heights[a] > 0f;
            // Resolve the curved surface along this model edge, rather than cutting against a flat height limit.
            for (var n = 0; n < 10; n++) {
                var mid = (left + right) * 0.5f;
                if ((mid.Y > body.RestingSkinHeight(mid)) == above) { left = mid; } else { right = mid; }
            }
            var hit = (left + right) * 0.5f;
            // The blade is extruded across local X: use its center plane for the incision's centerline.
            var local = toTool * hit;
            local.X = 0f;
            hit = toSite * local;
            // Only quiet skin still uses the fitted body surface. A simulated aperture has no surface here.
            if (body.UsesSkinSheet(hit)) { continue; }
            var uv = body.LocalToUv(hit);
            // The continued surface supplies a height across openings, but never creates skin past the body/site.
            if (!new Rect2(0f, 0f, 1f, 1f).HasPoint(uv) || !body.OnBody(uv)) { continue; }
            var at = hit.Dot(along);
            if (at < low) { low = at; first = hit; firstUv = uv; }
            if (at > high) { high = at; last = hit; lastUv = uv; }
        }
        return float.IsFinite(low) && high - low > 0.00001f ? (first, last, firstUv, lastUv) : null;
    }
}
