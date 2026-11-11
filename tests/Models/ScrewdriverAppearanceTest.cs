namespace Scalpel.Tests.Models;

/// <summary>The screwdriver's connected handle, ferrule, shaft and configured chisel tip.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tool_screwdriver"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class ScrewdriverAppearanceTest {
    [TestCase]
    public async Task HandleFerruleShaftAndTipStayConnectedAndClearBothGloves() {
        var studio = new ModelStudio(0.30f);
        try {
            var def = Db.Tool("screwdriver")!;
            for (var index = 0; index < 2; index++) {
                var hand = GripCheck.MakeHand(studio.Root, index);
                var model = GripCheck.Hold(hand, def, Db.GripFit(def, index), studio.Root);
                if (index == 0) {
                    var handle = (MeshInstance3D)model.FindChild("Handle", true, false);
                    var ferrule = (MeshInstance3D)model.FindChild("Ferrule", true, false);
                    var shaft = (MeshInstance3D)model.FindChild("Shaft", true, false);
                    var tip = (MeshInstance3D)model.FindChild("Tip", true, false);
                    AssertBool(Overlaps(Bounds(model, handle), Bounds(model, ferrule)))
                        .OverrideFailureMessage("the ferrule overlaps the handle at their joint").IsTrue();
                    AssertBool(Overlaps(Bounds(model, ferrule), Bounds(model, shaft)))
                        .OverrideFailureMessage("the ferrule overlaps the steel shaft at their joint").IsTrue();
                    AssertBool(Overlaps(Bounds(model, shaft), Bounds(model, tip)))
                        .OverrideFailureMessage("the flat tip overlaps the steel shaft at their joint").IsTrue();

                    var tipPoints = Vertices(model, tip).ToArray();
                    var workingEdge = tipPoints.Where(vertex => Mathf.Abs(vertex.Z + def.Length) < 0.0001f).ToArray();
                    AssertBool(workingEdge.Length > 0)
                        .OverrideFailureMessage("the screwdriver tip has an edge on the configured working plane").IsTrue();
                    AssertFloat(tipPoints.Min(vertex => vertex.Z))
                        .OverrideFailureMessage("the chisel edge ends at the screwdriver's configured point")
                        .IsEqualApprox(-def.Length, 0.0001f);
                    var edgeVertices = workingEdge.Distinct().ToArray();
                    AssertFloat((edgeVertices.Min(vertex => vertex.X) + edgeVertices.Max(vertex => vertex.X)) * 0.5f)
                        .OverrideFailureMessage("the chisel edge is centered on the screwdriver axis")
                        .IsEqualApprox(0f, 0.0001f);
                    AssertFloat((edgeVertices.Min(vertex => vertex.Y) + edgeVertices.Max(vertex => vertex.Y)) * 0.5f)
                        .OverrideFailureMessage("the chisel edge is centered on the screwdriver axis")
                        .IsEqualApprox(0f, 0.0001f);
                }

                await Frames.NextPhysics();
                AssertInt(GripCheck.Clipped(hand))
                    .OverrideFailureMessage($"the screwdriver clears glove {index}").IsEqual(0);
                if (index == 0) {
                    hand.Visible = false;
                    await studio.Capture(model, "screwdriver_model", "00_rest_oblique",
                        "the molded handle joins the ferrule and full steel shaft, ending in a flat chisel tip",
                        new Vector3(0f, 0f, -0.07f), new Vector3(0.32f, 0.24f, 0.22f));
                    await studio.Capture(model, "screwdriver_model", "01_tip_profile",
                        "the flat chisel tip continues from the centered steel shaft at its configured working point",
                        new Vector3(0f, 0f, -0.17f), new Vector3(0.10f, 0.07f, 0.14f), 0.10f);
                    hand.Visible = true;
                }
                await studio.Capture(model, "screwdriver_model", $"0{index + 2}_held_{index}",
                    $"glove {index} holds the screwdriver without clipping; the ferrule, shaft and tip remain exposed",
                    new Vector3(0f, 0f, -0.07f),
                    index == 1 ? new Vector3(0.32f, 0.24f, -0.22f) : new Vector3(0.32f, 0.24f, 0.22f));
                model.QueueFree();
                hand.GetParent().QueueFree();
                await Frames.NextPhysics();
            }
        } finally {
            await studio.Finish();
        }
    }

    private static IEnumerable<Vector3> Vertices(Node3D model, MeshInstance3D part) =>
        part.Mesh.GetFaces().Select(vertex => model.ToLocal(part.ToGlobal(vertex)));

    private static (Vector3 Minimum, Vector3 Maximum) Bounds(Node3D model, MeshInstance3D part) {
        var vertices = Vertices(model, part).ToArray();
        return (
            new Vector3(vertices.Min(vertex => vertex.X), vertices.Min(vertex => vertex.Y), vertices.Min(vertex => vertex.Z)),
            new Vector3(vertices.Max(vertex => vertex.X), vertices.Max(vertex => vertex.Y), vertices.Max(vertex => vertex.Z)));
    }

    private static bool Overlaps((Vector3 Minimum, Vector3 Maximum) first, (Vector3 Minimum, Vector3 Maximum) second) =>
        first.Minimum.X < second.Maximum.X && first.Maximum.X > second.Minimum.X
        && first.Minimum.Y < second.Maximum.Y && first.Maximum.Y > second.Minimum.Y
        && first.Minimum.Z < second.Maximum.Z && first.Maximum.Z > second.Minimum.Z;
}
