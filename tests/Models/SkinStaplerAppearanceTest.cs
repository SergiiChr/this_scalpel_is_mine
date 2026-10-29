namespace Scalpel.Tests.Models;

/// <summary>The stapler's forming nose and hinged lever, at rest, held, squeezed and released.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tool_skin_stapler"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class SkinStaplerAppearanceTest {
    [TestCase]
    public async Task FormingNoseReachesTheWorkingTipWhileTheLeverClosesTowardTheHousing() {
        var studio = new ModelStudio(0.24f);
        try {
            var def = Db.Tool("skin_stapler")!;
            for (var index = 0; index < 2; index++) {
                var hand = GripCheck.MakeHand(studio.Root, index);
                var model = GripCheck.Hold(hand, def, Db.GripFit(def, index), studio.Root);
                var anvil = (MeshInstance3D)model.FindChild("Anvil", true, false);
                var trigger = (MeshInstance3D)model.FindChild("Trigger", true, false);
                AssertFloat(ModelStudio.Vertices(model, anvil).Min(vertex => vertex.Z))
                    .OverrideFailureMessage("the forming shoe reaches the stapler's gameplay working plane")
                    .IsEqualApprox(-def.Length, 0.0001f);
                var noseRest = anvil.Transform;
                var leverRest = trigger.Transform;
                var rear = trigger.Mesh.GetFaces().MaxBy(vertex => model.ToLocal(trigger.ToGlobal(vertex)).Z);
                var rearAtRest = model.ToLocal(trigger.ToGlobal(rear));
                var animator = new ToolAnimator();
                animator.Setup(model, def.Action);
                await Frames.NextPhysics();
                AssertInt(GripCheck.Clipped(hand)).OverrideFailureMessage($"the stapler clears glove {index}").IsEqual(0);
                if (index == 0) {
                    hand.Visible = false;
                    await studio.Capture(model, "skin_stapler_model", "00_rest_oblique",
                        "a shaped white housing and blue squeeze lever join at the front hinge; the narrow cartridge and metal forming nose remain connected", -0.05f);
                    await studio.Capture(model, "skin_stapler_model", "01_forming_nose",
                        "the steel forming shoe surrounds the staple exit at the end of the narrow white cartridge",
                        new Vector3(0f, 0f, -0.05f), new Vector3(0.35f, 0.15f, -0.55f));
                    hand.Visible = true;
                }
                await studio.Capture(model, "skin_stapler_model", $"02_held_{index}",
                    $"glove {index} holds the white housing and blue lever without clipping; the cartridge and metal working nose project clear of the glove", -0.05f);
                hand.Trigger = true;
                hand.SnapPose(GripCheck.ShoulderOf(hand));
                animator.Animate(true, false, 1f);
                var rearSqueezed = model.ToLocal(trigger.ToGlobal(rear));
                AssertFloat(rearSqueezed.Y - rearAtRest.Y)
                    .OverrideFailureMessage("squeezing raises the rear of the lever toward the housing").IsGreater(0.015f);
                AssertBool(anvil.Transform == noseRest)
                    .OverrideFailureMessage("the forming nose stays fixed while the lever squeezes").IsTrue();
                GripCheck.RebuildSolid(model);
                await Frames.NextPhysics();
                AssertInt(GripCheck.Clipped(hand)).OverrideFailureMessage($"the squeezed lever clears glove {index}").IsEqual(0);
                if (index == 1) {
                    await studio.Capture(model, "skin_stapler_model", "03_squeezed",
                        "the blue lever rises toward the white housing at full squeeze; its front hinge stays connected and the steel nose remains exposed", -0.05f);
                }
                hand.Trigger = false;
                hand.SnapPose(GripCheck.ShoulderOf(hand));
                animator.Animate(false, false, 1f);
                AssertBool(trigger.Transform == leverRest)
                    .OverrideFailureMessage("release returns the lever to its exact resting pose").IsTrue();
                if (index == 1) {
                    await studio.Capture(model, "skin_stapler_model", "04_released",
                        "after release the lever is open again beneath the housing, with the fitted glove and forming nose retaining their shape", -0.05f);
                }
                model.QueueFree();
                hand.GetParent().QueueFree();
                await Frames.NextPhysics();
            }
        } finally {
            await studio.Finish();
        }
    }
}
