namespace Scalpel.Surgeons;

/// <summary>Local control: the player's input moves the body, the hands and the tools they hold.</summary>
public partial class Surgeon
{
    /// <summary>Zoomed all the way in with a syringe whose needle is in, the camera looks at it from the side and this
    /// far above (radians), with this much room around the syringe and its target (meters) (see FrameNeedle()).
    /// </summary>
    private const float NeedleViewElevation = 0.6f;
    private const float NeedleViewMargin = 0.025f;
    /// <summary>Zoomed all the way in, whatever they hold, the hands are this see-through.</summary>
    internal const float ZoomSeeThrough = 0.65f;
    /// <summary>A syringe's needle in the patient (Use tool held) keeps its tip where it went in: the mouse only tilts
    /// the syringe about it. A pull it can't follow (sideways, or past how far the hand tilts) stretches the skin by this
    /// share of the motion.</summary>
    private const float NeedleDrag = 0.2f;
    /// <summary>Stretched this far (meters) the needle tears out and leaves a small wound.</summary>
    private const float NeedleTear = 0.015f;
    /// <summary>Seconds Use tool is held before the needle counts as in: the hand comes down onto the skin first.
    /// </summary>
    private const float NeedleSettle = 0.1f;
    /// <summary>How a syringe picked up is held (radians): tilted this far down and turned this far in toward the body's
    /// middle.</summary>
    internal const float SyringeTilt = -0.6f;
    internal const float SyringeTurn = 0.4f;
    /// <summary>Seconds Grab is held on a bottle to stand it upright where it is instead of putting it down.</summary>
    internal const float StandHold = 1f;
    /// <summary>Fastest a syringe's hand rises or sinks to follow what's under it (m/s): it glides over a vial's edge,
    /// not hops.</summary>
    private const float SyringeGlide = 0.45f;

    /// <summary>How long Grab has been held on a bottle (seconds), null when it isn't: held <see cref="StandHold"/>, the
    /// bottle is stood upright.</summary>
    private float? _standHold;
    /// <summary>The zoom step before Use tool zoomed in on a syringe's needle, to go back to when it's let go (null: it
    /// didn't).</summary>
    private int? _zoomBefore;
    /// <summary>How far the hands have faded for the last zoom step (0..1).</summary>
    private float _needleFade;
    /// <summary>How far the camera has moved over to the needle view (0..1).</summary>
    private float _needleFraming;
    /// <summary>How far the camera has moved over to the needle view (0..1).</summary>
    internal float NeedleFraming => _needleFraming;
    /// <summary>The last needle view, to move back from.</summary>
    private Transform3D _needleView = Transform3D.Identity;
    /// <summary>The hand whose syringe the needle view rolls to show its scale to the camera (-1: none). Out of it, the
    /// syringe turns its scale back to the eyes (see UpdateHand()).</summary>
    private int _rolledHand = -1;
    /// <summary>Where the active hand's needle tip went into the patient (null: it isn't in).</summary>
    private Vector3? _needleAnchor;

    /// <summary>Where the active hand's needle went in, null while it isn't in.</summary>
    internal Vector3? NeedleAnchor => _needleAnchor;

    /// <summary>The needle tore out since Use tool was pressed.</summary>
    internal bool NeedleTorn => _needleTorn;
    /// <summary>How far the skin around a needle that's in is pulled.</summary>
    private Vector3 _needlePull;
    /// <summary>Whether the needle tore out since Use tool was pressed (it then moves freely until Use tool is let go).
    /// </summary>
    private bool _needleTorn;
    private float _needlePressed;
    /// <summary>The hand the needle view leaves solid: the one the needle is going into (-1: none).</summary>
    private int _solidHand = -1;
    /// <summary>Each hand's own tilt, turn and twist while it holds a syringe (see FaceSyringe()), to give back after.
    /// </summary>
    private readonly Dictionary<int, Vector3> _unfaced = [];
    /// <summary>Where the active hand's wrist is held (surgeon space) while Aim tool turns its tool about it.</summary>
    private Vector3? _aimWrist;
    /// <summary>Mouse moves held back by a sedative, oldest first.</summary>
    private readonly Queue<DelayedMove> _delayed = new();

    private readonly record struct DelayedMove(ulong DueMsec, int Hand, Vector2 Motion);

    /// <summary>Whether the tool has a needle the last zoom step frames: a syringe or the IV catheter.</summary>
    public static bool IsNeedle(SurgicalTool? tool) => tool is not null && NeedleActions.Contains(tool.Def.Action);

    private static bool IsSyringe(SurgicalTool? tool) => tool?.Def.Action == "syringe";

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!IsLocal || InputLocked || Status.IsOut)
        {
            return;
        }
        var hand = Hands[Active];
        // Headless there's no mouse to capture (Hud.CaptureMouse()): the only motion is what a test sends.
        if (@event is InputEventMouseMotion mouse)
        {
            if (Input.MouseMode == Input.MouseModeEnum.Captured || DisplayServer.GetName() == "headless")
            {
                OnMouseMotion(mouse.Relative * Settings.MouseSensitivity);
            }
        }
        else if (@event.IsActionPressed(InputActions.MoveLeftHand) || @event.IsActionPressed(InputActions.MoveRightHand))
        {
            var index = @event.IsActionPressed(InputActions.MoveLeftHand) ? 0 : 1;
            if (index != Active)
            {
                SwitchHand();
            }
        }
        else if (@event.IsActionPressed(InputActions.LevelUp) || @event.IsActionPressed(InputActions.LevelDown))
        {
            OnWheel(@event.IsActionPressed(InputActions.LevelUp));
        }
        else if (@event.IsActionPressed(InputActions.Zoom))
        {
            Zoom = (Zoom + 1) % ZoomFov.Length;
        }
        else if (@event.IsActionPressed(InputActions.UseTool))
        {
            // One button lowers the tool and fires its single action (a clamp pinches, the defibrillator charges).
            SetLowered(hand, true);
            hand.Trigger = true;
            if (IsSyringe(HeldTool(Active)) && _zoomBefore is null)
            {
                // Putting a needle in zooms all the way in until Use tool is let go (the needle view, see
                // FrameNeedle()).
                _zoomBefore = Zoom;
                Zoom = ZoomFov.Length - 1;
            }
        }
        else if (@event.IsActionReleased(InputActions.UseTool))
        {
            if (_needleAnchor is { } anchor)
            {
                Session.Tools.RequestNeedleWithdrawal(Active, anchor);
                // Release the anchor before another mouse event can bend or pull the withdrawn needle.
                _needleAnchor = null;
            }
            SetLowered(hand, false);
            hand.Trigger = false;
            EndNeedleZoom();
        }
        else if (@event.IsActionPressed(InputActions.Grab))
        {
            if (HeldTool(Active) is { Def.Tray: "bottles" } && !hand.Attached)
            {
                // A bottle: let go quickly it's put down as anything is, held on it's stood upright (see
                // LocalUpdate()).
                _standHold = 0f;
            }
            else
            {
                GrabOrRelease();
            }
        }
        else if (@event.IsActionReleased(InputActions.Grab) && _standHold is not null)
        {
            _standHold = null;
            GrabOrRelease();
        }
        else if (@event.IsActionPressed(InputActions.Interact) && Focused is not null)
        {
            Focused.Interact(this);
        }
        else if (@event.IsActionPressed(InputActions.Drink))
        {
            Session.RequestDrink(Active);
        }
        else
        {
            for (var i = 0; i < MaxBelt; i++)
            {
                if (@event.IsActionPressed(InputActions.BeltSlot(i + 1)))
                {
                    Session.Tools.RequestBelt(Active, i);
                }
            }
        }
    }

    private void OnMouseMotion(Vector2 motion)
    {
        if (Input.IsActionPressed(InputActions.AimTool))
        {
            AimTool(motion);
        }
        else if (MovingHand() < 0)
        {
            Rotation = Rotation with { Y = Rotation.Y - (motion.X * LookSensitivity) };
            Pitch = Mathf.Clamp(Pitch - (motion.Y * LookSensitivity), LookPitch.X, LookPitch.Y);
        }
        else if (_switchTimer <= 0f)
        {
            // Sedated, the move reaches the hand late (see ApplyDelayed()).
            var delay = Status.InputDelay();
            if (delay > 0f)
            {
                _delayed.Enqueue(new DelayedMove(Time.GetTicksMsec() + (ulong)(delay * 1000f), Active, motion));
            }
            else
            {
                SteerHand(motion);
            }
        }
    }

    /// <summary>The wheel: a syringe's plunger, a needle holder's thread tension, a spreader's jaws or the effort level.
    /// </summary>
    private void OnWheel(bool up)
    {
        var tools = Session.Tools;
        switch (HeldTool(Active)?.Def.Action)
        {
            case "syringe":
                tools.RequestPlunger(Active, up ? -1 : 1);
                break;
            case "sew":
                // As on a syringe, the wheel works the tool itself: down pulls the thread tight, up pays more out.
                tools.RequestSutureTension(Active, up ? 1 : -1);
                break;
            case "spread":
                // Up opens the spreader, down closes it, set in a wound or not.
                tools.RequestSpread(Active, up ? 1 : -1);
                break;
            default:
                if (UsesLevel(Active))
                {
                    Hands[Active].Level = Math.Clamp(Hands[Active].Level + (up ? 1 : -1), 0, 3);
                }
                break;
        }
    }

    /// <summary>Back to the zoom step from before Use tool zoomed in on a syringe's needle, if it did.</summary>
    private void EndNeedleZoom()
    {
        if (_zoomBefore is { } before)
        {
            Zoom = before;
            _zoomBefore = null;
        }
    }

    /// <summary>
    /// Moves a hand (the active one unless <paramref name="index"/> says) by a mouse motion (pixels, sensitivity
    /// applied). Zoomed in, the same motion moves it as much less as the view is magnified, so it crosses the screen at
    /// the same speed: finer control where you're looking closely. A needle stuck in the patient tilts about its tip
    /// instead.
    /// </summary>
    private void SteerHand(Vector2 motion, int index = -1)
    {
        index = index < 0 ? Active : index;
        var magnified = Mathf.Tan(Mathf.DegToRad(ZoomFov[Zoom]) * 0.5f) / Mathf.Tan(Mathf.DegToRad(ZoomFov[0]) * 0.5f);
        var step = motion * HandSensitivity * Status.HandSpeed() * magnified;
        var move = _needleFraming > 0.5f
            ? ScreenToFloor(step)
            : new Basis(Vector3.Up, Rotation.Y) * new Vector3(step.X, 0f, step.Y);
        if (index == Active && _needleAnchor is not null)
        {
            BendNeedle(move);
        }
        else
        {
            MoveHand(Hands[index], move);
        }
    }

    /// <summary>A hand move (world, across the floor) with the needle stuck: along the syringe it tilts it about its tip,
    /// the hand swinging round it. What the tilt can't take (sideways, or past its range) pulls on the skin.</summary>
    private void BendNeedle(Vector3 move)
    {
        var hand = Hands[Active];
        var length = HeldTool(Active)!.Def.Length;
        var back = new Basis(Vector3.Up, Rotation.Y + hand.Turn) * Vector3.Back;
        var along = move.Dot(back);
        var tiltBefore = hand.Tilt;
        hand.Tilt = Mathf.Clamp(hand.Tilt + (along / length), SurgeonHand.TiltRange.X, SurgeonHand.TiltRange.Y);
        var unbent = along - ((hand.Tilt - tiltBefore) * length);
        _needlePull += (move - (back * along) + (back * unbent)) * NeedleDrag;
    }

    /// <summary>Changes a tool's working pose about its tip, so pressing it never moves the aim point away from the skin.
    /// Most grips have no working angle and need no correction; the horizontal needle grip pitches down while the hand
    /// stays clear.</summary>
    private void SetLowered(SurgeonHand hand, bool value)
    {
        if (hand.Lowered == value)
        {
            return;
        }
        var length = HeldTool(hand.Index)?.Def.Length;
        var before = length is { } l ? hand.TipOffset(l) : Vector3.Zero;
        hand.Lowered = value;
        var shift = length is { } after ? before - hand.TipOffset(after) : Vector3.Zero;
        // Only a grip that pitches as it's lowered moves the hand. The hand's own spot moves by as much, not to where
        // the hand is held now (a syringe snapped into a vial would lose its snap).
        if (!shift.IsZeroApprox())
        {
            hand.Target += shift;
            if (!hand.Attached)
            {
                hand.LocalTarget += GlobalBasis.Inverse() * shift;
            }
        }
    }

    /// <summary>
    /// Turns the active hand's tool by a mouse motion, bending the wrist: up and down pitches it, left and right swings
    /// it, the tip following the mouse. It turns about the wrist (see AimsFromWrist()); a needle stuck in the patient
    /// turns about its tip instead (see HoldNeedle()). Snapped into a vial or the bag, the snap holds the angles: the
    /// turn goes to the hand's own, for when it lets go.
    /// </summary>
    private void AimTool(Vector2 motion)
    {
        var hand = Hands[Active];
        // Where the wrist is before the first turn, to hold it there.
        if (_aimWrist is null && AimsFromWrist(hand, HeldTool(Active), true))
        {
            _aimWrist = WristNow(hand);
        }
        var snap = _snaps.GetValueOrDefault(Active);
        var own = snap?.Own ?? new Vector2(hand.Tilt, hand.Turn);
        own.X = Mathf.Clamp(own.X - (motion.Y * AimSensitivity), SurgeonHand.TiltRange.X, SurgeonHand.TiltRange.Y);
        own.Y = Mathf.Clamp(own.Y - (motion.X * AimSensitivity), -SurgeonHand.TurnRange, SurgeonHand.TurnRange);
        if (snap is null)
        {
            hand.Tilt = own.X;
            hand.Turn = own.Y;
        }
        else
        {
            snap.Own = own;
        }
    }

    /// <summary>
    /// Holding Aim tool, only the wrist moves: it stays where it was when Aim tool was pressed, and the hand and tool go
    /// wherever the tool's angle puts them. The tip rises off what it rested on, and settles back down once Aim tool is
    /// let go. A tool held still by what it's in (a needle, a spreader, a clamp's grip) turns about that instead.
    /// </summary>
    private bool AimsFromWrist(SurgeonHand hand, SurgicalTool? tool, bool canAct) =>
        canAct && hand.Index == Active && tool is { InWound: false } && !hand.Attached && !hand.Inspecting
        && !_snaps.ContainsKey(hand.Index) && _needleAnchor is null && Input.IsActionPressed(InputActions.AimTool);

    /// <summary>Where the hand's wrist is (surgeon space), without its tremor.</summary>
    private Vector3 WristNow(SurgeonHand hand) =>
        ToLocal(hand.Target + (Vector3.Up * hand.Raise) + hand.WristOffset());

    /// <summary>The hand the mouse moves right now (its key held), or -1 while the mouse looks around.</summary>
    public static int MovingHand() =>
        Input.IsActionPressed(InputActions.MoveLeftHand) ? 0
        : Input.IsActionPressed(InputActions.MoveRightHand) ? 1
        : -1;

    /// <summary>The wheel sets this hand's effort level: its tool takes one (<see cref="ToolActions.LevelNames"/>). It
    /// can be set before lowering the tool, so a blade goes in at the depth picked.</summary>
    public bool UsesLevel(int hand) => HeldTool(hand) is { } tool && ToolActions.LevelNames.ContainsKey(tool.Def.Action);

    /// <summary>
    /// Picked up, a syringe is held ready to inject: a little down and pointing in toward the body's middle, the hand off
    /// to its outer side, its printed scale toward the eyes so it doesn't need turning to be read (and kept there while
    /// held, see UpdateHand()). <paramref name="tool"/>: what the hand now holds.
    /// </summary>
    private void FaceSyringe(int hand, SurgicalTool? tool)
    {
        Unface(hand);
        if (!IsSyringe(tool))
        {
            return;
        }
        var h = Hands[hand];
        _unfaced[hand] = new Vector3(h.Tilt, h.Turn, h.Twist);
        h.Tilt = SyringeTilt;
        h.Turn = SyringeTurn * (hand == 1 ? 1f : -1f);
        h.Twist = h.TwistFacing(Camera.GlobalPosition - h.GlobalPosition);
        // Snapped already this frame (beside a vial), it eases in from these angles, not the last tool's.
        _snaps.Remove(hand);
    }

    /// <summary>A hand that let go of a syringe holds things the way it did before it took it.</summary>
    private void Unface(int hand)
    {
        if (!_unfaced.Remove(hand, out var own))
        {
            return;
        }
        Hands[hand].Tilt = own.X;
        Hands[hand].Turn = own.Y;
        Hands[hand].Twist = own.Z;
        // Snapped into a vial or the bag as it went, it has nothing to ease back from.
        _snaps.Remove(hand);
    }

    /// <summary>
    /// Zoomed all the way in, the hands fade so they don't hide what they work on, whatever they hold. The camera stays
    /// at the eyes, so the mouse moves the hand the way it always does while aiming. Once a syringe's needle is in
    /// something with Use tool held, the camera moves over beside it so the needle and what it's in (a vial, the dish,
    /// the bag, the arm's vein) are both in view, and the hand rolls the syringe so its printed scale faces the camera.
    /// Use tool let go, both go back.
    /// </summary>
    private void FrameNeedle(float delta)
    {
        var tool = HeldTool(Active);
        var zoomed = Zoom == ZoomFov.Length - 1 && !Hands[Active].Inspecting;
        var target = zoomed && IsNeedle(tool) ? Syringe.NeedleTarget(tool!, Session.Patient) : null;
        var framing = target is not null and not AirTarget && IsSyringe(tool) && Hands[Active].Lowered;
        var faded = _needleFade;
        _needleFade = Mathf.MoveToward(_needleFade, zoomed ? 1f : 0f, delta * 4f);
        _needleFraming = Mathf.MoveToward(_needleFraming, framing ? 1f : 0f, delta * 4f);
        // The other hand stays solid while the needle is in it.
        var solid = target is SurgeonTarget { Peer: var peer } && peer == PeerId ? 1 - Active : -1;
        if (!Mathf.IsEqualApprox(_needleFade, faded) || solid != _solidHand)
        {
            _solidHand = solid;
            foreach (var hand in Hands)
            {
                hand.SetSeeThrough(hand.Index == solid ? 0f : _needleFade * ZoomSeeThrough);
            }
        }
        _rolledHand = framing ? Active : -1;
        if (framing)
        {
            _needleView = NeedleView(tool!);
            var facing = Hands[Active].TwistFacing(_needleView.Origin - tool!.Middle());
            Hands[Active].Twist = Mathf.LerpAngle(Hands[Active].Twist, facing, Mathf.Min(delta * 8f, 1f));
        }
        if (_needleFraming <= 0f)
        {
            Camera.Transform = Transform3D.Identity;
            return;
        }
        Camera.GlobalTransform = _head.GlobalTransform.InterpolateWith(
            _needleView, Mathf.SmoothStep(0f, 1f, _needleFraming));
    }

    /// <summary>A mouse move as seen through the camera, laid flat on the floor: right moves right on screen, up moves
    /// away. The needle view looks from the side, so moving the hand the body's way would look sideways there.</summary>
    private Vector3 ScreenToFloor(Vector2 step)
    {
        var flat = new Vector3(1f, 0f, 1f);
        var right = Camera.GlobalBasis.X * flat;
        var away = Camera.GlobalBasis.Y * flat;
        if (away.Length() < 0.1f)
        {
            away = -Camera.GlobalBasis.Z * flat;
        }
        return (right.Normalized() * step.X) - (away.Normalized() * step.Y);
    }

    /// <summary>Where the camera looks at a syringe or catheter from: side on and a little above (square on to it,
    /// however it's tilted), from the side the eyes are on, far enough back that the whole tool and the vial, dish or bag
    /// its needle is in fit the view.</summary>
    public Transform3D NeedleView(SurgicalTool tool)
    {
        List<Vector3> points = [tool.GlobalPosition, tool.TipPosition()];
        switch (Syringe.NeedleTarget(tool, Session.Patient))
        {
            case ContainerTarget container:
                points.Add(container.Container.Middle());
                break;
            case SurgeonTarget { Part: "hand" } glove:
                points.Add(glove.At);
                break;
        }
        var center = points.Aggregate(Vector3.Zero, (sum, p) => sum + p) / points.Count;
        var radius = points.Max(p => p.DistanceTo(center));
        var along = (points[1] - points[0]) * new Vector3(1f, 0f, 1f);
        var side = along.Length() > 0.001f ? along.Cross(Vector3.Up).Normalized() : _head.GlobalBasis.Z;
        if (side.Dot(_head.GlobalPosition - center) < 0f)
        {
            side = -side;
        }
        var direction = (side * Mathf.Cos(NeedleViewElevation)) + (Vector3.Up * Mathf.Sin(NeedleViewElevation));
        // Square on to the syringe however it's tilted: one standing in a vial is seen from level, not from above.
        var axis = (points[1] - points[0]).Normalized();
        direction = (direction - (axis * direction.Dot(axis))).Normalized();
        var distance = (radius + NeedleViewMargin) / Mathf.Tan(Mathf.DegToRad(ZoomFov[Zoom]) * 0.5f);
        return new Transform3D(Basis.Identity, center + (direction * distance)).LookingAt(center, Vector3.Up);
    }

    private void LocalUpdate(float delta)
    {
        _switchTimer = Mathf.Max(_switchTimer - delta, 0f);
        var canAct = !InputLocked && !Status.IsOut;
        ApplyDelayed(canAct);
        if (Status.IsKnockedOut)
        {
            LieStill(delta);
            HandleStatusEvents(Status.Update(delta, CurrentStatusContext()));
            return;
        }
        Crouch = Mathf.MoveToward(Crouch, canAct && Input.IsActionPressed(InputActions.Crouch) ? 1f : 0f, delta * 4f);
        var direction = canAct
            ? Input.GetVector(InputActions.MoveLeft, InputActions.MoveRight, InputActions.MoveForward, InputActions.MoveBack)
            : Vector2.Zero;
        var speed = Mathf.Lerp(WalkSpeed, CrouchSpeed, Crouch) * Status.MoveSpeed();
        var move = GlobalBasis * new Vector3(direction.X, 0f, direction.Y) * speed;
        Velocity = new Vector3(move.X, IsOnFloor() ? 0f : Velocity.Y - (9.8f * delta), move.Z);
        MoveAndSlide();
        var hand = Hands[Active];
        if (canAct)
        {
            // A syringe keeps its scale to the eyes on its own (FaceSyringe()): it doesn't roll.
            if (!_unfaced.ContainsKey(Active))
            {
                var twist = Input.GetAxis(InputActions.TwistLeft, InputActions.TwistRight);
                hand.Twist = Mathf.Wrap(hand.Twist + (twist * delta * TwistSpeed), -Mathf.Pi, Mathf.Pi);
            }
            var lift = Input.IsActionPressed(InputActions.Lift);
            hand.Lifted = lift && !hand.Attached;
            if (hand.Attached && lift)
            {
                hand.Target += new Vector3(0f, PullSpeed * delta, 0f);
            }
            Status.HoldingBreath = Input.IsActionPressed(InputActions.Steady) && Status.Breath > 0f;
        }
        HoldNeedle(delta);
        if (!canAct)
        {
            // Locked, in a menu or out cold, the release of Grab or Use tool may never come: a tap mustn't stand a
            // bottle, and the zoom goes back.
            _standHold = null;
            EndNeedleZoom();
        }
        if (_standHold is { } held)
        {
            _standHold = held + delta;
            if (_standHold >= StandHold)
            {
                _standHold = null;
                hand.Lowered = false;
                hand.Trigger = false;
                Session.Tools.RequestStand(Active);
            }
        }
        for (var i = 0; i < 2; i++)
        {
            PlaceHand(i, canAct, delta);
        }
        _jolt = _jolt.Lerp(Vector3.Zero, Mathf.Min(delta * 8f, 1f));
        CheckBumps();
        UpdateFocus();
        UpdateHover();
        HandleStatusEvents(Status.Update(delta, CurrentStatusContext()));
    }

    /// <summary>Where a hand goes this frame: held by a needle, a wound or the eyes, aimed from the wrist, or resting
    /// on what's under it; then its tremor, shiver and afterimages.</summary>
    private void PlaceHand(int i, bool canAct, float delta)
    {
        var hand = Hands[i];
        var tool = HeldTool(i);
        hand.Inspecting = canAct && i == Active && tool is not null && !hand.Attached
            && Input.IsActionPressed(InputActions.Inspect);
        var aiming = AimsFromWrist(hand, tool, canAct);
        hand.Aiming = aiming;
        if (i == Active && !aiming)
        {
            _aimWrist = null;
        }
        if (hand.Lowered || !aiming)
        {
            // Use tool brings it down onto its spot, no faster than letting go of Aim tool does.
            hand.Raise = Mathf.MoveToward(hand.Raise, 0f, delta * SurgeonHand.SettleSpeed);
        }
        if (tool?.Def.Action == "spread")
        {
            // Held tipped toward the skin, its points down, a spreader's jaws open flat across it (C/V turn them).
            hand.Tilt = SurgeonHand.SpreaderTilt;
        }
        if (i == Active && _needleAnchor is { } anchor && !hand.Inspecting)
        {
            // The tip stays where it went in, steady and unlifted: the hand goes wherever the tilt puts it.
            hand.Lifted = false;
            hand.Raise = 0f;
            hand.Target = anchor - hand.TipOffset(tool!.Def.Length);
            hand.LocalTarget = ToLocal(hand.Target);
            hand.Tremor = Vector3.Zero;
            _strain[i] = false;
            return;
        }
        if (tool is { InWound: true } && !hand.Inspecting)
        {
            HoldInWound(hand, tool);
            return;
        }
        if (hand.Inspecting)
        {
            // Held up in front of the eyes, the grip off to the hand's side so the whole tool crosses the view.
            hand.Lowered = false;
            hand.Trigger = false;
            hand.Target = Camera.GlobalTransform
                * new Vector3(Side(i) * tool!.Def.Length * 0.5f, -0.04f, -InspectDistance);
            _strain[i] = false;
            return;
        }
        if (!hand.Attached)
        {
            hand.Target = ToGlobal(hand.LocalTarget);
        }
        if (aiming)
        {
            _aimWrist ??= WristNow(hand);
            hand.Target = ToGlobal(_aimWrist.Value) - hand.WristOffset();
        }
        var heldAt = hand.Target.Y;
        _strain[i] = hand.Attached && hand.Target.DistanceTo(Shoulder(i)) > Reach + 0.06f;
        var was = hand.Target.Y;
        var snap = EaseSnap(hand, tool, delta);
        Constrain(hand);
        // Over a tray, a vial or the table, a syringe glides up and down rather than hops. Onto the patient or a glove
        // it rises at once: rising slowly, the needle would sit under the skin, where the wheel injects.
        if (IsSyringe(tool)
            && (hand.Target.Y < was || !SurfaceBelow(hand.Target + hand.TipOffset(tool!.Def.Length)).Soft))
        {
            hand.Target = hand.Target with { Y = Mathf.MoveToward(was, hand.Target.Y, SyringeGlide * delta) };
            if (!hand.Attached)
            {
                hand.LocalTarget = ToLocal(hand.Target);
            }
        }
        if (snap is { } eased)
        {
            // The tip goes straight from where the hand's own way of holding it would put it to the snapped spot, the
            // hand turning round it: turned first, the tip would swing wide. The hand's own spot (LocalTarget) stays
            // where the mouse put it, so moving on from there pulls it out again.
            var tipOffset = hand.TipOffset(tool!.Def.Length);
            var ownTip = ToGlobal(hand.LocalTarget) + OwnTipOffset(i);
            var freeTip = ownTip with { Y = hand.Target.Y + tipOffset.Y };
            hand.Target = freeTip.Lerp(eased.At, eased.Weight) - tipOffset;
        }
        if (aiming && !hand.Lowered)
        {
            // The tip rises off what it rested on rather than the wrist coming down after it. Into it, the hand rises.
            hand.Raise = Mathf.Max(heldAt - hand.Target.Y, 0f);
        }
        var amount = i == Active || Mods.Mult("switch_delay_mult") > 0f ? Status.TremorAmount() : 0f;
        var t = Time.GetTicksMsec() * 0.001f;
        hand.Tremor = (new Vector3(Mathf.Sin((t * 23f) + i), Mathf.Sin((t * 31f) + (2f * i)), Mathf.Cos((t * 19f) + i))
            * amount) + _jolt;
        hand.Shiver = new Vector3(
            Mathf.Sin((t * 41f) + (3f * i)), Mathf.Sin((t * 37f) + i), Mathf.Cos((t * 43f) + (2f * i))) * Status.Shiver();
        hand.Trail = Status.Calm;
    }

    /// <summary>A syringe pressed into the patient sticks where its tip went in. Pulled on too hard, or walked away from
    /// out of reach, it tears out.</summary>
    private void HoldNeedle(float delta)
    {
        var hand = Hands[Active];
        var tool = HeldTool(Active);
        if (!hand.Lowered || !IsSyringe(tool))
        {
            _needleTorn = false;
            _needleAnchor = null;
            _needlePressed = 0f;
            return;
        }
        if (_needleAnchor is not { } anchor)
        {
            _needlePressed += delta;
            // Once in, it stays in until Use tool is let go: breathing lifting the skin doesn't free it.
            if (_needlePressed >= NeedleSettle && !_needleTorn
                && Syringe.NeedleTarget(tool!, Session.Patient) is VeinTarget or TissueTarget)
            {
                _needleAnchor = tool!.TipPosition();
                _needlePull = Vector3.Zero;
            }
            return;
        }
        var outOfReach = (anchor - hand.TipOffset(tool!.Def.Length)).DistanceTo(Shoulder(Active)) > Reach;
        if (_needlePull.Length() > NeedleTear || outOfReach)
        {
            var pulled = _needlePull.Length() > 0f ? _needlePull.Normalized() * NeedleTear : Vector3.Zero;
            Session.Tools.RequestNeedleTear(Active, anchor, anchor + pulled);
            Session.Hud.Toast("The needle tears out of the skin.");
            _needleTorn = true;
            _needleAnchor = null;
        }
    }

    /// <summary>A spreader set in a wound stays where it went in, steady, and the hand holding it goes to it, as far as
    /// the arm turns that way. Walked away from out of reach, the host leaves it standing in the wound
    /// (Surgery.Overstretched()).</summary>
    private void HoldInWound(SurgeonHand hand, SurgicalTool tool)
    {
        var angles = tool.GlobalBasis.GetEuler(EulerOrder.Yxz);
        // A spreader's twist swings it (SurgeonHand.Spreads): what the arm doesn't turn, the twist does.
        var yaw = Mathf.Wrap(angles.Y - GlobalRotation.Y, -Mathf.Pi, Mathf.Pi);
        hand.Turn = Mathf.Clamp(yaw, -SurgeonHand.TurnRange, SurgeonHand.TurnRange);
        hand.Tilt = Mathf.Clamp(angles.X, SurgeonHand.TiltRange.X, SurgeonHand.TiltRange.Y);
        hand.Twist = yaw - hand.Turn;
        hand.Lifted = false;
        hand.Tremor = Vector3.Zero;
        var shoulder = Shoulder(hand.Index);
        _strain[hand.Index] = tool.GlobalPosition.DistanceTo(shoulder) > Reach + 0.06f;
        hand.Target = InReach(tool.GlobalPosition, shoulder);
        hand.LocalTarget = ToLocal(hand.Target);
    }

    /// <summary>Mouse moves a sedative held back, once they're due. Out cold or locked, they're dropped.</summary>
    private void ApplyDelayed(bool canAct)
    {
        var now = Time.GetTicksMsec();
        while (_delayed.TryPeek(out var move) && (!canAct || move.DueMsec <= now))
        {
            _delayed.Dequeue();
            if (canAct)
            {
                SteerHand(move.Motion, move.Hand);
            }
        }
    }

    /// <summary>Moves the hand by <paramref name="step"/> (world space), within reach. A free hand moves on from its own
    /// spot, not from where something holds it for now (a needle snapped into the IV bag, a tool held up to look at).
    /// </summary>
    private void MoveHand(SurgeonHand hand, Vector3 step)
    {
        var from = hand.Attached ? hand.Target : ToGlobal(hand.LocalTarget);
        hand.Target = InReach(from + step, Shoulder(hand.Index));
        if (!hand.Attached)
        {
            hand.LocalTarget = ToLocal(hand.Target);
        }
    }

    /// <summary><paramref name="point"/> kept between <see cref="MinReach"/> and <see cref="Reach"/> from the shoulder at
    /// <paramref name="from"/>.</summary>
    private Vector3 InReach(Vector3 point, Vector3 from)
    {
        var outward = point - from;
        if (outward.Length() < 0.001f)
        {
            outward = -GlobalBasis.Z * 0.001f;
        }
        return from + (outward.Normalized() * Mathf.Clamp(outward.Length(), MinReach, Reach));
    }

    /// <summary>The tool the active hand would pick up: the free tool nearest the hand's tip, highlighted with its name
    /// shown.</summary>
    private void UpdateHover()
    {
        var hand = Hands[Active];
        var near = HeldTool(Active) is null && !InputLocked
            ? Session.Tools.NearestGrabbable(hand.GlobalPosition + hand.TipOffset(0.05f))
            : null;
        if (near == Hovered)
        {
            return;
        }
        if (IsInstanceValid(Hovered))
        {
            Hovered!.SetHighlight(false);
        }
        near?.SetHighlight(true);
        Hovered = near;
    }

    private void SwitchHand()
    {
        Active = 1 - Active;
        _standHold = null;
        _needleAnchor = null;
        _aimWrist = null;
        _switchTimer = SwitchDelay * Mods.Mult("switch_delay_mult");
    }

    private void GrabOrRelease()
    {
        var hand = Hands[Active];
        var tools = Session.Tools;
        if (HeldTool(Active) is not null)
        {
            hand.Lowered = false;
            hand.Trigger = false;
            if (PassTarget(Active) is not null && !hand.Attached)
            {
                tools.RequestPass(Active);
            }
            else
            {
                tools.RequestRelease(Active, Vector3.Zero);
            }
            return;
        }
        if (tools.NearestGrabbable(hand.GlobalPosition + hand.TipOffset(0.05f)) is not { } near)
        {
            return;
        }
        if (Mods.Flag("contamination_vision") && !near.Sterile)
        {
            Status.AddStress(Mods.Num("dirty_stress"));
        }
        tools.RequestGrab(near, Active);
    }

    /// <summary>Hands that aren't lifted knock into other surgeons' hands.</summary>
    private void CheckBumps()
    {
        var mine = Hands[Active];
        var tool = HeldTool(Active);
        // A needle held into a partner's hand or body is close on purpose.
        if (mine.Lifted || (IsSyringe(tool) && Syringe.NeedleTarget(tool!, Session.Patient) is SurgeonTarget))
        {
            return;
        }
        foreach (var other in Session.Surgeons.Values.Where(other => other != this))
        {
            foreach (var theirs in other.Hands.Where(hand => !hand.Lifted))
            {
                var gap = mine.GlobalPosition - theirs.GlobalPosition;
                if (gap.Length() >= BumpDistance)
                {
                    continue;
                }
                mine.Target += gap.Normalized() * 0.05f;
                if (!mine.Attached)
                {
                    mine.LocalTarget = ToLocal(mine.Target);
                }
                Jolt(0.35f);
                Sfx.Play("bump", mine.GlobalPosition);
                return;
            }
        }
    }

    private void UpdateFocus()
    {
        var from = Camera.GlobalPosition;
        var focused = Rays.Cast(this, from, from - (Camera.GlobalBasis.Z * InteractRange), Interactable.Layer, areas: true)
            ?.Collider as Interactable;
        Focused = focused is not null && focused.OfferedTo(this) ? focused : null;
    }
}
