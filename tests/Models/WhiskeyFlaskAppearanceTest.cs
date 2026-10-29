namespace Scalpel.Tests.Models;

/// <summary>The flattened steel flask, fitted leather sleeve and seated chrome cap.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tool_whiskey_flask"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class WhiskeyFlaskAppearanceTest {
    private const float QueryScale = 1000f;

    [TestCase]
    public async Task LeatherSleeveFitsTheFlattenedBodyAndTheSeatedCapClearsBothGloves() {
        var studio = new ModelStudio(0.23f);
        try {
            var def = Db.Tool("whiskey_flask")!;
            for (var index = 0; index < 2; index++) {
                var hand = GripCheck.MakeHand(studio.Root, index);
                var model = GripCheck.Hold(hand, def, Db.GripFit(def, index), studio.Root);
                await Frames.NextPhysics();
                AssertInt(GripCheck.Clipped(hand)).OverrideFailureMessage($"the whiskey flask clears glove {index}").IsEqual(0);
                await studio.Capture(model, "whiskey_flask_model", $"0{index + 2}_held_{index}",
                    $"glove {index} holds the whiskey flask without clipping; the steel shoulders and chrome cap remain exposed",
                    new Vector3(0f, 0f, -0.055f),
                    index == 1 ? new Vector3(0.18f, 0.20f, -0.14f) : new Vector3(0.18f, 0.20f, 0.14f));
                model.QueueFree();
                hand.GetParent().QueueFree();
                await Frames.NextPhysics();
            }

            var tool = new SurgicalTool();
            studio.Root.AddChild(tool);
            tool.Setup(9000, def);
            tool.Freeze = true;
            var body = (MeshInstance3D)tool.FindChild("Flask", true, false);
            var wrap = (MeshInstance3D)tool.FindChild("Wrap", true, false);
            var cap = (MeshInstance3D)tool.FindChild("Cap", true, false);
            var bodyFaces = ModelStudio.Vertices(tool, body).ToArray();
            var capFaces = ModelStudio.Vertices(tool, cap).ToArray();
            AssertFloat(capFaces.Min(point => point.Z))
                .OverrideFailureMessage("the cap ends at the configured drinking point").IsEqualApprox(-def.Length, 0.0001f);
            AssertFloat(capFaces.Max(point => point.Z) - bodyFaces.Min(point => point.Z))
                .OverrideFailureMessage("the cap seats on the steel shoulder without an axial gap").IsEqualApprox(0f, 0.0001f);
            var width = bodyFaces.Max(point => point.X) - bodyFaces.Min(point => point.X);
            var thickness = bodyFaces.Max(point => point.Y) - bodyFaces.Min(point => point.Y);
            AssertFloat(width / thickness).OverrideFailureMessage("the flask body is flattened across its broad face").IsGreater(2.5f);
            using var bodyTree = new TriangleMesh();
            using var wrapTree = new TriangleMesh();
            AssertBool(bodyTree.CreateFromFaces(bodyFaces.Select(point => point * QueryScale).ToArray())
                && wrapTree.CreateFromFaces(ModelStudio.Vertices(tool, wrap).Select(point => point * QueryScale).ToArray()))
                .OverrideFailureMessage("the imported body and sleeve have queryable triangle meshes").IsTrue();
            foreach (var fraction in new[] { 0.25f, 0.5f, 0.7f }) {
                foreach (var x in new[] { -0.02f, 0f, 0.02f }) {
                    foreach (var side in new[] { -1f, 1f }) {
                        var from = new Vector3(x, side * 0.05f, -def.Length * 0.8f * fraction);
                        var to = new Vector3(x, 0f, from.Z);
                        var bodyHeight = OuterHeight(bodyTree, from, to, side);
                        var wrapHeight = OuterHeight(wrapTree, from, to, side);
                        AssertBool(bodyHeight.HasValue && wrapHeight.HasValue)
                            .OverrideFailureMessage($"the leather sleeve covers both broad faces of the steel body (x={x}, fraction={fraction}, side={side})").IsTrue();
                        var clearance = (wrapHeight!.Value - bodyHeight!.Value) * side;
                        AssertFloat(clearance).OverrideFailureMessage("the leather lies just outside the steel surface without crossing it or floating away")
                            .IsBetween(0.0001f, 0.002f);
                    }
                }
            }
            await studio.Capture(tool, "whiskey_flask_model", "00_rest_oblique",
                "a flattened steel flask has shaped shoulders, a clean leather sleeve and a seated stepped chrome cap",
                new Vector3(0f, 0f, -0.055f), new Vector3(0.18f, 0.20f, 0.14f), 0.18f);
            await studio.Capture(tool, "whiskey_flask_model", "01_wrap_front",
                "the leather sleeve follows the broad face with clean edges and visible steel above and below it",
                new Vector3(0f, 0f, -0.055f), new Vector3(0.0001f, 0.4f, 0.0001f), 0.18f);
            await studio.Capture(tool, "whiskey_flask_model", "04_cap_closeup",
                "the stepped chrome cap seats directly on the narrow steel shoulder without a floating gap",
                new Vector3(0f, 0f, -0.108f), new Vector3(0.07f, 0.09f, -0.11f), 0.065f);
            tool.QueueFree();
            await Frames.NextPhysics();
        } finally {
            await studio.Finish();
        }
    }

    // Query both segment directions and use the outermost surface, including for the hollow sleeve.
    private static float? OuterHeight(TriangleMesh mesh, Vector3 from, Vector3 to, float side) {
        // Express the same geometry in millimeters for stable intersection tolerances on small triangles.
        from *= QueryScale;
        to *= QueryScale;
        var forward = mesh.IntersectSegment(from, to);
        var reverse = mesh.IntersectSegment(to, from);
        if (forward.Count == 0 && reverse.Count == 0) {
            return null;
        }
        var first = forward.Count > 0 ? forward["position"].AsVector3().Y * side : float.NegativeInfinity;
        var second = reverse.Count > 0 ? reverse["position"].AsVector3().Y * side : float.NegativeInfinity;
        return Mathf.Max(first, second) * side / QueryScale;
    }

}
