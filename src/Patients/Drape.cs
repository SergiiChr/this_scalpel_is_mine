namespace Scalpel.Patients;

/// <summary>
/// The surgical drape: a sheet laid over the torso and legs with an opening (fenestration) framing the surgical site.
/// Built once from the body model at rest, in body space (patient along X, head at +X), a little off the skin so the
/// body never pokes through it, even breathing. It covers the edge of the site, where the simulated skin meets the
/// body, and the flanks the flat site can't follow. Hands rest on it (DrapeLayer), tools and rays for limbs don't.
/// </summary>
public partial class Drape : MeshInstance3D
{
    // Where the sheet lies, body space: from past the feet to just under the collarbones, and across the torso between
    // the arms (they lie at about |z| 0.22 and may lift, so the sheet stays clear of them).
    public const float FromX = -1.3f;
    public const float ToX = 0.44f;
    public const float HalfWidth = 0.185f;
    public const float Cell = 0.02f;
    /// <summary>How far the sheet stays off the skin (meters): more than a breath lifts the torso under it.</summary>
    public const float Offset = 0.012f;
    /// <summary>How far the sheet hangs over its edges.</summary>
    public const float Hem = 0.025f;
    /// <summary>How much of the site's edge the sheet covers, as a share of the site: the fixed border of the simulated
    /// skin.</summary>
    public const float Frame = 0.04f;
    public const uint DrapeLayer = 512;
    public static readonly Color DrapeColor = new(0.24f, 0.42f, 0.4f);

    /// <summary>
    /// Lays the sheet. <paramref name="meshes"/>: the body model's meshes (skin, gown). <paramref name="site"/>: the
    /// surgical site node, a child of <paramref name="root"/>. <paramref name="up"/>: +1 face up, -1 for a site on the
    /// back (the sheet goes on that side).
    /// </summary>
    public void Build(Node3D root, IEnumerable<MeshInstance3D> meshes, Node3D site, Vector2 siteSize, float up)
    {
        Name = "Drape";
        var faces = new List<Vector3>();
        foreach (var mesh in meshes)
        {
            var toRoot = root.GlobalTransform.AffineInverse() * mesh.GlobalTransform;
            faces.AddRange(mesh.Mesh.GetFaces().Select(v => toRoot * v));
        }
        var body = new TriangleMesh();
        body.CreateFromFaces([.. faces]);
        var columns = Mathf.CeilToInt((ToX - FromX) / Cell) + 1;
        var rows = Mathf.CeilToInt(HalfWidth * 2f / Cell) + 1;
        var heights = Lay(body, columns, rows, up);
        Vector3 PointAt(int i, int j) => new(FromX + i * Cell, heights[j * columns + i], -HalfWidth + j * Cell);
        // Grid points inside the opening move out onto its edge, so the opening is a clean rectangle, not grid steps.
        var points = new Vector3[columns * rows];
        var inside = new bool[columns * rows];
        for (var j = 0; j < rows; j++)
        {
            for (var i = 0; i < columns; i++)
            {
                var k = j * columns + i;
                var p = PointAt(i, j);
                inside[k] = InOpening(site, siteSize, p);
                points[k] = inside[k] ? ToEdge(body, site, siteSize, p, up) : p;
            }
        }
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        for (var j = 0; j < rows - 1; j++)
        {
            for (var i = 0; i < columns - 1; i++)
            {
                int[] ids = [j * columns + i, j * columns + i + 1, (j + 1) * columns + i + 1, (j + 1) * columns + i];
                if (ids.All(k => inside[k]))
                {
                    continue;
                }
                Quad(surface, [.. ids.Select(k => points[k])], up);
            }
        }
        // Hems: the sheet hangs a little over its long sides and its ends instead of stopping in the air.
        var hang = Vector3.Down * up * Hem;
        for (var i = 0; i < columns - 1; i++)
        {
            foreach (var j in new[] { 0, rows - 1 })
            {
                var (a, b) = (PointAt(i, j), PointAt(i + 1, j));
                var outward = new Vector3(0, 0, j == 0 ? -1f : 1f) * 0.004f;
                Quad(surface, [a, b, b + outward + hang, a + outward + hang], j == 0 ? up : -up);
            }
        }
        for (var j = 0; j < rows - 1; j++)
        {
            foreach (var i in new[] { 0, columns - 1 })
            {
                var (a, b) = (PointAt(i, j), PointAt(i, j + 1));
                var outward = new Vector3(i == 0 ? -1f : 1f, 0, 0) * 0.004f;
                Quad(surface, [a, b, b + outward + hang, a + outward + hang], i == 0 ? -up : up);
            }
        }
        surface.GenerateNormals();
        Mesh = surface.Commit();
        var material = Materials.FamilyUnique("cloth", DrapeColor, 0.95f);
        material.NextPass = Materials.OutlineFor(0.002f);
        MaterialOverride = material;
        CastShadow = ShadowCastingSetting.On;
        var solid = new StaticBody3D { CollisionLayer = DrapeLayer, CollisionMask = 0 };
        solid.AddChild(new CollisionShape3D { Shape = Mesh.CreateTrimeshShape() });
        AddChild(solid);
    }

    /// <summary>Height of the sheet at each grid point: the skin under it plus Offset. Over gaps (between the legs) it
    /// spans from side to side, sagging a little, never below the table.</summary>
    private static float[] Lay(TriangleMesh body, int columns, int rows, float up)
    {
        var heights = new float[columns * rows];
        var hit = new bool[columns * rows];
        for (var j = 0; j < rows; j++)
        {
            for (var i = 0; i < columns; i++)
            {
                var result = body.IntersectRay(new Vector3(FromX + i * Cell, up * 0.5f, -HalfWidth + j * Cell), Vector3.Down * up);
                if (result.Count > 0)
                {
                    heights[j * columns + i] = result["position"].AsVector3().Y + up * Offset;
                    hit[j * columns + i] = true;
                }
            }
        }
        for (var j = 0; j < rows; j++)
        {
            for (var i = 0; i < columns; i++)
            {
                if (hit[j * columns + i])
                {
                    continue;
                }
                // The nearest skin on either side across the sheet, lowered a little.
                var near = up > 0f ? float.NegativeInfinity : float.PositiveInfinity;
                for (var step = 0; step < rows && !float.IsFinite(near); step++)
                {
                    foreach (var jj in new[] { j - step, j + step })
                    {
                        if (jj >= 0 && jj < rows && hit[jj * columns + i])
                        {
                            near = up > 0f ? Mathf.Max(near, heights[jj * columns + i]) : Mathf.Min(near, heights[jj * columns + i]);
                        }
                    }
                }
                heights[j * columns + i] = float.IsFinite(near) ? near - up * 0.015f : -up * PatientBody.HalfHeight;
            }
        }
        return heights;
    }

    /// <summary>Inside the site, short of its edge by Frame: left open for the surgery.</summary>
    private static bool InOpening(Node3D site, Vector2 siteSize, Vector3 p)
    {
        var uv = SiteUv(site, siteSize, p);
        return uv.X > Frame && uv.X < 1f - Frame && uv.Y > Frame && uv.Y < 1f - Frame;
    }

    private static Vector2 SiteUv(Node3D site, Vector2 siteSize, Vector3 p)
    {
        var local = site.Transform.AffineInverse() * p;
        return new Vector2(local.X / siteSize.X + 0.5f, local.Z / siteSize.Y + 0.5f);
    }

    /// <summary>A point inside the opening moved to its nearest edge, back on the skin (plus Offset).</summary>
    private static Vector3 ToEdge(TriangleMesh body, Node3D site, Vector2 siteSize, Vector3 p, float up)
    {
        var local = site.Transform.AffineInverse() * p;
        var uv = SiteUv(site, siteSize, p);
        float[] gaps = [uv.X - Frame, 1f - Frame - uv.X, uv.Y - Frame, 1f - Frame - uv.Y];
        switch (Array.IndexOf(gaps, gaps.Min()))
        {
            case 0: uv.X = Frame; break;
            case 1: uv.X = 1f - Frame; break;
            case 2: uv.Y = Frame; break;
            default: uv.Y = 1f - Frame; break;
        }
        var edge = site.Transform * new Vector3((uv.X - 0.5f) * siteSize.X, local.Y, (uv.Y - 0.5f) * siteSize.Y);
        var hit = body.IntersectRay(new Vector3(edge.X, up * 0.5f, edge.Z), Vector3.Down * up);
        return new Vector3(edge.X, hit.Count > 0 ? hit["position"].AsVector3().Y + up * Offset : p.Y, edge.Z);
    }

    /// <summary>Two triangles, wound so the side facing <paramref name="up"/> is the front.</summary>
    private static void Quad(SurfaceTool surface, Vector3[] corners, float up)
    {
        int[] order = up > 0f ? [0, 1, 2, 0, 2, 3] : [0, 2, 1, 0, 3, 2];
        foreach (var n in order)
        {
            surface.AddVertex(corners[n]);
        }
    }
}
