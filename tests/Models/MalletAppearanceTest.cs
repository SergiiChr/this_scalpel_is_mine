namespace Scalpel.Tests.Models;

/// <summary>The mallet's assembled crosshead, working crown and fitted gloves.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tool_mallet"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class MalletAppearanceTest {
    [TestCase]
    public async Task HeadReachesTheWorkingPointAndBothGlovesClearTheGrip() {
        var studio = new ModelStudio(0.31f);
        try {
            var def = Db.Tool("mallet")!;
            for (var index = 0; index < 2; index++) {
                var hand = GripCheck.MakeHand(studio.Root, index);
                var model = GripCheck.Hold(hand, def, Db.GripFit(def, index), studio.Root);
                var head = (MeshInstance3D)model.FindChild("Head", true, false);
                var points = head.Mesh.GetFaces().Select(vertex => model.ToLocal(head.ToGlobal(vertex))).ToArray();
                AssertFloat(points.Min(vertex => vertex.Z))
                    .OverrideFailureMessage("the forward crown reaches the mallet's gameplay plane")
                    .IsEqualApprox(-def.Length, 0.0001f);
                var crownCoversAxis = false;
                for (var corner = 0; corner < points.Length; corner += 3) {
                    var triangle = points.Skip(corner).Take(3).ToArray();
                    if (triangle.All(vertex => Mathf.Abs(vertex.Z + def.Length) < 0.0001f)
                        && Geometry2D.IsPointInPolygon(Vector2.Zero, triangle.Select(vertex => new Vector2(vertex.X, vertex.Y)).ToArray())) {
                        crownCoversAxis = true;
                    }
                }
                AssertBool(crownCoversAxis).OverrideFailureMessage("the working axis meets the head's flat exterior crown").IsTrue();
                await Frames.NextPhysics();
                AssertInt(GripCheck.Clipped(hand)).OverrideFailureMessage($"the mallet clears glove {index}").IsEqual(0);
                if (index == 0) {
                    hand.Visible = false;
                    await studio.Capture(model, "mallet_model", "00_rest_oblique",
                        "a shaped black grip joins the tapered steel shank and beveled crosshead, with pale impact caps and a steel butt cap", -0.11f);
                    await studio.Capture(model, "mallet_model", "01_head_detail",
                        "the pale circular impact cap seats inside the beveled steel head; the shank joins the head without a gap",
                        new Vector3(0f, 0f, -0.258f), new Vector3(0.55f, 0.4f, 0.18f), 0.1f);
                    hand.Visible = true;
                }
                await studio.Capture(model, "mallet_model", $"02_held_{index}",
                    $"glove {index} wraps the shaped black grip without clipping; the tapered shank and complete crosshead remain exposed", -0.11f);
                model.QueueFree();
                hand.GetParent().QueueFree();
                await Frames.NextPhysics();
            }
        } finally {
            await studio.Finish();
        }
    }
}
