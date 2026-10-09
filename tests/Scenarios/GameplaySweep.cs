namespace Scalpel.Tests.Scenarios;

/// <summary>
/// One scenario's sweep for <see cref="GameplayRegressionTest"/>. It works the game's systems directly, faster than a
/// player could, to reach every one of them in every scenario: what each mechanic does for a player is proven by its
/// own suite. A check that goes wrong is noted (<see cref="Fail"/>) and the sweep goes on, so one run shows them all.
/// </summary>
internal sealed partial class GameplaySweep
{
    private const string SurgeryScene = "res://scenes/surgery.tscn";

    private readonly Surgery _surgery;
    private readonly List<string> _failures = [];

    private GameplaySweep(Surgery surgery) => _surgery = surgery;

    private Patient Patient => _surgery.Patient;
    private PatientBody Body => _surgery.Patient.Body;
    private ToolManager Tools => _surgery.Tools;
    private Surgeon Me => _surgery.LocalSurgeon!;
    private string Site => _surgery.Scenario.Site;

    /// <summary>Sweeps <paramref name="scenario"/> and returns what went wrong. <paramref name="once"/>: also run the
    /// checks that come out the same whatever the scenario.</summary>
    public static async Task<List<string>> Run(ScenarioDef scenario, bool once)
    {
        GD.Print($"--- {scenario.Id}");
        var rng = new RandomNumberGenerator { Seed = (ulong)scenario.Order };
        var player = new LobbyPlayer("Tester", QuirkRoller.RollSurgeon(rng), Ready: true);
        Net.Instance.StartLocalSession(scenario, rng.Randi(), player, QuirkRoller.RollPatient(scenario, rng),
            Db.RunModifiers.GetSections());
        var surgery = GD.Load<PackedScene>(SurgeryScene).Instantiate<Surgery>();
        Frames.Root.AddChild(surgery);
        try
        {
            var sweep = new GameplaySweep(surgery);
            await sweep.Sweep(once);
            return sweep._failures;
        }
        finally
        {
            surgery.QueueFree();
            await Frames.Physics(3);
        }
    }

    private void Fail(string message)
    {
        GD.Print($"    FAIL: {message}");
        _failures.Add(message);
    }

    private async Task Sweep(bool once)
    {
        await Frames.Physics(5);
        if (!_surgery.Running)
        {
            Fail("surgery did not start");
            return;
        }
        await SurgeryDriver.PlayerPutsCardBack(_surgery);
        CheckDefibCart();
        // First, while the site is still whole and the tray untouched.
        await TableChecks(once);
        await AnatomyChecks();
        await EveryToolOnThePatient();
        await NewMechanics();
        await FeedbackChecks();
        if (once)
        {
            await OnceChecks();
        }
        foreach (var id in Db.Events.GetSections())
        {
            _surgery.Director.Fire(id, _surgery);
        }
        foreach (var drug in Db.Drugs.Keys)
        {
            Patient.Administer(drug, DrugRoute.Direct);
        }
        if (Patient.IvSet)
        {
            Patient.IvRemoved();
        }
        Patient.SetIv(Body.Root.ToGlobal(Patient.PreopIvPoint), true);
        Patient.Administer("saline", DrugRoute.Iv);
        for (var i = 0; i < 4; i++)
        {
            Patient.Shock(1f);
        }
        _surgery.Lab.Request("full", _surgery);
        _surgery.Nurse.Request(1, ["scalpel"], _surgery);
        _surgery.TurnPatient();
        _surgery.TurnResult(3, false);
        await Frames.Physics(30);
        _surgery.OpenManual();
        _surgery.OpenCard();
        _surgery.OpenNurse();
        _surgery.Hud.CloseOverlay();
        _surgery.Finish(true, "");
        await Frames.Physics(5);
        GD.Print($"    wounds={Patient.Wounds.Count} score={_surgery.Scoring.Points} flags={string.Join(", ", Patient.Flags.Keys)}");
    }

    /// <summary>Every tool lying free, taken in the right hand, lowered over the site and worked at medium then full
    /// effort with the tool action held, moving along a blade's edge.</summary>
    private async Task EveryToolOnThePatient()
    {
        var site = Body.Site.GlobalPosition;
        var hand = Me.Hands[1];
        foreach (var tool in Tools.Tools.Values.ToList())
        {
            if (tool.State != ToolState.Free)
            {
                continue;
            }
            hand.Lowered = false;
            Tools.RequestGrab(tool, 1);
            await Frames.Physics(2);
            hand.Attached = false;
            hand.LocalTarget = Me.ToLocal(site + new Vector3(0f, 0.12f, 0f));
            hand.Target = site + new Vector3(0f, 0.12f, 0f);
            hand.Lowered = true;
            foreach (var level in (int[])[2, 3])
            {
                hand.Level = level;
                hand.Trigger = true;
                for (var i = 0; i < 20; i++)
                {
                    hand.LocalTarget += new Vector3(0.0005f, 0f, 0.002f);
                    await Frames.NextPhysics();
                }
                hand.Trigger = false;
                await Frames.Physics(2);
            }
            hand.Lowered = false;
            if (OS.GetCmdlineUserArgs().Contains("--verbose"))
            {
                var probe = Body.Probe(tool.TipPosition());
                GD.Print($"    {tool.Def.Id,-20} zone={probe.Zone,-6} uv={probe.Uv} wounds={Patient.Wounds.Count}");
            }
            Tools.RequestRelease(1, Vector3.Zero);
            await Frames.Physics(2);
        }
    }

    private async Task NewMechanics()
    {
        MuscleFirstChecks();
        var clips = Db.Tool("paper_clips")!;
        // Tight clips tear new wounds: only the ones there now.
        foreach (var wound in Patient.Wounds.Where(wound => !wound.IsInternal && wound.Points.Count > 1).ToList())
        {
            for (var pressure = 1; pressure <= 3; pressure++)
            {
                for (var i = 0; i < 30; i++)
                {
                    Patient.CloseAt(wound.Midpoint, clips, 0.1f, 1f, pressure);
                }
            }
        }
        foreach (var organ in Body.Organs)
        {
            organ.Position += new Vector3(0.05f, 0f, 0f);
        }
        Patient.HandleOrgans(6f);
        Tools.RequestPass(1);
        _surgery.CorrectChart();
        _surgery.OpenCard();
        _surgery.Hud.CloseOverlay();
        if (_surgery.Room.Xray is not { } cart)
        {
            return;
        }
        var push = Station(cart, "Push");
        push.Interact(Me);
        await Frames.Physics(10);
        push.Interact(Me);
        cart.GlobalPosition = Patient.GlobalPosition + new Vector3(0f, -Room.TableHeight, 1f);
        Station(cart, "Take an X-ray").Interact(Me);
        await Frames.Physics((int)(XrayCart.ExposeTime * 60) + 10);
        if (cart.Print is null)
        {
            Fail("x-ray print missing");
        }
        _surgery.Hud.OpenXray(cart);
        await Frames.Physics(3);
        _surgery.Hud.CloseOverlay();
    }

    /// <summary>The spot under <paramref name="parent"/> whose prompt starts with <paramref name="prompt"/>.</summary>
    private static Interactable Station(Node parent, string prompt) =>
        parent.FindChildren("*", "", true, false).OfType<Interactable>().First(spot => spot.Prompt.StartsWith(prompt));

    /// <summary>A cut through the muscle: the skin won't close over it and tight clips tear, until the muscle is
    /// closed. (The needle's running thread is tested with the needle, NeedleTest.)</summary>
    private void MuscleFirstChecks()
    {
        var clips = Db.Tool("paper_clips")!;
        Patient.Cut(424242, new Vector2(0.3f, 0.2f), new Vector2(0.7f, 0.2f), 1f, 1f, false, 0.1f);
        var wound = Patient.StrokeWound(424242);
        if (wound is not { ThroughMuscle: true })
        {
            return;
        }
        void CloseEveryBin(Func<Vector2, bool> close)
        {
            for (var bin = 0; bin < wound.Bins.Length; bin++)
            {
                for (var i = 0; i < 20; i++)
                {
                    close(wound.BinPosition(bin));
                }
            }
        }
        CloseEveryBin(uv => Patient.CloseAt(uv, clips, 0.1f, 1f, 2));
        if (wound.Closure > 0f)
        {
            Fail($"the skin closed over open muscle (closure {wound.Closure:0.00})");
        }
        var tears = Patient.Flags.GetValueOrDefault("tears");
        for (var i = 0; i < 20; i++)
        {
            Patient.CloseAt(wound.Midpoint, clips, 0.1f, 1f, 3);
        }
        if (Patient.Flags.GetValueOrDefault("tears") <= tears)
        {
            Fail("tight clips over open muscle didn't tear");
        }
        CloseEveryBin(uv => Patient.CloseMuscleAt(uv, clips, 0.1f));
        if (Body.Tissue.MuscleOpenNear(wound.Midpoint, Patient.MuscleReach))
        {
            Fail("sewing inside the wound didn't close the muscle");
        }
        CloseEveryBin(uv => Patient.CloseAt(uv, clips, 0.1f, 1f, 2));
        if (wound.Closure < 0.9f)
        {
            Fail($"the skin didn't close over sewn muscle (closure {wound.Closure:0.00})");
        }
    }

    /// <summary>Before anything gets moved: the defibrillator waits on its cart.</summary>
    private void CheckDefibCart()
    {
        var layout = _surgery.Room.Layout;
        if (!layout.Has("defib_cart"))
        {
            return;
        }
        var cart = layout["defib_cart"];
        if (!Tools.Tools.Values.Any(tool => tool.Def.Id == "defibrillator"
                && (tool.GlobalPosition - cart).Slide(Vector3.Up).Length() < 0.4f))
        {
            Fail("no defibrillator on the defib cart");
        }
    }

    /// <summary>Deliveries, floor dirt and the IV line.</summary>
    private async Task FeedbackChecks()
    {
        var room = _surgery.Room;
        // A nurse delivery ends up lying on the delivery tray.
        if (room.Layout.Has("delivery_tray"))
        {
            var delivered = Tools.ByUid(Tools.Spawn("gauze", room.DeliverySpot()))!;
            await Frames.Physics(90);
            var tray = room.Layout["delivery_tray"];
            if (delivered.GlobalPosition.Y < 0.85f || (delivered.GlobalPosition - tray).Slide(Vector3.Up).Length() > 0.35f)
            {
                Fail($"delivery didn't land on the delivery tray: {delivered.GlobalPosition}");
            }
        }
        // Floor dirt: the sanitizer refuses a soiled tool until it's been washed.
        if (FreeTools().FirstOrDefault() is { } tool)
        {
            Tools.RequestGrab(tool, 1);
            tool.Sterile = false;
            tool.SetSoiled(true);
            Tools.RequestSterilize(1);
            if (tool.Sterile)
            {
                Fail("sanitizer made a soiled tool sterile");
            }
            Tools.RequestWash(1);
            Tools.RequestSterilize(1);
            if (tool.Soiled || !tool.Sterile)
            {
                Fail("washing then sanitizing didn't clean the tool");
            }
            Tools.RequestRelease(1, Vector3.Zero);
        }
        await IvTripChecks();
        await TourniquetChecks();
    }

    /// <summary>Tools lying free that the local surgeon's quirks let them take.</summary>
    private IEnumerable<SurgicalTool> FreeTools() =>
        Tools.Tools.Values.Where(tool => tool.State == ToolState.Free && Me.BlockedReason(tool.Def).Length == 0);

    /// <summary>Walking into the IV tubing at full speed rips the line out.</summary>
    private async Task IvTripChecks()
    {
        // The catheter pressed on the patient earlier may already have put a line in somewhere else (it depends on
        // where the shaky tip landed): take it out, so the line runs to the back of the hand like a pre-op one.
        if (Patient.IvSet)
        {
            Patient.IvRemoved();
        }
        Patient.SetIv(Body.Root.ToGlobal(Patient.PreopIvPoint), true);
        await Frames.Physics(5);
        var line = _surgery.Room.IvLine;
        var low = line.Points.Where(p => p.Y < IvLine.TripHeight).ToList();
        if (low.Count == 0)
        {
            Fail($"the IV line doesn't hang low enough to trip on (attached {line.IsAttached}, iv set {Patient.IvSet}, {line.Points.Count} points)");
            return;
        }
        var crossing = low.MinBy(p => p.Y);
        var from = new Vector3(crossing.X - 0.5f, 0f, crossing.Z);
        for (var i = 0; i < 34; i++)
        {
            Me.GlobalPosition = from + new Vector3(i * 0.03f, 0f, 0f);
            await Frames.NextPhysics();
        }
        if (Patient.IvSet)
        {
            Fail("walking through the IV line didn't pull it out");
        }
        await Frames.Physics(90);
        var catheters = Tools.Tools.Values.Where(tool => tool.Def.Id == "iv_catheter").ToList();
        if (!catheters.Any(tool => tool.State == ToolState.Free && tool.Soiled))
        {
            var where = catheters.Select(tool => $"{tool.State} {tool.GlobalPosition} soiled {tool.Soiled}");
            Fail($"the IV catheter ripped out of the arm didn't land on the floor: {string.Join(", ", where)}");
        }
    }

    /// <summary>A tourniquet pressed onto a thigh wraps around it: a band snug around the leg, not lying on top of it.
    /// Taking it off loosens it again.</summary>
    private async Task TourniquetChecks()
    {
        var tourniquet = Tools.ByUid(Tools.Spawn("tourniquet", _surgery.Room.TraySpots()[7]))!;
        await Frames.Physics(3);
        if (Me.BlockedReason(tourniquet.Def).Length > 0)
        {
            return;
        }
        // Straight above the thigh, whichever way up the patient lies.
        var above = Body.Root.ToGlobal(new Vector3(-0.75f, 0f, 0.1f)) + (Vector3.Up * 0.4f);
        if (RayDown(Patient, above, 0.8f, PatientBody.SurfaceLayer) is not { } top)
        {
            Fail("no thigh to put the tourniquet on");
            return;
        }
        Patient.TourniquetOn = false;
        Tools.RequestGrab(tourniquet, 1);
        // Pointing straight down onto the top of the thigh.
        tourniquet.GlobalTransform = new Transform3D(new Basis(Vector3.Right, Vector3.Forward, Vector3.Up),
            top + (Vector3.Up * tourniquet.Def.Length));
        var hand = new HandInput(true, true, 0, 0f, 1, new Modifiers());
        var tip = tourniquet.TipPosition();
        // Held for two frames: a press counts once the tool has come down.
        for (var frame = 0; frame < 2; frame++)
        {
            ToolActions.Update(tourniquet, hand, Patient, 1f / 60f);
        }
        await Frames.Physics(2);
        if (!Patient.TourniquetOn || tourniquet.Band is not { } band)
        {
            Fail($"the tourniquet didn't go on the thigh at {tip} (on={Patient.TourniquetOn}, part {Body.PartAt(tip, 0.08f)})");
            Tools.RequestRelease(1, Vector3.Zero);
            return;
        }
        var torus = (TorusMesh)band.Mesh;
        var center = band.GlobalPosition;
        var axis = band.GlobalBasis.Y.Normalized();
        if (Mathf.Abs(axis.Dot(Body.Root.GlobalBasis.X.Normalized())) < 0.95f)
        {
            Fail($"the tourniquet band doesn't run around the leg (axis {axis})");
        }
        if (center.Y > top.Y - 0.03f)
        {
            Fail($"the tourniquet lies on top of the leg instead of around it (center {center.Y:0.000}, skin on top {top.Y:0.000})");
        }
        // Snug: the band's inside passes just over the top of the thigh.
        if (Mathf.Abs(center.Y + torus.InnerRadius - top.Y) > 0.015f)
        {
            Fail($"the tourniquet band isn't snug on the thigh (inside at {center.Y + torus.InnerRadius:0.000}, skin at {top.Y:0.000})");
        }
        if (tourniquet.State != ToolState.Standing || Tools.ToolInHand(1, 1) is not null)
        {
            Fail("the hand still holds the tourniquet once it's on");
        }
        Tools.RequestGrab(tourniquet, 1);
        await Frames.Physics(2);
        if (Patient.TourniquetOn || tourniquet.Band is not null)
        {
            Fail("taking the tourniquet off didn't loosen it");
        }
        Tools.RequestRelease(1, Vector3.Zero);
        await Frames.Physics(2);
    }

    /// <summary>Where a ray from <paramref name="from"/> straight down for <paramref name="length"/> meters first hits
    /// something on <paramref name="mask"/>, null for nothing.</summary>
    private static Vector3? RayDown(Node3D node, Vector3 from, float length, uint mask)
    {
        var query = PhysicsRayQueryParameters3D.Create(from, from + (Vector3.Down * length), mask);
        var hit = node.GetWorld3D().DirectSpaceState.IntersectRay(query);
        return hit.Count > 0 ? hit["position"].AsVector3() : null;
    }
}
