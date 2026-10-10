namespace Scalpel.Tests.World;

/// <summary>Ceiling fixture geometry and two oblique views in each indoor environment, including the shorter
/// ambulance.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class RoomTest
{
    [TestCase("or")]
    [TestCase("ambulance")]
    public async Task CeilingPanelClearsTheSlab(string environment)
    {
        RenderingServer.RenderLoopEnabled = false;
        var room = AutoFree(new Room())!;
        Frames.Root.AddChild(room);
        room.BuildShell(environment);
        var ceiling = room.GetNode<MeshInstance3D>("Ceiling");
        var panel = room.GetNode<MeshInstance3D>("CeilingPanel");
        var underside = ceiling.Position.Y + ceiling.Mesh.GetAabb().Position.Y;
        var panelTop = panel.Position.Y + panel.Mesh.GetAabb().End.Y;
        // Also what the key frames should show.
        var claim = $"{environment}: the whole ceiling panel clears the slab";
        AssertFloat(underside - panelTop).OverrideFailureMessage(claim).IsGreater(0.01f);
        if (KeyFrames.Wanted())
        {
            var camera = new Camera3D { Fov = 55f };
            room.AddChild(camera);
            camera.MakeCurrent();
            var height = room.Layout.Size.Y;
            var lookAt = new Vector3(0f, height - 0.05f, 0f);
            var folder = KeyFrames.Folder("room");
            foreach (var (name, from) in (Shot[])[new("oblique", new(1.6f, height - 1.1f, 0.9f)), new("reverse", new(-1.6f, height - 1.1f, -0.9f))])
            {
                camera.Position = from;
                camera.LookAt(lookAt);
                var path = folder.PathJoin($"{environment}_ceiling_{name}.png");
                await KeyFrames.SaveViewport(path, claim);
            }
        }
        RenderingServer.RenderLoopEnabled = true;
    }

    private readonly record struct Shot(string Name, Vector3 From);
}
