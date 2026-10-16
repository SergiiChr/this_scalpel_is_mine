namespace Scalpel.Patients;

/// <summary>
/// The surgical drape: a sheet laid over the torso and legs with an opening (fenestration) framing the surgical site.
/// Built once from the body model at rest, in body space (patient along X, head at +X), a little off the skin so the
/// body never pokes through it, even breathing. It covers the edge of the site, where the simulated skin meets the
/// body, and the flanks the flat site can't follow. Hands rest on it (DrapeLayer), tools and rays for limbs don't.
/// </summary>
public partial class Drape : MeshInstance3D {
    // Where the sheet lies, body space: from past the feet to just under the collarbones, and across the table as far as
    // the arms leave room (they lie at about |z| 0.22 and may lift, so the sheet stays clear of them).
    public const float FromX = -1.3f;
    public const float ToX = 0.44f;
    /// <summary>How far the sheet reaches across from the middle where no arm is beside the body: over most of the
    /// table's pads (0.28), leaving their edge bare.</summary>
    public const float MaxHalfWidth = 0.27f;
    /// <summary>How far the sheet reaches across from the middle beside the shoulders, where the arms lie close.</summary>
    public const float MinHalfWidth = 0.185f;
    /// <summary>How far the sheet's side stays from the arm beside it (meters).</summary>
    public const float ArmClearance = 0.01f;
    public const float Cell = 0.02f;
    /// <summary>How far the sheet stays off the skin (meters): more than a breath lifts the torso under it.</summary>
    public const float Offset = 0.012f;
    /// <summary>How steeply the sheet falls past what holds it up (height per meter across): down the flanks to the
    /// table.</summary>
    public const float Fall = 2.5f;
    /// <summary>How far the sheet hangs over its ends.</summary>
    public const float EndHem = 0.04f;
    /// <summary>How much of the site's edge the sheet covers, as a share of the site: the fixed border of the simulated
    /// skin.</summary>
    public const float Frame = 0.04f;
    public const uint DrapeLayer = 512;
    public static readonly Color DrapeColor = new(0.24f, 0.42f, 0.4f);
    /// <summary>Bones of the body model that move the arms: the sheet keeps clear of the skin they carry.</summary>
    private static readonly string[] ArmBones = ["UpperArm", "Forearm", "Hand"];
    /// <summary>The side of the body the sheet lies on: +1 the front, -1 the back.</summary>
    internal float Up { get; private set; } = 1f;
    /// <summary>How much of a vertex's weight the arm bones carry for it to be the arm's.</summary>
    private const float ArmWeight = 0.9f;
    /// <summary>How far apart (meters) the skin under the sheet is measured, finer than the sheet's grid so a flank
    /// between its points stays under it.</summary>
    private const float Sample = Cell / 2f;
    /// <summary>Columns either side of one whose narrowest side holds it in, so the sheet's side runs straight past an
    /// elbow or a hand instead of zigzagging round it.</summary>
    private const int SideSpan = 3;

    /// <summary>
    /// Lays the sheet. <paramref name="meshes"/>: the body model's meshes (skin, gown). <paramref name="site"/>: the
    /// surgical site node, a child of <paramref name="root"/>. <paramref name="up"/>: +1 face up, -1 for a site on the
    /// back (the sheet goes on that side).
    /// </summary>
    public void Build(Node3D root, IEnumerable<MeshInstance3D> meshes, Node3D site, Vector2 siteSize, float up) {
        Name = "Drape";
        Up = up;
        var trunkFaces = new List<Vector3>();
        var armFaces = new List<Vector3>();
        foreach (var mesh in meshes) {
            Split(mesh, root.GlobalTransform.AffineInverse() * mesh.GlobalTransform, trunkFaces, armFaces);
        }
        var trunk = new TriangleMesh();
        trunk.CreateFromFaces([.. trunkFaces]);
        var columns = Mathf.CeilToInt((ToX - FromX) / Cell) + 1;
        // An odd count: a row runs down the middle, between the legs.
        var rows = (Mathf.RoundToInt(MaxHalfWidth / Cell) * 2) + 1;
        // The arm's skin nearest the body beside each column on each side (0: -z, 1: +z), at any height.
        var armNear = new float[2, columns];
        for (var i = 0; i < columns; i++) {
            armNear[0, i] = armNear[1, i] = float.PositiveInfinity;
        }
        foreach (var v in armFaces) {
            var side = v.Z > 0f ? 1 : 0;
            var i = Mathf.RoundToInt((v.X - FromX) / Cell);
            for (var n = Mathf.Max(i - 1, 0); n <= Mathf.Min(i + 1, columns - 1); n++) {
                armNear[side, n] = Mathf.Min(armNear[side, n], Mathf.Abs(v.Z));
            }
        }
        var (left, right) = (Side(armNear, 0, columns), Side(armNear, 1, columns));
        // Each column spans its own width, so the grid's last rows run along the sheet's sides.
        Vector2 Across(int i, int j) => new(FromX + i * Cell, Mathf.Lerp(-left[i], right[i], j / (rows - 1f)));
        var heights = Lay(trunk, columns, rows, Across, up);
        Vector3 PointAt(int i, int j) {
            var at = Across(i, j);
            return new(at.X, heights[j * columns + i], at.Y);
        }
        // Clip each cloth triangle against the rectangular opening. Moving its corners to their nearest edge
        // can reverse or overlap triangles at the opening's corners, producing triangular creases and shadows.
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        for (var j = 0; j < rows - 1; j++) {
            for (var i = 0; i < columns - 1; i++) {
                var a = PointAt(i, j);
                var b = PointAt(i + 1, j);
                var c = PointAt(i + 1, j + 1);
                var d = PointAt(i, j + 1);
                OutsideOpening(surface, site, siteSize, [a, b, c], up);
                OutsideOpening(surface, site, siteSize, [a, c, d], up);
            }
        }
        // Hems: the ends hang a little, down to the body under them at most. The long sides hang down to the table,
        // out round the hips and the chest where they bulge past the sheet's side, but no nearer the arm.
        void Hem(Vector3 a, Vector3 b, Vector3[] below, float facing) {
            if (Mathf.Abs(below[0].Y - a.Y) > 0.002f || Mathf.Abs(below[1].Y - b.Y) > 0.002f) {
                Quad(surface, [a, b, below[1], below[0]], facing);
            }
        }
        Vector3 EndBelow(Vector3 p, Vector3 outward) {
            var hit = trunk.IntersectRay(p + outward, Vector3.Down * up);
            var under = hit.Count > 0 ? up * hit["position"].AsVector3().Y : -PatientBody.HalfHeight;
            return new Vector3(p.X, up * Mathf.Max(up * p.Y - EndHem, under + Offset / 2f), p.Z) + outward;
        }
        Vector3 SideBelow(int i, int j, float sign) {
            var p = PointAt(i, j);
            var room = Mathf.Max(Mathf.Abs(p.Z), Mathf.Min(Mathf.Abs(p.Z) + ArmClearance, armNear[sign > 0f ? 1 : 0, i] - Offset / 2f));
            var z = Mathf.Clamp(Widest(trunk, p.X, sign) + Offset / 2f, Mathf.Abs(p.Z), room);
            return new Vector3(p.X, up * (-PatientBody.HalfHeight + Offset / 2f), sign * z);
        }
        foreach (var (j, sign) in new[] { (0, -1f), (rows - 1, 1f) }) {
            var below = Enumerable.Range(0, columns).Select(i => SideBelow(i, j, sign)).ToArray();
            for (var i = 0; i < columns - 1; i++) {
                Hem(PointAt(i, j), PointAt(i + 1, j), [below[i], below[i + 1]], j == 0 ? -up : up);
            }
        }
        for (var j = 0; j < rows - 1; j++) {
            foreach (var i in new[] { 0, columns - 1 }) {
                var (a, b) = (PointAt(i, j), PointAt(i, j + 1));
                var outward = new Vector3(i == 0 ? -1f : 1f, 0, 0) * 0.004f;
                Hem(a, b, [EndBelow(a, outward), EndBelow(b, outward)], i == 0 ? up : -up);
            }
        }
        surface.Index();
        surface.GenerateNormals();
        Mesh = surface.Commit();
        var material = Materials.FamilyUnique("cloth", DrapeColor, 0.95f);
        MaterialOverride = material;
        // A thin cloth sheet blocks the light from either side. Its edge is shaded by the lights rather than an
        // expanded inverted hull, which can draw black triangular wedges where the opening bends.
        CastShadow = ShadowCastingSetting.DoubleSided;
        var solid = new StaticBody3D { CollisionLayer = DrapeLayer, CollisionMask = 0 };
        solid.AddChild(new CollisionShape3D { Shape = Mesh.CreateTrimeshShape() });
        AddChild(solid);
    }

    /// <summary>Sorts the faces of <paramref name="mesh"/> (at rest, moved by <paramref name="toRoot"/>) into those of
    /// the trunk and legs and those the arm bones carry.</summary>
    internal static void Split(MeshInstance3D mesh, Transform3D toRoot, List<Vector3> trunk, List<Vector3> arms) {
        var skeleton = mesh.GetNodeOrNull<Skeleton3D>(mesh.Skeleton);
        // A skin binds its bones by name or by index into the skeleton; without one the index is the skeleton's.
        string BoneName(int bone) => mesh.Skin is { } skin
            ? skin.GetBindBone(bone) is >= 0 and var index ? skeleton?.GetBoneName(index) ?? "" : skin.GetBindName(bone).ToString()
            : skeleton?.GetBoneName(bone) ?? "";
        var armBones = new Dictionary<int, bool>();
        bool IsArm(int bone) => armBones.TryGetValue(bone, out var arm) ? arm : armBones[bone] = ArmBones.Any(BoneName(bone).StartsWith);
        for (var s = 0; s < mesh.Mesh.GetSurfaceCount(); s++) {
            var arrays = mesh.Mesh.SurfaceGetArrays(s);
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
            var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
            var perVertex = vertices.Length == 0 ? 0 : bones.Length / vertices.Length;
            var onArm = new bool[vertices.Length];
            for (var v = 0; v < vertices.Length && perVertex > 0; v++) {
                // Skin the shoulder shares with the chest is the chest's: the sheet lies over it.
                onArm[v] = Enumerable.Range(v * perVertex, perVertex)
                    .Where(n => IsArm(bones[n])).Sum(n => weights[n]) >= ArmWeight;
            }
            for (var n = 0; n + 2 < indices.Length; n += 3) {
                var faces = Enumerable.Range(n, 3).Any(k => onArm[indices[k]]) ? arms : trunk;
                for (var k = n; k < n + 3; k++) {
                    faces.Add(toRoot * vertices[indices[k]]);
                }
            }
        }
    }

    /// <summary>How far the sheet reaches to <paramref name="side"/> (0: -z, 1: +z) at each column: up to the table's
    /// width, or <see cref="ArmClearance"/> short of the arm beside it (<paramref name="armNear"/>), held in by its
    /// neighbors so its side runs straight.</summary>
    private static float[] Side(float[,] armNear, int side, int columns) {
        IEnumerable<int> Around(int i, int span) => Enumerable.Range(Mathf.Max(i - span, 0), Mathf.Min(i + span, columns - 1) - Mathf.Max(i - span, 0) + 1);
        var raw = Enumerable.Range(0, columns).Select(i => Mathf.Clamp(armNear[side, i] - ArmClearance, MinHalfWidth, MaxHalfWidth)).ToArray();
        var held = Enumerable.Range(0, columns).Select(i => Around(i, SideSpan).Min(n => raw[n])).ToArray();
        // Averaging columns that each hold in this one keeps it short of its arm too.
        return [.. Enumerable.Range(0, columns).Select(i => Around(i, SideSpan / 2).Average(n => held[n]))];
    }

    /// <summary>How far out (|z|) the trunk and legs reach to the <paramref name="sign"/> side at <paramref name="x"/>,
    /// at any height: hips and chests bulge past the top that rays from above find.</summary>
    private static float Widest(TriangleMesh trunk, float x, float sign) {
        var widest = 0f;
        for (var y = -PatientBody.HalfHeight; y < PatientBody.HalfHeight; y += Sample) {
            var hit = trunk.IntersectRay(new Vector3(x, y, sign * 0.5f), new Vector3(0, 0, -sign));
            if (hit.Count > 0) {
                widest = Mathf.Max(widest, sign * hit["position"].AsVector3().Z);
            }
        }
        return widest;
    }

    /// <summary>Height of the sheet at each grid point (<paramref name="across"/> gives its x and z): Offset over the
    /// skin of the trunk and legs, measured finer than the grid, falling away at <see cref="Fall"/> where the skin
    /// drops faster (down the flanks), never below the table. Each measured point holds the sheet up
    /// over a sample around it, so it stays over sharp tops (toes, knees) between its grid points.</summary>
    private static float[] Lay(TriangleMesh trunk, int columns, int rows, Func<int, int, Vector2> across, float up) {
        var sampleColumns = Mathf.CeilToInt((ToX - FromX) / Sample) + 1;
        var sampleRows = Mathf.CeilToInt(MaxHalfWidth * 2f / Sample) + 1;
        // Heights are measured up from the sheet's side of the body, so a sheet on the back works the same.
        var skin = new float[sampleColumns * sampleRows];
        var top = float.NegativeInfinity;
        for (var j = 0; j < sampleRows; j++) {
            for (var i = 0; i < sampleColumns; i++) {
                var origin = new Vector3(FromX + i * Sample, up * 0.5f, -MaxHalfWidth + j * Sample);
                var hit = trunk.IntersectRay(origin, Vector3.Down * up);
                skin[j * sampleColumns + i] = hit.Count > 0 ? up * hit["position"].AsVector3().Y + Offset : float.NegativeInfinity;
                top = Mathf.Max(top, skin[j * sampleColumns + i]);
            }
        }
        var table = -PatientBody.HalfHeight + Offset;
        var reach = Mathf.CeilToInt((top - table) / Fall / Sample);
        var heights = new float[columns * rows];
        for (var j = 0; j < rows; j++) {
            for (var i = 0; i < columns; i++) {
                var at = across(i, j);
                var (si, sj) = (Mathf.RoundToInt((at.X - FromX) / Sample), Mathf.RoundToInt((at.Y + MaxHalfWidth) / Sample));
                // Right under the point too: the samples around it are up to half a sample away.
                var under = trunk.IntersectRay(new Vector3(at.X, up * 0.5f, at.Y), Vector3.Down * up);
                var height = under.Count > 0 ? Mathf.Max(table, up * under["position"].AsVector3().Y + Offset) : table;
                for (var b = Mathf.Max(sj - reach, 0); b <= Mathf.Min(sj + reach, sampleRows - 1); b++) {
                    for (var a = Mathf.Max(si - reach, 0); a <= Mathf.Min(si + reach, sampleColumns - 1); a++) {
                        var near = skin[b * sampleColumns + a];
                        if (near > height) {
                            var gap = new Vector2(FromX + a * Sample, -MaxHalfWidth + b * Sample).DistanceTo(at);
                            height = Mathf.Max(height, near - Fall * Mathf.Max(gap - Sample, 0f));
                        }
                    }
                }
                heights[j * columns + i] = up * height;
            }
        }
        return heights;
    }

    private static Vector2 SiteUv(Node3D site, Vector2 siteSize, Vector3 p) {
        var local = site.Transform.AffineInverse() * p;
        return new Vector2(local.X / siteSize.X + 0.5f, local.Z / siteSize.Y + 0.5f);
    }

    /// <summary>Disjoint strips outside the opening, with intersection heights on the original cloth triangle.</summary>
    private static void OutsideOpening(SurfaceTool surface, Node3D site, Vector2 size, List<Vector3> polygon, float up) {
        foreach (var (axis, boundary, sign) in new[] { (0, Frame, 1f), (0, 1f - Frame, -1f), (1, Frame, 1f), (1, 1f - Frame, -1f) }) {
            float Distance(Vector3 p) => (SiteUv(site, size, p)[axis] - boundary) * sign;
            var outside = Clip(polygon, Distance, false);
            for (var n = 1; n + 1 < outside.Count; n++) {
                var a = outside[0];
                var b = outside[n];
                var c = outside[n + 1];
                if ((b - a).Cross(c - a).LengthSquared() < 1e-16f) { continue; }
                foreach (var p in up > 0f ? new[] { a, b, c } : new[] { a, c, b }) {
                    surface.AddVertex(p);
                }
            }
            polygon = Clip(polygon, Distance, true);
            if (polygon.Count < 3) { break; }
        }
    }

    private static List<Vector3> Clip(List<Vector3> polygon, Func<Vector3, float> distance, bool inside) {
        var result = new List<Vector3>();
        for (var n = 0; n < polygon.Count; n++) {
            var a = polygon[n];
            var b = polygon[(n + 1) % polygon.Count];
            var da = distance(a);
            var db = distance(b);
            var keepA = inside ? da >= 0f : da <= 0f;
            var keepB = inside ? db >= 0f : db <= 0f;
            if (keepA) { result.Add(a); }
            if (keepA != keepB) { result.Add(a.Lerp(b, da / (da - db))); }
        }
        return result;
    }

    /// <summary>Two triangles, wound so the side facing <paramref name="up"/> is the front.</summary>
    private static void Quad(SurfaceTool surface, Vector3[] corners, float up) {
        int[] order = up > 0f ? [0, 1, 2, 0, 2, 3] : [0, 2, 1, 0, 3, 2];
        foreach (var n in order) {
            surface.AddVertex(corners[n]);
        }
    }
}
