namespace Scalpel.Patients;

/// <summary>Cut endpoints: continuous topology and local lip tapering on the deformed tissue sheet.</summary>
public partial class PatientBody {
    /// <summary>
    /// The vertex where a cut that crossed edge ab of triangle (a, b, c) at <paramref name="entry"/> (uv) stops inside
    /// it (TissueSim.CutEndIn()), exactly where the blade stopped, including just beyond the entry or at the next
    /// edge. Clamping that point inside the cell hides the last part of the incision under intact skin.
    /// A tear, with no blade path, runs half way across along
    /// <paramref name="cutDir"/>.
    /// </summary>
    private int PlanApex(LayerPlan plan, int a, int b, int c, int lipA, Vector2 cutDir) {
        var (ua, ub, uc) = (plan.Uv[a], plan.Uv[b], plan.Uv[c]);
        var entry = plan.Uv[lipA];
        var traced = Tissue.CutEndIn(ua, ub, uc, entry);
        // In uv, toward c's side of the edge.
        var along = traced is { } end && end.DistanceSquaredTo(entry) > 1e-10f
            ? (end - entry).Normalized()
            : new Vector2(cutDir.X / SiteSize.X, cutDir.Y / SiteSize.Y).Normalized();
        var edge = ub - ua;
        if (Mathf.Sign(edge.Cross(along)) != Mathf.Sign(edge.Cross(uc - ua))) {
            along = -along;
        }
        var across = AcrossTriangle(entry, along, ua, ub, uc);
        var at = traced ?? entry + along * across * 0.5f;
        var weights = Barycentric(at, ua, ub, uc);
        if (Mathf.Min(weights.X, Mathf.Min(weights.Y, weights.Z)) <= EndEdgeTolerance) {
            // Crossings already snap to this grid tolerance. Use the same edge for the endpoint, or transform
            // noise leaves a near-edge tip disconnected from the adjacent triangle.
            weights = new Vector3(Mathf.Abs(weights.X) <= EndEdgeTolerance ? 0f : weights.X,
                Mathf.Abs(weights.Y) <= EndEdgeTolerance ? 0f : weights.Y,
                Mathf.Abs(weights.Z) <= EndEdgeTolerance ? 0f : weights.Z);
            weights /= weights.X + weights.Y + weights.Z;
            at = ua * weights.X + ub * weights.Y + uc * weights.Z;
            // A grid-aligned cut can end in both adjacent triangles. They meet at one shared point, not two
            // coincident vertices whose separate edges leave a seam across the end of the opening.
            foreach (var existing in plan.Apex.Keys) {
                if (plan.Uv[existing].DistanceSquaredTo(at) < 1e-12f) { return existing; }
            }
        }
        var v = plan.Owner.Count;
        plan.Owner.Add(plan.Owner[c]);
        plan.Other.Add(-1);
        plan.Share.Add(0f);
        plan.Slide.Add(-1);
        plan.Uv.Add(at);
        plan.Apex[v] = (a, b, c, weights);
        return v;
    }

    /// <summary>How far (uv) from <paramref name="from"/> on edge ab the triangle (a, b, c) reaches along
    /// <paramref name="along"/>.</summary>
    private static float AcrossTriangle(Vector2 from, Vector2 along, Vector2 a, Vector2 b, Vector2 c) {
        var nearest = float.PositiveInfinity;
        foreach (var (p, q) in new[] { (b, c), (c, a) }) {
            var side = q - p;
            var det = along.Cross(side);
            if (Mathf.Abs(det) < 1e-12f) {
                continue;
            }
            var t = (p - from).Cross(side) / det;
            var u = (p - from).Cross(along) / det;
            if (t > 0f && u >= -0.001f && u <= 1.001f) {
                nearest = Mathf.Min(nearest, t);
            }
        }
        return float.IsPositiveInfinity(nearest) ? 0f : nearest;
    }

    /// <summary>Weights of p between a, b and c (uv).</summary>
    private static Vector3 Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c) {
        var area = (b - a).Cross(c - a);
        var wb = (p - a).Cross(c - a) / area;
        var wc = (b - a).Cross(p - a) / area;
        return new Vector3(1f - wb - wc, wb, wc);
    }

    /// <summary>Cache lip groups and their taper weights once per topology change, including grid-aligned cuts.</summary>
    private void PlanEndTapers(LayerPlan plan) {
        // At a grid vertex one lip uses the particle itself; the other can have several virtual crossings.
        // Group by material position so every representation of that lip tapers together, including exact cuts.
        var lipVertices = new HashSet<int>();
        for (var w = 0; w < plan.Wall.Count; w += 3) {
            lipVertices.Add(plan.Wall[w]);
            lipVertices.Add(plan.Wall[w + 1]);
        }
        foreach (var (_, _, pointed) in plan.Ends) {
            lipVertices.Add(pointed[1]);
            lipVertices.Add(pointed[4]);
        }
        var lipGroups = new Dictionary<Vector2I, List<int>>();
        foreach (var v in lipVertices) {
            var key = (Vector2I)(plan.Uv[v] * 1_000_000f).Round();
            if (!lipGroups.TryGetValue(key, out var group)) { lipGroups[key] = group = []; }
            group.Add(v);
        }
        foreach (var group in lipGroups.Values) {
            if (group.Count < 2) { continue; }
            var distance = CutEndTaper;
            foreach (var (apex, _, _) in plan.Ends) {
                distance = Mathf.Min(distance, ((plan.Uv[group[0]] - plan.Uv[apex]) * SiteSize).Length());
            }
            if (distance < CutEndTaper) { plan.EndTapers.Add(([.. group], Mathf.SmoothStep(0f, CutEndTaper, distance))); }
        }
    }

    // A tip can stop immediately past a grid edge. Spread its closing gap over a cell so it reads as a V.
    private const float CutEndTaper = TissueSim.Cell;

    private static void TaperCutEnds(LayerPlan plan, Vector3[] vertices) {
        foreach (var (group, share) in plan.EndTapers) {
            // Change only the gap. Its center retains translation and rotation of the surrounding skin.
            var middle = Vector3.Zero;
            foreach (var v in group) { middle += vertices[v]; }
            middle /= group.Length;
            foreach (var v in group) { vertices[v] = middle + (vertices[v] - middle) * share; }
        }
    }

    // Match the solver's snapped crossings so near-edge tips share their intact neighbour's geometry.
    private const float EndEdgeTolerance = TissueSim.CrossMargin;

    /// <summary>When a stroke stops on an uncut grid edge, its V and the adjacent intact triangle share that
    /// point. Leaving the neighbour's edge whole creates a T-junction over the finishing blade.</summary>
    private static void JoinEndAtEdge(LayerPlan plan, List<int> indices, Vector3[] vertices, int apex) {
        var (a, b, c, weights) = plan.Apex[apex];
        foreach (var (from, to, opposite) in new[] { (a, b, weights.Z), (b, c, weights.X), (c, a, weights.Y) }) {
            // The endpoint and spring crossing use the same grid-edge tolerance.
            if (Mathf.Abs(opposite) > EndEdgeTolerance || plan.Uv[apex].DistanceSquaredTo(plan.Uv[from]) < 1e-12f
                || plan.Uv[apex].DistanceSquaredTo(plan.Uv[to]) < 1e-12f) { continue; }
            var edgeFrom = from;
            var edgeTo = to;
            var along = plan.Uv[to] - plan.Uv[from];
            var share = (plan.Uv[apex] - plan.Uv[from]).Dot(along) / along.LengthSquared();
            if (plan.MidpointOf.TryGetValue((Math.Min(from, to), Math.Max(from, to)), out var mid)) {
                // Curved lip edges are already split: join the correct half, or share its midpoint outright.
                if (Mathf.Abs(share - 0.5f) <= EndEdgeTolerance) {
                    vertices[apex] = vertices[mid];
                    for (var n = 0; n < indices.Count; n++) {
                        if (indices[n] == mid) { indices[n] = apex; }
                    }
                    continue;
                }
                if (share < 0.5f) {
                    edgeTo = mid;
                    share *= 2f;
                } else {
                    edgeFrom = mid;
                    share = (share - 0.5f) * 2f;
                }
                vertices[apex] = vertices[edgeFrom].Lerp(vertices[edgeTo], share);
            }
            var count = indices.Count;
            for (var t = 0; t < count; t += 3) {
                if (indices[t] == apex || indices[t + 1] == apex || indices[t + 2] == apex) { continue; }
                for (var edge = 0; edge < 3; edge++) {
                    var p = indices[t + edge];
                    var q = indices[t + (edge + 1) % 3];
                    if (!((p == edgeFrom && q == edgeTo) || (p == edgeTo && q == edgeFrom))) { continue; }
                    var inside = indices[t + (edge + 2) % 3];
                    indices[t] = p;
                    indices[t + 1] = apex;
                    indices[t + 2] = inside;
                    indices.AddRange([apex, q, inside]);
                    break;
                }
            }
        }
    }

    /// <summary>Keep an interior V inside its own deformed end triangle. Sliding lips can pass a very short
    /// final segment; move only its tip toward the intact corner until its four faces fit, rather than closing
    /// skin over it. Boundary tips stay shared with the neighbouring intact triangle.</summary>
    private static void FitCutEnds(LayerPlan plan, Vector3[] vertices) {
        foreach (var (apex, _, pointed) in plan.Ends) {
            var (_, _, c, weights) = plan.Apex[apex];
            if (Mathf.Min(weights.X, Mathf.Min(weights.Y, weights.Z)) <= EndEdgeTolerance
                || PointedEndFits(plan, vertices, apex, pointed)) { continue; }
            var original = vertices[apex];
            var inside = vertices[c];
            vertices[apex] = inside;
            if (!PointedEndFits(plan, vertices, apex, pointed)) {
                vertices[apex] = original;
                continue;
            }
            var (low, high) = (0f, 1f);
            // Twelve bisections locate the first valid point to roughly a micrometre on this grid.
            for (var step = 0; step < 12; step++) {
                var mid = (low + high) * 0.5f;
                vertices[apex] = original.Lerp(inside, mid);
                if (PointedEndFits(plan, vertices, apex, pointed)) { high = mid; } else { low = mid; }
            }
            vertices[apex] = original.Lerp(inside, high);
        }
    }

    /// <summary>A pointed end fits while all four pieces and its open wedge keep the source triangle's winding.
    /// This geometric check preserves the closed fallback on a compressed or overturned end without capping
    /// unrelated ends or a safely stretched incision.</summary>
    private static bool PointedEndFits(LayerPlan plan, Vector3[] vertices, int apex, int[] pointed) {
        var (a, b, c, _) = plan.Apex[apex];
        var normal = (vertices[b] - vertices[a]).Cross(vertices[c] - vertices[a]);
        var area = normal.LengthSquared();
        if (area < 1e-16f) { return false; }
        // Relative tolerance admits collapsed edges at exact grid crossings, but not an inverted skin face.
        var tolerance = area * 0.0001f;
        for (var t = 0; t < pointed.Length; t += 3) {
            var p = vertices[pointed[t]];
            var q = vertices[pointed[t + 1]];
            var r = vertices[pointed[t + 2]];
            if ((q - p).Cross(r - p).Dot(normal) < -tolerance) { return false; }
        }
        var lipA = vertices[pointed[1]];
        var lipB = vertices[pointed[4]];
        return (lipB - lipA).Cross(vertices[apex] - lipA).Dot(normal) >= -tolerance;
    }

}
