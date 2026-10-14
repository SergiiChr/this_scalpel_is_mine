namespace Scalpel.Tests.Support;

/// <summary>Checks on the drawn tissue layers (PatientBody.Layers): the skin, fat and muscle sheets and the walls down
/// their cuts, as their meshes are published.</summary>
public static class TissueLayers {
    /// <summary>
    /// Triangles of the skin's cut walls and of the fat and muscle (sheet and walls) passing through a triangle of the
    /// exposed skin, as "Fat wall at (u, v) through skin at (u, v)": a lip pushed over its own opening, or a wall
    /// standing up through the skin beside it. Skin under the drape is left out: the cloth hides it, and it's drawn
    /// sunk under the cloth, where a lifted lip's deep layers hang into it.
    /// </summary>
    public static List<string> ThroughSkin(PatientBody body) {
        var skin = body.Layers[0].Mesh;
        if (skin.GetSurfaceCount() == 0) {
            return [];
        }
        var tissue = body.Tissue;
        var arrays = skin.SurfaceGetArrays(0);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        bool Exposed(int v) {
            var uv = uvs[v];
            var k = tissue.Index(Mathf.RoundToInt(uv.X * tissue.ResX), Mathf.RoundToInt(uv.Y * tissue.ResY));
            return k < tissue.Exposed.Length && tissue.Exposed[k];
        }
        var skinTriangles = new List<(Vector3 A, Vector3 B, Vector3 C, Vector3 Low, Vector3 High)>();
        for (var t = 0; t < indices.Length; t += 3) {
            if (!Exposed(indices[t]) && !Exposed(indices[t + 1]) && !Exposed(indices[t + 2])) {
                continue;
            }
            var (a, b, c) = (vertices[indices[t]], vertices[indices[t + 1]], vertices[indices[t + 2]]);
            skinTriangles.Add((a, b, c, a.Min(b).Min(c), a.Max(b).Max(c)));
        }
        var through = new List<string>();
        for (var layer = 0; layer < body.Layers.Count; layer++) {
            var mesh = body.Layers[layer].Mesh;
            // The skin's own sheet is what's checked against; its walls are not.
            for (var surface = layer == 0 ? 1 : 0; surface < mesh.GetSurfaceCount(); surface++) {
                var surfaceArrays = mesh.SurfaceGetArrays(surface);
                var points = surfaceArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var faces = surfaceArrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                for (var t = 0; t < faces.Length; t += 3) {
                    var (p, q, r) = (points[faces[t]], points[faces[t + 1]], points[faces[t + 2]]);
                    var low = p.Min(q).Min(r);
                    var high = p.Max(q).Max(r);
                    foreach (var (a, b, c, skinLow, skinHigh) in skinTriangles) {
                        if (low.X > skinHigh.X || high.X < skinLow.X || low.Y > skinHigh.Y || high.Y < skinLow.Y
                            || low.Z > skinHigh.Z || high.Z < skinLow.Z) {
                            continue;
                        }
                        if (Crosses(p, q, a, b, c) || Crosses(q, r, a, b, c) || Crosses(r, p, a, b, c)
                            || Crosses(a, b, p, q, r) || Crosses(b, c, p, q, r) || Crosses(c, a, p, q, r)) {
                            through.Add($"{body.Layers[layer].Name} {(surface == 0 ? "sheet" : "wall")} at {body.LocalToUv((p + q + r) / 3f)} through skin at {body.LocalToUv((a + b + c) / 3f)}");
                            break;
                        }
                    }
                }
            }
        }
        return through;
    }

    /// <summary>Asserts that no cut wall, fat or muscle triangle passes through the exposed skin (ThroughSkin()): what
    /// lies under the skin stays under it, and the lips stay on their own side of the opening, whatever the skin
    /// does.</summary>
    public static void AssertUnderSkin(PatientBody body) {
        var through = ThroughSkin(body);
        AssertInt(through.Count)
            .OverrideFailureMessage($"the cut's walls, fat and muscle stay under the skin ({through.Count} triangles through it: {string.Join("; ", through.Take(6))})")
            .IsEqual(0);
    }

    /// <summary>Whether the segment from <paramref name="from"/> to <paramref name="to"/> passes through the inside
    /// of triangle abc. Touching it, or crossing its plane within float noise of it, doesn't count: adjoining faces
    /// meet at shared edges without penetrating.</summary>
    public static bool Crosses(Vector3 from, Vector3 to, Vector3 a, Vector3 b, Vector3 c) {
        var direction = to - from;
        var ab = b - a;
        var ac = c - a;
        var normal = ab.Cross(ac).Normalized();
        var fromSide = (from - a).Dot(normal);
        var toSide = (to - a).Dot(normal);
        // 20 micrometres: far below a visible crease or the skin's thickness.
        const float ContactTolerance = 0.00002f;
        if (fromSide * toSide >= 0f || Mathf.Abs(fromSide) < ContactTolerance || Mathf.Abs(toSide) < ContactTolerance) {
            return false;
        }
        var h = direction.Cross(ac);
        var det = ab.Dot(h);
        if (Mathf.Abs(det) < 1e-12f) {
            return false;
        }
        var offset = from - a;
        var u = offset.Dot(h) / det;
        var q = offset.Cross(ab);
        var v = direction.Dot(q) / det;
        var t = ac.Dot(q) / det;
        const float Margin = 0.0001f;
        return u > Margin && v > Margin && u + v < 1f - Margin && t > Margin && t < 1f - Margin;
    }

    /// <summary>Barycentric weights of p in triangle abc, negative outside, all -1 for a degenerate triangle.</summary>
    public static Vector3 Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c) {
        var area = (b - a).Cross(c - a);
        if (Mathf.Abs(area) < 1e-12f) {
            return new Vector3(-1f, -1f, -1f);
        }
        var wb = (p - a).Cross(c - a) / area;
        var wc = (b - a).Cross(p - a) / area;
        return new Vector3(1f - wb - wc, wb, wc);
    }
}
