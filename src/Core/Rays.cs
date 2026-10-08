namespace Scalpel.Core;

/// <summary>What a ray met first: where, the surface's normal there, and the collider.</summary>
public readonly record struct RayHit(Vector3 Position, Vector3 Normal, GodotObject Collider);

/// <summary>
/// Ray casts that leave no garbage behind. A cast through the physics space's API returns its hit as a new dictionary,
/// and every such wrapper costs the garbage collector a handle and a finalizer even when disposed: done many times a
/// frame, its pauses grow to hundreds of milliseconds. These go through one RayCast3D node per viewport instead, whose
/// results are plain values. Main thread only.
/// </summary>
public static class Rays
{
    private static RayCast3D? _ray;

    /// <summary>The first collider on <paramref name="mask"/> from <paramref name="from"/> to <paramref name="to"/>
    /// (world space) in <paramref name="world"/>'s space, null for none. <paramref name="areas"/>: areas only, not
    /// bodies. <paramref name="inside"/>: also hits a shape from inside it.</summary>
    public static RayHit? Cast(Node3D world, Vector3 from, Vector3 to, uint mask, Rid exclude = default,
        bool areas = false, bool inside = false)
    {
        var ray = RayIn(world.GetViewport());
        ray.Position = from;
        ray.TargetPosition = to - from;
        ray.CollisionMask = mask;
        ray.CollideWithAreas = areas;
        ray.CollideWithBodies = !areas;
        ray.HitFromInside = inside;
        ray.ClearExceptions();
        if (exclude.IsValid)
        {
            ray.AddExceptionRid(exclude);
        }
        ray.ForceRaycastUpdate();
        return ray.IsColliding()
            ? new RayHit(ray.GetCollisionPoint(), ray.GetCollisionNormal(), ray.GetCollider())
            : null;
    }

    /// <summary>The ray node in <paramref name="viewport"/>'s world, moved there from the last one asked for.</summary>
    private static RayCast3D RayIn(Viewport viewport)
    {
        if (_ray is null || !GodotObject.IsInstanceValid(_ray))
        {
            // Placed by hand before every cast, never by its own physics step.
            _ray = new RayCast3D { Enabled = false, TopLevel = true, HitBackFaces = true };
        }
        if (_ray.GetParent() != viewport)
        {
            _ray.GetParent()?.RemoveChild(_ray);
            viewport.AddChild(_ray, false, Node.InternalMode.Back);
        }
        return _ray;
    }
}
