namespace Scalpel.Tests.Support;

/// <summary>Holding a tool the way the game does and finding where it goes through the glove. The model suite checks
/// every tool with it; fitting the grips (data/grips.json) uses the same check so that nothing does.</summary>
public static class GripCheck
{
    private const uint SolidLayer = 1 << 19;
    /// <summary>Where the hand holds a tool for the check: in front of the right or left shoulder, about where it
    /// works. </summary>
    private static readonly Vector3 HandAt = new(0.17f, 1.05f, -0.42f);
    private static readonly Vector3 Shoulder = new(0.19f, 1.4f, -0.08f);
    /// <summary>Liquid in a tool isn't solid: a finger may dip into a full dish.</summary>
    private static readonly HashSet<string> LiquidParts = ["Level", "Liquid", "Pool"];

    /// <summary>A glove under <paramref name="holder"/>, the way a surgeon's hand is built (on a body node of its own).
    /// </summary>
    public static SurgeonHand MakeHand(Node3D holder, int index)
    {
        var body = new Node3D();
        holder.AddChild(body);
        var hand = new SurgeonHand();
        body.AddChild(hand);
        hand.Build(index, Materials.ToonUnique(new Color(0.2f, 0.36f, 0.34f)));
        return hand;
    }

    /// <summary>Poses the hand holding a tool of this kind with the given fit; returns the tool's model, placed in the
    /// hand, with a solid for point queries (wait a physics frame before querying it).</summary>
    public static Node3D Hold(SurgeonHand hand, ToolDef def, GripFit fit, Node3D holder)
    {
        var side = hand.Index == 0 ? -1f : 1f;
        hand.Holding = true;
        hand.Grip = def.Grip;
        hand.Fit = fit;
        hand.Tilt = hand.DefaultTilt();
        hand.Turn = hand.DefaultTurn();
        hand.Target = new Vector3(HandAt.X * side, HandAt.Y, HandAt.Z);
        hand.SnapPose(ShoulderOf(hand));
        var tool = new Node3D();
        holder.AddChild(tool);
        ModelSlot.InstantiateTool(def, tool);
        tool.GlobalTransform = hand.GripTransform();
        AddSolid(tool);
        return tool;
    }

    public static Vector3 ShoulderOf(SurgeonHand hand) =>
        new(Shoulder.X * (hand.Index == 0 ? -1f : 1f), Shoulder.Y, Shoulder.Z);

    /// <summary>How many points inside the glove (or its <paramref name="part"/>, see SurgeonHand.BonePoints()) are
    /// inside the tool or too close to its surface: a tool may rest against the fingers, not pass through
    /// them.</summary>
    public static int Clipped(SurgeonHand hand, string part = "")
    {
        var space = hand.GetWorld3D().DirectSpaceState;
        return hand.BonePoints(part)
            .Count(point => Inside(space, point.Position) || Near(space, point.Position, point.Radius * 0.5f));
    }

    /// <summary>A static trimesh of a model where it stands, for point queries, without the liquid in it.</summary>
    private static void AddSolid(Node3D model)
    {
        var faces = new List<Vector3>();
        foreach (var mesh in model.FindChildren("*", nameof(MeshInstance3D), true, false).Cast<MeshInstance3D>())
        {
            if (LiquidParts.Contains(mesh.Name))
            {
                continue;
            }
            var xform = model.GlobalTransform.AffineInverse() * mesh.GlobalTransform;
            faces.AddRange(mesh.Mesh.GetFaces().Select(vertex => xform * vertex));
        }
        var shape = new ConcavePolygonShape3D { BackfaceCollision = true };
        shape.SetFaces([.. faces]);
        var body = new StaticBody3D { CollisionLayer = SolidLayer, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = shape });
        model.AddChild(body);
    }

    /// <summary>Inside a closed mesh: a ray out from the point crosses its surface an odd number of times.</summary>
    private static bool Inside(PhysicsDirectSpaceState3D space, Vector3 point)
    {
        var direction = new Vector3(1f, 0.013f, 0.007f).Normalized();
        var crossings = 0;
        var from = point;
        for (var i = 0; i < 32; i++)
        {
            var query = PhysicsRayQueryParameters3D.Create(from, point + direction, SolidLayer);
            query.HitBackFaces = true;
            var hit = space.IntersectRay(query);
            if (hit.Count == 0)
            {
                break;
            }
            crossings++;
            from = hit["position"].AsVector3() + (direction * 0.0002f);
        }
        return crossings % 2 == 1;
    }

    private static bool Near(PhysicsDirectSpaceState3D space, Vector3 point, float radius)
    {
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = new SphereShape3D { Radius = radius },
            Transform = new Transform3D(Basis.Identity, point),
            CollisionMask = SolidLayer,
        };
        return space.IntersectShape(query, 1).Count > 0;
    }
}
