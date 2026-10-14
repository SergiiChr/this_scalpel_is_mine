namespace Scalpel.Tests.Support;

/// <summary>Tools lie on the tray, never in it.</summary>
public static class InitialSceneChecks {
    /// <summary>How far (meters) a tool or glove may go into what it lies on: collision margins.</summary>
    private const float SinkMargin = 0.003f;

    /// <summary>The lowest point of a node's visible meshes as they're drawn, world space.</summary>
    private static float LowestPoint(Node3D node) {
        var lowest = float.MaxValue;
        foreach (var mesh in node.FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>()) {
            if (mesh.IsVisibleInTree()) {
                lowest = DrawnMesh.Vertices(mesh).Aggregate(lowest, (low, v) => Mathf.Min(low, v.Y));
            }
        }
        return lowest;
    }

    /// <summary>Height of the room (table, tray, floor) under <paramref name="p"/>, ignoring tools and the patient.
    /// </summary>
    private static float SupportBelow(Node3D node, Vector3 p) {
        var ray = PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 0.3f, p - Vector3.Up * 3f, 1);
        var hit = node.GetWorld3D().DirectSpaceState.IntersectRay(ray);
        return hit.Count > 0 ? hit["position"].AsVector3().Y : float.MinValue;
    }

    private static string Touching(SurgicalTool tool) =>
        string.Join(", ", tool.GetCollidingBodies().Select(body => body.Name.ToString()));

    /// <summary>Tools lie on their supports and the defibrillator is on its cart, without moving or using any tool.</summary>
    public static async Task Inspect(Surgery surgery) {
        var tools = surgery.Tools;
        var failures = new List<string>();
        void Fail(string message) => failures.Add(message);
        var layout = surgery.Room.Layout;
        if (layout.Has("defib_cart")) {
            var cart = layout["defib_cart"];
            if (!tools.Tools.Values.Any(tool => tool.Def.Id == "defibrillator"
                && (tool.GlobalPosition - cart).Slide(Vector3.Up).Length() < 0.4f)) {
                Fail("no defibrillator on the defib cart");
            }
        }
        await Frames.Physics(60);
        foreach (var tool in tools.Tools.Values.Where(tool => tool.State == ToolState.Free)) {
            var below = SupportBelow(tool, tool.GlobalTransform * tool.Bounds.GetCenter());
            if (LowestPoint(tool) < below - SinkMargin) {
                Fail($"the {tool.Def.Id} sinks {(below - LowestPoint(tool)) * 1000f:0.0} mm into what it lies on (at {tool.GlobalPosition}, touching {Touching(tool)}, sleeping {tool.Sleeping}, basis {tool.GlobalBasis})");
            }
        }
        AssertBool(failures.Count == 0).OverrideFailureMessage(string.Join("\n", failures)).IsTrue();
    }

    /// <summary>Presses and releases tools on the tray. It may empty containers, so it runs in the destructive
    /// sweep rather than before a successful surgery. Every tool is checked once, one per other configuration.</summary>
    public static async Task ExerciseTray(Surgery surgery, bool all) {
        var tools = surgery.Tools;
        var me = surgery.LocalSurgeon!;
        var failures = new List<string>();
        void Fail(string message) => failures.Add(message);
        var hand = me.Hands[1];
        var tray = surgery.Room.Layout.Tray + new Vector3(0f, 0.93f, 0.1f);
        // Standing at the tray, facing it.
        var standing = me.GlobalTransform;
        me.GlobalPosition = new Vector3(tray.X + 0.45f, me.GlobalPosition.Y, tray.Z);
        me.LookAt(new Vector3(tray.X, me.GlobalPosition.Y, tray.Z));
        await Frames.Physics(2);
        var seen = new HashSet<string>();
        foreach (var tool in tools.Tools.Values.Where(tool => tool.State == ToolState.Free
            && me.BlockedReason(tool.Def).Length == 0).ToList()) {
            if (!seen.Add(tool.Def.Id)) {
                continue;
            }
            tools.RequestGrab(tool, 1);
            await Frames.Physics(2);
            hand.Attached = false;
            hand.LocalTarget = me.ToLocal(tray + new Vector3(0f, 0.1f, 0f));
            hand.Lowered = true;
            hand.Level = 3;
            await Frames.Physics(20);
            var top = SupportBelow(tool, tool.TipPosition());
            if (LowestPoint(tool) < top - SinkMargin) {
                Fail($"the {tool.Def.Id} lowered onto the tray goes {(top - LowestPoint(tool)) * 1000f:0.0} mm into it");
            }
            if (hand.BonePoints().Any(point => point.Position.Y - point.Radius < SupportBelow(tool, point.Position) - SinkMargin)) {
                Fail($"the glove holding the {tool.Def.Id} goes into the tray");
            }
            hand.Lowered = false;
            hand.Level = 0;
            tools.RequestRelease(1, Vector3.Zero);
            // Time to settle: a tool can land on its edge and roll over first.
            await Frames.Physics(150);
            // Lying on the tray itself (one tilted across another tool touches both, and that's a different contact).
            var onTray = tool.GetCollidingBodies().All(body => body is not SurgicalTool);
            if (tool.State == ToolState.Free && onTray) {
                var below = SupportBelow(tool, tool.GlobalTransform * tool.Bounds.GetCenter());
                if (LowestPoint(tool) < below - SinkMargin) {
                    var shapes = tool.FindChildren("*", "CollisionShape3D", false, false).Cast<CollisionShape3D>()
                        .Select(shape => $"{shape.Shape.Get("size")} {shape.Position}");
                    Fail($"the {tool.Def.Id} put down sinks {(below - LowestPoint(tool)) * 1000f:0.0} mm into what it lies on (at {tool.GlobalPosition}, touching {Touching(tool)}, sleeping {tool.Sleeping}, basis {tool.GlobalBasis}, bounds {tool.Bounds}, shapes {string.Join(", ", shapes)})");
                }
            }
            if (!all) {
                break;
            }
        }
        me.GlobalTransform = standing;
        await Frames.Physics(2);
        AssertBool(failures.Count == 0).OverrideFailureMessage(string.Join("\n", failures)).IsTrue();
    }
}
