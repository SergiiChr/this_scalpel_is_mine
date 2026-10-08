namespace Scalpel.Core;

/// <summary>What a ray met first: where, the surface's normal there, and the collider.</summary>
public readonly record struct RayHit(Vector3 Position, Vector3 Normal, GodotObject Collider);

/// <summary>
/// Ray casts that leave no garbage behind. A cast through the engine's API makes a query object and a result
/// dictionary, both C# wrappers the runtime has to track until they're collected; done many times a frame, every
/// collection then stalls the game. These reuse one query and free each result straight away. Main thread only.
/// </summary>
public static class Rays
{
    private static readonly PhysicsRayQueryParameters3D Query = new();
    private static readonly Godot.Collections.Array<Rid> Excluded = [];
    private static readonly Variant Position = "position";
    private static readonly Variant Normal = "normal";
    private static readonly Variant Collider = "collider";
    private static Viewport? _viewport;
    private static PhysicsDirectSpaceState3D? _space;

    /// <summary>The first collider on <paramref name="mask"/> from <paramref name="from"/> to <paramref name="to"/>
    /// (world space) in <paramref name="world"/>'s space, null for none. <paramref name="areas"/>: areas only, not
    /// bodies. <paramref name="inside"/>: also hits a shape from inside it.</summary>
    public static RayHit? Cast(Node3D world, Vector3 from, Vector3 to, uint mask, Rid exclude = default,
        bool areas = false, bool inside = false)
    {
        Query.From = from;
        Query.To = to;
        Query.CollisionMask = mask;
        Query.CollideWithAreas = areas;
        Query.CollideWithBodies = !areas;
        Query.HitFromInside = inside;
        Query.HitBackFaces = true;
        Excluded.Clear();
        if (exclude.IsValid)
        {
            Excluded.Add(exclude);
        }
        Query.Exclude = Excluded;
        using var hit = SpaceOf(world).IntersectRay(Query);
        return hit.Count == 0
            ? null
            : new RayHit(hit[Position].AsVector3(), hit[Normal].AsVector3(), hit[Collider].AsGodotObject());
    }

    /// <summary>The physics space <paramref name="node"/> is in, kept per viewport: asking for the world makes a new
    /// wrapper every time.</summary>
    private static PhysicsDirectSpaceState3D SpaceOf(Node3D node)
    {
        var viewport = node.GetViewport();
        if (viewport != _viewport || _space is null || !GodotObject.IsInstanceValid(_viewport))
        {
            _viewport = viewport;
            _space = node.GetWorld3D().DirectSpaceState;
        }
        return _space;
    }
}
