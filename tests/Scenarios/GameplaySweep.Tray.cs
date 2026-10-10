namespace Scalpel.Tests.Scenarios;

/// <summary>Tools lie on the tray, never in it.</summary>
internal sealed partial class GameplaySweep
{
    /// <summary>How far (meters) a tool or glove may go into what it lies on: collision margins.</summary>
    private const float SinkMargin = 0.003f;

    /// <summary>The lowest point of a node's visible meshes as they're drawn, world space.</summary>
    private static float LowestPoint(Node3D node)
    {
        var lowest = float.MaxValue;
        foreach (var mesh in node.FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>())
        {
            if (mesh.IsVisibleInTree())
            {
                lowest = DrawnMesh.Vertices(mesh).Aggregate(lowest, (low, v) => Mathf.Min(low, v.Y));
            }
        }
        return lowest;
    }

    /// <summary>Height of the room (table, tray, floor) under <paramref name="p"/>, ignoring tools and the patient.
    /// </summary>
    private static float SupportBelow(Node3D node, Vector3 p) =>
        RayDown(node, p + (Vector3.Up * 0.3f), 3.3f, 1) is { } hit ? hit.Y : float.MinValue;

    private static string Touching(SurgicalTool tool) =>
        string.Join(", ", tool.GetCollidingBodies().Select(body => body.Name.ToString()));

    /// <summary>Tools lie on the tray, never in it: at the start, lowered onto it with full effort (the tip only
    /// presses into skin), and once put down. The glove stays out of it too. <paramref name="all"/>: every kind of
    /// tool, otherwise one.</summary>
    private async Task TableChecks(bool all)
    {
        await Frames.Physics(60);
        foreach (var tool in Tools.Tools.Values.Where(tool => tool.State == ToolState.Free))
        {
            var below = SupportBelow(tool, tool.GlobalTransform * tool.Bounds.GetCenter());
            if (LowestPoint(tool) < below - SinkMargin)
            {
                Fail($"the {tool.Def.Id} sinks {(below - LowestPoint(tool)) * 1000f:0.0} mm into what it lies on (at {tool.GlobalPosition}, touching {Touching(tool)}, sleeping {tool.Sleeping}, basis {tool.GlobalBasis})");
            }
        }
        var hand = Me.Hands[1];
        var tray = _surgery.Room.Layout.Tray + new Vector3(0f, 0.93f, 0.1f);
        // Standing at the tray, facing it.
        var standing = Me.GlobalTransform;
        Me.GlobalPosition = new Vector3(tray.X + 0.45f, Me.GlobalPosition.Y, tray.Z);
        Me.LookAt(new Vector3(tray.X, Me.GlobalPosition.Y, tray.Z));
        await Frames.Physics(2);
        var seen = new HashSet<string>();
        foreach (var tool in FreeTools().ToList())
        {
            if (!seen.Add(tool.Def.Id))
            {
                continue;
            }
            Tools.RequestGrab(tool, 1);
            await Frames.Physics(2);
            hand.Attached = false;
            hand.LocalTarget = Me.ToLocal(tray + new Vector3(0f, 0.1f, 0f));
            hand.Lowered = true;
            hand.Level = 3;
            await Frames.Physics(20);
            var top = SupportBelow(tool, tool.TipPosition());
            if (LowestPoint(tool) < top - SinkMargin)
            {
                Fail($"the {tool.Def.Id} lowered onto the tray goes {(top - LowestPoint(tool)) * 1000f:0.0} mm into it");
            }
            if (hand.BonePoints().Any(point => point.Position.Y - point.Radius < SupportBelow(tool, point.Position) - SinkMargin))
            {
                Fail($"the glove holding the {tool.Def.Id} goes into the tray");
            }
            hand.Lowered = false;
            hand.Level = 0;
            Tools.RequestRelease(1, Vector3.Zero);
            // Time to settle: a tool can land on its edge and roll over first.
            await Frames.Physics(150);
            // Lying on the tray itself (one tilted across another tool touches both, and that's a different contact).
            var onTray = tool.GetCollidingBodies().All(body => body is not SurgicalTool);
            if (tool.State == ToolState.Free && onTray)
            {
                var below = SupportBelow(tool, tool.GlobalTransform * tool.Bounds.GetCenter());
                if (LowestPoint(tool) < below - SinkMargin)
                {
                    var shapes = tool.FindChildren("*", "CollisionShape3D", false, false).Cast<CollisionShape3D>()
                        .Select(shape => $"{shape.Shape.Get("size")} {shape.Position}");
                    Fail($"the {tool.Def.Id} put down sinks {(below - LowestPoint(tool)) * 1000f:0.0} mm into what it lies on (at {tool.GlobalPosition}, touching {Touching(tool)}, sleeping {tool.Sleeping}, basis {tool.GlobalBasis}, bounds {tool.Bounds}, shapes {string.Join(", ", shapes)})");
                }
            }
            if (!all)
            {
                break;
            }
        }
        Me.GlobalTransform = standing;
        await Frames.Physics(2);
    }
}
