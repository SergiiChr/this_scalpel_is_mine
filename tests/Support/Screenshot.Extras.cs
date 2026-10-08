namespace Scalpel.Tests.Support;

/// <summary>The anatomy, material and menu views.</summary>
public partial class Screenshot
{
    /// <summary>--anatomy: the site opened wide (chest, belly) or cut to the bone (limbs), a top organ held aside, a
    /// tourniquet on the thigh and a syringe held up to read.</summary>
    private async Task Anatomy(Surgery surgery)
    {
        var patient = surgery.Patient;
        var body = patient.Body;
        var camera = NewCamera();
        var site = body.Site.GlobalPosition;
        if (body.Organs.Count == 0)
        {
            patient.Cut(5, new Vector2(0.15f, 0.51f), new Vector2(0.85f, 0.51f), 1f, 1f, false, 0.1f);
            (Vector2 At, float Side)[] pulls = [(new(0.5f, 0.46f), -1f), (new(0.5f, 0.56f), 1f)];
            foreach (var (at, side) in pulls)
            {
                body.Tissue.Grip(950 + (int)side, at);
            }
            for (var step = 0; step < 30; step++)
            {
                foreach (var (at, side) in pulls)
                {
                    body.Tissue.MoveGrip(950 + (int)side, body.Tissue.Rest[body.Tissue.Nearest(at)] + new Vector3(0f, 0.004f, side * 0.001f * (step + 1)));
                }
                body.Tissue.Substep();
            }
        }
        else
        {
            SurgeryState.SiteIsOpenedWide(patient);
        }
        await Frames(30);
        camera.GlobalPosition = site + (body.Site.GlobalBasis.Y * 0.42f) + new Vector3(0f, 0f, 0.12f);
        camera.LookAt(site);
        await Shot("20_open_from_above");
        var me = surgery.LocalSurgeon!;
        me.Camera.Current = true;
        me.Pitch = -1.05f;
        me.Zoom = 1;
        await Frames(30);
        await Shot("21_open_first_person");
        var top = body.Organs.FindIndex(organ => organ.Layer == 0);
        if (top >= 0)
        {
            var organ = body.Organs[top];
            body.HoldOrgan(top, organ.Position + new Vector3(0f, 0.04f, 0f) + (new Vector3(organ.Position.X, 0f, organ.Position.Z).Normalized() * 0.1f));
            for (var i = 0; i < 20; i++)
            {
                body.SettleOrgans(1f / Engine.PhysicsTicksPerSecond);
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            }
            camera.Current = true;
            await Shot("22_top_organ_aside");
            body.ReleaseOrgan(top);
        }
        camera.Current = true;
        var thigh = body.Root.ToGlobal(new Vector3(-0.75f, 0f, 0.1f));
        if (body.LimbRingAt(thigh + (Vector3.Up * 0.06f)) is { } ring)
        {
            var tourniquet = surgery.Tools.ByUid(surgery.Tools.Spawn("tourniquet", thigh))!;
            await Frames(2);
            surgery.Tools.Wrap(tourniquet, ring);
            camera.GlobalPosition = thigh + new Vector3(0.1f, 0.3f, 0.35f);
            camera.LookAt(thigh);
            await Shot("23_tourniquet");
        }
        if (surgery.Tools.Tools.Values.FirstOrDefault(tool => tool.Def.Action == "syringe" && tool.State == ToolState.Free) is { } syringe)
        {
            surgery.Tools.AddLiquid(syringe, syringe.Def.Volume * 0.35f);
            surgery.Tools.RequestGrab(syringe, 1);
            me.Active = 1;
            me.Camera.Current = true;
            me.Pitch = -0.3f;
            me.Zoom = 0;
            PlayerInput.Action(InputActions.Inspect);
            await Frames(30);
            await Shot("24_syringe_held_up");
            PlayerInput.Action(InputActions.Inspect, false);
        }
    }

    /// <summary>A row of samples on a stand over the site, lit like the site: the four skin tones, glove rubber, scrubs
    /// and gown cloth, steel tools, wet tissue and blood. Then both hands in each grip at the arm's limits.</summary>
    private async Task MaterialViews(Surgery surgery)
    {
        var site = surgery.Patient.Body.Site.GlobalPosition + new Vector3(0f, 0.12f, 0f);
        var board = new Node3D();
        AddChild(board);
        List<Material> samples =
        [
            .. Materials.SkinTones.Select(Materials.BodySkin),
            Materials.FamilyUnique("cloth", Materials.Scrubs[0], 0.9f),
            Materials.FamilyUnique("cloth", Materials.PatientGown, 0.9f),
            Materials.FleshMaterial(),
            Materials.BloodPool(),
        ];
        for (var i = 0; i < samples.Count; i++)
        {
            var ball = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.025f, Height = 0.05f },
                MaterialOverride = samples[i],
            };
            board.AddChild(ball);
            ball.GlobalPosition = site + new Vector3(-0.21f + (i * 0.06f), 0f, -0.05f);
        }
        var glove = ModelSlot.Instantiate("surgeon", "glove", board);
        glove.GlobalPosition = site + new Vector3(-0.2f, -0.01f, 0.06f);
        string[] shown = ["scalpel", "forceps", "needle"];
        for (var i = 0; i < shown.Length; i++)
        {
            var holder = new Node3D();
            board.AddChild(holder);
            ModelSlot.InstantiateTool(Db.Tool(shown[i])!, holder);
            holder.GlobalTransform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2f) * new Basis(Vector3.Right, -0.2f),
                site + new Vector3(i * 0.07f, 0f, 0.06f));
        }
        await Frames(20);
        var camera = NewCamera();
        // About where a surgeon's eyes are, then close up.
        camera.GlobalPosition = site + new Vector3(0f, 0.35f, 0.45f);
        camera.LookAt(site);
        await Shot("30_materials");
        camera.GlobalPosition = site + new Vector3(-0.1f, 0.12f, 0.2f);
        camera.LookAt(site + new Vector3(-0.1f, 0f, 0f));
        await Shot("31_materials_close");
        board.QueueFree();
        surgery.LocalSurgeon!.Visible = false;
        foreach (var grip in GripStyle.All.Keys)
        {
            await GripViews(grip, site, camera);
        }
    }

    /// <summary>Both hands holding a tool in <paramref name="grip"/>, the arm stretched out and folded up, seen from
    /// across the table.</summary>
    private async Task GripViews(string grip, Vector3 site, Camera3D camera)
    {
        var def = Db.Tools.Values.First(tool => tool.Grip == grip);
        var body = new Node3D();
        AddChild(body);
        // Standing on the floor at the table's side, facing the site.
        body.GlobalPosition = new Vector3(site.X, 0f, site.Z + 0.55f);
        var hands = new SurgeonHand[2];
        var tools = new Node3D[2];
        for (var index = 0; index < 2; index++)
        {
            var hand = new SurgeonHand();
            body.AddChild(hand);
            hand.Build(index, Materials.FamilyUnique("cloth", Materials.Scrubs[0], 0.9f));
            hand.Holding = true;
            hand.Grip = grip;
            hand.Fit = Db.GripFit(def, index);
            hands[index] = hand;
            tools[index] = new Node3D();
            AddChild(tools[index]);
            ModelSlot.InstantiateTool(def, tools[index]);
        }
        foreach (var limit in (string[])["stretched", "folded"])
        {
            foreach (var hand in hands)
            {
                var side = hand.Index == 0 ? -1f : 1f;
                var shoulder = body.ToGlobal(new Vector3(0.19f * side, 1.4f, -0.08f));
                var reach = limit == "stretched" ? new Vector3(0.2f * side, -0.3f, -0.6f) : new Vector3(0.06f * side, -0.18f, -0.17f);
                hand.Target = shoulder + reach;
                hand.SnapPose(shoulder);
                tools[hand.Index].GlobalTransform = hand.GripTransform();
            }
            // From across the table, a little above the hands.
            var between = hands[0].GlobalPosition.Lerp(hands[1].GlobalPosition, 0.5f);
            camera.GlobalPosition = between + new Vector3(0f, 0.3f, -0.6f);
            camera.LookAt(between);
            await Shot($"32_grip_{grip}_{limit}");
        }
        body.QueueFree();
        foreach (var tool in tools)
        {
            tool.QueueFree();
        }
    }

    /// <summary>--menus: the main menu's scenarios, codex and settings with every quirk unlocked, and the lobby.
    /// </summary>
    private async Task Menus()
    {
        foreach (var quirk in Db.PatientQuirks.Values.Concat(Db.SurgeonQuirks.Values))
        {
            Progress.UnlockForSession(quirk);
        }
        var menu = GD.Load<PackedScene>("res://scenes/ui/main_menu.tscn").Instantiate<MainMenu>();
        AddChild(menu);
        await Shot("10_menu_scenarios");
        menu.ShowCodex();
        await Shot("11_menu_codex");
        menu.ShowSettings();
        await Shot("12_menu_settings");
        menu.QueueFree();
        var doctor = new LobbyPlayer("Doctor",
            [new QuirkRoll("shaky_hands", ""), new QuirkRoll("hand_size", ""), new QuirkRoll("divine_knowledge", "")], Ready: true);
        Net.Instance.StartLocalSession(Db.Scenario("appendectomy")!, 1, doctor, [], ["chart_error", "understaffed"]);
        AddChild(GD.Load<PackedScene>("res://scenes/ui/lobby.tscn").Instantiate());
        await Shot("13_lobby");
    }
}
