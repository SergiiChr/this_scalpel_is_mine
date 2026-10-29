namespace Scalpel.Tests.Models;

/// <summary>The loose tourniquet and its fitted grips, plus the applied band on narrow and broad limbs.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tool_tourniquet"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class TourniquetAppearanceTest {
    private static readonly string[] LooseParts = ["Strap", "Tail", "Keeper"];
    [TestCase]
    public async Task CompleteTourniquetClearsBothGloves() {
        var studio = new ModelStudio(0.28f);
        try {
            var def = Db.Tool("tourniquet")!;
            for (var index = 0; index < 2; index++) {
                var hand = GripCheck.MakeHand(studio.Root, index);
                var model = GripCheck.Hold(hand, def, Db.GripFit(def, index), studio.Root);
                await Frames.NextPhysics();
                AssertInt(GripCheck.Clipped(hand))
                    .OverrideFailureMessage($"the tourniquet clears glove {index}").IsEqual(0);
                if (index == 0) {
                    hand.Visible = false;
                    await studio.Capture(model, "tourniquet_model", "00_rest_oblique",
                        "the complete loose tourniquet, with its loop and adjustment hardware visible together", -0.09f);
                    await studio.Capture(model, "tourniquet_model", "01_hardware_detail",
                        "the buckle and windlass sit against the strap without a gap",
                        new Vector3(0f, 0f, -0.15f), new Vector3(0.45f, 0.35f, -0.25f), 0.12f);
                    hand.Visible = true;
                }
                await studio.Capture(model, "tourniquet_model", $"0{index + 2}_held_{index}",
                    $"glove {index} holds the tourniquet without clipping; the loop and adjustment hardware remain visible", -0.09f);
                model.QueueFree();
                hand.GetParent().QueueFree();
                await Frames.NextPhysics();
            }
        } finally {
            await studio.Finish();
        }
    }

    [TestCase(0.035f)]
    [TestCase(0.09f)]
    public async Task WrappedBandFitsTheLimbAndUnwrapRestoresTheModel(float radius) {
        var studio = new ModelStudio(0.28f);
        try {
            var def = Db.Tool("tourniquet")!;
            var tool = new SurgicalTool();
            studio.Root.AddChild(tool);
            tool.Setup(9000, def);
            tool.Freeze = true;
            var originalBounds = tool.Bounds;
            var looseParts = LooseParts
                .Select(name => (MeshInstance3D)tool.FindChild(name, true, false))
                .ToArray();
            var modelRoot = looseParts[0].GetParent<Node3D>()!;
            var originalModelTransform = modelRoot.Transform;

            tool.WrapAround(Vector3.Zero, Vector3.Right, radius);
            var firstBand = tool.Band;
            AssertBool(firstBand is not null).OverrideFailureMessage("wrapping creates a band").IsTrue();
            tool.WrapAround(Vector3.Zero, Vector3.Right, radius);
            AssertBool(tool.Band is not null && tool.Band != firstBand)
                .OverrideFailureMessage("wrapping again replaces the previous band").IsTrue();
            await Frames.NextProcess();
            AssertInt(tool.FindChildren("Band", nameof(MeshInstance3D), true, false).Count)
                .OverrideFailureMessage("only the current band remains after wrapping twice").IsEqual(1);

            var band = tool.Band!;
            AssertFloat(band.Mesh.GetAabb().Size.Y)
                .OverrideFailureMessage("the wrapped band keeps the tourniquet's full width")
                .IsEqualApprox(def.Width, 0.0001f);
            var radial = band.Mesh.GetFaces().Select(vertex => new Vector2(vertex.X, vertex.Z).Length()).ToArray();
            AssertFloat(radial.Min()).OverrideFailureMessage("the inside of the band matches the limb radius")
                .IsEqualApprox(radius, 0.0001f);
            AssertFloat(radial.Max()).OverrideFailureMessage("the outside of the band adds only its narrow fabric thickness")
                .IsEqualApprox(radius + 0.003f, 0.0001f);
            foreach (var name in new[] { "Buckle", "Windlass", "Saddle" }) {
                var hardware = tool.FindChild(name, true, false);
                AssertBool(hardware is Node3D visible && visible.IsVisibleInTree())
                    .OverrideFailureMessage($"the applied tourniquet keeps its {name} visible").IsTrue();
            }
            foreach (var part in looseParts) {
                AssertBool(part.IsVisibleInTree())
                    .OverrideFailureMessage($"the loose {part.Name} is hidden while the tourniquet is applied").IsFalse();
            }

            tool.Unwrap();
            AssertBool(tool.Band is null).OverrideFailureMessage("unwrapping clears the band").IsTrue();
            foreach (var part in looseParts) {
                AssertBool(part.IsVisibleInTree())
                    .OverrideFailureMessage($"unwrapping restores the loose {part.Name}").IsTrue();
            }
            AssertBool(modelRoot.Transform == originalModelTransform)
                .OverrideFailureMessage("unwrapping restores the loose model's original transform").IsTrue();
            AssertBool(tool.Bounds == originalBounds)
                .OverrideFailureMessage("wrapping leaves the tool's model bounds unchanged").IsTrue();
            tool.QueueFree();
            await Frames.NextProcess();
        } finally {
            await studio.Finish();
        }
    }
}
