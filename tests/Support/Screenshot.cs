namespace Scalpel.Tests.Support;

/// <summary>
/// Renders a few views of a scenario with staged damage, for checking the look without playing (./build.py shots).
/// Needs a real renderer: xvfb-run godot --path . --rendering-method gl_compatibility
/// res://tests/Support/Screenshot.tscn -- --scenario=appendectomy --out=/tmp/shots
/// <list type="bullet">
/// <item>--materials instead renders the material board (every material family and skin tone under the surgical lamp)
/// and both hands in every grip with the arm stretched out and folded up.</item>
/// <item>--anatomy: the site opened wide (chest, belly) or cut to the bone (limbs), a top organ held aside, a tourniquet
/// on the thigh and a syringe held up to read.</item>
/// <item>--syringe renders every <see cref="SyringeBench"/> case zoomed in: a vial, dish or bag case aimed first (the
/// hands see-through), then in the needle view the needle in, halfway through the wheel notches and done, and the first
/// one held up to read (41_syringe_held_up). Then the IV catheter on the vein and beside it: aimed, in, the line from
/// the stand and the dressing close up (42_*), then a sedated and a knocked out surgeon (43_* to 46_*,
/// --only=sedation). --only=&lt;case&gt; renders one.</item>
/// <item>--menus: the main menu's pages and the lobby.</item>
/// </list>
/// Without those, --only=monitor stops after the monitor views and --only=site after the site close ups.
/// </summary>
public partial class Screenshot : Node
{
    private const string SurgeryScene = "res://scenes/surgery.tscn";

    private string _out = "user://screenshots";
    private string _only = "";

    public override void _Ready() => _ = Render();

    private static bool Has(string flag) => OS.GetCmdlineUserArgs().Contains(flag);

    private static string Arg(string name, string fallback) =>
        OS.GetCmdlineUserArgs().FirstOrDefault(arg => arg.StartsWith($"--{name}="))?.Split('=', 2)[1] ?? fallback;

    private async Task Render()
    {
        var scenarioId = Arg("scenario", "appendectomy");
        _out = Arg("out", _out);
        _only = Arg("only", "");
        DirAccess.MakeDirRecursiveAbsolute(_out);
        if (Has("--menus"))
        {
            await Menus();
        }
        else if (Has("--syringe"))
        {
            await SyringeViews();
        }
        else
        {
            var player = new LobbyPlayer("Tester", [new QuirkRoll("normal_dude", "")], Ready: true);
            Net.Instance.StartLocalSession(Db.Scenario(scenarioId)!, 42, player, []);
            var surgery = GD.Load<PackedScene>(SurgeryScene).Instantiate<Surgery>();
            AddChild(surgery);
            await Frames(10);
            if (Has("--anatomy"))
            {
                await Anatomy(surgery);
            }
            else if (Has("--materials"))
            {
                await MaterialViews(surgery);
            }
            else
            {
                await ScenarioViews(surgery);
            }
        }
        GetTree().Quit();
    }

    private async Task Shot(string file)
    {
        await Frames(6);
        GetViewport().GetTexture().GetImage().SavePng(_out.PathJoin(file + ".png"));
    }

    private async Task Frames(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private Camera3D NewCamera()
    {
        var camera = new Camera3D();
        AddChild(camera);
        camera.Current = true;
        return camera;
    }

    /// <summary>The scenario with staged damage: cuts held open, burns, a bruise, a marker line, blood and iodine.
    /// Seen first person, on the monitor, close up, around the room, and the hands, the surgeon, the X-ray, the manual
    /// and the patient card.</summary>
    private async Task ScenarioViews(Surgery surgery)
    {
        var patient = surgery.Patient;
        patient.Cut(1, new Vector2(0.25f, 0.6f), new Vector2(0.5f, 0.55f), 1f, 1f, false, 0.1f);
        patient.Cut(1, new Vector2(0.5f, 0.55f), new Vector2(0.72f, 0.62f), 1f, 1f, false, 0.1f);
        patient.Cut(2, new Vector2(0.3f, 0.3f), new Vector2(0.55f, 0.25f), 0.5f, 0.5f, false, 0.5f);
        // Retract the long incision with two pins, like forceps holding the edges apart.
        var tissue = patient.Body.Tissue;
        foreach (var (at, pull) in ((Vector2, Vector3)[])[(new(0.48f, 0.52f), new(0f, 0.004f, -0.025f)), (new(0.48f, 0.6f), new(0f, 0.004f, 0.025f))])
        {
            var key = 900 + tissue.Grips().Count;
            tissue.Grip(key, at);
            tissue.MoveGrip(key, tissue.Pos[tissue.Nearest(at)] + pull);
        }
        patient.CauterizeAt(SiteZone.Site, new Vector2(0.75f, 0.3f), Db.Tool("lighter")!, 0.5f);
        patient.CauterizeAt(SiteZone.Site, new Vector2(0.8f, 0.35f), Db.Tool("cautery")!, 0.5f);
        patient.Bruise(new Vector2(0.2f, 0.25f), 0.1f, 0.8f);
        patient.Mark(new Vector2(0.2f, 0.8f), new Vector2(0.8f, 0.82f));
        patient.Paint(WoundMap.Layer.Fluids, WoundMap.Blood, new Vector2(0.45f, 0.62f), new Vector2(0.45f, 0.62f), 0.08f, 1f, WoundMap.Mode.Max);
        patient.SwabAt(SiteZone.Site, new Vector2(0.6f, 0.8f), Db.Tool("cotton_pad")!, 1f, "iodine");
        await Frames(20);
        var me = surgery.LocalSurgeon!;
        var camera = NewCamera();
        camera.GlobalTransform = me.Camera.GlobalTransform;
        await Shot("01_first_person");
        // The monitor face on, with lab results, then alarming.
        var monitor = surgery.Room.Monitor;
        monitor.ShowLab(Lab.Result("full", patient));
        camera.GlobalPosition = monitor.ToGlobal(new Vector3(0f, 0f, 0.45f));
        camera.LookAt(monitor.GlobalPosition);
        await Frames(240);
        await Shot("10_monitor");
        var saved = patient.Vitals.ToVariant();
        for (var i = 0; i < 60; i++)
        {
            patient.Vitals.Spo2 = 84f;
            patient.Vitals.Systolic = 72f;
            await Frames(1);
        }
        await Shot("10b_monitor_alarm");
        patient.Vitals.Apply(saved);
        if (_only == "monitor")
        {
            return;
        }
        me.Camera.Current = true;
        me.Pitch = -1f;
        await Shot("02_looking_down");
        // What a player sees zoomed all the way in on the site.
        me.Zoom = Surgeon.ZoomFov.Length - 1;
        me.Pitch = -0.8f;
        await Frames(40);
        await Shot("02b_zoomed");
        me.Zoom = 0;
        await Frames(40);
        camera.Current = true;
        var site = patient.Body.Site.GlobalPosition;
        camera.GlobalPosition = site + new Vector3(0f, 0.35f, 0.25f);
        camera.LookAt(site);
        await Shot("03_site_closeup");
        var incision = patient.Body.UvToWorld(new Vector2(0.45f, 0.5f));
        camera.GlobalPosition = incision + new Vector3(0f, 0.16f, 0.1f);
        camera.LookAt(incision);
        await Shot("03b_tissue_layers");
        if (_only == "site")
        {
            return;
        }
        await RoomViews(surgery, camera);
        await HandViews(surgery, camera);
        await PaperViews(surgery);
    }

    /// <summary>The room, the foot of the table, the tray with iodine poured and the board over the bell.</summary>
    private async Task RoomViews(Surgery surgery, Camera3D camera)
    {
        camera.GlobalPosition = new Vector3(2.3f, 2.5f, 1.9f);
        camera.LookAt(new Vector3(0f, 0.9f, 0f));
        await Shot("04_room");
        // The feet end of the table, with the instrument tray past it.
        camera.GlobalPosition = new Vector3(-1f, 1.5f, 1.4f);
        camera.LookAt(new Vector3(-1.4f, 0.9f, 0f));
        await Shot("04b_table_foot");
        // Iodine in the dish and one soaked pad.
        foreach (var tool in surgery.Tools.Tools.Values.Where(tool => tool.Def.Id is "iodine_dish" or "cotton_pad"))
        {
            surgery.Tools.SetFill(tool, 1f);
        }
        var tray = surgery.Room.Layout.Tray + new Vector3(0f, 0.95f, 0f);
        camera.GlobalPosition = tray + new Vector3(0.45f, 0.45f, 0f);
        camera.LookAt(tray);
        await Shot("07_tray");
        // The board over the bell with an order on its way.
        if (surgery.Room.Layout.Has("bell"))
        {
            surgery.Nurse.Request(1, ["gauze", "gauze", "scalpel"], surgery);
            await Frames(40);
            var bell = surgery.Room.Layout["bell"];
            camera.GlobalPosition = bell + new Vector3(0.3f, 1.5f, -1.3f);
            camera.LookAt(bell + new Vector3(0f, 1.3f, 0f));
            await Shot("07b_bell");
        }
    }

    /// <summary>Both hands holding tools over the site, then bloody, then working over the thighs; the surgeon from
    /// the room.</summary>
    private async Task HandViews(Surgery surgery, Camera3D camera)
    {
        var me = surgery.LocalSurgeon!;
        var patient = surgery.Patient;
        var site = patient.Body.Site.GlobalPosition;
        var free = surgery.Tools.Tools.Values.Where(tool => tool.State == ToolState.Free).ToList();
        if (free.LastOrDefault(tool => tool.Def.Action == "cut") is { } scalpel)
        {
            surgery.Tools.RequestGrab(scalpel, 1);
        }
        if (free.LastOrDefault(tool => tool.Def.Action == "clamp") is { } forceps)
        {
            surgery.Tools.RequestGrab(forceps, 0);
        }
        me.Hands[1].LocalTarget = me.ToLocal(site + new Vector3(0.05f, 0.1f, 0.05f));
        me.Hands[0].LocalTarget = me.ToLocal(site + new Vector3(-0.08f, 0.12f, 0.05f));
        me.Pitch = -0.75f;
        await Frames(10);
        me.Camera.Current = true;
        await Shot("08_hands");
        // Later in a bloody surgery: gloves soaked, scrubs stained, a spurt just hit the view.
        foreach (var hand in me.Hands)
        {
            hand.SetBlood(0.8f);
        }
        me.SetStains(0.6f);
        patient.Body.Blood.EmitSignal(BloodFlow.SignalName.Splashed, 0.8f);
        await Frames(2);
        await Shot("08c_bloody");
        surgery.Hud.ClearLensBlood();
        // Hands working over the thighs: nothing may sink into the legs or the table.
        var spot = me.GlobalPosition;
        me.GlobalPosition = patient.GlobalPosition + new Vector3(-0.45f, -Room.TableHeight, 0.62f);
        var legs = patient.GlobalPosition + new Vector3(-0.5f, 0f, 0f);
        me.Hands[1].LocalTarget = me.ToLocal(legs + new Vector3(0.05f, 0.05f, 0.09f));
        me.Hands[0].LocalTarget = me.ToLocal(legs + new Vector3(-0.08f, 0.05f, -0.05f));
        me.Pitch = -0.9f;
        await Frames(20);
        await Shot("08b_hands_on_legs");
        me.GlobalPosition = spot;
        camera.Current = true;
        camera.GlobalPosition = me.GlobalPosition + new Vector3(0.9f, 1.7f, 0.6f);
        camera.LookAt(me.GlobalPosition + new Vector3(0f, 1.1f, -0.4f));
        // Seen from outside, the parts hidden from the surgeon's own eyes show.
        foreach (var part in me.FindChildren("*", "Node3D", false, false).Cast<Node3D>())
        {
            part.Visible = true;
        }
        foreach (var face in me.FindChildren("Face", "Node3D", true, false))
        {
            face.GetParent<Node3D>().Visible = true;
        }
        await Shot("09_surgeon");
    }

    /// <summary>The X-ray print, the manual (a condition page too) and the patient card.</summary>
    private async Task PaperViews(Surgery surgery)
    {
        if (surgery.Room.Xray is { } cart)
        {
            cart.GlobalPosition = surgery.Patient.GlobalPosition + new Vector3(-0.3f, -Room.TableHeight, 1.1f);
            cart.FindChildren("*", "", true, false).OfType<Interactable>().First(spot => spot.Prompt.StartsWith("Take an X-ray"))
                .Interact(surgery.LocalSurgeon!);
            await Frames((int)(XrayCart.ExposeTime * 60) + 30);
            await ToSignal(GetTree().CreateTimer(XrayCart.DevelopTime + 0.5), SceneTreeTimer.SignalName.Timeout);
            surgery.Hud.OpenXray(cart);
            await Shot("14_xray_print");
            surgery.Hud.CloseOverlay();
        }
        surgery.Hud.OpenManual();
        await Shot("05_manual");
        foreach (var title in (string[])["17. Chronic conditions", "Aneurysm"])
        {
            surgery.Hud.Manual!.Entries.First(entry => entry.Page.Title == title).Button.EmitSignal(BaseButton.SignalName.Pressed);
        }
        await Shot("05b_manual_condition");
        surgery.Hud.OpenCard();
        await Shot("06_card");
    }
}
