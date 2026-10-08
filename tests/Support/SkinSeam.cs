namespace Scalpel.Tests.Support;

/// <summary>Where the simulated skin hands over to the body model, the two must meet without a step.</summary>
public static class SkinSeam
{
    /// <summary>Most the simulated skin may lie off the body model where one takes over from the other (meters): more
    /// shows a step.</summary>
    public const float Max = 0.0003f;

    /// <summary>
    /// How far the simulated skin lies off the body model (meters) along the edge of the region, where it hands over
    /// to the model: its grid points next to one the model draws, straight down the site's normal onto the model.
    /// Breathing lifts the site and the trunk together, but not the body's colliders: that lift is added back.
    /// </summary>
    public static float Measure(PatientBody body)
    {
        var sim = body.Tissue;
        var region = sim.Region();
        var up = body.Site.GlobalBasis.Y.Normalized();
        var lift = body.Site.Position.Y - body.SiteDef.Position.Y;
        var space = body.GetWorld3D().DirectSpaceState;
        var worst = 0f;
        for (var k = 0; k < region.Length; k++)
        {
            if (region[k] == 0 || sim.Off[k] || sim.Excised[k])
            {
                continue;
            }
            var at = sim.CellOf(k);
            var edge = ((Vector2I[])[Vector2I.Left, Vector2I.Right, Vector2I.Up, Vector2I.Down])
                .Select(step => (at + step).Clamp(Vector2I.Zero, new Vector2I(sim.ResX, sim.ResY)))
                .Any(n => region[sim.Index(n.X, n.Y)] == 0);
            if (!edge)
            {
                continue;
            }
            var skin = body.Site.ToGlobal(body.LayerPoint(0, k));
            var hit = space.IntersectRay(
                PhysicsRayQueryParameters3D.Create(skin + (up * 0.05f), skin - (up * 0.05f), PatientBody.SurfaceLayer));
            if (hit.Count > 0)
            {
                var onModel = body.Site.ToLocal(hit["position"].AsVector3()).Y + lift;
                worst = Mathf.Max(worst, Mathf.Abs(onModel - body.LayerPoint(0, k).Y));
            }
        }
        return worst;
    }
}
