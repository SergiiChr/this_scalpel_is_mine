namespace Scalpel.Tests.Models;

/// <summary>The paddle's contact face, bridge grip, charge light and resting cart pose.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tool_defibrillator"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class DefibrillatorAppearanceTest {
    [TestCase]
    public async Task ContactFaceMeetsTheWorkingTipAndBothGlovesClearTheBridge() {
        var studio = new ModelStudio(0.26f);
        try {
            var def = Db.Tool("defibrillator")!;
            for (var index = 0; index < 2; index++) {
                var hand = GripCheck.MakeHand(studio.Root, index);
                var model = GripCheck.Hold(hand, def, Db.GripFit(def, index), studio.Root);
                var plate = (MeshInstance3D)model.FindChild("Plate", true, false);
                var face = ModelStudio.Vertices(model, plate).ToArray();
                AssertFloat(face.Min(vertex => vertex.Z)).OverrideFailureMessage("the exposed steel face reaches the gameplay contact plane")
                    .IsEqualApprox(-def.Length, 0.0001f);
                AssertBool(face.Any(vertex => Mathf.Abs(vertex.Z + def.Length) < 0.0001f && vertex.X > 0f)
                        && face.Any(vertex => Mathf.Abs(vertex.Z + def.Length) < 0.0001f && vertex.X < 0f))
                    .OverrideFailureMessage("the working point lies between the broad face's edges").IsTrue();
                var animator = new ToolAnimator();
                animator.Setup(model, def.Action);
                var light = (MeshInstance3D)model.FindChild("Light", true, false);
                AssertBool(light.Visible).OverrideFailureMessage("the charge light is off before use").IsFalse();
                var button = (MeshInstance3D)model.FindChild("Button", true, false);
                AssertFloat(ModelStudio.Vertices(model, light).Min(vertex => vertex.Z))
                    .OverrideFailureMessage("the charge indicator clears the button surface instead of being buried in the handle")
                    .IsGreater(ModelStudio.Vertices(model, button).Max(vertex => vertex.Z));
                await Frames.NextPhysics();
                AssertInt(GripCheck.Clipped(hand)).OverrideFailureMessage($"the paddle clears glove {index}").IsEqual(0);
                if (index == 0) {
                    hand.Visible = false;
                    await studio.Capture(model, "defibrillator_model", "00_contact_face",
                        "a broad rounded steel face sits within the thin black housing; the short cord remains attached",
                        new Vector3(0f, -0.018f, -0.035f), new Vector3(0.55f, -0.3f, -0.45f));
                    hand.Visible = true;
                }
                await studio.Capture(model, "defibrillator_model", $"01_held_{index}",
                    $"glove {index} holds the raised bridge handle without clipping the housing or supports; the orange button and broad head remain visible", -0.025f);
                if (index == 1) {
                    animator.Animate(true, false, 1f / 60f);
                    AssertBool(light.Visible).OverrideFailureMessage("the first charging pose shows the charge light").IsTrue();
                    hand.Visible = false;
                    await studio.Capture(model, "defibrillator_model", "02_charging",
                        "the charge indicator is exposed on the orange button of the black bridge handle", -0.025f);
                    hand.Visible = true;
                    animator.Animate(false, false, 1f / 60f);
                    AssertBool(light.Visible).OverrideFailureMessage("release turns off the charge indicator").IsFalse();
                    await studio.Capture(model, "defibrillator_model", "03_released",
                        "after release the indicator is off; the bridge handle, orange button and steel plate retain their resting shape", -0.025f);
                }
                model.QueueFree();
                hand.GetParent().QueueFree();
                await Frames.NextPhysics();
            }
        } finally {
            await studio.Finish();
        }
    }

    [TestCase]
    public async Task ParkedPaddleRestsFaceDownOnTheCartUnit() {
        var studio = new ModelStudio(0.48f);
        var room = new Room();
        try {
            var cart = ModelSlot.Instantiate("props", "defib_cart", studio.Root);
            var model = ModelSlot.InstantiateTool(Db.Tool("defibrillator")!, studio.Root);
            var station = room.StationTools().Single(tool => tool.Id == "defibrillator");
            model.Transform = station.Transform with { Origin = station.Transform.Origin - room.Layout["defib_cart"] };
            var plate = (MeshInstance3D)model.FindChild("Plate", true, false);
            var lowest = plate.Mesh.GetFaces().Min(vertex => plate.ToGlobal(vertex).Y);
            var unit = (MeshInstance3D)cart.FindChild("Unit", true, false);
            var roof = unit.Mesh.GetFaces().Max(vertex => unit.ToGlobal(vertex).Y);
            AssertFloat(lowest - roof).OverrideFailureMessage("the steel face rests just above the cart unit with a collision margin")
                .IsBetween(0f, 0.004f);
            AssertFloat((model.GlobalBasis * Vector3.Forward).Dot(Vector3.Down))
                .OverrideFailureMessage("the contact face points down and the handle faces up on the cart").IsGreater(0.999f);
            var animator = new ToolAnimator();
            animator.Setup(model, "shock");
            await studio.Capture(cart, "defibrillator_model", "04_parked",
                "the paddle rests face down on the cart unit, its bridge handle and orange button above the steel plate, with the short cord clear of the unit",
                new Vector3(0f, 1.03f, -0.035f), new Vector3(0.45f, 0.4f, 0.55f));
        } finally {
            room.Free();
            await studio.Finish();
        }
    }
}
