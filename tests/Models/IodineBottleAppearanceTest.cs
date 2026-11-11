namespace Scalpel.Tests.Models;

/// <summary>The iodine bottle's shaped body, fitted grips and open dispensing nozzle.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tool_iodine_bottle"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class IodineBottleAppearanceTest {
    private const uint NozzleLayer = 1 << 18;

    [TestCase]
    public async Task BottleCollarAndOpenNozzleStayConnectedAndClearBothGloves() {
        var studio = new ModelStudio(0.22f);
        try {
            var def = Db.Tool("iodine_bottle")!;
            for (var index = 0; index < 2; index++) {
                var hand = GripCheck.MakeHand(studio.Root, index);
                var model = GripCheck.Hold(hand, def, Db.GripFit(def, index), studio.Root);
                await Frames.NextPhysics();
                AssertInt(GripCheck.Clipped(hand))
                    .OverrideFailureMessage($"the iodine bottle clears glove {index}").IsEqual(0);
                await studio.Capture(model, "iodine_bottle_model", $"0{index + 2}_held_{index}",
                    $"glove {index} holds the iodine bottle without clipping; its shoulder, collar and nozzle remain exposed",
                    new Vector3(0f, 0f, -0.06f),
                    index == 1 ? new Vector3(0.18f, 0.20f, -0.14f) : new Vector3(0.18f, 0.20f, 0.14f));
                model.QueueFree();
                hand.GetParent().QueueFree();
                await Frames.NextPhysics();
            }

            var tool = new SurgicalTool();
            studio.Root.AddChild(tool);
            tool.Setup(9000, def);
            tool.Freeze = true;
            var body = (MeshInstance3D)tool.FindChild("Bottle", true, false);
            var collar = (MeshInstance3D)tool.FindChild("Collar", true, false);
            var spout = (MeshInstance3D)tool.FindChild("Spout", true, false);
            AssertBool(Overlaps(Bounds(tool, body), Bounds(tool, collar)))
                .OverrideFailureMessage("the screw collar overlaps the bottle neck").IsTrue();
            AssertBool(Overlaps(Bounds(tool, collar), Bounds(tool, spout)))
                .OverrideFailureMessage("the nozzle seats inside the screw collar").IsTrue();
            var spoutPoints = ModelStudio.Vertices(tool, spout).ToArray();
            AssertFloat(spoutPoints.Min(point => point.Z))
                .OverrideFailureMessage("the nozzle ends at the configured pouring point")
                .IsEqualApprox(-def.Length, 0.0001f);
            var shape = new ConcavePolygonShape3D { BackfaceCollision = true };
            shape.SetFaces(spoutPoints);
            var solid = new StaticBody3D { CollisionLayer = NozzleLayer, CollisionMask = 0 };
            tool.AddChild(solid);
            solid.AddChild(new CollisionShape3D { Shape = shape });
            await Frames.NextPhysics();
            var space = tool.GetWorld3D().DirectSpaceState;
            // Offset the lip probes from triangle seams so shared-edge roundoff cannot miss a lip face.
            foreach (var (x, wall) in new[] { (0f, false), (0.003f, true), (-0.003f, true) }) {
                var y = wall ? 0.0003f : 0f;
                var from = tool.ToGlobal(new Vector3(x, y, -def.Length - 0.01f));
                var to = tool.ToGlobal(new Vector3(x, y, -def.Length + 0.004f));
                var query = PhysicsRayQueryParameters3D.Create(from, to, NozzleLayer);
                query.HitBackFaces = true;
                AssertBool(space.IntersectRay(query).Count > 0)
                    .OverrideFailureMessage(wall ? $"the nozzle has a solid lip beside its bore at x={x}" : "the nozzle bore is open along the pouring axis")
                    .IsEqual(wall);
            }
            await studio.Capture(tool, "iodine_bottle_model", "00_rest_oblique",
                "the amber bottle has rounded shoulders, a paper label and a seated ribbed screw collar",
                new Vector3(0f, 0f, -0.068f), new Vector3(0.18f, 0.20f, 0.14f), 0.19f);
            await studio.Capture(tool, "iodine_bottle_model", "01_nozzle_closeup",
                "the tapered nozzle has an open bore and joins the ribbed white collar without a gap",
                new Vector3(0f, 0f, -0.129f), new Vector3(0.07f, 0.09f, -0.11f), 0.055f);
            tool.QueueFree();
            await Frames.NextPhysics();
        } finally {
            await studio.Finish();
        }
    }

    private static (Vector3 Minimum, Vector3 Maximum) Bounds(Node3D model, MeshInstance3D part) {
        var vertices = ModelStudio.Vertices(model, part).ToArray();
        return (
            new Vector3(vertices.Min(vertex => vertex.X), vertices.Min(vertex => vertex.Y), vertices.Min(vertex => vertex.Z)),
            new Vector3(vertices.Max(vertex => vertex.X), vertices.Max(vertex => vertex.Y), vertices.Max(vertex => vertex.Z)));
    }

    private static bool Overlaps((Vector3 Minimum, Vector3 Maximum) first, (Vector3 Minimum, Vector3 Maximum) second) =>
        first.Minimum.X < second.Maximum.X && first.Maximum.X > second.Minimum.X
        && first.Minimum.Y < second.Maximum.Y && first.Maximum.Y > second.Minimum.Y
        && first.Minimum.Z < second.Maximum.Z && first.Maximum.Z > second.Minimum.Z;
}
