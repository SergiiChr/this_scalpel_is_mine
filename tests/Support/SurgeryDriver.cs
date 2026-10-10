namespace Scalpel.Tests.Support;

/// <summary>
/// Plays a surgery the way a player does, for tests. It walks up to things, picks tools up from the tray or orders them
/// from the nurse, steers the active hand across the floor and works the controls: Use tool, the wheel, Grab and
/// Interact. The game does the rest exactly as for a player: the surgeon's hand settles onto what's under it, and the
/// host runs what each held tool does (ToolManager -> ToolActions).
/// <para>
/// Add it to the tree, <see cref="Start"/> a scenario, then call the Player* steps, built on the input and movement
/// primitives. A step does its work and returns; tests assert on the game's state. Direct state changes for a test's
/// setup live apart, in <see cref="SurgeryState"/>. Walking is a step to where the surgeon wants to stand: no route is
/// walked, so nothing is bumped on the way.
/// </para>
/// </summary>
public partial class SurgeryDriver : Node
{
    private const string SurgeryScene = "res://scenes/surgery.tscn";
    /// <summary>How far from what it works on (meters, across the floor) the surgeon stands.</summary>
    public const float StandOff = 0.45f;
    /// <summary>A hand move across the floor this slow (m/s) keeps cuts clean and pulls slow.</summary>
    public const float Slow = 0.02f;
    /// <summary>The right hand works; the left holds what a second tool needs.</summary>
    public const int Right = 1;
    public const int Left = 0;

    public Surgery Surgery { get; private set; } = null!;
    public Surgeon Me { get; private set; } = null!;
    public Patient Patient { get; private set; } = null!;
    public PatientBody Body { get; private set; } = null!;
    /// <summary>Game work per frame while the surgery runs, unless paused (a screenshot being drawn isn't gameplay).
    /// </summary>
    public FrameBudget Budget { get; } = new();
    /// <summary>What the steps did, newest last: test failure messages show the tail.</summary>
    public List<string> Trail { get; } = [];
    /// <summary>Called (and awaited) with a name right after a step's key interaction: a cut made, a target out. Unset:
    /// no pictures.</summary>
    public Func<string, Task>? OnKeyFrame { get; set; }
    private bool _budgetPaused;

    /// <summary>A driver added to the scene tree's root, ready to <see cref="Start"/>.</summary>
    public static SurgeryDriver Create()
    {
        // A test that failed half way leaves its surgery running: it would play on beside this one.
        foreach (var stale in Frames.Root.GetChildren().OfType<SurgeryDriver>())
        {
            // Out of the tree at once, so it stops playing; freed at the end of the frame, as freeing it in the middle
            // of one trips up the engine's C# bindings.
            Frames.Root.RemoveChild(stale);
            stale.QueueFree();
        }
        var driver = new SurgeryDriver();
        Frames.Root.AddChild(driver);
        return driver;
    }

    /// <summary>
    /// Starts <paramref name="scenarioId"/> solo, like the menu's single player: one surgeon who rolled nothing special,
    /// a patient with only <paramref name="patientQuirks"/> and every tool the scenario lists on the tray, so the run
    /// only depends on the scenario. <paramref name="randomEvents"/> false leaves the escalation events out (scripted
    /// ones in the scenario still happen). The surgery opens on the patient card: the player puts it back first, unless
    /// <paramref name="readingCard"/>.
    /// </summary>
    public async Task Start(string scenarioId, bool randomEvents = false, uint seed = 1,
        IReadOnlyList<QuirkRoll>? patientQuirks = null, bool readingCard = false)
    {
        // The game's own unseeded rolls (where the nurse leaves things, jitter) come out the same every run too.
        GD.Seed(seed);
        // The tray as the scenario lists it: a missing tool is a twist for another test.
        var scenario = Db.Scenario(scenarioId)! with { MissingToolChance = 0f };
        var player = new LobbyPlayer("Driver", [new QuirkRoll("normal_dude", "")], Ready: true);
        Net.Instance.StartLocalSession(scenario, seed, player, patientQuirks ?? []);
        await Loaded();
        Surgery = GD.Load<PackedScene>(SurgeryScene).Instantiate<Surgery>();
        AddChild(Surgery);
        await Frames.Physics(10);
        Me = Surgery.LocalSurgeon!;
        Patient = Surgery.Patient;
        Body = Patient.Body;
        if (!randomEvents)
        {
            Surgery.Director.StopRandomEvents();
        }
        Me.Active = Right;
        Note($"started {scenarioId}");
        if (!readingCard)
        {
            await PlayerPutsCardBack(Surgery);
            Note("puts the card back");
        }
    }

    /// <summary>Puts the patient card down with Esc, and waits until it hangs on its hook.</summary>
    public static async Task PlayerPutsCardBack(Surgery surgery)
    {
        Tap(InputActions.Pause);
        await PlayerInput.Delivered();
        // Another player's copy can get there first: this one is back once it's no longer on its way.
        var card = surgery.Room.Card;
        await Frames.Until(() => card.OnHook && card.PaperOf(surgery.LocalSurgeon!.PeerId) is null,
            PatientCard.TravelSeconds + 1f);
    }

    /// <summary>What a player's loading screen waits for (<see cref="LoadingScreen"/>): the runtime warmed up and the
    /// tool models and sounds loaded, so a test's frames are timed as a player's are.</summary>
    private static async Task Loaded()
    {
        await ManagedRuntime.WarmUp;
        foreach (var (file, keep) in LoadingScreen.Ahead())
        {
            keep(GD.Load<Resource>(file));
        }
    }

    /// <summary>Ends the surgery and frees the driver with it.</summary>
    public async Task Stop()
    {
        QueueFree();
        await Frames.Process(2);
    }

    public override void _Process(double delta)
    {
        if (IsInstanceValid(Surgery) && Surgery.Running && !_budgetPaused)
        {
            Budget.Sample(Trail.Count > 0 ? Trail[^1] : "");
        }
    }

    public void Note(string text)
    {
        var elapsed = IsInstanceValid(Surgery) ? Surgery.Elapsed : 0f;
        Trail.Add($"{elapsed,6:0.0} s  {text}");
        if (OS.GetEnvironment("SURGERY_TRACE") == "1")
        {
            GD.Print($"[{Net.Instance.ScenarioId}] {Trail[^1]}");
        }
    }

    /// <summary>The last steps, for a failure message.</summary>
    public string Recent(int count = 12) => string.Join("\n", Trail.Skip(Math.Max(Trail.Count - count, 0)));

    public async Task Capture(string keyFrame)
    {
        if (OnKeyFrame is not null)
        {
            await Unbudgeted(() => OnKeyFrame(keyFrame));
        }
    }

    /// <summary>Does <paramref name="work"/> (drawing a screenshot, say) outside the frame budget: it isn't
    /// gameplay.</summary>
    public async Task Unbudgeted(Func<Task> work)
    {
        _budgetPaused = true;
        await work();
        _budgetPaused = false;
        Budget.Resume();
    }

    // --- Controls ------------------------------------------------------------------------------------------------

    /// <summary>Presses (and with <paramref name="pressed"/> false, lets go of) an input action.</summary>
    public static void Press(string action, bool pressed = true) => PlayerInput.Action(action, pressed);

    public static void Release(string action) => Press(action, false);

    /// <summary>Presses and lets go of an action straight away, like a click: Grab puts a held bottle down rather than
    /// standing it.</summary>
    public static void Tap(string action) => PlayerInput.Tap(action);

    /// <summary>One wheel notch on the active hand: its effort level, or a syringe's plunger (up pushes it in).
    /// </summary>
    public static async Task Notch(bool up)
    {
        Tap(up ? InputActions.LevelUp : InputActions.LevelDown);
        await Frames.Physics(2);
    }

    public async Task SetLevel(int level)
    {
        var hand = Me.Hands[Me.Active];
        for (var i = 0; i < 4 && hand.Level != level; i++)
        {
            await Notch(level > hand.Level);
        }
    }

    public static void Use(bool on = true) => Press(InputActions.UseTool, on);

    public async Task SwitchTo(int hand)
    {
        if (Me.Active == hand)
        {
            return;
        }
        var key = hand == Right ? InputActions.MoveRightHand : InputActions.MoveLeftHand;
        Press(key);
        Release(key);
        await Frames.Seconds(0.3f);
    }

    public Vector3 SitePoint(Vector2 uv, float depth = 0f) => Body.UvToWorld(uv, depth);

    // --- Moving --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Stands at the table's long side nearest <paramref name="point"/>, facing it, close enough to reach it with
    /// either hand. Off the table (the tray, the IV stand) it stands on the side facing the room's middle. Either way it
    /// keeps its feet off the IV tubing, like a player stepping around it (walking into it rips the line out).
    /// </summary>
    public async Task PlayerWalksTo(Vector3 point, float off = StandOff)
    {
        // Holding onto something with the other hand, stay put while the point is in reach: walking off would drag it.
        if (Me.Hands.Any(hand => hand.Attached) && point.DistanceTo(Me.Shoulder(Me.Active)) < Surgeon.Reach - 0.05f)
        {
            return;
        }
        var standingY = Me.GlobalPosition.Y;
        var table = Patient.GlobalPosition;
        var atTable = Mathf.Abs(point.X - table.X) < 1.1f && Mathf.Abs(point.Z - table.Z) < 0.5f;
        IEnumerable<Vector3> StandingSpots()
        {
            if (atTable)
            {
                var side = Mathf.Abs(point.Z - table.Z) > 0.05f
                    ? Mathf.Sign(point.Z - table.Z)
                    : Mathf.Sign(Me.GlobalPosition.Z - table.Z);
                foreach (var s in (float[])[side, -side])
                {
                    foreach (var along in (float[])[0f, 0.15f, -0.15f, 0.3f, -0.3f, 0.45f, -0.45f, 0.6f, -0.6f])
                    {
                        yield return new Vector3(point.X + along, standingY, table.Z + (s * Mathf.Max(Mathf.Abs(point.Z - table.Z) + off, 0.55f + (Me.SafeMargin * 2f))));
                    }
                }
            }
            else
            {
                var flat = point with { Y = standingY };
                var outward = -(flat with { Y = 0f }).Normalized();
                for (var step = 0; step <= 9; step++)
                {
                    for (var turn = 0; turn < 72; turn++)
                    {
                        var signed = ((turn + 1) / 2) * (turn % 2 == 0 ? 1 : -1);
                        yield return flat + (outward.Rotated(Vector3.Up, signed * Mathf.Pi / 36f) * (off + (step * 0.05f)));
                    }
                }
            }
        }
        // Stand on the room's floor, clear of furniture and tubing. Between the tray and table, an overlapping
        // capsule slides out during recovery and can pull a needle out of a vial halfway through drawing a dose.
        var line = Surgery.Room.IvLine;
        var body = Me.GetChildren().OfType<CollisionShape3D>().Single();
        using var query = new PhysicsShapeQueryParameters3D
        {
            Shape = body.Shape,
            CollisionMask = Me.CollisionMask,
            Margin = Me.SafeMargin,
            Exclude = [Me.GetRid()],
        };
        var syringe = Me.HeldTool(Me.Active);
        var vial = syringe?.Def.Action == "syringe"
            ? Surgery.Tools.Tools.Values.FirstOrDefault(tool => tool.Def.Action == "vial" && tool.State == ToolState.Free
                && tool.TipPosition().DistanceTo(point) < 0.001f)
            : null;
        bool Fits(Vector3 at) => !line.HangsLowAt(at, 0.12f) && CanStandAt(at, body, query);
        Vector3? chosen = null;
        var chosenYaw = 0f;
        foreach (var candidate in StandingSpots())
        {
            if (!Fits(candidate))
            {
                continue;
            }
            var at = candidate;
            if (!atTable && at.DistanceTo(point with { Y = standingY }) > off + 0.001f)
            {
                // Refine the first clear ring toward the target: a narrow aisle can leave only a few millimeters
                // between clearing the tray and reaching a vial at its far side.
                var inward = (point with { Y = standingY }) - at;
                var blocked = at + (inward.Normalized() * 0.05f);
                for (var i = 0; i < 6; i++)
                {
                    var middle = at.Lerp(blocked, 0.5f);
                    if (Fits(middle))
                    {
                        at = middle;
                    }
                    else
                    {
                        blocked = middle;
                    }
                }
            }
            var toward = point - at;
            var yaw = Mathf.Atan2(-toward.X, -toward.Z);
            if (vial is not null)
            {
                var into = (vial.GlobalPosition - point).Normalized();
                var wrist = point - (into * syringe!.Def.Length);
                // Turn a little so the working shoulder faces a vial at the back of a cramped tray, rather than
                // wasting the arm's reach across the chest. The needle's own snap still decides its contact.
                var reaches = false;
                foreach (var turn in (float[])[0f, -0.25f, 0.25f, -0.5f, 0.5f, -0.75f, 0.75f])
                {
                    var turned = yaw + turn;
                    if (wrist.DistanceTo(Surgeon.StandingShoulder(new Transform3D(new Basis(Vector3.Up, turned), at), Me.Active, Me.Crouch)) <= Surgeon.Reach)
                    {
                        yaw = turned;
                        reaches = true;
                        break;
                    }
                }
                if (!reaches)
                {
                    continue;
                }
            }
            chosenYaw = yaw;
            chosen = at;
            break;
        }
        var spot = chosen ?? throw new InvalidOperationException($"No clear standing position reaches {point} in {Surgery.Scenario.Id}.");
        Me.GlobalPosition = spot;
        Me.Velocity = Vector3.Zero;
        Me.Rotation = Me.Rotation with { Y = chosenYaw };
        await Frames.Physics(3);
    }

    /// <summary>The surgeon's capsule fits here without overlapping furniture or another surgeon.</summary>
    private bool CanStandAt(Vector3 at, CollisionShape3D body, PhysicsShapeQueryParameters3D query)
    {
        var size = Surgery.Room.Layout.Size;
        var radius = ((CapsuleShape3D)body.Shape).Radius + Me.SafeMargin;
        if (Mathf.Abs(at.X) > (size.X * 0.5f) - radius || Mathf.Abs(at.Z) > (size.Z * 0.5f) - radius)
        {
            return false;
        }
        query.Transform = new Transform3D(Me.GlobalBasis, at + (Me.GlobalBasis * body.Position) + (Vector3.Up * 0.01f));
        var hits = Me.GetWorld3D().DirectSpaceState.IntersectShape(query, 1);
        using var storage = (Godot.Collections.Array)hits;
        var clear = hits.Count == 0;
        foreach (var hit in hits)
        {
            hit.Dispose();
        }
        return clear;
    }

    /// <summary>Holds a walking key for <paramref name="time"/> seconds, so the body walks the way a player's does.
    /// <paramref name="eachFrame"/> is called once every frame of it, after the frame's physics.</summary>
    public async Task PlayerHoldsWalkKey(string action, float time, Action eachFrame)
    {
        PlayerInput.Action(action);
        for (var i = 0; i < (int)(time * Engine.PhysicsTicksPerSecond); i++)
        {
            await Frames.NextProcess();
            eachFrame();
        }
        PlayerInput.Action(action, false);
        Note($"walked ({action}) for {time:0.0} s");
    }

    /// <summary>Steers the active hand so its tool's tip (or the empty hand's fingertips) is over
    /// <paramref name="point"/>, across the floor. The game decides the height: hovering, or resting on what's under it
    /// while Use tool is held.</summary>
    public async Task PlayerReaches(Vector3 point)
    {
        var hand = Me.Hands[Me.Active];
        HoldHandKey(true);
        for (var i = 0; i < 8; i++)
        {
            var miss = (point - Tip(hand)) with { Y = 0f };
            if (i > 0 && miss.Length() < 0.002f)
            {
                break;
            }
            Steer(miss);
            await Frames.Physics(6);
        }
        HoldHandKey(false);
    }

    /// <summary>Turns the active hand's tool straight ahead with Aim tool: the hands start turned in, and straight the
    /// tip reaches further and the tool lies along where it points, not across what's beside it.</summary>
    public async Task PlayerAimsStraight()
    {
        var hand = Me.Hands[Me.Active];
        await PlayerAims(new Vector2(hand.Turn / (Surgeon.AimSensitivity * Settings.MouseSensitivity), 0f), 1);
        PlayerLetsGoOfAim();
        await Frames.Physics(10);
    }

    /// <summary>Moves the active hand's tip from where it is to <paramref name="point"/> across the floor at
    /// <paramref name="speed"/> m/s, steering back onto the straight line if it drifts.</summary>
    public async Task PlayerSweepsTo(Vector3 point, float speed = Slow)
    {
        var hand = Me.Hands[Me.Active];
        var from = Tip(hand) with { Y = 0f };
        var to = point with { Y = 0f };
        var steps = Math.Max((int)(from.DistanceTo(to) / speed * Engine.PhysicsTicksPerSecond), 1);
        HoldHandKey(true);
        for (var i = 0; i < steps; i++)
        {
            var planned = from.Lerp(to, (float)(i + 1) / steps);
            var drift = (planned - Tip(hand)) with { Y = 0f };
            Steer(((to - from) / steps) + (drift * 0.3f));
            await Frames.NextPhysics();
        }
        // The last move reaches the hand with the next input.
        await Frames.NextPhysics();
        HoldHandKey(false);
    }

    /// <summary>Holds (or lets go of) the active hand's key, so the mouse moves that hand.</summary>
    private void HoldHandKey(bool on) => Press(PlayerInput.HandKey(Me), on);

    /// <summary>Moves the mouse so the active hand moves <paramref name="move"/> (world, across the floor).</summary>
    private void Steer(Vector3 move) => PlayerInput.Mouse(PlayerInput.HandMotion(Me, move));

    public Vector3 Tip(SurgeonHand hand) =>
        Me.HeldTool(hand.Index)?.TipPosition() ?? hand.GlobalPosition + hand.TipOffset(0.05f);

    /// <summary>Holds Aim tool and moves the mouse <paramref name="motion"/> pixels a frame for
    /// <paramref name="count"/> frames. Aim tool stays held until <see cref="PlayerLetsGoOfAim"/>.</summary>
    public async Task PlayerAims(Vector2 motion, int count)
    {
        Press(InputActions.AimTool);
        for (var i = 0; i < count; i++)
        {
            PlayerInput.Mouse(motion);
            await Frames.NextPhysics();
        }
        // The last move reaches the hand with the next input.
        await Frames.NextPhysics();
        Note($"aimed {motion * count} px");
    }

    public static void PlayerLetsGoOfAim() => Release(InputActions.AimTool);

    /// <summary>Points the active hand's blade edge (ToolActions.BladeDirection()) along <paramref name="direction"/>
    /// (world, across the floor) by twisting the wrist, as C/V do.</summary>
    public async Task PlayerTurnsBlade(Vector3 direction)
    {
        var hand = Me.Hands[Me.Active];
        var now = hand.Twist;
        var best = now;
        var bestDot = -1f;
        // The hand's own way of turning its tool: a spreader swings about the upright where a blade rolls.
        for (var i = -60; i <= 60; i++)
        {
            hand.Twist = i * Mathf.Pi / 60f;
            var side = hand.GripTransform().Basis.X;
            var dot = Mathf.Abs(side.Cross(Vector3.Up).Normalized().Dot(direction.Normalized()));
            if (dot > bestDot)
            {
                bestDot = dot;
                best = hand.Twist;
            }
        }
        hand.Twist = now;
        // Held for as long as the turn takes. A half turn lines the edge up as well, so it never turns more than a
        // quarter.
        var turn = Mathf.Wrap(best - now, -Mathf.Pi / 2f, Mathf.Pi / 2f);
        var key = turn > 0f ? InputActions.TwistRight : InputActions.TwistLeft;
        var held = Mathf.RoundToInt(Mathf.Abs(turn) / Surgeon.TwistSpeed * Engine.PhysicsTicksPerSecond);
        if (held > 0)
        {
            Press(key);
            await Frames.Physics(held);
            Release(key);
        }
        await Frames.Physics(5);
    }

    /// <summary>Walks over only when <paramref name="point"/> is out of the active hand's comfortable reach.</summary>
    private async Task WithinReach(Vector3 point)
    {
        var hand = Me.Hands[Me.Active];
        var grip = Me.HeldTool(Me.Active) is { } tool ? point - hand.TipOffset(tool.Def.Length) : point;
        if (grip.DistanceTo(Me.Shoulder(Me.Active)) > Surgeon.Reach - 0.12f)
        {
            await PlayerWalksTo(point);
        }
    }

    /// <summary>Turns the view with the mouse until the middle of the screen is on <paramref name="point"/>.</summary>
    public async Task PlayerLooksAt(Vector3 point)
    {
        for (var i = 0; i < 3; i++)
        {
            var to = point - Me.Camera.GlobalPosition;
            var yaw = Mathf.Atan2(-to.X, -to.Z);
            var pitch = Mathf.Atan2(to.Y, new Vector2(to.X, to.Z).Length());
            var turn = new Vector2(Mathf.Wrap(Me.Rotation.Y - yaw, -Mathf.Pi, Mathf.Pi), Me.Pitch - pitch);
            PlayerInput.Mouse(turn / (Surgeon.LookSensitivity * Settings.MouseSensitivity));
            await PlayerInput.Delivered();
        }
        Note($"looks at {point}");
    }

    /// <summary>Interacts with the room station whose prompt is <paramref name="prompt"/>, standing in front of it.
    /// </summary>
    public async Task<bool> PlayerInteracts(string prompt)
    {
        var station = Surgery.Room.FindChildren("*", "", true, false)
            .OfType<Interactable>()
            .FirstOrDefault(node => node.Prompt == prompt && node.OfferedTo(Me));
        if (station is null)
        {
            Note($"no station '{prompt}' on offer");
            return false;
        }
        await PlayerWalksTo(station.GlobalPosition, 0.6f);
        station.Interact(Me);
        await Frames.Physics(5);
        Note($"interacts: {prompt}");
        return true;
    }
}
