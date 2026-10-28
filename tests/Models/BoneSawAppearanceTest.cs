namespace Scalpel.Tests.Models;

/// <summary>The manual saw's assembled silhouette, at rest and at a named oscillation pose in its fitted fist.
/// These are model fixtures; player pickup and blade animation behavior are covered by the existing tool suites.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tool_bone_saw"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class BoneSawAppearanceTest {
    [TestCase]
    public async Task BladeAndTeethShareAPlaneTowardTheWorkingTip() {
        FrameBudget.RenderingOff();
        var holder = new Node3D();
        Frames.Root.AddChild(holder);
        try {
            var environment = new Godot.Environment {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color("bfcac7"),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color("e3eeef"),
                AmbientLightEnergy = 0.6f,
                ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled,
                TonemapMode = Godot.Environment.ToneMapper.Linear,
            };
            holder.AddChild(new WorldEnvironment { Environment = environment });
            holder.AddChild(new DirectionalLight3D {
                RotationDegrees = new Vector3(-38f, -35f, 0f),
                LightEnergy = 1.1f,
                LightColor = new Color("fff2e0"),
            });
            holder.AddChild(new DirectionalLight3D {
                RotationDegrees = new Vector3(-20f, 145f, 0f),
                LightEnergy = 0.45f,
                LightColor = new Color("d6e7ff"),
            });
            var camera = new Camera3D {
                Projection = Camera3D.ProjectionType.Orthogonal,
                Size = 0.24f,
                Current = true,
                Near = 0.001f,
            };
            holder.AddChild(camera);
            var def = Db.Tool("bone_saw")!;
            var hand = GripCheck.MakeHand(holder, 1);
            var model = GripCheck.Hold(hand, def, Db.GripFit(def, 1), holder);
            var blade = (MeshInstance3D)model.FindChild("Blade", true, false);
            var box = blade.Mesh.GetAabb();
            AssertFloat(box.Position.Z).OverrideFailureMessage("the manual saw reaches its -Z gameplay tip")
                .IsEqualApprox(-def.Length, 0.0001f);
            AssertFloat(box.Size.X).OverrideFailureMessage("the blade and teeth share a thin YZ plane")
                .IsLessEqual(0.0031f);
            AssertFloat(box.End.Z).OverrideFailureMessage("the tang ends inside the grip rather than behind it")
                .IsLess(0.05f);
            AssertFloat(box.Size.Y).OverrideFailureMessage("the broad face is vertical, including the tooth edge")
                .IsGreater(0.045f);
            await Frames.NextPhysics();
            // Existing model contracts cover both hands; this view uses the actual right-hand grip fit.
            hand.Visible = false;
            await Capture(camera, model, "00_unheld_oblique", "the complete saw: one broad vertical steel blade, connected teeth, black grip, steel collar and pin");
            hand.Visible = true;
            await Capture(camera, model, "01_held_oblique", "the right glove wraps the black grip; the collar, blade and teeth stay outside the fist");
            var animator = new ToolAnimator();
            animator.Setup(model, def.Action);
            animator.Animate(true, false, 1f / 60f);
            await Capture(camera, model, "02_oscillating_oblique", "at the first use pose the blade slides through its collar without separating from the grip; the teeth stay on the blade edge");
            animator.Animate(false, false, 1f / 60f);
            await Capture(camera, model, "03_released_oblique", "after release the blade rests on its collar again, with the same complete silhouette and fitted fist");
        } finally {
            holder.QueueFree();
            await Frames.NextProcess();
            RenderingServer.RenderLoopEnabled = true;
        }
    }

    private static async Task Capture(Camera3D camera, Node3D model, string name, string description) {
        if (!KeyFrames.Wanted()) {
            return;
        }
        var at = model.ToGlobal(new Vector3(0f, 0f, -0.09f));
        camera.GlobalPosition = at + model.GlobalBasis * new Vector3(0.55f, 0.4f, 0.18f);
        camera.LookAt(at, model.GlobalBasis.Y);
        await KeyFrames.SaveViewport(KeyFrames.Folder("bone_saw_model").PathJoin(name + ".png"), description);
    }
}
