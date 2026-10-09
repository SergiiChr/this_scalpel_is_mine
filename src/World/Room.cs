namespace Scalpel.World;

/// <summary>Where everything stands in one environment, in meters with the table at the origin.</summary>
/// <param name="Size">The room's floor and wall height (no height outdoors).</param>
/// <param name="Spawns">Where surgeons start: right at the table edge, so both reach the middle of the patient.</param>
/// <param name="Tray">The instrument tray.</param>
/// <param name="Stations">Props and stations by name (see Room.BuildStations()).</param>
/// <param name="Yaw">Stations turned to face the room (radians); the rest face +Z.</param>
public sealed record RoomLayout(
    Vector3 Size, Vector3[] Spawns, Vector3 Tray, IReadOnlyDictionary<string, Vector3> Stations,
    IReadOnlyDictionary<string, float> Yaw)
{
    public bool Has(string station) => Stations.ContainsKey(station);

    public Vector3 this[string station] => Stations[station];

    public float YawOf(string station) => Yaw.GetValueOrDefault(station);
}

/// <summary>
/// Builds the place where the surgery happens: geometry, lighting and the stations you walk to. Layout per environment
/// lives in <see cref="Layouts"/>. Props are generated models (tools/assetgen/props.py) loaded through
/// <see cref="ModelSlot"/>.
/// </summary>
public partial class Room : Node3D
{
    public const float TableHeight = 0.85f;
    /// <summary>Height of the instrument tray's surface above its base.</summary>
    public const float TraySurface = 0.915f;
    /// <summary>Table top end along x where the feet lie.</summary>
    public const float TableFoot = -1.3f;
    /// <summary>Table top end along x where the head lies.</summary>
    public const float TableHead = 1f;
    /// <summary>The patient card hangs facing out from the middle of the table's head rail (HEAD_RAIL_X in
    /// tools/assetgen/props.py): in view from either side of the table, where the surgeons start.</summary>
    private const float CardHook = TableHead + 0.04f;
    /// <summary>Between items the nurse brings together, across the delivery tray (meters): five fit inside its rim.
    /// </summary>
    private const float DeliveryGap = 0.095f;
    /// <summary>Bottom of the drip chamber on the IV stand model, where the tubing starts.</summary>
    public static readonly Vector3 IvDripPoint = new(0.08f, 1.6f, 0f);
    /// <summary>
    /// Where each group of tools (tray in tools.cfg) lies on the instrument tray at the start: x and z from the tray's
    /// middle, its near side (+x) toward the table. Ungrouped tools fill "". Each group fills its spot from the near
    /// side, so the usual kit lies mid-tray and extras spread toward the far rim. Nothing lies at the near rim, where a
    /// tall patient's feet reach over the tray. The strip down the middle stays clear.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Rect2> TrayZones = new Dictionary<string, Rect2>
    {
        ["instruments"] = new(0.09f, -0.36f, 0.16f, 0.26f),
        ["swabs"] = new(0.14f, -0.07f, 0.06f, 0.06f),
        ["syringes"] = new(0.12f, 0.03f, 0.13f, 0.17f),
        ["bottles"] = new(-0.32f, 0.24f, 0.27f, 0.08f),
        [""] = new(-0.32f, -0.3f, 0.27f, 0.5f),
    };
    /// <summary>Groups that stand on their bottom instead of lying down.</summary>
    public static readonly IReadOnlySet<string> Upright = new HashSet<string> { "bottles" };
    /// <summary>Groups that lie in a small tray of their own, the size of their zone (built by
    /// tools/assetgen/props.py). The swab tray fits one pad, so the pads pile up in it.</summary>
    public static readonly IReadOnlyList<string> SmallTrays = ["instruments", "swabs"];
    /// <summary>A small tray's floor above the instrument tray's surface.</summary>
    private const float SmallTrayFloor = 0.004f;
    /// <summary>A small tray's rim top above the instrument tray's surface.</summary>
    private const float SmallTrayRim = 0.016f;
    private const float SmallTrayWall = 0.006f;
    /// <summary>Solid footprint (width, height, depth) of props you can put things on and can't walk through.</summary>
    private static readonly Dictionary<string, Vector3> StationSolids = new Dictionary<string, Vector3>
    {
        ["bell"] = new(0.6f, 0.9f, 0.45f),
        ["gloves"] = new(0.6f, 0.9f, 0.45f),
        ["sanitizer"] = new(0.6f, 0.9f, 0.45f),
        ["sink"] = new(0.7f, 0.9f, 0.5f),
        ["delivery_tray"] = new(0.5f, 0.92f, 0.38f),
        ["defib_cart"] = new(0.55f, 1.06f, 0.45f),
    };

    public static readonly IReadOnlyDictionary<string, RoomLayout> Layouts = new Dictionary<string, RoomLayout>
    {
        // A cramped old operating room: about 1.3 m between the table and the cabinets behind you. Stations stand against
        // the walls, so reaching them still means walking away from the table.
        ["or"] = new(
            new(5.2f, 2.8f, 4.4f), [new(0f, 0f, 0.62f), new(0f, 0f, -0.62f)], new(-1.7f, 0f, 0f),
            new Dictionary<string, Vector3>
            {
                ["manual"] = new(1.9f, 0f, -2f),
                ["card"] = new(CardHook, 0f, 0f),
                ["bell"] = new(-2.25f, 0f, 1.92f),
                ["gloves"] = new(-1.55f, 0f, 1.92f),
                ["delivery_tray"] = new(-0.85f, 0f, 1.95f),
                ["sink"] = new(1.5f, 0f, 1.9f),
                ["sanitizer"] = new(2.25f, 0f, 1.92f),
                ["iv"] = new(0.95f, 0f, 0.85f),
                ["monitor"] = new(1.25f, 1.55f, -0.95f),
                ["defib_cart"] = new(0.3f, 0f, -1.95f),
                ["xray"] = new(-1.9f, 0f, -1.7f),
                ["smoking"] = new(2.35f, 0f, -1.5f),
            },
            new Dictionary<string, float>
            {
                ["card"] = Mathf.Pi / 2f,
                ["bell"] = Mathf.Pi,
                ["gloves"] = Mathf.Pi,
                ["delivery_tray"] = Mathf.Pi,
                ["sink"] = Mathf.Pi,
                ["sanitizer"] = Mathf.Pi,
            }),
        ["ambulance"] = new(
            new(4.2f, 2.1f, 2.3f), [new(0f, 0f, 0.62f), new(-0.5f, 0f, -0.62f)], new(-1.72f, 0f, 0.55f),
            new Dictionary<string, Vector3>
            {
                ["manual"] = new(1.8f, 0f, -0.85f),
                ["card"] = new(CardHook, 0f, 0f),
                ["gloves"] = new(1.8f, 0f, 0.85f),
                ["sanitizer"] = new(-1.8f, 0f, -0.8f),
                ["iv"] = new(0.85f, 0f, -0.75f),
                ["monitor"] = new(1.4f, 1.4f, -1.05f),
                ["defib_cart"] = new(1.8f, 0f, 0f),
                // By the back doors.
                ["smoking"] = new(-1.88f, 0f, -0.2f),
            },
            new Dictionary<string, float> { ["card"] = Mathf.Pi / 2f, ["defib_cart"] = -Mathf.Pi / 2f }),
        ["sidewalk"] = new(
            new(14f, 0f, 10f), [new(0f, 0f, 0.62f), new(0f, 0f, -0.62f)], new(-1.7f, 0f, 0.3f),
            new Dictionary<string, Vector3>
            {
                ["manual"] = new(1.6f, 0f, 1.6f),
                ["card"] = new(CardHook, 0f, 0f),
                ["gloves"] = new(-1.6f, 0f, -1.5f),
                ["iv"] = new(0.95f, 0f, 0.85f),
                ["monitor"] = new(1.4f, 1.1f, -1f),
                ["defib_cart"] = new(-0.6f, 0f, 1.8f),
                // Under the streetlight.
                ["smoking"] = new(2.6f, 0f, 3f),
            },
            new Dictionary<string, float> { ["card"] = Mathf.Pi / 2f, ["defib_cart"] = Mathf.Pi }),
    };

    public string EnvironmentId { get; private set; } = "or";
    public RoomLayout Layout { get; private set; } = Layouts["or"];
    public PatientMonitor Monitor { get; private set; } = null!;
    public PatientCard Card { get; private set; } = null!;
    public XrayCart? Xray { get; private set; }
    /// <summary>Tubing from the IV stand to the patient, shown once a line is in.</summary>
    public IvLine IvLine { get; private set; } = null!;
    private Node3D _ivStand = null!;
    private readonly List<Light3D> _flickerLights = [];
    /// <summary>The steady brightness of each flickering light and its running flicker.</summary>
    private readonly Dictionary<Light3D, (float Energy, Tween? Tween)> _flickers = [];
    /// <summary>Over the bell: the order on its way and the nurse's cooldown.</summary>
    private Label3D? _nurseBoard;

    private bool Indoors => EnvironmentId != "sidewalk";

    public void Build(string environmentId, Surgery surgery)
    {
        BuildShell(environmentId);
        BuildTable();
        BuildTray();
        BuildStations(surgery);
        var ambience = EnvironmentId switch
        {
            "ambulance" => "ambulance_rumble",
            "sidewalk" => "street_ambience",
            _ => "fluorescent_buzz",
        };
        Sfx.PlayLoop(ambience, this);
    }

    public override void _Process(double delta)
    {
        if (_nurseBoard is not null && Surgery.Current is { } surgery)
        {
            _nurseBoard.Text = surgery.Status.NurseBoard;
        }
    }

    public Transform3D SpawnTransform(int index)
    {
        var spots = Layout.Spawns;
        var position = spots[index % spots.Length] + new Vector3(0.35f * (index / spots.Length), 0f, 0f);
        var facing = position.Z > 0f ? 0f : Mathf.Pi;
        return new Transform3D(new Basis(Vector3.Up, facing), position);
    }

    /// <summary>Height of the instrument tray's surface (world space).</summary>
    public float TrayTop() => Layout.Tray.Y + TraySurface;

    /// <summary>Where a group of tools lies at the start (<see cref="TrayZones"/>): the area seen from above, in world
    /// space, at the height of what it lies on. Inside a small tray it keeps a little off the walls.</summary>
    public Aabb TrayZone(string group)
    {
        var area = TrayZones.GetValueOrDefault(group, TrayZones[""]);
        var bottom = TrayTop();
        if (SmallTrays.Contains(group))
        {
            area = area.Grow(-SmallTrayWall - 0.004f);
            bottom += SmallTrayFloor;
        }
        var tray = Layout.Tray;
        return new Aabb(
            new Vector3(tray.X + area.Position.X, bottom, tray.Z + area.Position.Y), new Vector3(area.Size.X, 0f, area.Size.Y));
    }

    public List<Vector3> TraySpots()
    {
        var origin = Layout.Tray + new Vector3(0f, 0.93f, 0f);
        var spots = new List<Vector3>();
        for (var row = 0; row < 5; row++)
        {
            for (var column = 0; column < 5; column++)
            {
                spots.Add(origin + new Vector3(-0.25f + (column * 0.125f), 0f, -0.28f + (row * 0.14f)));
            }
        }
        return spots;
    }

    /// <summary>
    /// On top of the delivery tray, or the instrument tray where there's no nurse (they bring nothing there anyway).
    /// Items delivered together (<paramref name="slot"/> of <paramref name="count"/>) lie side by side across it, so
    /// they don't land on each other. Each lands somewhere in its own share of the tray, so separate deliveries don't
    /// all land on one spot either.
    /// </summary>
    public Vector3 DeliverySpot(int slot = 0, int count = 1)
    {
        if (!Layout.Has("delivery_tray"))
        {
            return Layout.Tray + new Vector3((float)GD.RandRange(-0.2, 0.2), 1f, (float)GD.RandRange(-0.2, 0.2));
        }
        var spread = Mathf.Max(0.12f - (DeliveryGap * (count - 1) / 2f), 0.01f);
        var across = (DeliveryGap * (slot - ((count - 1) / 2f))) + (float)GD.RandRange(-spread, spread);
        return Layout["delivery_tray"] + new Vector3(across, 1f, (float)GD.RandRange(-0.08, 0.08));
    }

    /// <summary>
    /// Tools that live on their own station instead of the tray. The defibrillator always waits on its cart, whatever
    /// the scenario put on the tray. The bag hangs on the IV stand's hook, its port down at the drip chamber.
    /// </summary>
    public List<StationTool> StationTools()
    {
        var stations = new List<StationTool>();
        if (Layout.Has("defib_cart"))
        {
            var basis = new Basis(Vector3.Up, Layout.YawOf("defib_cart"));
            stations.Add(new StationTool("defibrillator",
                new Transform3D(basis, Layout["defib_cart"] + (basis * new Vector3(0f, 1.1f, 0.12f)))));
        }
        if (Layout.Has("iv"))
        {
            var basis = new Basis(Vector3.Up, Layout.YawOf("iv"));
            // A tool's tip is at -Z: pointing down, flat face toward the room's Z.
            var hanging = basis * new Basis(Vector3.Right, Vector3.Forward, Vector3.Up);
            stations.Add(new StationTool("iv_drip",
                new Transform3D(hanging, Layout["iv"] + (basis * (IvDripPoint + new Vector3(0f, 0.24f, 0f))))));
        }
        return stations;
    }

    /// <summary>Flickering room lights, the one cheap trick every horror hospital needs. The surgical lamp stays on.
    /// </summary>
    public void Flicker(float duration)
    {
        foreach (var light in _flickerLights)
        {
            // The steady brightness is kept so a flicker that starts during another one doesn't dim the room for good.
            var (energy, running) = _flickers.GetValueOrDefault(light, (light.LightEnergy, null));
            running?.Kill();
            var tween = CreateTween();
            _flickers[light] = (energy, tween);
            for (var i = 0; i < (int)(duration * 6f); i++)
            {
                tween.TweenProperty(light, "light_energy", GD.RandRange(0.0, 0.2) * energy, 0.06);
                tween.TweenProperty(light, "light_energy", GD.RandRange(0.6, 1.0) * energy, 0.1);
            }
            tween.TweenProperty(light, "light_energy", energy, 0.1);
        }
    }

    private void BuildEnvironment()
    {
        AddChild(new WorldEnvironment
        {
            Environment = new Environment
            {
                BackgroundMode = Environment.BGMode.Color,
                BackgroundColor = Indoors ? new Color(0.02f, 0.025f, 0.03f) : new Color(0.03f, 0.035f, 0.06f),
                AmbientLightSource = Environment.AmbientSource.Color,
                AmbientLightColor = Materials.SurgicalGreen.Darkened(0.2f),
                AmbientLightEnergy = 0.5f,
                TonemapMode = Environment.ToneMapper.Filmic,
                // Highlights roll off instead of clipping: pale skin and white linen keep their shape under the lamp.
                TonemapWhite = 2.5f,
                TonemapExposure = 1.15f,
                GlowEnabled = true,
                GlowIntensity = 0.3f,
                // Only real light sources glow (lamp lens, screens); lit skin up close must not bloom the whole view
                // white.
                GlowHdrThreshold = 1.6f,
                FogEnabled = true,
                FogLightColor = new Color(0.25f, 0.32f, 0.3f),
                FogDensity = 0.02f,
                SsaoEnabled = true,
                AdjustmentEnabled = true,
                AdjustmentSaturation = 0.85f,
            },
        });
        var size = Layout.Size;
        // The surgical lamp's light starts just under its lens: from inside the lamp head it would shadow itself.
        AddChild(new SpotLight3D
        {
            Name = "SurgicalLamp",
            Position = new Vector3(0f, LampHeight - 0.14f, 0f),
            Rotation = new Vector3(-Mathf.Pi / 2f, 0f, 0f),
            SpotRange = 3f,
            SpotAngle = 30f,
            // Bright enough to pick the site out of the room, not so bright it bleaches skin and gloves under it.
            LightEnergy = 0.32f,
            SpotAttenuation = 0.5f,
            LightColor = new Color(1f, 0.97f, 0.9f),
            ShadowEnabled = true,
            // Soft-edged shadows, like under a dish of bulbs. Only blurred: a sized light would also spread every glossy
            // highlight (blood, wet tissue) into a big white patch.
            ShadowBlur = 2f,
        });
        // Overhead room light: a ceiling panel over the table that lights the whole room from above.
        if (Indoors)
        {
            AddChild(new MeshInstance3D
            {
                Name = "CeilingPanel",
                Mesh = new BoxMesh { Size = new Vector3(1.4f, 0.04f, 0.5f) },
                MaterialOverride = Materials.Glow(Materials.Fluorescent),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                // Ceiling underside is size.Y - 0.05. Keep the entire fixture below it: coincident faces blink.
                Position = new Vector3(0f, size.Y - 0.085f, 0f),
            });
        }
        // Outside it hangs off the streetlight and leans toward the table.
        var overhead = new SpotLight3D
        {
            Name = "Overhead",
            Position = Indoors ? new Vector3(0f, size.Y - 0.08f, 0f) : new Vector3(3f, 4.6f, 2.9f),
            Rotation = Indoors ? new Vector3(-Mathf.Pi / 2f, 0f, 0f) : new Vector3(-0.75f, 0.8f, 0f),
            SpotRange = 12f,
            SpotAngle = 70f,
            SpotAttenuation = 0.3f,
            LightEnergy = Indoors ? 0.55f : 1f,
            LightColor = Indoors ? Materials.Fluorescent : new Color(1f, 0.75f, 0.45f),
            ShadowEnabled = true,
            ShadowBlur = 2.5f,
        };
        AddChild(overhead);
        _flickerLights.Add(overhead);
        Vector3[] tubes = EnvironmentId switch
        {
            "or" => [new(-size.X * 0.28f, size.Y - 0.2f, size.Z * 0.28f), new(size.X * 0.28f, size.Y - 0.2f, -size.Z * 0.28f)],
            "sidewalk" => [new(3f, 4.5f, 2f)],
            _ => [new(0f, size.Y - 0.2f, 0f)],
        };
        foreach (var position in tubes)
        {
            var tube = new OmniLight3D
            {
                Position = position,
                OmniRange = 9f,
                LightColor = Indoors ? Materials.Fluorescent : new Color(1f, 0.7f, 0.35f),
                LightEnergy = 0.35f,
            };
            AddChild(tube);
            _flickerLights.Add(tube);
        }
    }

    private float LampHeight => EnvironmentId == "or" ? 2.35f : 1.95f;

    /// <summary>The room's lights, floor, walls and ceiling alone, without its furniture.</summary>
    internal void BuildShell(string environmentId)
    {
        EnvironmentId = Layouts.ContainsKey(environmentId) ? environmentId : "or";
        Layout = Layouts[EnvironmentId];
        BuildEnvironment();
        BuildWalls();
    }

    private void BuildWalls()
    {
        var size = Layout.Size;
        var floorColor = Indoors ? new Color(0.28f, 0.3f, 0.29f) : new Color(0.2f, 0.2f, 0.21f);
        Shapes.Slab(this, size with { Y = 0.1f }, floorColor, new Vector3(0f, -0.05f, 0f), 0.4f);
        Shapes.StaticBox(this, size with { Y = 0.1f }, new Vector3(0f, -0.05f, 0f)).AddToGroup(ToolManager.FloorGroup);
        if (!Indoors)
        {
            BuildStreet(size);
            return;
        }
        var wall = EnvironmentId == "or" ? Materials.SurgicalGreen : new Color(0.75f, 0.78f, 0.8f);
        foreach (var side in (float[])[-1f, 1f])
        {
            Wall(new Vector3(size.X, size.Y, 0.1f), new Vector3(0f, size.Y * 0.5f, side * size.Z * 0.5f), wall);
            Wall(new Vector3(0.1f, size.Y, size.Z), new Vector3(side * size.X * 0.5f, size.Y * 0.5f, 0f), wall);
        }
        var ceiling = Shapes.Slab(this, size with { Y = 0.1f }, wall.Darkened(0.5f), new Vector3(0f, size.Y, 0f), 0.45f);
        ceiling.Name = "Ceiling";
    }

    private void Wall(Vector3 size, Vector3 position, Color color)
    {
        Shapes.Slab(this, size, color, position, 0.3f);
        Shapes.StaticBox(this, size, position);
    }

    private void BuildStreet(Vector3 size)
    {
        Shapes.Slab(this, new Vector3(size.X, 0.15f, 2f), new Color(0.35f, 0.35f, 0.35f),
            new Vector3(0f, 0.075f, -size.Z * 0.35f), 0.8f);
        var pole = ModelSlot.Instantiate("props", "streetlight", this);
        pole.Position = new Vector3(3f, 0f, 2.9f);
        var coat = new Dictionary<string, Material>
        {
            ["tint"] = Materials.ToonShaded(new Color(0.12f, 0.12f, 0.14f), 0.5f),
            ["mask"] = Materials.ToonShaded(new Color(0.2f, 0.18f, 0.16f), 0.5f),
            ["skin"] = Materials.ToonShaded(new Color(0.7f, 0.55f, 0.45f), 0.2f),
        };
        for (var i = 0; i < 6; i++)
        {
            var angle = (Mathf.Tau * i / 6f) + 0.3f;
            var bystander = new Node3D { Name = $"Bystander{i}" };
            AddChild(bystander);
            bystander.Position = new Vector3(Mathf.Cos(angle) * 3.2f, 0f, Mathf.Sin(angle) * 2.6f);
            bystander.LookAt(Vector3.Zero);
            ModelSlot.Instantiate("surgeon", "body", bystander, coat);
            var head = ModelSlot.Instantiate("surgeon", "head", bystander, coat);
            head.Position = new Vector3(0f, Surgeon.EyeHeight, 0f);
            foreach (var part in ModelSlot.Parts(head, "Mask", "Cap").Values)
            {
                part.Visible = false;
            }
        }
    }

    private void BuildTable()
    {
        ModelSlot.Instantiate("props", "operating_table", this);
        Shapes.StaticBox(this, new Vector3(TableHead - TableFoot, TableHeight, 0.62f),
            new Vector3((TableHead + TableFoot) * 0.5f, TableHeight * 0.5f, 0f));
        if (EnvironmentId != "or")
        {
            ModelSlot.Instantiate("props", "straps", this);
        }
        var lamp = ModelSlot.Instantiate("props", "surgical_lamp", this);
        lamp.Position = new Vector3(0f, LampHeight, 0f);
        // The lamp head hangs right under the ceiling light; its shadow would black out the middle of the table.
        foreach (var mesh in lamp.FindChildren("*", nameof(MeshInstance3D), true, false))
        {
            ((MeshInstance3D)mesh).CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        }
        lamp.Visible = Indoors;
    }

    private void BuildTray()
    {
        var tray = ModelSlot.Instantiate("props", "instrument_tray", this);
        tray.Position = Layout.Tray;
        Shapes.StaticBox(this, new Vector3(0.7f, 0.05f, 0.8f), Layout.Tray + new Vector3(0f, TraySurface - 0.025f, 0f));
        foreach (var group in SmallTrays)
        {
            var area = TrayZones[group];
            var middle = Layout.Tray + new Vector3(area.GetCenter().X, TraySurface, area.GetCenter().Y);
            Shapes.StaticBox(this, new Vector3(area.Size.X, SmallTrayFloor, area.Size.Y),
                middle + new Vector3(0f, SmallTrayFloor * 0.5f, 0f));
            var rim = middle + new Vector3(0f, SmallTrayRim * 0.5f, 0f);
            foreach (var side in (float[])[-1f, 1f])
            {
                Shapes.StaticBox(this, new Vector3(SmallTrayWall, SmallTrayRim, area.Size.Y),
                    rim + new Vector3(side * (area.Size.X - SmallTrayWall) * 0.5f, 0f, 0f));
                Shapes.StaticBox(this, new Vector3(area.Size.X, SmallTrayRim, SmallTrayWall),
                    rim + new Vector3(0f, 0f, side * (area.Size.Y - SmallTrayWall) * 0.5f));
            }
        }
    }

    private void BuildStations(Surgery surgery)
    {
        Station("manual", "Read the manual", new Vector3(0.6f, 1.8f, 0.4f), _ => surgery.OpenManual());
        Card = new PatientCard { Name = "PatientCard" };
        AddChild(Card);
        Card.Setup(Prop("card"));
        // Just the board: any taller and it would reach into "Talk to the patient" around the head.
        var read = Interactable.Create(this, "Read the patient card", new Vector3(0.08f, 0.3f, 0.25f),
            Layout["card"] + new Vector3(0.02f, 0.74f, 0f), _ => surgery.OpenCard());
        read.Offered = _ => Card.OnHook;
        if (surgery.Scenario.Nurse && Layout.Has("bell"))
        {
            Station("bell", "Ring for the nurse", new Vector3(0.5f, 1.2f, 0.5f), _ => surgery.OpenNurse());
            _nurseBoard = Shapes.Label(this, "", Layout["bell"] + new Vector3(0f, 1.5f, 0f), 40);
            _nurseBoard.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
        }
        if (Layout.Has("gloves"))
        {
            Station("gloves", "Change gloves", new Vector3(0.4f, 1.2f, 0.4f), surgery.ChangeGloves);
        }
        if (Layout.Has("sanitizer"))
        {
            Station("sanitizer", "Sanitize held tool", new Vector3(0.5f, 1.2f, 0.5f), surgery.SanitizeTool);
        }
        if (Layout.Has("sink"))
        {
            Station("sink", "Wash held tool", new Vector3(0.6f, 1.2f, 0.5f), surgery.WashTool);
        }
        if (Layout.Has("delivery_tray"))
        {
            Prop("delivery_tray");
        }
        if (Layout.Has("smoking"))
        {
            var smoke = Station("smoking", "Smoke a cigarette", new Vector3(0.4f, 1.2f, 0.4f), surgery.Smoke);
            smoke.Offered = surgeon => surgeon.HeldTool(surgeon.Active)?.Def.Id == "cig_pack";
        }
        if (Layout.Has("defib_cart"))
        {
            Prop("defib_cart");
        }
        // A syringe goes straight into the hanging bag (the iv_drip tool, see StationTools()); a bag in hand swaps it.
        _ivStand = Prop("iv");
        var swap = Interactable.Create(this, "Swap IV bag", new Vector3(0.4f, 2f, 0.4f),
            Layout["iv"] + new Vector3(0f, 1f, 0f), surgery.UseIv);
        swap.Offered = surgeon => surgeon.HeldTool(surgeon.Active)?.Def.IvOnly == true;
        IvLine = new IvLine { Name = "IvLine" };
        AddChild(IvLine);
        Monitor = new PatientMonitor { Name = "Monitor" };
        AddChild(Monitor);
        Monitor.Position = Layout["monitor"];
        Monitor.Rotation = new Vector3(0f, Mathf.Atan2(-Monitor.Position.X, -Monitor.Position.Z), 0f);
        Monitor.Build();
        Interactable.Create(this, "Order blood work", new Vector3(0.5f, 0.5f, 0.3f), Layout["monitor"],
            _ => surgery.OpenLab());
        foreach (var side in (float[])[-1f, 1f])
        {
            Interactable.Create(this, "Turn the patient", new Vector3(0.3f, 0.3f, 0.2f),
                new Vector3(-0.35f, 1f, 0.38f * side), _ => surgery.TurnPatient());
        }
        if (Layout.Has("xray"))
        {
            Xray = new XrayCart { Name = "XrayCart" };
            AddChild(Xray);
            Xray.Position = Layout["xray"];
            Xray.Bounds = (new Vector2(Layout.Size.X, Layout.Size.Z) * 0.5f) - new Vector2(0.45f, 0.45f);
            Xray.Build(surgery);
        }
        Interactable.Create(this, "Talk to the patient", new Vector3(0.25f, 0.3f, 0.3f), new Vector3(0.92f, 1.05f, 0f),
            _ => surgery.ComfortPatient());
    }

    private Interactable Station(string key, string prompt, Vector3 size, Action<Surgeon> action, float height = 1f)
    {
        Prop(key);
        return Interactable.Create(this, prompt, size, Layout[key] + new Vector3(0f, height, 0f), action);
    }

    /// <summary>Runs the IV tubing from the stand's drip chamber to a catheter taped on at <paramref name="site"/> (local
    /// to <paramref name="to"/>, see PatientBody.IvSite()) on an arm of this radius.</summary>
    public void ConnectIv(Node3D to, Transform3D site, float armRadius) =>
        IvLine.Attach(_ivStand, IvDripPoint, to, site, armRadius);

    /// <summary>Places a layout prop, turned by its yaw, solid if it has a footprint in <see cref="StationSolids"/>.
    /// </summary>
    private Node3D Prop(string key)
    {
        var position = Layout[key];
        var yaw = Layout.YawOf(key);
        var root = ModelSlot.Instantiate("props", key, this);
        root.Position = position;
        root.Rotation = new Vector3(0f, yaw, 0f);
        if (StationSolids.TryGetValue(key, out var solid))
        {
            if (Mathf.Abs(Mathf.Sin(yaw)) > 0.5f)
            {
                solid = new Vector3(solid.Z, solid.Y, solid.X);
            }
            Shapes.StaticBox(this, solid, position + new Vector3(0f, solid.Y * 0.5f, 0f));
        }
        return root;
    }
}
