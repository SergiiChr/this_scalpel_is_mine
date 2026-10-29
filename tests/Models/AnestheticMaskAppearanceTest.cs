namespace Scalpel.Tests.Models;

/// <summary>The anesthetic mask's face cushion, open cup, connected hose and fitted gloves.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tool_gas_mask"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class AnestheticMaskAppearanceTest {
    [TestCase]
    public async Task FaceCushionReachesTheWorkingPlaneAndBothGlovesClearTheMask() {
        var studio = new ModelStudio(0.24f);
        try {
            var def = Db.Tool("gas_mask")!;
            for (var index = 0; index < 2; index++) {
                var hand = GripCheck.MakeHand(studio.Root, index);
                var model = GripCheck.Hold(hand, def, Db.GripFit(def, index), studio.Root);
                var cushion = (MeshInstance3D)model.FindChild("Cushion", true, false);
                var cushionPoints = Vertices(model, cushion).ToArray();
                AssertFloat(cushionPoints.Min(vertex => vertex.Z))
                    .OverrideFailureMessage("the face cushion reaches the anesthetic mask's gameplay plane")
                    .IsEqualApprox(-def.Length, 0.0001f);

                var cup = (MeshInstance3D)model.FindChild("Cup", true, false);
                var cupPoints = Vertices(model, cup).ToArray();
                var blocksOpening = false;
                for (var corner = 0; corner < cupPoints.Length; corner += 3) {
                    var projected = cupPoints.Skip(corner).Take(3)
                        .Select(vertex => new Vector2(vertex.X, vertex.Y)).ToArray();
                    blocksOpening |= Geometry2D.IsPointInPolygon(Vector2.Zero, projected);
                }
                AssertBool(blocksOpening).OverrideFailureMessage("the cup leaves its central airway open").IsFalse();
                AssertBool(cup.GetSurfaceOverrideMaterial(0) is BaseMaterial3D {
                    Transparency: BaseMaterial3D.TransparencyEnum.Alpha,
                }).OverrideFailureMessage("the cup uses a translucent material").IsTrue();

                await Frames.NextPhysics();
                AssertInt(GripCheck.Clipped(hand))
                    .OverrideFailureMessage($"the anesthetic mask clears glove {index}").IsEqual(0);
                if (index == 0) {
                    hand.Visible = false;
                    await studio.Capture(model, "gas_mask_model", "00_rest_oblique",
                        "the face cushion and cup join the connector, elbow and complete hose in one loose mask", -0.02f);
                    await studio.Capture(model, "gas_mask_model", "01_face_cushion",
                        "the cushion surrounds an open cup, with the connector visible behind it",
                        new Vector3(0f, 0f, -0.085f), new Vector3(0.18f, 0.12f, -0.40f), 0.145f);
                    hand.Visible = true;
                }
                await studio.Capture(model, "gas_mask_model", $"0{index + 2}_held_{index}",
                    $"glove {index} holds the anesthetic mask without clipping; the face cup and hose remain exposed", -0.02f);
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
}
