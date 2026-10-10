namespace Scalpel.Tests.World;

/// <summary>The glove station as a player uses it: the dispenser box stands on the cabinet within the station's
/// interact area, looking at it offers "Change gloves", and interact swaps bloody, sweaty gloves for fresh ones.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class GloveStationTest
{
    /// <summary>Top of the cabinet the box stands on (tools/assetgen/props.py).</summary>
    private const float CabinetTop = 0.9f;

    [TestCase]
    public async Task LookingAtTheBoxAndInteractingGivesFreshGloves()
    {
        RenderingServer.RenderLoopEnabled = false;
        var driver = SurgeryDriver.Create();
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
            .OverrideFailureMessage($"the glove box {bounds} is inside the station's interact area {area}").IsTrue();
        // The top is closed under the box: a ray down through the middle meets it (it once had a hole there).
        var cabinet = box.GetParent().GetNode<MeshInstance3D>("Cabinet");
        var faces = cabinet.Mesh.GetFaces();
        var above = new Vector3(0f, 2f, 0f);
        var closed = Enumerable.Range(0, faces.Length / 3).Any(t =>
            Geometry3D.RayIntersectsTriangle(above, Vector3.Down, faces[t * 3], faces[(t * 3) + 1], faces[(t * 3) + 2])
                .VariantType != Variant.Type.Nil);
        AssertBool(closed).OverrideFailureMessage("the cabinet's top is closed in the middle").IsTrue();
        KeyFrames? shots = null;
        if (KeyFrames.Wanted())
        {
            shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(driver.Surgery, "glove_station");
            await shots.CaptureAt("untouched",
                "the glove box on a closed cabinet top, gloves out of its slot, a loose pair in front, no black patch",
                bounds.GetCenter(), 0.6f);
        }
        // Loading the room isn't gameplay: the frame budget counts from here.
        driver.Budget.Clear();
        SurgeryState.GlovesAreSoiled(me);
        await driver.PlayerWalksTo(bounds.GetCenter(), 0.6f);
        await driver.PlayerLooksAt(bounds.GetCenter());
        await Frames.Physics(2);
        AssertObject(me.Focused).OverrideFailureMessage("looking at the glove box offers \"Change gloves\"")
            .IsSame(station);
        if (shots is not null)
        {
            await driver.Unbudgeted(() => shots.CaptureView("box_in_view",
                "the glove box in the middle of the view, the \"Change gloves\" prompt, bloody gloves on the hands"));
        }
        PlayerInput.Tap(InputActions.Interact);
        await PlayerInput.Delivered();
        await Frames.Physics(2);
        AssertBool(me.Hands.All(hand => hand.Blood == 0f))
            .OverrideFailureMessage($"fresh gloves are clean: {string.Join(", ", me.Hands.Select(hand => hand.Blood))}")
            .IsTrue();
        AssertFloat(me.Status.Sweat).OverrideFailureMessage("fresh gloves are dry").IsLess(0.05f);
        if (shots is not null)
        {
            await driver.Unbudgeted(() => shots.CaptureView("fresh_gloves",
                "clean blue gloves on the hands, the \"Fresh gloves.\" toast, no sweat bar"));
            shots.End();
        }
        driver.Budget.Check(shots is not null);
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }
}
