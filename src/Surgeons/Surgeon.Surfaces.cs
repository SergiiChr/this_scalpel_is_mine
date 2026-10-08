namespace Scalpel.Surgeons;

/// <summary>What's under a point: how high (negative infinity for nothing), whether it's an opened incision (then the
/// height is what's inside) and whether it gives like skin.</summary>
public readonly record struct Surface(float Y, bool Open, bool Soft)
{
    public static readonly Surface None = new(float.NegativeInfinity, false, false);

    public bool Found => !float.IsNegativeInfinity(Y);
}

/// <summary>Hands resting on what's under them, and syringes snapping into vials and the IV bag.</summary>
public partial class Surgeon
{
    /// <summary>A syringe whose tip comes this close (meters, across the floor) to the middle of the IV bag on the stand
    /// snaps its needle into it.</summary>
    private const float DripSnap = 0.12f;
    /// <summary>The same for a vial's cap.</summary>
    private const float VialSnap = 0.03f;
    /// <summary>A vial's cap faces a surgeon when it points up this much (standing, the share of straight up), or
    /// lying, it points at them this much (the cosine of how far off it points across the floor).</summary>
    private const float VialUpright = 0.7f;
    private const float VialFacing = 0.5f;
    /// <summary>Furthest a syringe's tip may be above or below a vial's cap (meters) to snap into it.</summary>
    private const float VialAbove = 0.06f;
    /// <summary>Snapped, a syringe lets go only this much further out (see SnapSpot()).</summary>
    private const float UnsnapMargin = 0.015f;
    /// <summary>Snapping in or out takes this long (seconds).</summary>
    private const float SnapTime = 0.25f;
    /// <summary>A syringe snapped into the IV bag points this far up (radians): the bag hangs high and the forearm rises
    /// to it, so level or lower the wrist would bend back.</summary>
    internal const float DripTilt = 0.5f;

    /// <summary>Hands whose syringe is snapped into a vial or the IV bag, or easing in or out.</summary>
    private readonly Dictionary<int, SnapState> _snaps = [];

    /// <summary>Where a syringe snaps to: its needle tip and the angles it's held at there.</summary>
    private readonly record struct SnapSpot(Vector3 At, float Tilt, float Turn);

    /// <summary>Where the hand's needle tip goes and how far (0..1, eased) the hand moves there from its own spot.
    /// </summary>
    private readonly record struct SnapPull(Vector3 At, float Weight);

    private sealed class SnapState
    {
        /// <summary>The hand's own tilt and turn, to give back after.</summary>
        public Vector2 Own;
        /// <summary>How far snapped (0..1).</summary>
        public float Weight;
        /// <summary>Still over what it snapped to.</summary>
        public bool On;
        /// <summary>The spot it eases toward; its At glides from one vial to the next.</summary>
        public SnapSpot Spot;
    }

    /// <summary>
    /// Where the active hand is working: its tool's tip, or just past the fingers when empty (what Grab reaches for).
    /// A tool hovering over something shows the spot right under its tip: Use tool lowers the hand straight down, so
    /// that's where the tip lands, whatever angle the tool is held at and wherever the eyes look from.
    /// </summary>
    public Vector3 AimPoint()
    {
        var hand = Hands[Active];
        var tool = HeldTool(Active);
        var tip = tool?.TipPosition() ?? hand.GlobalPosition + hand.TipOffset(0.05f);
        // A grip with a working angle (a needle holder) pitches about its tip as it's lowered: the tip is where it
        // works.
        if (tool is null || hand.Lowered || GripStyle.For(tool.Def.Grip).WorkTilt is not null)
        {
            return tip;
        }
        var under = SurfaceBelow(tip);
        if (IsNeedle(tool))
        {
            under = GloveBelow(hand, tip, under);
        }
        return Dropped(tip, under);
    }

    /// <summary><paramref name="point"/> moved straight down onto what's under it, tools lying there left out: where that
    /// point of a tool lands on the patient when it's lowered, for the aim marks to lie on. However far down that is: a
    /// mark past the edge of an arm lies on what's below it, not in the air.</summary>
    public Vector3 OnSurface(Vector3 point)
    {
        var under = SurfaceBelow(point, false);
        return under.Y < point.Y ? point with { Y = under.Y } : point;
    }

    /// <summary><paramref name="point"/> moved down to <paramref name="under"/> when that's at most
    /// <see cref="AimDrop"/> below it.</summary>
    private static Vector3 Dropped(Vector3 point, Surface under) =>
        under.Y < point.Y && point.Y - under.Y < AimDrop ? point with { Y = under.Y } : point;

    /// <summary>
    /// Rests the tool tip just above whatever is under it. Lowered onto skin, it touches and presses in by effort level.
    /// Then keeps the hand within reach: far out, or down at the floor while standing, it stops short in the air.
    /// </summary>
    private void Constrain(SurgeonHand hand)
    {
        var tool = HeldTool(hand.Index);
        var offset = tool is not null ? hand.TipOffset(tool.Def.Length) : new Vector3(0f, -0.03f, 0f);
        var from = Shoulder(hand.Index);
        var target = hand.Target;
        // A hand holding onto something keeps its height; Lift pulls it up (see LocalUpdate()).
        var surface = hand.Attached ? Surface.None : SurfaceBelow(target + offset);
        if (IsNeedle(tool) && !hand.Attached)
        {
            surface = GloveBelow(hand, target + offset, surface);
        }
        hand.OnHard = false;
        // A needle hovers right over what's under it, so its tip is on the aim, not a centimeter above it.
        var hover = IsNeedle(tool) ? NeedleHover : HoverGap;
        if (surface.Found)
        {
            if (surface.Open)
            {
                // A lowered blade goes into an opening as deep as its level: onto what's inside only at full effort. Any
                // other tool lowered comes down onto what's inside (a saw onto the bone), like onto anything hard.
                var gap = !hand.Lowered || tool is null ? hover
                    : tool.Def.Action == "cut" ? BladeInOpening[hand.Level]
                    : 0.001f;
                target.Y = surface.Y + gap - offset.Y;
            }
            else if (hand.Lowered && surface.Soft)
            {
                // Skin gives: a lowered tip presses into it, deeper with effort.
                target.Y = surface.Y - 0.002f - (hand.Level * 0.004f) - offset.Y;
            }
            else if (surface.Soft && IsNeedle(tool))
            {
                // A needle over skin (or a glove) rests by its tip alone: its box reaches below the thin needle and would
                // hold the tip well off the aim.
                target.Y = surface.Y + hover - offset.Y;
            }
            else if (tool is not null)
            {
                hand.OnHard = true;
                target.Y = ClearOfHard(hand, tool, target, surface, hand.Lowered ? 0.001f : hover);
            }
            else
            {
                target.Y = surface.Y + HoverGap - offset.Y;
            }
            // Too far down to reach (the floor while standing): carry the hand instead of stretching for it.
            if (target.Y < from.Y - (Reach * 0.9f))
            {
                target.Y = GlobalPosition.Y + CarryHeight - (Crouch * CrouchDrop);
            }
            // The hand and the end of the forearm stay out of whatever is under them (a leg, the table edge): the hand
            // rises instead, lifting the tool tip off if it has to.
            foreach (var point in (Vector3[])[target, from.Lerp(target, 0.75f)])
            {
                var under = SurfaceBelow(point);
                if (under.Found && !under.Open)
                {
                    target.Y += Mathf.Max(under.Y + HandClearance - point.Y, 0f);
                }
            }
            // Fingers wrapped round a tool can reach below the hand's middle: they stay out of hard surfaces too.
            var underHand = SurfaceBelow(target);
            if (underHand.Found && !underHand.Open && !underHand.Soft)
            {
                target.Y += Mathf.Max(underHand.Y + 0.002f - (target.Y + hand.GloveDrop), 0f);
            }
        }
        hand.Target = InReach(target, from);
        if (!hand.Attached)
        {
            hand.LocalTarget = ToLocal(hand.Target);
        }
    }

    /// <summary>The hand's height over something hard (a tray, the table, a tool lying there), which doesn't give: every
    /// corner of the tool clears whatever is under that corner, not only its tip.</summary>
    private float ClearOfHard(SurgeonHand hand, SurgicalTool tool, Vector3 target, Surface surface, float gap)
    {
        var basis = hand.GripTransform().Basis;
        var needed = surface.Y + gap - LowestPoint(hand, tool);
        for (var i = 0; i < 8; i++)
        {
            var corner = basis * tool.Bounds.GetEndpoint(i);
            var under = SurfaceBelow(target + corner);
            if (under.Found && !under.Open)
            {
                needed = Mathf.Max(needed, under.Y + gap - corner.Y);
            }
        }
        return needed;
    }

    /// <summary>How far from the hand its tool's tip is at the hand's own angles, as if nothing snapped it anywhere
    /// (EaseSnap()): where the mouse aims it.</summary>
    public Vector3 OwnTipOffset(int hand)
    {
        var own = _snaps.GetValueOrDefault(hand)?.Own ?? new Vector2(Hands[hand].Tilt, Hands[hand].Turn);
        return Hands[hand].TipOffsetAt(HeldTool(hand)?.Def.Length ?? 0.05f, own.X, own.Y);
    }

    /// <summary>
    /// Eases a hand's syringe into the vial or IV bag it's over (SnapSpot()), or back out: the hand turns the syringe
    /// from its own angles toward the snapped ones as it goes. Null when it isn't snapped at all. Let go of, the hand
    /// gets its angles back at once.
    /// </summary>
    private SnapPull? EaseSnap(SurgeonHand hand, SurgicalTool? tool, float delta)
    {
        var state = _snaps.GetValueOrDefault(hand.Index);
        var own = state?.Own ?? new Vector2(hand.Tilt, hand.Turn);
        var spot = tool is null ? null : FindSnapSpot(hand, tool, own, state?.On ?? false);
        var weight = Mathf.MoveToward(state?.Weight ?? 0f, spot is null ? 0f : 1f, delta / SnapTime);
        if (tool is null || weight <= 0f)
        {
            if (state is not null)
            {
                hand.Tilt = own.X;
                hand.Turn = own.Y;
                _snaps.Remove(hand.Index);
            }
            return null;
        }
        if (state is null)
        {
            state = new SnapState { Spot = spot!.Value };
            _snaps[hand.Index] = state;
        }
        if (spot is { } found)
        {
            // From one vial straight to the next, the needle glides over rather than jumps.
            state.Spot = found with { At = state.Spot.At.Lerp(found.At, Mathf.Min(delta / SnapTime, 1f)) };
        }
        state.Own = own;
        state.Weight = weight;
        state.On = spot is not null;
        var eased = Mathf.SmoothStep(0f, 1f, weight);
        hand.Tilt = Mathf.Lerp(own.X, state.Spot.Tilt, eased);
        hand.Turn = Mathf.LerpAngle(own.Y, state.Spot.Turn, eased);
        return new SnapPull(state.Spot.At, eased);
    }

    /// <summary>
    /// Where a syringe held at its own angles (<paramref name="own"/>: tilt, turn) snaps to, before Use tool is pressed
    /// and after, or null when it's over nothing to snap to (or that's out of reach).
    /// Its tip over a vial's cap (within <see cref="VialSnap"/> across the floor), the needle goes in through it along
    /// the vial, if the cap faces this surgeon: up, or lying, toward them.
    /// Over the IV bag (within <see cref="DripSnap"/> of its middle), it goes into the middle of the bag's face on the
    /// hand's side, a little upward (<see cref="DripTilt"/>).
    /// Already snapped (<paramref name="held"/>), it lets go only <see cref="UnsnapMargin"/> further out, so passing over
    /// doesn't hold it for long.
    /// </summary>
    private SnapSpot? FindSnapSpot(SurgeonHand hand, SurgicalTool tool, Vector2 own, bool held)
    {
        if (!IsSyringe(tool) || hand.Attached)
        {
            return null;
        }
        var tip = hand.Target + OwnTipOffset(hand.Index);
        var margin = held ? UnsnapMargin : 0f;
        var spot = VialSpot(tip, own, VialSnap + margin) ?? BagSpot(hand, tip, DripSnap + margin);
        if (spot is not { } found)
        {
            return null;
        }
        var handThere = found.At - hand.TipOffsetAt(tool.Def.Length, found.Tilt, found.Turn);
        return handThere.DistanceTo(Shoulder(hand.Index)) > Reach ? null : found;
    }

    /// <summary>The nearest free vial's cap within <paramref name="nearest"/> of the tip across the floor that faces this
    /// surgeon: in through the cap at the vial's tip, along the vial. Down into one standing, level into one lying with
    /// its cap toward this surgeon. A cap facing down or away can't be lined up from here.</summary>
    private SnapSpot? VialSpot(Vector3 tip, Vector2 own, float nearest)
    {
        SnapSpot? spot = null;
        foreach (var vial in Session.Tools.Tools.Values)
        {
            if (vial.Def.Action != "vial" || vial.State != ToolState.Free)
            {
                continue;
            }
            var cap = vial.TipPosition();
            var into = (vial.GlobalPosition - cap).Normalized();
            var across = new Vector2(tip.X - cap.X, tip.Z - cap.Z).Length();
            var toward = new Vector2(GlobalPosition.X - cap.X, GlobalPosition.Z - cap.Z).Normalized();
            var facing = -into.Y > VialUpright || new Vector2(-into.X, -into.Z).Normalized().Dot(toward) > VialFacing;
            // Carried over it higher up (lifted, or at chest height), it isn't pulled down onto it.
            if (across >= nearest || !facing || Mathf.Abs(tip.Y - cap.Y) > VialAbove)
            {
                continue;
            }
            var turn = new Vector2(into.X, into.Z).Length() > 0.1f
                ? Mathf.Wrap(Mathf.Atan2(-into.X, -into.Z) - Rotation.Y, -Mathf.Pi, Mathf.Pi)
                : own.Y;
            // Lined up past where the wrist turns, it can't be.
            if (Mathf.Abs(turn) > SurgeonHand.TurnRange)
            {
                continue;
            }
            nearest = across;
            spot = new SnapSpot(cap + (into * 0.004f), Mathf.Asin(Mathf.Clamp(into.Y, -1f, 1f)), turn);
        }
        return spot;
    }

    /// <summary>The middle of the IV bag's face on the hand's side, when the tip is within <paramref name="reach"/> of it
    /// across the floor.</summary>
    private SnapSpot? BagSpot(SurgeonHand hand, Vector3 tip, float reach)
    {
        if (Session.Tools.DripBag() is not { } bag)
        {
            return null;
        }
        var port = bag.Middle();
        if (new Vector2(tip.X - port.X, tip.Z - port.Z).Length() >= reach)
        {
            return null;
        }
        // The bag hangs along its length: its thinnest side across is the way through its faces.
        var face = (bag.Bounds.Size.X < bag.Bounds.Size.Y ? bag.GlobalBasis.X : bag.GlobalBasis.Y) * new Vector3(1f, 0f, 1f);
        face *= Mathf.Sign(face.Dot(hand.Target - port));
        var turn = Mathf.Clamp(
            Mathf.Wrap(Mathf.Atan2(face.X, face.Z) - Rotation.Y, -Mathf.Pi, Mathf.Pi),
            -SurgeonHand.TurnRange, SurgeonHand.TurnRange);
        return new SnapSpot(port, DripTilt, turn);
    }

    /// <summary>How far below the hand the lowest point of its tool is (negative: below), the way the hand holds it now.
    /// </summary>
    private static float LowestPoint(SurgeonHand hand, SurgicalTool tool)
    {
        var basis = hand.GripTransform().Basis;
        var lowest = float.PositiveInfinity;
        for (var i = 0; i < 8; i++)
        {
            lowest = Mathf.Min(lowest, (basis * tool.Bounds.GetEndpoint(i)).Y);
        }
        return lowest;
    }

    /// <summary>
    /// What's under <paramref name="point"/>. Over an opening it finds what's inside: organs and targets, or the cavity
    /// floor. Rests on the patient's real skin (<see cref="PatientBody.SurfaceLayer"/>), the table, trays, tools lying
    /// there (unless not <paramref name="tools"/>) and the floor.
    /// </summary>
    internal Surface SurfaceBelow(Vector3 point, bool tools = true)
    {
        var space = GetWorld3D().DirectSpaceState;
        var query = PhysicsRayQueryParameters3D.Create(point + (Vector3.Up * 0.35f), point + (Vector3.Down * 2f), 4);
        var body = Session.Patient.Body;
        // An opening is looked for on the site plane first: the skin mesh around it would hide it from above.
        var siteHit = space.IntersectRay(query);
        var overSite = siteHit.Count > 0;
        if (overSite)
        {
            var sitePosition = siteHit["position"].AsVector3();
            if (body.IsOpen(body.WorldToUv(sitePosition)))
            {
                query.CollisionMask = PatientBody.CavityLayer | CavityTarget.TouchLayer;
                var inside = space.IntersectRay(query);
                var y = inside.Count > 0 ? inside["position"].AsVector3().Y : sitePosition.Y - 0.1f;
                return new Surface(y, true, true);
            }
        }
        // Tools lying about count too: set down on top of one, not into it (the two would be shoved apart, through the
        // tray).
        const uint SoftLayers = 4 | PatientBody.SurfaceLayer | Drape.DrapeLayer;
        query.CollisionMask = 1 | SoftLayers | (tools ? SurgicalTool.ToolLayer : 0);
        var hit = space.IntersectRay(query);
        if (hit.Count == 0)
        {
            return Surface.None;
        }
        var collider = (CollisionObject3D)hit["collider"].AsGodotObject();
        var position = hit["position"].AsVector3();
        var layer = collider.CollisionLayer;
        // The body's collider is its rest shape: skin lifted by a grip lies above it. The site's own collider is a flat
        // plane over the site, above skin that curves away under it (a belly), and the gown's collider isn't cut away
        // over the site: there the skin as drawn now is what's touched.
        if (overSite)
        {
            var uv = body.WorldToUv(point);
            var local = body.Site.ToLocal(point);
            var skin = body.Site.ToGlobal(local with { Y = body.SkinHeight(uv) });
            var onSiteCollider = collider == body.SiteCollider || (layer & PatientBody.SurfaceLayer) != 0;
            if (skin.Y > position.Y || (onSiteCollider && body.OnBody(uv)))
            {
                return new Surface(skin.Y, false, true);
            }
        }
        return new Surface(position.Y, false, (layer & SoftLayers) != 0);
    }

    /// <summary>A needle over a glove (this surgeon's other hand or anyone's) rests on the back of it like on skin, from
    /// the wrist to the fingertips, so it can go in.</summary>
    private static Surface GloveBelow(SurgeonHand hand, Vector3 point, Surface surface)
    {
        foreach (var glove in Session.Surgeons.Values.SelectMany(other => other.Hands))
        {
            var middle = glove.GloveMiddle(point);
            var top = middle.Y + SurgeonHand.PalmHalfThickness + 0.001f;
            var across = (middle - point) * new Vector3(1f, 0f, 1f);
            if (glove != hand && across.Length() < GloveReach && top > surface.Y)
            {
                return new Surface(top, false, true);
            }
        }
        return surface;
    }
}
