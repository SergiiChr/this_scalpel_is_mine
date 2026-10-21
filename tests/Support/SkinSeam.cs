namespace Scalpel.Tests.Support;

/// <summary>Where the simulated skin hands over to the body model, the two must meet without a step.</summary>
public static class SkinSeam {
    /// <summary>Most the simulated skin may lie off the body model where one takes over from the other (meters): more
    /// shows a step.</summary>
    public const float Max = 0.0003f;

    /// <summary>
    /// How far the simulated skin lies off the body model (meters) along the edge of the region, where it hands over
    /// to the model: its grid points next to one the model draws, straight down the site's normal onto the model.
    /// Breathing lifts the site and the trunk together, but not the body's colliders: that lift is added back.
    /// </summary>
    public static float Measure(PatientBody body) {
        var sim = body.Tissue;
        // The published replacement mask excludes quiet gown points even when they belong to the solver's
        // working region. Only where skin is actually drawn does it hand over to the body model.
        using var mask = body.RegionTexture.GetImage();
        var region = mask.GetData();
        if (body.Layers[0].Mesh.GetSurfaceCount() == 0) { return 0f; }
        using var arrays = body.Layers[0].Mesh.SurfaceGetArrays(0);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        var drawn = new Dictionary<Vector2I, List<Vector3>>();
        for (var v = 0; v < vertices.Length; v++) {
            var grid = uvs[v] * new Vector2(sim.ResX, sim.ResY);
            var cell = (Vector2I)grid.Round();
            if (grid.DistanceSquaredTo(cell) > 1e-8f) { continue; }
            if (!drawn.TryGetValue(cell, out var points)) { drawn[cell] = points = []; }
            points.Add(vertices[v]);
        }
        var up = body.Site.GlobalBasis.Y.Normalized();
        // Along the site's own up: a site on the back is turned over in the body.
        var lift = (body.Site.Basis.Inverse() * (body.Site.Position - body.SiteDef.Position)).Y;
        var space = body.GetWorld3D().DirectSpaceState;
        var worst = 0f;
        for (var k = 0; k < region.Length; k++) {
            if (region[k] == 0 || sim.Off[k] || sim.Excised[k]) {
                continue;
            }
            var at = sim.CellOf(k);
            var edge = ((Vector2I[])[Vector2I.Left, Vector2I.Right, Vector2I.Up, Vector2I.Down])
                .Select(step => (at + step).Clamp(Vector2I.Zero, new Vector2I(sim.ResX, sim.ResY)))
                .Any(n => region[sim.Index(n.X, n.Y)] == 0);
            if (!edge) {
                continue;
            }
            if (!drawn.TryGetValue(at, out var points)) { continue; }
            foreach (var point in points) {
                var skin = body.Site.ToGlobal(point);
                using var query = PhysicsRayQueryParameters3D.Create(skin + (up * 0.05f), skin - (up * 0.05f), PatientBody.SurfaceLayer);
                using var hit = space.IntersectRay(query);
                if (hit.Count > 0) {
                    var onModel = body.Site.ToLocal(hit["position"].AsVector3()).Y + lift;
                    worst = Mathf.Max(worst, Mathf.Abs(onModel - point.Y));
                }
            }
        }
        return worst;
    }

    /// <summary>
    /// Over one breath (at the 12 a minute of a patient under), how far the trunk moved between its lowest and highest
    /// (meters), and the most the site and the trunk moved apart: the site has to ride the trunk, or the drawn body
    /// slides out from under its skin.
    /// </summary>
    public static async Task<(float Rise, float Apart)> OverBreath(PatientBody body) {
        var skeleton = body.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().First();
        var torso = skeleton.FindBone("Torso");
        var rest = body.SiteDef.Position;
        var (lowest, highest, apart) = (float.MaxValue, float.MinValue, 0f);
        for (var frame = 0; frame < 5 * Engine.PhysicsTicksPerSecond; frame++) {
            await Frames.NextPhysics();
            var trunk = skeleton.GlobalBasis * (skeleton.GetBoneGlobalPose(torso).Origin - skeleton.GetBoneGlobalRest(torso).Origin);
            var site = body.Site.GetParentNode3D().GlobalBasis * (body.Site.Position - rest);
            lowest = Mathf.Min(lowest, trunk.Length());
            highest = Mathf.Max(highest, trunk.Length());
            apart = Mathf.Max(apart, trunk.DistanceTo(site));
        }
        return (highest - lowest, apart);
    }
}
