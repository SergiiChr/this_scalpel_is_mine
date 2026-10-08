namespace Scalpel.Patients;

/// <summary>What tools take hold of: skin, targets, vessels, organs (forceps, clamps, retractors, spreaders).</summary>
public partial class Patient
{
    /// <summary>
    /// Called when a clamp tool closes at the tip. Returns what it took hold of, for <see cref="UpdateGrip"/> and
    /// <see cref="ReleaseGrip"/>; null when nothing. <paramref name="skinOnly"/>: a hook (the retractor) takes hold of
    /// skin and nothing under it: the skin it's pressed onto, or pressed into an opening, the edge of the cut on that
    /// side. It pulls the skin aside, never down into the opening.
    /// </summary>
    public ToolHold? Grip(int toolUid, SiteZone zone, Vector2 uv, float depthM, bool skinOnly = false)
    {
        var tissue = Body.Tissue;
        if (skinOnly && zone == SiteZone.Cavity)
        {
            if (NearestWound(uv, 0.03f, false) is not { } cut)
            {
                return null;
            }
            var line = cut.ClosestPoint(uv);
            if (tissue.GripBeside(toolUid, uv, line, uv - line) < 0)
            {
                tissue.Grip(toolUid, uv);
            }
            return new SkinHold(cut.Id, uv, Hook: true);
        }
        // The nearest target the jaws close on, not one another tool already holds (two broken ends lie close
        // together).
        var nearest = skinOnly ? null : Targets
            .Where(t => !t.Extracted && !t.IsSuctionTarget && !(t.IsBone && t.Anchor > 0f))
            .Where(t => t.GrippedBy == 0 || t.GrippedBy == toolUid)
            .Where(t => zone is SiteZone.Cavity or SiteZone.Site && t.Uv.DistanceTo(uv) < 0.05f
                && Mathf.Abs(t.Depth - depthM) < 0.05f && !Covered(t))
            .MinBy(t => t.Uv.DistanceTo(uv));
        if (nearest is not null)
        {
            nearest.GrippedBy = toolUid;
            return new TargetHold(nearest.Index, depthM);
        }
        if (!skinOnly && Inside(zone, uv) && Wounds.FirstOrDefault(w => w.IsInternal && w.Points[0].DistanceTo(uv) < 0.05f) is { } vessel)
        {
            vessel.Clamped = 0.9f;
            return new VesselHold(vessel.Id);
        }
        if (zone == SiteZone.Cavity && !skinOnly)
        {
            // An organ in the way can be taken hold of and moved aside, to get at what's under it.
            var at = Body.UvToWorld(uv, depthM);
            var organ = Body.OrganAt(at, 0.02f);
            if (organ >= 0)
            {
                var position = Body.Organs[organ].Position;
                Body.HoldOrgan(organ, position);
                return new OrganHold(organ, position - Body.Site.ToLocal(at));
            }
        }
        var wound = NearestWound(uv, 0.03f, false);
        if (wound is not null && zone == SiteZone.Cavity && !skinOnly && wound.BleedRate(1f, 1f) > 0f && wound.Depth > 0.6f)
        {
            wound.Clamped = 0.85f;
        }
        // Skin can be pinched anywhere on the site, but from inside the cavity only near a wound edge.
        if (zone == SiteZone.Site || (zone == SiteZone.Cavity && wound is not null))
        {
            tissue.Grip(toolUid, uv);
            var piece = zone == SiteZone.Site && tissue.PieceOf(tissue.Nearest(uv)).Count > 0;
            return new SkinHold(wound?.Id ?? 0, uv, skinOnly, piece);
        }
        return null;
    }

    /// <summary>
    /// Called when a spreader (the Gelpi retractor) is set into the skin with its jaws' tips at
    /// <paramref name="tips"/> (world): each jaw takes hold of the edge on its own side of the middle. Null when a tip
    /// isn't on the site or there's no cut between them to go into.
    /// </summary>
    public SpreadHold? SetSpreader(int toolUid, IReadOnlyList<Vector3> tips, float spread)
    {
        var uvs = new List<Vector2>();
        foreach (var tip in tips)
        {
            var probe = Body.Probe(tip);
            if (probe.Zone is not (SiteZone.Site or SiteZone.Cavity))
            {
                return null;
            }
            uvs.Add(probe.Uv);
        }
        var middle = (uvs[0] + uvs[1]) * 0.5f;
        var depth = Body.OpeningDepth(middle, uvs[0].DistanceTo(uvs[1]) * 0.5f + Body.MetersToUv(0.003f));
        if (depth <= 0f)
        {
            return null;
        }
        var keys = new int[2];
        var starts = new Vector3[2];
        for (var side = 0; side < 2; side++)
        {
            keys[side] = SpreaderKey(toolUid, side);
            var held = Body.Tissue.GripBeside(keys[side], uvs[side], middle, uvs[side] - middle);
            if (held < 0)
            {
                for (var k = 0; k < side; k++)
                {
                    Body.Tissue.Release(keys[k]);
                }
                return null;
            }
            starts[side] = Body.Tissue.Pos[held];
        }
        var axis = Body.Site.ToLocal(tips[1]) - Body.Site.ToLocal(tips[0]);
        return new SpreadHold(keys, starts, new Vector3(axis.X, 0f, axis.Z).Normalized(), spread, middle, depth);
    }

    /// <summary>Opens or closes a set spreader to <paramref name="spread"/> (meters between its tips): each jaw moves
    /// its edge half the change away from the middle (toward it when closing).</summary>
    public void OpenSpreader(SpreadHold hold, float spread)
    {
        var move = hold.Axis * (spread - hold.Spread) * 0.5f;
        Body.Tissue.MoveGrip(hold.Keys[0], hold.Starts[0] - move);
        Body.Tissue.MoveGrip(hold.Keys[1], hold.Starts[1] + move);
    }

    /// <summary>The tissue grip key of a spreader's jaw (side 0 or 1). Negative, so it never meets a clamp's, which is
    /// its uid.</summary>
    public static int SpreaderKey(int toolUid, int side) => -(toolUid * 2 + side);

    /// <summary>Moves what a tool holds with its tip. Returns what it holds now: a target pulled free is carried, a piece
    /// of skin lifted away comes off (null).</summary>
    public ToolHold? UpdateGrip(int toolUid, ToolHold hold, Vector3 tip, float power, float dt, float speed)
    {
        switch (hold)
        {
            case TargetHold grip:
                var target = Targets[grip.Target];
                var local = Body.Site.ToLocal(tip);
                var height = local.Y;
                if (target.Anchor > 0f)
                {
                    var pulled = -target.Depth - height;
                    if (pulled < -0.02f)
                    {
                        if (speed < 0.06f)
                        {
                            target.Anchor = Mathf.Max(target.Anchor - dt * 0.3f, 0f);
                        }
                        if (speed > 0.25f || pulled < -0.05f)
                        {
                            Session.Scoring.Add("forced_extraction");
                            Session.Announce("Ripped it out!");
                            Tear(target.Uv, RandomDirection(), 0.05f);
                            target.Anchor = 0f;
                        }
                    }
                    return hold;
                }
                target.Position = local;
                target.Uv = Body.LocalToUv(local);
                target.Depth = -height;
                if (!target.IsFragment && height > 0.03f)
                {
                    Extract(target);
                    return new CarryHold(target.Index);
                }
                return hold;
            case CarryHold carry:
                Targets[carry.Target].GlobalPosition = tip;
                return hold;
            case SkinHold skin:
                var to = Body.Site.ToLocal(tip);
                if (skin.Hook)
                {
                    // A hook pulls the skin aside or up, not down after the tip into the opening.
                    to.Y = Mathf.Max(to.Y, Body.SurfaceHeight(Body.LocalToUv(to)));
                }
                Body.Tissue.MoveGrip(toolUid, to);
                if (skin.Piece && Body.Site.ToLocal(tip).Y - Body.SurfaceHeight(skin.Anchor) > PieceLift)
                {
                    TakePiece(toolUid, skin.Anchor);
                    return null;
                }
                return hold;
            case OrganHold organ:
                Body.HoldOrgan(organ.Organ, Body.Site.ToLocal(tip) + organ.Offset);
                return hold;
            default:
                return hold;
        }
    }

    /// <summary>Lets go of what a tool held. A self-retaining tool's clamp stays on a vessel or wound.</summary>
    public void ReleaseGrip(int toolUid, ToolHold? hold, bool selfRetaining)
    {
        Body.Tissue.Release(toolUid);
        switch (hold)
        {
            case TargetHold target:
                Targets[target.Target].GrippedBy = 0;
                break;
            case CarryHold carry:
                // Taken out: let go of, it's put aside instead of hanging over the opening.
                Targets[carry.Target].GrippedBy = 0;
                Targets[carry.Target].SetAside();
                break;
            case OrganHold organ:
                Body.ReleaseOrgan(organ.Organ);
                break;
            case SpreadHold spread:
                foreach (var key in spread.Keys)
                {
                    Body.Tissue.Release(key);
                }
                break;
            case VesselHold vessel when !selfRetaining:
                if (WoundWithId(vessel.Wound) is { } clamped)
                {
                    clamped.Clamped = 0f;
                }
                break;
            case SkinHold skin when !selfRetaining:
                if (WoundWithId(skin.Wound) is { } wound)
                {
                    wound.Clamped = 0f;
                }
                break;
        }
    }

    /// <summary>Host: the piece of skin cut out all round at uv comes off in the forceps that lifted it, as a skin graft
    /// for a burn.</summary>
    private void TakePiece(int toolUid, Vector2 uv)
    {
        Body.Tissue.Release(toolUid);
        Rpc(MethodName.TissueExcise, Body.Tissue.Nearest(uv));
        Session.Tools.GiveGraft(toolUid);
        AddFlag("graft_taken");
        Session.Announce("The skin comes away in one piece.");
    }

    /// <summary>An organ lies over the target, between it and the opening.</summary>
    public bool Covered(CavityTarget target) => Body.Organs.Any(organ =>
        Body.LocalToUv(organ.Position).DistanceTo(target.Uv) < 0.07f
        && organ.Position.Y > Body.SurfaceHeight(target.Uv) - target.Depth);

    private void Extract(CavityTarget target)
    {
        if (target.Extracted)
        {
            return;
        }
        target.Extracted = true;
        if (target.IsSuctionTarget)
        {
            target.SetAside();
        }
        else
        {
            target.UpdateLook();
        }
        if (target.Surge > 0f)
        {
            NewWound(WoundKind.Internal, target.Uv, Mathf.Clamp(0.4f + target.Surge * 0.15f, 0f, 1f)).DepthM = target.Depth;
            Session.Announce("Blood wells up where it came out!");
            Session.Sound("blood_spurt", Body.UvToWorld(target.Uv));
        }
        Session.Announce($"{target.Kind.Capitalize()}: done.");
    }
}
