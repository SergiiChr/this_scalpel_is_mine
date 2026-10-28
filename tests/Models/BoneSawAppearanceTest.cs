namespace Scalpel.Tests.Models;

/// <summary>The manual and circular saws' assembled silhouettes, at rest and at a named active pose in their fitted fists.
/// These are model fixtures; player pickup and blade animation behavior are covered by the existing tool suites.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tool_bone_saw"), TestCategory("tool_heavy_saw"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class BoneSawAppearanceTest {
    [TestCase("bone_saw")]
    [TestCase("heavy_saw")]
    public async Task BladeAndTeethShareAPlaneTowardTheWorkingTip(string id) {
        var studio = new ModelStudio(id == "heavy_saw" ? 0.26f : 0.24f);
        try {
            var def = Db.Tool(id)!;
            var circular = id == "heavy_saw";
            var folder = circular ? "heavy_saw_model" : "bone_saw_model";
            var centerZ = circular ? -0.105f : -0.09f;
            var hand = GripCheck.MakeHand(studio.Root, 1);
            var model = GripCheck.Hold(hand, def, Db.GripFit(def, 1), studio.Root);
            var blade = (MeshInstance3D)model.FindChild("Blade", true, false);
            var box = blade.Mesh.GetAabb();
            AssertFloat(blade.Position.Z + box.Position.Z).OverrideFailureMessage("the saw reaches its -Z gameplay tip")
                .IsEqualApprox(-def.Length, 0.0001f);
            AssertFloat(box.Size.X).OverrideFailureMessage("the blade and teeth share a thin YZ plane")
                .IsLessEqual(0.0031f);
            if (!circular) {
                AssertFloat(box.End.Z).OverrideFailureMessage("the tang ends inside the grip rather than behind it")
                    .IsLess(0.05f);
            }
            AssertFloat(box.Size.Y).OverrideFailureMessage("the broad face is vertical, including the tooth edge")
                .IsGreater(0.045f);
            if (circular) {
                var radius = box.Size.Y * 0.5f;
                AssertFloat(blade.Position.Z + def.Length)
                    .OverrideFailureMessage("the circular blade pivot is one radius behind the working tip")
                    .IsEqualApprox(radius, 0.0001f);
                var guard = (MeshInstance3D)model.FindChild("Guard", true, false);
                var closest = guard.Mesh.GetFaces().Min(vertex => {
                    var at = blade.ToLocal(guard.ToGlobal(vertex));
                    return new Vector2(at.Y, at.Z).Length();
                });
                AssertFloat(closest - radius).OverrideFailureMessage("the fixed guard and its mount clear every rotary pose")
                    .IsGreater(0.0015f);
            }
            await Frames.NextPhysics();
            // Existing model contracts cover both hands; this view uses the actual right-hand grip fit.
            hand.Visible = false;
            await studio.Capture(model, folder, "00_unheld_oblique", circular
                ? "the circular steel disc is centered on its hub, clear of the fixed upper guard, with the lower and leading cutting arc exposed"
                : "the complete saw: one broad vertical steel blade, connected teeth, black grip, steel collar and pin", centerZ);
            hand.Visible = true;
            await studio.Capture(model, folder, "01_held_oblique", circular
                ? "the right glove wraps the powered saw's black grip; the white and blue housing, guard and circular disc stay outside the fist"
                : "the right glove wraps the black grip; the collar, blade and teeth stay outside the fist", centerZ);
            var animator = new ToolAnimator();
            animator.Setup(model, def.Action);
            animator.Animate(true, false, 1f / 60f);
            await studio.Capture(model, folder, circular ? "02_rotating_oblique" : "02_oscillating_oblique", circular
                ? "at the first rotary use pose only the disc turns around its hub, staying clear of the fixed guard and carrier"
                : "at the first use pose the blade slides through its collar without separating from the grip; the teeth stay on the blade edge", centerZ);
            animator.Animate(false, false, 1f / 60f);
            await studio.Capture(model, folder, "03_released_oblique", circular
                ? "after release the circular disc returns to its original orientation, with the housing, guard and fitted fist unchanged"
                : "after release the blade rests on its collar again, with the same complete silhouette and fitted fist", centerZ);
        } finally {
            await studio.Finish();
        }
    }
}
