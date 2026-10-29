namespace Scalpel.Tests.Models;

/// <summary>The coffee thermos's connected body, fitted grips, cup cap and open side handle.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tool_coffee_thermos"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class CoffeeThermosAppearanceTest {
    private const uint HandleLayer = 1 << 18;

    [TestCase]
    public async Task CupCapAndOpenHandleStayConnectedAndClearBothGloves() {
        var studio = new ModelStudio(0.27f);
        try {
            var def = Db.Tool("coffee_thermos")!;
            for (var index = 0; index < 2; index++) {
                var hand = GripCheck.MakeHand(studio.Root, index);
                var model = GripCheck.Hold(hand, def, Db.GripFit(def, index), studio.Root);
                await Frames.NextPhysics();
                AssertInt(GripCheck.Clipped(hand))
                    .OverrideFailureMessage($"the coffee thermos clears glove {index}").IsEqual(0);
                await studio.Capture(model, "coffee_thermos_model", $"0{index + 2}_held_{index}",
                    $"glove {index} holds the coffee thermos without clipping; its handle and steel cap remain exposed",
                    new Vector3(0f, 0f, -0.085f),
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
            var seal = (MeshInstance3D)tool.FindChild("NeckSeal", true, false);
            var cap = (MeshInstance3D)tool.FindChild("Cap", true, false);
            var handle = (MeshInstance3D)tool.FindChild("Handle", true, false);
            var mounts = (MeshInstance3D)tool.FindChild("Mounts", true, false);
            AssertBool(Overlaps(Bounds(tool, body), Bounds(tool, seal)) && Overlaps(Bounds(tool, seal), Bounds(tool, cap)))
                .OverrideFailureMessage("the steel cup cap overlaps its seal and the seal overlaps the bottle neck").IsTrue();
            AssertBool(Overlaps(Bounds(tool, body), Bounds(tool, mounts)) && Overlaps(Bounds(tool, mounts), Bounds(tool, handle)))
                .OverrideFailureMessage("the handle joins mounting pads that overlap the bottle").IsTrue();
            AssertFloat(ModelStudio.Vertices(tool, cap).Min(point => point.Z))
                .OverrideFailureMessage("the thermos cap ends at the configured drinking point")
                .IsEqualApprox(-def.Length, 0.0001f);
            var faces = tool.FindChildren("*", nameof(MeshInstance3D), true, false).Cast<MeshInstance3D>()
                .SelectMany(part => ModelStudio.Vertices(tool, part)).ToArray();
            var shape = new ConcavePolygonShape3D { BackfaceCollision = true };
            shape.SetFaces(faces);
            var solid = new StaticBody3D { CollisionLayer = HandleLayer, CollisionMask = 0 };
            tool.AddChild(solid);
            solid.AddChild(new CollisionShape3D { Shape = shape });
            await Frames.NextPhysics();
            var space = tool.GetWorld3D().DirectSpaceState;
            foreach (var (x, solidExpected) in new[] { (0.035f, false), (0.045f, true), (0.02f, true) }) {
                var from = tool.ToGlobal(new Vector3(x, -0.05f, -def.Length * 0.4f));
                var to = tool.ToGlobal(new Vector3(x, 0.05f, -def.Length * 0.4f));
                var query = PhysicsRayQueryParameters3D.Create(from, to, HandleLayer);
                query.HitBackFaces = true;
                AssertBool(space.IntersectRay(query).Count > 0)
                    .OverrideFailureMessage(solidExpected ? $"the handle opening has a solid boundary at x={x}" : "the side handle has an open gap beside the insulated body")
                    .IsEqual(solidExpected);
            }
            await studio.Capture(tool, "coffee_thermos_model", "00_rest_oblique",
                "the thermos has an insulated sleeve with steel trim, connected handle mounts and a seated cup cap",
                new Vector3(0f, 0f, -0.085f), new Vector3(0.18f, 0.20f, 0.14f), 0.24f);
            await studio.Capture(tool, "coffee_thermos_model", "01_handle_profile",
                "the side handle has a continuous open gap and two connected mounting pads",
                new Vector3(0.01f, 0f, -0.085f), new Vector3(0.0001f, 0.4f, 0.0001f), 0.24f);
            await studio.Capture(tool, "coffee_thermos_model", "04_cap_closeup",
                "the steel cup cap seats against its neck seal with a clean dark band and no floating gap",
                new Vector3(0f, 0f, -0.161f), new Vector3(0.07f, 0.09f, -0.11f), 0.09f);
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
