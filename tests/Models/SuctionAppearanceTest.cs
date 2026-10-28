namespace Scalpel.Tests.Models;

/// <summary>The suction handpiece's recessed inlet, connected hose and fitted gloves.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tool_suction"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class SuctionAppearanceTest {
    [TestCase]
    public async Task RecessedInletIsCenteredOnTheWorkingPointAndBothGlovesClearTheHandle() {
        var studio = new ModelStudio(0.34f);
        try {
            var def = Db.Tool("suction")!;
            for (var index = 0; index < 2; index++) {
                var hand = GripCheck.MakeHand(studio.Root, index);
                var model = GripCheck.Hold(hand, def, Db.GripFit(def, index), studio.Root);
                var bulb = (MeshInstance3D)model.FindChild("Bulb", true, false);
                var points = Vertices(model, bulb).ToArray();
                AssertFloat(points.Min(vertex => vertex.Z))
                    .OverrideFailureMessage("the inlet lip reaches the suction tool's gameplay plane")
                    .IsEqualApprox(-def.Length, 0.0001f);
                var lip = points.Where(vertex => Mathf.Abs(vertex.Z + def.Length) < 0.0001f).ToArray();
                AssertFloat(lip.Average(vertex => vertex.X)).IsEqualApprox(0f, 0.0001f);
                AssertFloat(lip.Average(vertex => vertex.Y)).IsEqualApprox(0f, 0.0001f);
                AssertFloat(lip.Min(vertex => new Vector2(vertex.X, vertex.Y).Length()))
                    .OverrideFailureMessage("the working axis opens into the inlet instead of a capped bulb")
                    .IsGreater(0.0029f);
                var bore = (MeshInstance3D)model.FindChild("Bore", true, false);
                AssertFloat(Vertices(model, bore).Min(vertex => vertex.Z) + def.Length)
                    .OverrideFailureMessage("the dark inlet floor is recessed behind the lip").IsGreater(0.01f);
                await Frames.NextPhysics();
                AssertInt(GripCheck.Clipped(hand)).OverrideFailureMessage($"the suction handle clears glove {index}").IsEqual(0);
                if (index == 0) {
                    hand.Visible = false;
                    await studio.Capture(model, "suction_model", "00_rest_oblique",
                        "a shaped white handpiece joins the bent shaft and blue hose through a single connector; the thumb vent is visible", -0.045f);
                    await studio.Capture(model, "suction_model", "01_recessed_inlet",
                        "the bulb tip has a round inlet with a visible lip and recessed dark bore, centered on the working axis",
                        new Vector3(0f, -0.012f, -0.204f), new Vector3(0.12f, 0.1f, -0.55f), 0.085f);
                    hand.Visible = true;
                }
                await studio.Capture(model, "suction_model", $"02_held_{index}",
                    $"glove {index} holds the molded handpiece without clipping; its shaft, bulb tip and connected hose remain exposed", -0.045f);
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
