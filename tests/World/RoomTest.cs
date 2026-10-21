namespace Scalpel.Tests.World;

/// <summary>Ceiling fixture clearance, the theatre panels' independent shadow-casting lights and the monitor's mount on
/// the table.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class RoomTest {
    [TestCase]
    public async Task TheatrePanelsEachCastShadows() {
        FrameBudget.RenderingOff();
        var room = AutoFree(new Room())!;
        Frames.Root.AddChild(room);
        room.BuildShell("or");
        var lights = room.GetChildren().OfType<SpotLight3D>().Where(light => light.Name.ToString().StartsWith("CeilingLight")).ToArray();
        AssertInt(lights.Length).IsEqual(4);
        AssertInt(lights.Select(light => light.Position).Distinct().Count()).IsEqual(4);
        foreach (var light in lights) {
            AssertBool(light.Visible).IsTrue();
            AssertFloat(light.LightEnergy).IsGreater(0f);
            AssertBool(light.ShadowEnabled).IsTrue();
            AssertFloat(light.Position.Y).IsBetween(2.7f, 2.75f);
            AssertFloat((-light.Basis.Z).Dot(Vector3.Down)).IsGreater(0.999f);
        }
        if (KeyFrames.Wanted()) {
            // A neutral upright occluder makes each panel's distinct shadow direction easy to review.
            Shapes.Slab(room, new Vector3(0.25f, 0.9f, 0.25f), new Color(0.6f, 0.6f, 0.6f), new Vector3(0f, 0.45f, 0f));
            room.GetNode<SpotLight3D>("SurgicalLamp").Visible = false;
            var camera = new Camera3D { Position = new Vector3(0f, 2.3f, 2f), Fov = 75f };
            room.AddChild(camera);
            camera.LookAt(new Vector3(0f, 0.3f, 0f));
            camera.MakeCurrent();
            var folder = KeyFrames.Folder("room");
            await KeyFrames.SaveViewport(folder.PathJoin("theatre_all_panels.png"),
                "four ceiling panels cast four soft shadows from the upright block across the floor");
            foreach (var light in lights) {
                foreach (var other in lights) {
                    other.Visible = other == light;
                }
                await KeyFrames.SaveViewport(folder.PathJoin($"theatre_{light.Name}.png"),
                    $"{light.Name} alone lights the block and casts its shadow away from that ceiling panel");
            }
        }
        RenderingServer.RenderLoopEnabled = true;
    }

    [TestCase("ambulance")]
    public async Task CeilingPanelClearsTheSlab(string environment) {
        FrameBudget.RenderingOff();
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
        if (KeyFrames.Wanted()) {
            var camera = new Camera3D { Fov = 55f };
            room.AddChild(camera);
            camera.MakeCurrent();
            var height = room.Layout.Size.Y;
            var lookAt = new Vector3(0f, height - 0.05f, 0f);
            var folder = KeyFrames.Folder("room");
            foreach (var (name, from) in (Shot[])[new("oblique", new(1.6f, height - 1.1f, 0.9f)), new("reverse", new(-1.6f, height - 1.1f, -0.9f))]) {
                camera.Position = from;
                camera.LookAt(lookAt);
                var path = folder.PathJoin($"{environment}_ceiling_{name}.png");
                await KeyFrames.SaveViewport(path, claim);
            }
        }
        RenderingServer.RenderLoopEnabled = true;
    }

    [TestCase("or")]
    [TestCase("ambulance")]
    [TestCase("sidewalk")]
    public void ShellIsStaticGiAndSdfgiSetUp(string environmentId) {
        FrameBudget.RenderingOff();
        var room = AutoFree(new Room())!;
        Frames.Root.AddChild(room);
        room.BuildShell(environmentId);
        var environment = room.GetChildren().OfType<WorldEnvironment>().Single().Environment;
        AssertBool(environment.SdfgiEnabled).IsEqual(RenderingServer.GetCurrentRenderingMethod() == "forward_plus");
        AssertFloat(environment.SdfgiMinCellSize).IsEqual(0.1f);
        AssertInt(environment.SdfgiCascades).IsEqual(4);
        AssertBool(environment.SdfgiReadSkyLight).IsEqual(environmentId == "sidewalk");
        var meshes = room.FindChildren("*", nameof(MeshInstance3D), true, false).Cast<MeshInstance3D>().ToArray();
        AssertInt(meshes.Length).IsGreater(0);
        AssertBool(meshes.All(mesh => mesh.GIMode == GeometryInstance3D.GIModeEnum.Static)).IsTrue();
        RenderingServer.RenderLoopEnabled = true;
    }

    [TestCase]
    public async Task GameplayMeshesAreExcludedFromStaticGi() {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        try {
            await driver.Start("appendectomy");
            var meshes = driver.Patient.FindChildren("*", nameof(MeshInstance3D), true, false).Cast<MeshInstance3D>().ToArray();
            AssertInt(meshes.Length).IsGreater(0);
            AssertBool(meshes.All(mesh => mesh.GIMode == GeometryInstance3D.GIModeEnum.Disabled)).IsTrue();
            var delivered = new MeshInstance3D { Mesh = new BoxMesh() };
            driver.Me.AddChild(delivered);
            AssertBool(delivered.GIMode == GeometryInstance3D.GIModeEnum.Disabled).IsTrue();
        } finally {
            await driver.Stop();
        }
    }

    /// <summary>How far past the table's head end and sides the monitor may stand out into the room.</summary>
    private const float MonitorReach = 0.25f;

    [TestCase]
    public async Task MonitorIsMountedOnTheTableHead() {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        try {
            await driver.Start("appendectomy");
            var surgery = driver.Surgery;
            var monitor = surgery.Room.Monitor;
            var bounds = RoomBounds(monitor, surgery.Room);
            // Its pole's lower clamp reaches into the frame's end under the pads, inside the table's sides.
            var pole = RoomBounds(ModelSlot.Parts(monitor, "Pole")["Pole"], surgery.Room);
            var clamped = pole.Position.X < Room.TableHead && pole.Position.Y < Room.TableHeight - 0.05f
                && Mathf.Abs(pole.GetCenter().Z) < Room.TableWidth * 0.5f;
            AssertBool(clamped).OverrideFailureMessage($"the monitor's pole is clamped to the table's head end: {pole}").IsTrue();
            var tucked = bounds.End.X - Room.TableHead <= MonitorReach
                && Mathf.Max(-bounds.Position.Z, bounds.End.Z) <= (Room.TableWidth * 0.5f) + MonitorReach;
            AssertBool(tucked).OverrideFailureMessage($"the monitor stays close to the table: {bounds}").IsTrue();
            if (KeyFrames.Wanted()) {
                var shots = new KeyFrames();
                driver.AddChild(shots);
                shots.Begin(surgery, "room/monitor");
                await shots.CaptureView("start",
                    "from where the surgeon starts, the patient and the table's head end are in full view, the monitor up "
                    + "at the edge of the view beyond the head");
                await shots.CaptureFacing("mount", "the flat screen on a short arm from a pole clamped to the table's "
                    + "head rail and frame, the module rack under the arm's elbow, all within 25 cm of the table",
                    new Vector3(Room.TableHead + 0.05f, 1.2f, -0.3f), new Vector3(-1f, 0f, -1f), 1.8f);
                await driver.PlayerLooksAt(monitor.GlobalPosition);
                await shots.CaptureView("screen", "the vitals on the screen read clearly, its glass turned toward the surgeon");
                shots.End();
            }
        } finally {
            await driver.Stop();
        }
    }

    /// <summary>The box round every vertex of the meshes at and under <paramref name="node"/>, in <paramref name="room"/>'s
    /// space. Not their boxes turned into it, which would stand further out than the parts do.</summary>
    private static Aabb RoomBounds(Node3D node, Node3D room) {
        var toRoom = room.GlobalTransform.AffineInverse();
        var points = node.FindChildren("*", nameof(MeshInstance3D), true, false).Append(node).OfType<MeshInstance3D>()
            .SelectMany(mesh => mesh.Mesh.GetFaces().Select(vertex => toRoom * mesh.GlobalTransform * vertex)).ToArray();
        return points.Aggregate(new Aabb(points[0], Vector3.Zero), (box, point) => box.Expand(point));
    }

    private readonly record struct Shot(string Name, Vector3 From);
}
