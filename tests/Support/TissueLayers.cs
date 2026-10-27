namespace Scalpel.Tests.Support;

/// <summary>Checks on the drawn tissue layers (PatientBody.Layers): the skin, fat and muscle sheets and the walls down
/// their cuts, as their meshes are published.</summary>
public static class TissueLayers {
    /// <summary>Minimum site-space distance from a point to the actual skin sheet triangles (not just their vertices).
    /// </summary>
    public static float DistanceToSkin(PatientBody body, Vector3 point) {
        var skinLayer = body.Layers[0];
        var skin = skinLayer.Mesh;
        if (skin.GetSurfaceCount() == 0) { return float.PositiveInfinity; }
        var arrays = skin.SurfaceGetArrays(0);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        if (vertices.Length == 0 || indices.Length < 3) { return float.PositiveInfinity; }
        var toSite = body.Site.GlobalTransform.AffineInverse() * skinLayer.GlobalTransform;
        for (var v = 0; v < vertices.Length; v++) { vertices[v] = toSite * vertices[v]; }
        return DistanceToTriangles(vertices, indices, point);
    }

    /// <summary>The skin's cut wall must meet its sheet without a visible slit along the lip.</summary>
    public static void AssertSkinWallJoinsSheet(PatientBody body) {
        var mesh = body.Layers[0].Mesh;
        if (mesh.GetSurfaceCount() < 2) { return; }
        var sheet = mesh.SurfaceGetArrays(0);
        var vertices = sheet[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var indices = sheet[(int)Mesh.ArrayType.Index].AsInt32Array();
        var wall = mesh.SurfaceGetArrays(1);
        var points = wall[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var colors = wall[(int)Mesh.ArrayType.Color].AsColorArray();
        var worst = 0f;
        var at = Vector3.Zero;
        for (var v = 0; v < points.Length; v++) {
            if (colors[v].R > 0f) { continue; }
            var gap = DistanceToTriangles(vertices, indices, points[v]);
            if (gap > worst) {
                (worst, at) = (gap, points[v]);
            }
        }
        AssertFloat(worst).OverrideFailureMessage($"the skin wall joins its sheet without a slit ({worst * 1000f:0.00} mm gap at {at}, uv {body.WorldToUv(body.Layers[0].GlobalTransform * at)})")
            .IsLess(0.0001f);
    }

    private static float DistanceToTriangles(Vector3[] vertices, int[] indices, Vector3 point) {
        var nearest = float.PositiveInfinity;
        for (var t = 0; t + 2 < indices.Length; t += 3) {
            var (a, b, c) = (vertices[indices[t]], vertices[indices[t + 1]], vertices[indices[t + 2]]);
            var normal = (b - a).Cross(c - a);
            if (normal.LengthSquared() > 1e-16f) {
                normal = normal.Normalized();
                var projected = point - normal * (point - a).Dot(normal);
                var weights = Geometry3D.GetTriangleBarycentricCoords(projected, a, b, c);
                if (weights.X >= 0f && weights.Y >= 0f && weights.Z >= 0f) {
                    nearest = Mathf.Min(nearest, point.DistanceTo(projected));
                    continue;
                }
            }
            nearest = Mathf.Min(nearest, Mathf.Min(
                point.DistanceTo(Geometry3D.GetClosestPointToSegment(point, a, b)),
                Mathf.Min(point.DistanceTo(Geometry3D.GetClosestPointToSegment(point, b, c)),
                    point.DistanceTo(Geometry3D.GetClosestPointToSegment(point, c, a)))));
        }
        return nearest;
    }

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

    /// <summary>Proper non-coplanar intersections between exposed triangles on each layer's cut wall. Shared edges
    /// and coplanar duplicates do not count as crossings.</summary>
    public static HashSet<(string Layer, int First, int Second)> WallSelfCrossings(PatientBody body) {
        var crossings = new HashSet<(string Layer, int First, int Second)>();
        foreach (var layer in body.Layers) {
            var mesh = layer.Mesh;
            if (mesh.GetSurfaceCount() < 2) {
                continue;
            }

            var arrays = mesh.SurfaceGetArrays(1);
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            if (vertices.Length == 0 || indices.Length < 3 || uvs.Length == 0) {
                continue;
            }

            bool Exposed(int triangle) {
                for (var corner = 0; corner < 3; corner++) {
                    var k = body.Tissue.Nearest(uvs[indices[triangle * 3 + corner]]);
                    if (k < body.Tissue.Exposed.Length && body.Tissue.Exposed[k]) {
                        return true;
                    }
                }
                return false;
            }
            var triangleCount = indices.Length / 3;
            var triangles = new List<(int Index, Vector3 A, Vector3 B, Vector3 C, Vector3 Low, Vector3 High)>();
            for (var index = 0; index < triangleCount; index++) {
                if (!Exposed(index)) { continue; }
                var (a, b, c) = (vertices[indices[index * 3]], vertices[indices[index * 3 + 1]], vertices[indices[index * 3 + 2]]);
                triangles.Add((index, a, b, c, a.Min(b).Min(c), a.Max(b).Max(c)));
            }
            triangles.Sort((left, right) => left.Low.X.CompareTo(right.Low.X));
            for (var first = 0; first < triangles.Count; first++) {
                var (firstIndex, a, b, c, firstLow, firstHigh) = triangles[first];
                for (var second = first + 1; second < triangles.Count; second++) {
                    var (secondIndex, p, q, r, secondLow, secondHigh) = triangles[second];
                    if (secondLow.X > firstHigh.X) { break; }
                    if (firstLow.Y > secondHigh.Y || firstHigh.Y < secondLow.Y
                        || firstLow.Z > secondHigh.Z || firstHigh.Z < secondLow.Z) {
                        continue;
                    }

                    if (!Crosses(a, b, p, q, r) && !Crosses(b, c, p, q, r) && !Crosses(c, a, p, q, r)
                        && !Crosses(p, q, a, b, c) && !Crosses(q, r, a, b, c) && !Crosses(r, p, a, b, c)) {
                        continue;
                    }

                    crossings.Add((layer.Name, Math.Min(firstIndex, secondIndex), Math.Max(firstIndex, secondIndex)));
                }
            }
        }

        return crossings;
    }

    /// <summary>Asserts that exposed cut walls have no proper non-coplanar self-intersections.</summary>
    public static void AssertWallsDoNotCrossThemselves(PatientBody body) {
        var crossings = WallSelfCrossings(body).Take(6).ToArray();
        AssertInt(crossings.Length)
            .OverrideFailureMessage($"cut walls do not cross themselves ({string.Join("; ", crossings.Select(pair => $"{pair.Layer} triangles {pair.First}/{pair.Second}"))})")
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
