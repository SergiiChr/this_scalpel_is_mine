namespace Scalpel.Tests.Models;

/// <summary>The cautery pencil's electrode and heat envelope, inspected cold, held, active and after release.
/// Model fixtures use the same grip and animator as the game; tool behavior is unchanged by this art revision.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tool_cautery"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class CauteryAppearanceTest {
    [TestCase]
    public async Task ElectrodeReachesTheWorkingTipAndHeatStaysOnItsEnd() {
        var studio = new ModelStudio(0.155f);
        try {
            var def = Db.Tool("cautery")!;
            var hand = GripCheck.MakeHand(studio.Root, 1);
            var model = GripCheck.Hold(hand, def, Db.GripFit(def, 1), studio.Root);
            var tip = (MeshInstance3D)model.FindChild("Tip", true, false);
            var glow = (MeshInstance3D)model.FindChild("Glow", true, false);
            var tipEnd = ModelStudio.Vertices(model, tip).Min(vertex => vertex.Z);
            AssertFloat(tipEnd).OverrideFailureMessage("the steel electrode reaches the cautery's gameplay tip")
                .IsEqualApprox(-def.Length, 0.0001f);
            AssertFloat(tip.Mesh.GetAabb().Size.X).OverrideFailureMessage("the electrode is a narrow strip beyond the insulated nose")
                .IsLess(0.0031f);
            var animator = new ToolAnimator();
            animator.Setup(model, def.Action);
            AssertBool(glow.Visible).OverrideFailureMessage("the cold electrode has no detached glowing bead").IsFalse();
            await Frames.NextPhysics();
            hand.Visible = false;
            await studio.Capture(model, "cautery_model", "00_cold_oblique",
                "the complete white and blue pencil, tapered insulated nose and slim steel electrode; no heat glow while cold", -0.06f);
            hand.Visible = true;
            await studio.Capture(model, "cautery_model", "01_held_oblique",
                "the right glove holds the pencil without clipping its barrel or button; the nose and electrode remain exposed", -0.06f);
            animator.Animate(true, false, 1f / 60f);
            AssertBool(glow.Visible).OverrideFailureMessage("using the cautery shows its heat envelope").IsTrue();
            var heat = ModelStudio.Vertices(model, glow).ToArray();
            AssertFloat(heat.Min(vertex => vertex.Z)).OverrideFailureMessage("heat does not extend beyond the working tip")
                .IsGreaterEqual(-def.Length - 0.0001f);
            AssertFloat(heat.Max(vertex => vertex.Z)).OverrideFailureMessage("the heat envelope stays on the electrode's last few millimeters")
                .IsLess(-def.Length + 0.007f);
            await studio.Capture(model, "cautery_model", "02_active_oblique",
                "the first active pose: a small heat glow hugs the electrode tip, with no floating bead ahead of it", -0.06f);
            animator.Animate(false, false, 1f / 60f);
            AssertBool(glow.Visible).OverrideFailureMessage("release hides the heat envelope again").IsFalse();
            await studio.Capture(model, "cautery_model", "03_released_oblique",
                "after release the electrode is cold again; the tapered pencil, button and fitted glove retain their shape", -0.06f);
        } finally {
            await studio.Finish();
        }
    }
}
