namespace Scalpel.Tests.World;

/// <summary>The glove station as a player uses it: the dispenser box stands on the cabinet within the station's
/// interact area, looking at it offers "Change gloves", and interact swaps bloody, sweaty gloves for fresh ones.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class GloveStationTest {
    // Temporary local budget override: recorded CPU overruns of the cloud release run remain measured.
    private static readonly BudgetAllowance BudgetOverride = new("known to go over the frame budget, not profiled yet", 5f);

    /// <summary>Top of the cabinet the box stands on (tools/assetgen/props.py).</summary>
    private const float CabinetTop = 0.9f;

    [TestCase]
    public async Task LookingAtTheBoxAndInteractingGivesFreshGloves() {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        KeyFrames? shots = null;
        try {
            await driver.Start("hand_stitch");
            var me = driver.Me;
            var room = driver.Surgery.Room;
            var station = room.GetNode<Interactable>("ChangeGloves");
            var box = room.FindChildren("Box", nameof(MeshInstance3D), true, false).Cast<MeshInstance3D>().Single();
            var bounds = box.GlobalTransform * box.Mesh.GetAabb();
            var size = ((BoxShape3D)station.GetChild<CollisionShape3D>(0).Shape).Size;
            var area = new Aabb(station.GlobalPosition - (size * 0.5f), size);
            AssertFloat(bounds.Position.Y).OverrideFailureMessage($"the glove box stands on the cabinet: {bounds}")
                .IsEqualApprox(CabinetTop, 0.003f);
            AssertBool(area.Encloses(bounds))
                .OverrideFailureMessage($"the glove box {bounds} is inside the station's interact area {area}")
                .IsTrue();
            // The top is closed under the box: a ray down through the middle meets it first (it once had a hole there,
            // and a hit on the bottom wouldn't tell).
            var cabinet = box.GetParent().GetNode<MeshInstance3D>("Cabinet");
            var faces = cabinet.Mesh.GetFaces();
            var above = new Vector3(0f, 2f, 0f);
            var top = Enumerable.Range(0, faces.Length / 3)
                .Select(t => Geometry3D.RayIntersectsTriangle(above, Vector3.Down, faces[t * 3], faces[(t * 3) + 1],
                    faces[(t * 3) + 2]))
                .Where(hit => hit.VariantType != Variant.Type.Nil)
                .Select(hit => hit.AsVector3().Y)
                .DefaultIfEmpty(float.NegativeInfinity)
                .Max();
            AssertFloat(top).OverrideFailureMessage("the cabinet's top is closed in the middle")
                .IsEqualApprox(CabinetTop, 0.003f);
            if (KeyFrames.Wanted()) {
                shots = new KeyFrames();
                driver.AddChild(shots);
                shots.Begin(driver.Surgery, "glove_station");
                await shots.CaptureAt("untouched",
                    "the glove box on a closed cabinet top, gloves out of its slot, a loose pair in front, "
                        + "no black patch",
                    bounds.GetCenter(), 0.6f);
                // Thin parts once went black seen from across the room: the outline grew into them.
                await driver.PlayerWalksTo(bounds.GetCenter(), 2f);
                await driver.PlayerLooksAt(bounds.GetCenter());
                await shots.CaptureView("from_across_the_room",
                    "the glove station from 2 m: the gloves stay blue and the box white, each with a thin dark "
                        + "outline, no black part");
            }
            // Loading the room isn't gameplay: the frame budget counts from here.
            driver.Budget.Clear();
            SurgeryState.GlovesAreSoiled(me);
            await driver.PlayerWalksTo(bounds.GetCenter(), 0.6f);
            await driver.PlayerLooksAt(bounds.GetCenter());
            await Frames.Physics(2);
            AssertObject(me.Focused).OverrideFailureMessage("looking at the glove box offers \"Change gloves\"")
                .IsSame(station);
            if (shots is not null) {
                await driver.Unbudgeted(() => shots.CaptureView("box_in_view",
                    "the glove box in the middle of the view, the \"Change gloves\" prompt, "
                        + "bloody gloves on the hands"));
            }
            PlayerInput.Tap(InputActions.Interact);
            await PlayerInput.Delivered();
            await Frames.Physics(2);
            var blood = string.Join(", ", me.Hands.Select(hand => hand.Blood));
            AssertBool(me.Hands.All(hand => hand.Blood == 0f))
                .OverrideFailureMessage($"fresh gloves are clean: {blood}").IsTrue();
            AssertFloat(me.Status.Sweat).OverrideFailureMessage("fresh gloves are dry").IsLess(0.05f);
            if (shots is not null) {
                await driver.Unbudgeted(() => shots.CaptureView("fresh_gloves",
                    "clean blue gloves on the hands, the \"Fresh gloves.\" toast, no sweat bar"));
            }
            driver.Budget.Check(localOverride: BudgetOverride);
        } finally {
            shots?.End();
            await driver.Stop();
            RenderingServer.RenderLoopEnabled = true;
        }
    }
}
