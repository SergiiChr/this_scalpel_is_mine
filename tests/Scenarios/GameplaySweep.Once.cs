namespace Scalpel.Tests.Scenarios;

/// <summary>The checks that come out the same whatever the scenario: run in the first one only.</summary>
internal sealed partial class GameplaySweep
{
    private async Task OnceChecks()
    {
        await EffectChecks();
        await ControlChecks();
        await IodineChecks();
        await SyringeChecks();
        NurseChecks();
        AnesthesiaChecks();
        await SmokingChecks();
    }

    /// <summary>Every tool effect plays on the body, tools pick up blood and wash clean.</summary>
    private async Task EffectChecks()
    {
        var at = Body.UvToWorld(new Vector2(0.5f, 0.5f));
        foreach (var kind in (ToolEffect[])[ToolEffect.Smoke, ToolEffect.Dust, ToolEffect.Spatter, ToolEffect.Spark, ToolEffect.Bead])
        {
            _surgery.Effect(kind, at);
        }
        await Frames.Physics(20);
        if (_surgery.GetNode("Effects").GetChildCount() == 0)
        {
            Fail("tool effects left nothing on screen");
        }
        if (FreeTools().FirstOrDefault() is { } tool)
        {
            Tools.RequestGrab(tool, 1);
            Tools.AddBlood(tool, 0.6f);
            if (tool.Blood < 0.5f)
            {
                Fail($"working in blood didn't bloody the tool: {tool.Blood}");
            }
            await Frames.Physics(30);
            if (Me.Hands[1].Blood <= 0f)
            {
                Fail("a bloody tool didn't bloody the glove holding it");
            }
            // With every quirk on, a sweaty glove or a cough can make it slip meanwhile: then it's picked up again.
            if (Me.HeldTool(1) != tool)
            {
                Tools.RequestGrab(tool, 1);
            }
            Tools.RequestWash(1);
            if (tool.Blood > 0f)
            {
                Fail("washing didn't take the blood off");
            }
            Tools.RequestRelease(1, Vector3.Zero);
            Tools.RequestWash(1);
            await Frames.Physics(2);
            if (Me.Hands[1].Blood > 0f)
            {
                Fail("washing empty hands didn't clean the glove");
            }
        }
        Body.Blood.EmitSignal(BloodFlow.SignalName.Splashed, 1f);
        await Frames.Physics(2);
        if (_surgery.Hud.LensBlood <= 0f)
        {
            Fail("blood splashed on the view didn't show");
        }
    }

    /// <summary>Sends <paramref name="press"/> through the engine's input and waits <paramref name="frames"/> physics
    /// frames for the game to act on it.</summary>
    private static async Task Input(Action press, int frames = 1)
    {
        press();
        await PlayerInput.Delivered();
        await Frames.Physics(frames);
    }

    /// <summary>Controls: RMB picks up and puts down, LMB lowers and works the tool, the wheel sets its level, Shift
    /// steps the zoom between two levels, and the controls shown follow a held hand key.</summary>
    private async Task ControlChecks()
    {
        // On the tray or the table: standing, a hand doesn't reach one dropped on the floor.
        // NOTE: by here a scalpel lies on the floor. Nothing this test checks says how it got there; not looked into yet.
        if (FreeTools().FirstOrDefault(tool => tool.Def.Action == "cut" && tool.GlobalPosition.Y > 0.5f) is not { } blade)
        {
            return;
        }
        var hand = Me.Hands[Me.Active];
        Me.Hovered = null;
        hand.LocalTarget = Me.ToLocal(blade.GlobalPosition - hand.TipOffset(0.05f));
        // Settled over it, as the hand rests there by the time a player presses RMB.
        await Frames.Physics(5);
        await Input(() => PlayerInput.Tap(InputActions.Grab), 2);
        if (Me.HeldTool(Me.Active) != blade)
        {
            Fail("RMB (grab) didn't pick up the tool under the hand");
            return;
        }
        var looking = Hud.ControlLines(Me);
        await Input(() => PlayerInput.Tap(InputActions.LevelUp));
        if (hand.Level != 1)
        {
            Fail($"the wheel didn't raise a blade's depth: {hand.Level}");
        }
        await Input(() => PlayerInput.Action(InputActions.UseTool));
        if (!(hand.Lowered && hand.Trigger))
        {
            Fail("LMB (use) didn't lower and work the tool");
        }
        await Input(() => PlayerInput.Action(InputActions.UseTool, false));
        if (hand.Lowered || hand.Trigger)
        {
            Fail("letting go of LMB left the tool working");
        }
        await AimChecks(blade, hand);
        var zooms = new List<int>();
        for (var i = 0; i < Surgeon.ZoomFov.Length; i++)
        {
            await Input(() => PlayerInput.Tap(InputActions.Zoom));
            zooms.Add(Me.Zoom);
        }
        if (!zooms.SequenceEqual([1, 0]))
        {
            Fail($"Shift doesn't step between two zoom levels: {string.Join(", ", zooms)}");
        }
        await Input(() => PlayerInput.Action(InputActions.MoveRightHand));
        if (Hud.ControlLines(Me).SequenceEqual(looking))
        {
            Fail("the controls shown didn't change while holding a hand key");
        }
        PlayerInput.Action(InputActions.MoveRightHand, false);
        await Input(() => PlayerInput.Tap(InputActions.Grab), 2);
        if (Me.HeldTool(Me.Active) is not null)
        {
            Fail("RMB (grab) didn't put the tool down");
        }
    }

    /// <summary>Aiming with the mouse (MMB) turns the tool and the hand together: the glove stays where it is on the
    /// tool.</summary>
    private async Task AimChecks(SurgicalTool blade, SurgeonHand hand)
    {
        // Out to the side over the floor, so nothing under the tip lifts the hand while it turns, and calm: a shaking
        // hand moves the elbow, and the glove follows that.
        hand.LocalTarget = new Vector3(0.5f, 1.1f, 0.15f);
        var ownStatus = Me.Status;
        Me.Status = new SurgeonStatus(new Modifiers());
        await Frames.Physics(10);
        // Against the hand's grip frame, worked out with the glove: the tool's own transform follows a frame later.
        var onTool = hand.GripTransform().AffineInverse() * hand.Glove.GlobalTransform;
        var before = blade.GlobalBasis;
        PlayerInput.Action(InputActions.AimTool);
        PlayerInput.Mouse(new Vector2(120f, -60f) / Settings.MouseSensitivity);
        await PlayerInput.Delivered();
        await Frames.Physics(5);
        var after = hand.GripTransform().AffineInverse() * hand.Glove.GlobalTransform;
        var slid = Mathf.RadToDeg((onTool.Basis.Orthonormalized().Inverse() * after.Basis.Orthonormalized()).GetRotationQuaternion().GetAngle());
        var turned = before.Z.AngleTo(blade.GlobalBasis.Z);
        if (turned < 0.2f || slid > 3f || onTool.Origin.DistanceTo(after.Origin) > 0.005f)
        {
            Fail($"aiming the tool didn't turn it with the hand (turned {turned:0.00} rad, glove moved on it {slid:0.0} deg, {onTool.Origin.DistanceTo(after.Origin) * 100f:0.0} cm)");
        }
        PlayerInput.Mouse(new Vector2(-120f, 60f) / Settings.MouseSensitivity);
        await Input(() => PlayerInput.Action(InputActions.AimTool, false), 2);
        Me.Status = ownStatus;
    }

    /// <summary>The rolled tray holds the starter kit; iodine goes bottle to dish, soaks a pad held in forceps and
    /// sanitizes the skin.</summary>
    private async Task IodineChecks()
    {
        var rolled = _surgery.Scenario.RollTools(new RandomNumberGenerator());
        if (_surgery.Scenario.MissingToolChance == 0f && Db.StarterKit.Any(id => !rolled.Contains(id)))
        {
            Fail($"the starter kit isn't all on the tray: {string.Join(", ", rolled)}");
        }
        var spot = _surgery.Room.TraySpots()[12];
        var forceps = Tools.ByUid(Tools.Spawn("forceps", spot))!;
        var pad = Tools.ByUid(Tools.Spawn("cotton_pad", spot))!;
        var dish = Tools.ByUid(Tools.Spawn("iodine_dish", spot))!;
        await Frames.Physics(10);
        if (Me.BlockedReason(forceps.Def).Length > 0)
        {
            return;
        }
        Tools.RequestGrab(forceps, 1);
        Tools.Carry(pad, forceps);
        await Frames.Physics(3);
        if (pad.State != ToolState.Carried || pad.GlobalPosition.DistanceTo(forceps.TipPosition()) > 0.05f)
        {
            Fail("forceps didn't pick up the cotton pad");
        }
        Tools.AddLiquid(dish, dish.Def.Volume, new Dictionary<string, float> { ["iodine"] = dish.Def.Volume });
        var dishMiddle = dish.GlobalTransform * new Vector3(0f, 0f, -dish.Def.Length * 0.5f);
        Wipe(pad, SiteZone.None, Vector2.Zero, dishMiddle, 1f);
        if (pad.Fill < 0.9f || dish.Fill > 0.9f)
        {
            Fail($"the pad didn't soak up iodine from the dish: pad={pad.Fill:0.00} dish={dish.Fill:0.00}");
        }
        Array.Fill(Patient.Sanitized, 0f);
        var uv = new Vector2(0.5f, 0.5f);
        var wiped = Body.UvToWorld(uv);
        // A dish left beside the site must not turn the wipe into a dip.
        dish.GlobalPosition = wiped;
        Wipe(pad, SiteZone.Site, uv, wiped, 0.5f);
        if (Patient.SanitizedFraction() <= 0f || pad.Fill >= 0.99f)
        {
            Fail("the soaked pad didn't sanitize the skin");
        }
        // A second of wiping, one physics frame at a time: no single frame may take a big bite out of the frame
        // budget. Best of three runs, so a busy machine doesn't fail it.
        var worstMs = double.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            Tools.SetFill(pad, 1f);
            var runWorst = 0.0;
            for (var i = 0; i < 60; i++)
            {
                var at = new Vector2(0.3f + (i * 0.006f), 0.5f);
                var started = Time.GetTicksUsec();
                Wipe(pad, SiteZone.Site, at, Body.UvToWorld(at), 1f / 60f);
                Body.WoundMap.Flush();
                runWorst = Math.Max(runWorst, (Time.GetTicksUsec() - started) / 1000.0);
            }
            worstMs = Math.Min(worstMs, runWorst);
        }
        GD.Print($"    iodine wipe: worst frame {worstMs:0.00} ms");
        if (worstMs > 4.0 && FrameBudget.Enforced)
        {
            Fail($"wiping iodine takes {worstMs:0.00} ms in one frame (stutters)");
        }
        Tools.RequestRelease(1, Vector3.Zero);
        await Frames.Physics(3);
        // Let go over an opened chest or belly, the pad falls in: that's fine, it isn't on the forceps.
        if (pad.State == ToolState.Carried)
        {
            Fail("the pad stayed on forceps that were let go");
        }
    }

    /// <summary>One frame of <paramref name="pad"/> wiping at <paramref name="tip"/> (world), over
    /// <paramref name="uv"/> in <paramref name="zone"/>, held in forceps.</summary>
    private void Wipe(SurgicalTool pad, SiteZone zone, Vector2 uv, Vector3 tip, float dt)
    {
        var hand = new HandInput(true, true, 0, 0f, 1, new Modifiers());
        var step = new ToolStep(pad, hand, Patient, dt) { Tip = tip, Probe = new SiteProbe(zone, uv, 0f) };
        ToolActions.Wipe(pad, step, dt, false);
    }

    /// <summary><paramref name="seconds"/> of drugs soaking into the patient's blood, in steps like the patient's own.
    /// </summary>
    private static void SoakIn(Patient patient, float seconds)
    {
        for (var i = 0; i < (int)(seconds * 10f); i++)
        {
            patient.DrugEffectsOver(0.1f);
        }
    }

    /// <summary>A syringe draws from a vial, a roughly right dose works and too little doesn't, drugs mix, the floor
    /// breaks it.</summary>
    private async Task SyringeChecks()
    {
        if (Patient.WeightKg is < 15f or > 150f)
        {
            Fail($"odd patient weight {Patient.WeightKg:0} kg");
        }
        var spot = _surgery.Room.TraySpots()[17];
        var vial = Tools.ByUid(Tools.Spawn("vial_propofol", spot))!;
        var morphine = Tools.ByUid(Tools.Spawn("vial_morphine", spot))!;
        var syringe = Tools.ByUid(Tools.Spawn("syringe_50", spot))!;
        await Frames.Physics(3);
        // Three wheel notches with the needle in the vial draw 3 ml.
        var vialMiddle = vial.GlobalTransform * new Vector3(0f, 0f, -vial.Def.Length * 0.5f);
        syringe.GlobalTransform = new Transform3D(Basis.Identity, vialMiddle + new Vector3(0f, 0f, syringe.Def.Length));
        for (var i = 0; i < 3; i++)
        {
            Syringe.Plunge(syringe, ToolActions.PlungerStep, Patient);
        }
        var drawn = 3f * ToolActions.PlungerStep;
        if (Mathf.Abs(syringe.Ml - drawn) > 0.01f || Mathf.Abs(vial.Ml - (vial.Def.Volume - drawn)) > 0.01f
            || syringe.Label().EndsWith("(empty)"))
        {
            Fail($"the syringe didn't draw from the vial: syringe={syringe.Ml:0.00} ml vial={vial.Ml:0.00} ml");
        }
        // The right dose, pushed into the patient, counts once it has soaked in. Checked on a body with no drugs in yet.
        var rightMl = Db.Drug("propofol")!.Dose * Patient.WeightKg / vial.Def.Concentration;
        Tools.Transfer(vial, syringe, rightMl - syringe.Ml);
        var drugsWere = Patient.Drugs;
        Patient.Drugs = new DrugLevels();
        Patient.Flags.Remove("drug_propofol");
        var site = Body.UvToWorld(new Vector2(0.5f, 0.5f));
        syringe.GlobalTransform = new Transform3D(Basis.Identity, site + new Vector3(0f, 0f, syringe.Def.Length));
        // Tools dropped on the site earlier (the kidney dish) would catch the needle: put them back on the tray.
        while (Tools.NearestContainer(site) is { } inWay)
        {
            inWay.GlobalPosition = spot + (Vector3.Up * 0.1f);
        }
        var aimed = $"{Body.Probe(syringe.TipPosition())}, {Syringe.NeedleTarget(syringe, Patient)}";
        while (syringe.Ml > 0f)
        {
            Syringe.Plunge(syringe, -ToolActions.PlungerStep, Patient);
        }
        syringe.GlobalPosition += Vector3.Up * 0.3f;
        SoakIn(Patient, 10f);
        if (syringe.Ml > 0f || !Patient.Flags.ContainsKey("drug_propofol"))
        {
            Fail($"the right dose of propofol didn't count: left={syringe.Ml:0.00} ml flags={string.Join(", ", Patient.Flags.Keys)}, the needle was in {aimed}");
        }
        // A third of the dose doesn't do the job.
        Patient.Drugs = new DrugLevels();
        Patient.Flags.Remove("drug_propofol");
        Tools.Transfer(vial, syringe, rightMl * 0.3f);
        Patient.Administer("propofol", DrugRoute.Direct, Tools.Transfer(syringe, null, syringe.Ml)["propofol"]);
        SoakIn(Patient, 10f);
        if (Patient.Flags.ContainsKey("drug_propofol"))
        {
            Fail("a third of the dose counted as a full one");
        }
        Patient.Drugs = drugsWere;
        // Two vials into one syringe make a mix.
        Tools.Transfer(vial, syringe, 1f);
        Tools.Transfer(morphine, syringe, 1f);
        if (!(syringe.Contents.ContainsKey("propofol") && syringe.Contents.ContainsKey("morphine")) || Mathf.Abs(syringe.Ml - 2f) > 0.01f)
        {
            Fail($"drugs from two vials didn't mix: {string.Join(", ", syringe.Contents)}");
        }
        await InspectChecks(syringe, spot);
        // Dropped on the floor, it shatters.
        syringe.GlobalPosition = _surgery.Room.SpawnTransform(1).Origin + new Vector3(0f, 0.6f, 0f);
        syringe.LinearVelocity = Vector3.Zero;
        syringe.Sleeping = false;
        // Let the physics server receive the teleported, awake body before marking it as a live drop; otherwise stale
        // tray contacts can settle it immediately.
        await Frames.Physics(1);
        syringe.Falling = true;
        await Frames.Physics(90);
        if (syringe.State != ToolState.Consumed)
        {
            Fail("a syringe dropped on the floor didn't break");
        }
    }

    /// <summary>Holding Inspect brings a syringe up in front of the eyes, across the view with the hand behind it, and
    /// the liquid and plunger show exactly how many ml are in it against the graduation.</summary>
    private async Task InspectChecks(SurgicalTool syringe, Vector3 putBack)
    {
        if (Me.BlockedReason(syringe.Def).Length > 0)
        {
            return;
        }
        var hand = Me.Active;
        Tools.RequestGrab(syringe, hand);
        await Frames.Physics(2);
        if (Me.HeldTool(hand) != syringe)
        {
            Fail("couldn't pick up the syringe to look at it");
            return;
        }
        await Input(() => PlayerInput.Action(InputActions.Inspect), 10);
        var camera = Me.Camera;
        foreach (var end in (Vector3[])[syringe.GlobalTransform * Vector3.Zero, syringe.TipPosition()])
        {
            if (!camera.IsPositionInFrustum(end) || camera.GlobalPosition.DistanceTo(end) > 0.45f)
            {
                Fail($"the syringe held up to look at isn't in view close up: {end}");
            }
        }
        // The graduation runs all the way round the barrel; the hand has to be behind it, not in front of it.
        var barrel = syringe.GlobalTransform * new Vector3(0f, 0f, -syringe.Def.Length * 0.35f);
        var glove = Me.Hands[hand].Glove.GlobalTransform * new Vector3(0.05f, 0f, 0f);
        if (camera.GlobalPosition.DistanceTo(glove) < camera.GlobalPosition.DistanceTo(barrel) + 0.01f)
        {
            Fail("the hand holding the syringe up is in front of the barrel");
        }
        var across = Mathf.Abs(syringe.GlobalBasis.Z.Normalized().Dot(camera.GlobalBasis.X.Normalized()));
        if (across < 0.9f)
        {
            Fail($"the syringe isn't held across the view ({across:0.00})");
        }
        // The liquid runs from the needle end to the plunger, a tick every tenth of the volume.
        var level = (MeshInstance3D)syringe.FindChild("Level", true, false);
        var plunger = (Node3D)syringe.FindChild("Plunger", true, false);
        // The full Level part spans the ticks from empty to full; how much of it shows is how full the syringe reads.
        var travel = level.GetAabb().Size.Z;
        var liquid = level.Visible ? level.GetAabb().Size.Z * level.Scale.Z : 0f;
        var shownMl = liquid / travel * syringe.Def.Volume;
        if (Mathf.Abs(shownMl - syringe.Ml) > syringe.Def.Volume * 0.02f)
        {
            Fail($"the syringe reads {shownMl:0.00} ml against its ticks but holds {syringe.Ml:0.00} ml");
        }
        // The plunger's stopper starts at the needle end (its rest) and sits right behind the liquid and any air.
        if (Mathf.Abs(plunger.Position.Z - liquid - (travel * syringe.Air / syringe.Def.Volume)) > travel * 0.02f)
        {
            Fail($"the plunger doesn't sit right behind the liquid (pulled back {plunger.Position.Z:0.0000} m, liquid {liquid:0.0000} m)");
        }
        await Input(() => PlayerInput.Action(InputActions.Inspect, false), 2);
        // Put down on the tray, not into whatever is open under the hand.
        Tools.RequestRelease(hand, Vector3.Zero);
        syringe.GlobalPosition = putBack + (Vector3.Up * 0.05f);
        syringe.Falling = false;
        await Frames.Physics(2);
    }

    /// <summary>One order at a time: the board shows it on its way. The first five deliveries come without a
    /// cooldown, after the sixth it starts.</summary>
    private void NurseChecks()
    {
        var nurse = _surgery.Nurse;
        string[] gauze = ["gauze"];
        nurse.Tick(1000f, _surgery);
        nurse.CooldownLeft = 0f;
        nurse.Delivered = 0;
        nurse.Request(1, gauze, _surgery);
        var board = new SurgeryStatus([], 0, [], nurse.CooldownLeft, nurse.Current, 0f, 0f).NurseBoard;
        if (nurse.Current is null || !board.Contains("Gauze"))
        {
            Fail("the nurse board doesn't show the order on its way");
        }
        for (var i = 0; i < Nurse.FreeOrders; i++)
        {
            nurse.Request(1, gauze, _surgery);
            nurse.Tick(1000f, _surgery);
            if (nurse.CooldownLeft > 0f)
            {
                Fail($"the nurse cooldown started after free delivery {i + 1}");
            }
        }
        nurse.Request(1, gauze, _surgery);
        nurse.Tick(1000f, _surgery);
        if (nurse.Current is not null || nurse.CooldownLeft <= 0f)
        {
            Fail("the nurse cooldown didn't start after the sixth delivery");
        }
        nurse.Request(1, gauze, _surgery);
        if (nurse.Current is not null)
        {
            Fail("the nurse took an order during her cooldown");
        }
    }

    /// <summary>Without quirks, one right dose of propofol keeps a patient whose bleeding is under control asleep and
    /// their heart going for five minutes (it used to wear off in under three).</summary>
    private void AnesthesiaChecks()
    {
        var mods = Patient.Mods;
        var vitals = Patient.Vitals.ToVariant();
        var drugs = Patient.Drugs;
        var wounds = Patient.Wounds.ToList();
        Patient.Mods = new Modifiers();
        Patient.Drugs = new DrugLevels();
        Patient.Wounds.Clear();
        var v = Patient.Vitals;
        v.Rhythm = Rhythm.Sinus;
        v.BloodMl = v.MaxBloodMl;
        v.Systolic = 120f;
        v.HeartRate = 75f;
        v.Temperature = 36.8f;
        v.Glucose = 5.5f;
        v.Swelling = 0f;
        Patient.Administer("propofol", DrugRoute.Vein, Db.Drug("propofol")!.Dose * Patient.WeightKg);
        var depth = 0f;
        for (var i = 0; i < 3000; i++)
        {
            Patient.Simulate(0.1f);
            if (i == 600)
            {
                depth = v.Anesthesia;
            }
        }
        // Run modifiers (expired drugs) may weaken the dose, but whatever depth it reaches has to hold.
        if (Mathf.Abs(v.Anesthesia - depth) > 0.01f || v.IsAwake || v.IsArrested)
        {
            Fail($"one right dose of propofol didn't hold for five minutes (anesthesia {depth:0.00} -> {v.Anesthesia:0.00}, awake {v.IsAwake}, arrested {v.IsArrested})");
        }
        Patient.Mods = mods;
        v.Apply(vitals);
        Patient.Drugs = drugs;
        Patient.Wounds.AddRange(wounds);
    }

    /// <summary>The smoking spot is offered only to a hand holding cigarettes; a smoke uses one, stops stress and
    /// speeds you up.</summary>
    private async Task SmokingChecks()
    {
        Tools.RequestRelease(Me.Active, Vector3.Zero);
        if (_surgery.Room.FindChild("SmokeACigarette", false, false) is not Interactable spot || spot.OfferedTo(Me))
        {
            Fail("the smoking spot is missing or offered to an empty hand");
            return;
        }
        var pack = Tools.ByUid(Tools.Spawn("cig_pack", Me.GlobalPosition + new Vector3(0f, 1f, 0f)))!;
        Tools.RequestGrab(pack, Me.Active);
        await Frames.Physics(2);
        if (!spot.OfferedTo(Me))
        {
            Fail("the smoking spot isn't offered to a hand holding cigarettes");
        }
        var speed = Me.Status.MoveSpeed();
        spot.Interact(Me);
        await Frames.Physics(2);
        var stress = Me.Status.Stress;
        Me.Status.AddStress(0.3f);
        if (pack.Charges != pack.Def.Charges - 1 || Me.Status.Stress > stress || Me.Status.MoveSpeed() <= speed)
        {
            Fail($"smoking didn't use a cigarette, stop stress and speed you up ({pack.Charges} left)");
        }
        Me.Status.SmokeLeft = 0f;
        Tools.RequestRelease(Me.Active, Vector3.Zero);
    }
}
