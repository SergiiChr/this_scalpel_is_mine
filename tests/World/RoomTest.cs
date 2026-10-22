namespace Scalpel.Tests.World;

/// <summary>Ceiling fixture clearance, the theatre panels' independent shadow-casting lights and the monitor's mount on
/// the table.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation"), TestCategory("room")]
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
        var points = node.FindChildren("*", nameof(MeshInstance3D), true, false).Append(node).OfType<MeshInstance3D>().Where(mesh => mesh.IsVisibleInTree())
            .SelectMany(mesh => mesh.Mesh.GetFaces().Select(vertex => toRoom * mesh.GlobalTransform * vertex)).ToArray();
        return points.Aggregate(new Aabb(points[0], Vector3.Zero), (box, point) => box.Expand(point));
    }

    [TestCase]
    public async Task AmbulanceHasOnePersonAislesAndDividedSupplies() {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        try {
            await driver.Start("ambulance_bullet");
            var room = driver.Surgery.Room;
            await InitialSceneChecks.Inspect(driver.Surgery);
            var monitor = room.Monitor;
            AssertBool(ModelSlot.Parts(monitor, "Pole")["Pole"].IsVisibleInTree())
                .OverrideFailureMessage("the ambulance monitor has no bed clamp or supporting bed pole").IsFalse();
            var mount = monitor.GetNode<Node3D>("WallMount");
            var mountBounds = RoomBounds(mount, room);
            var wall = -room.Layout.Size.Z * 0.5f + 0.05f;
            AssertFloat(Mathf.Abs(mountBounds.Position.Z - wall))
                .OverrideFailureMessage("the monitor plate touches the side wall beside the patient's head").IsLess(0.002f);
            AssertFloat(mountBounds.GetCenter().X).IsGreater(Room.TableHead - 0.5f);
            AssertFloat(RoomBounds(monitor, room).Position.Y)
                .OverrideFailureMessage("the monitor and its module rack clear the fitted worktop").IsGreater(Room.TraySurface);
            AssertFloat(driver.FreeTools("gauze").Single().GlobalPosition.Z)
                .OverrideFailureMessage("gauze is with the surgical doctor's dressings").IsGreater(0f);
            var stationIds = room.StationTools().Select(tool => tool.Id).ToHashSet();
            foreach (var tool in driver.Surgery.Tools.Tools.Values.Where(tool => tool.State == ToolState.Free
                && !stationIds.Contains(tool.Def.Id))) {
                var zone = room.TrayZone(room.InitialToolGroup(tool.Def));
                var center = tool.GlobalTransform * tool.Bounds.GetCenter();
                AssertBool(new Rect2(zone.Position.X, zone.Position.Z, zone.Size.X, zone.Size.Z)
                    .Grow(0.01f).HasPoint(new Vector2(center.X, center.Z)))
                    .OverrideFailureMessage($"{tool.Def.Id} rests in its assigned wall compartment").IsTrue();
            }
            // First walk from the actual local-player spawn: input moves the body down the aisle while it stays on
            // its side of the stretcher.
            var surgeon = driver.Me;
            var firstSpawn = room.SpawnTransform(0);
            var startedAt = surgeon.GlobalPosition;
            await driver.PlayerHoldsWalkKey(InputActions.MoveRight, 0.5f, () => { });
            var walked = surgeon.GlobalPosition - startedAt;
            AssertFloat(walked.Dot(firstSpawn.Basis.X)).OverrideFailureMessage("normal input moves along the aisle")
                .IsGreater(0.3f);
            AssertFloat(Mathf.Abs(walked.Z)).OverrideFailureMessage("normal input keeps the surgeon on the same aisle")
                .IsLess(0.1f);

            // At each end, try to go around the stretcher from both spawn sides. Only the test fixture is repositioned
            // between attempts; every attempted route uses walking input and must stop at the fitted end cabinetry.
            async Task TryAroundEnd(int spawnIndex, bool head) {
                var spawn = room.SpawnTransform(spawnIndex);
                var side = Mathf.Sign(spawn.Origin.Z);
                SurgeryState.SurgeonStandsAt(surgeon, spawn.Origin, spawn.Basis.GetEuler().Y);
                await Frames.Physics(3);
                var towardEnd = (head == (spawnIndex == 0)) ? InputActions.MoveRight : InputActions.MoveLeft;
                var route = new[] { InputActions.MoveForward, towardEnd };
                foreach (var action in route) { PlayerInput.Action(action); }
                await PlayerInput.Delivered();
                await Frames.Physics(180);
                foreach (var action in route) { PlayerInput.Action(action, false); }
                await PlayerInput.Delivered();
                var atEnd = surgeon.GlobalPosition;
                var localAtEnd = room.ToLocal(atEnd);
                AssertFloat((localAtEnd.X - spawn.Origin.X) * (head ? 1f : -1f))
                    .OverrideFailureMessage($"walking toward the {(head ? "head" : "foot")} end makes progress along the aisle")
                    .IsGreater(0.35f);
                AssertFloat(localAtEnd.Z * side)
                    .OverrideFailureMessage($"the {(head ? "head" : "foot")} end has no walking route to the opposite aisle")
                    .IsGreater(0.1f);
                foreach (var action in route) { PlayerInput.Action(action); }
                await PlayerInput.Delivered();
                await Frames.Physics(24);
                foreach (var action in route) { PlayerInput.Action(action, false); }
                await PlayerInput.Delivered();
                AssertFloat(surgeon.GlobalPosition.DistanceTo(atEnd))
                    .OverrideFailureMessage($"the fitted {(head ? "head" : "foot")} locker stops the attempted crossing")
                    .IsLess(0.08f);
            }
            foreach (var spawnIndex in (int[])[0, 1]) {
                await TryAroundEnd(spawnIndex, head: true);
                await TryAroundEnd(spawnIndex, head: false);
            }
            SurgeryState.SurgeonStandsAt(surgeon, firstSpawn.Origin, firstSpawn.Basis.GetEuler().Y);
            await Frames.Physics(3);
            var camera = new Camera3D { Fov = 80f, Near = 0.02f };
            room.AddChild(camera);
            async Task Cabin(string name, Vector3 from, Vector3 toward, string claim) {
                if (!KeyFrames.Wanted()) { return; }
                var overview = name is "fitted_cabin" or "rear_doors" or "wall_monitor";
                driver.Surgery.Hud.Visible = !overview;
                foreach (var hand in driver.Me.Hands) { hand.Visible = !overview; }
                if (overview) {
                    camera.Position = from;
                    camera.LookAt(toward);
                    camera.MakeCurrent();
                } else {
                    driver.Me.Camera.MakeCurrent();
                }
                await KeyFrames.SaveViewport(KeyFrames.Folder("room").PathJoin($"ambulance_{name}.png"), claim);
                driver.Surgery.Hud.Visible = true;
                foreach (var hand in driver.Me.Hands) { hand.Visible = true; }
            }
            await Cabin("fitted_cabin", new(-1.39f, 1.8f, 0f), new(0.8f, 1.15f, 0f),
                "ambulance cabin with fitted blue and white storage, a cab partition, overhead grab rails and two narrow aisles beside the patient");
            await Cabin("wall_monitor", new(0.15f, 1.55f, -0.4f), new(0.8f, 1.25f, -1.05f),
                "the new monitor and module rack sit on a swivel arm fixed to the ambulance side wall beside the patient's head, clear of the IV and worktop, without a bed-mounted pole");
            await Cabin("rear_doors", new(1.18f, 1.8f, 0f), new(-1.6f, 1.2f, 0f),
                "rear double doors and windows, the loading locker, folded seat and opposite work compartments clear of the patient");
            driver.StandingConstraint = at => at.Z > 0f;
            var scalpel = driver.FreeTools("scalpel").Single();
            AssertObject(await driver.PlayerPicksUp(scalpel)).IsNotNull();
            AssertFloat(driver.Me.GlobalPosition.Z).IsGreater(0f);
            await Cabin("instrument_pickup", new(-0.9f, 1.6f, 0.55f), new(0f, 1f, 0.9f),
                "the surgical doctor holds the scalpel picked up from the surgery compartment, staying beside the patient");
            SurgeryDriver.Tap(InputActions.BeltSlot(1));
            await PlayerInput.Delivered();
            AssertBool(scalpel.State == ToolState.Belt).IsTrue();
            await Cabin("instrument_stowed", new(-0.9f, 1.6f, 0.55f), new(0f, 1f, 0.9f),
                "the scalpel remains on the surgical doctor's belt after pickup, leaving its wall compartment clear");
        } finally {
            await driver.Stop();
            RenderingServer.RenderLoopEnabled = true;
        }
    }

    private readonly record struct Shot(string Name, Vector3 From);
}
