namespace Scalpel.Patients;

/// <summary>What tools take hold of: skin, targets, vessels, organs (forceps, clamps, retractors, spreaders).</summary>
public partial class Patient {
    /// <summary>In an opening, jaws close on a target only with the tip at most this far (meters) over what's under it:
    /// lowered onto it they close just above it, a wheel notch higher (Surgeon.DepthStep) on nothing.</summary>
    public const float JawReach = 0.004f;
    /// <summary>How far under an opening's lip (meters) its edge is: jaws closing this shallow in the opening take the
    /// edge, deeper there's none to take. A forceps tip held deeper can't move out under the skin
    /// (Surgeon.InsideLip()).</summary>
    public const float LipDepth = 0.005f;
    /// <summary>Jaws close on embedded glass with the tip over any of it that shows, or at most this far (meters)
    /// beside it: open, they straddle a blade seen edge on.</summary>
    public const float GlassReach = 0.005f;
    /// <summary>How far (meters) from a cut's line a tip over an opening still finds that cut: as wide as a deep cut
    /// gapes or a lip is drawn back, whatever the site's size.</summary>
    private const float OpeningReach = 0.02f;

    /// <summary>
    /// Called when a clamp tool closes at the tip. Returns what it took hold of, for <see cref="UpdateGrip"/> and
    /// <see cref="ReleaseGrip"/>; null when nothing. <paramref name="skinOnly"/>: a hook (the retractor) takes hold of
    /// skin and nothing under it: the skin it's pressed onto, or pressed into an opening, the edge of the cut on that
    /// side. It pulls the skin aside, never down into the opening.
    /// </summary>
    public ToolHold? Grip(int toolUid, SiteZone zone, Vector2 uv, float depthM, bool skinOnly = false) {
        var tissue = Body.Tissue;
        if (skinOnly && zone == SiteZone.Cavity) {
            if (NearestWound(uv, Body.MetersToUv(OpeningReach), false) is not { } cut) {
                return null;
            }
            var line = cut.ClosestPoint(uv);
            if (tissue.GripBeside(toolUid, uv, line, uv - line) < 0) {
                tissue.Grip(toolUid, uv);
            }
            var held = tissue.GrippedParticle(toolUid);
            var contact = Body.LayerPoint(0, held);
            return new SkinHold(cut.Id, uv, Hook: true, Offset: contact - tissue.Pos[held], Origin: contact);
        }
        // The nearest target the jaws close on, not one another tool already holds (two broken ends lie close
        // together). Forceps held up in the opening close on nothing under them: no target, vessel or bleeding wound.
        // Glass sticking out of the skin is taken anywhere along it, not only where it went in.
        var at = Body.UvToWorld(uv, depthM);
        var down = zone != SiteZone.Cavity || Body.TouchesInside(at, JawReach);
        // Glass the tip is over comes before a target whose middle is merely near.
        var candidates = skinOnly || !down ? [] : Targets
            .Where(t => !t.Extracted && !t.IsSuctionTarget && !(t.IsBone && t.Anchor > 0f))
            .Where(t => t.GrippedBy == 0 || t.GrippedBy == toolUid)
            .Where(t => zone is SiteZone.Cavity or SiteZone.Site && Mathf.Abs(t.Depth - depthM) < 0.05f && !Covered(t) && t.InReach)
            .ToList();
        var nearest = candidates.FirstOrDefault(t => t.Embedded && OverGlass(t, Body.Site.ToLocal(at)))
            ?? candidates.Where(t => t.Uv.DistanceTo(uv) < 0.05f).MinBy(t => t.Uv.DistanceTo(uv));
        if (nearest is not null) {
            nearest.GrippedBy = toolUid;
            var offset = nearest.Embedded ? (nearest.Position - Body.Site.ToLocal(at)) with { Y = 0f } : Vector3.Zero;
            return new TargetHold(nearest.Index, depthM, offset);
        }
        var organ = zone == SiteZone.Cavity && !skinOnly ? Body.OrganAt(at, 0.02f) : -1;
        if (!skinOnly && down && Inside(zone, uv) && Wounds.FirstOrDefault(w => w.IsInternal
                && w.Points[0].DistanceTo(uv) < 0.05f && (organ < 0 || w.DepthM <= depthM + JawReach)) is { } vessel) {
            vessel.Clamped = 0.9f;
            return new VesselHold(vessel.Id);
        }
        if (zone == SiteZone.Cavity && !skinOnly) {
            // An organ in the way can be taken hold of and moved aside, to get at what's under it.
            if (organ >= 0) {
                var position = Body.Organs[organ].Position;
                Body.HoldOrgan(organ, position);
                return new OrganHold(organ, position - Body.Site.ToLocal(at));
            }
        }
        var wound = NearestWound(uv, Body.MetersToUv(OpeningReach), false);
        // Closed on a bleeding wound's edge from inside the opening, the jaws clamp it until they let go (Unclamp()).
        var clamps = wound is not null && zone == SiteZone.Cavity && !skinOnly && down && wound.BleedRate(1f, 1f) > 0f
            && wound.Depth > 0.6f;
        if (clamps) {
            wound!.Clamped = 0.85f;
        }
        // Skin can be pinched anywhere on the site, but from inside the cavity only at a wound's edge, up by the lip.
        if (zone == SiteZone.Site || (zone == SiteZone.Cavity && wound is not null && depthM < LipDepth)) {
            // Both jaws and hooks take the skin they actually touch, including the model fit and its current
            // deformation. Preserve the contact offset from the simulation particle so pinching doesn't drag that
            // particle up to the tip and spend part of the skin's give before the mouse has moved.
            var material = zone == SiteZone.Site
                ? Body.CutUv(Body.SitePoint(uv, Body.SkinHeight(uv) - depthM)) : uv;
            // A shallow pinch inside the opening must choose the approached edge, just like a hook. Find that
            // side's nearest deformed particle; rest-grid rounding can pick the opposite lip of a narrow cut.
            var line = wound?.ClosestPoint(uv) ?? uv;
            if (zone != SiteZone.Cavity || wound is null || tissue.GripBeside(toolUid, uv, line, uv - line) < 0) {
                tissue.Grip(toolUid, material);
            }
            var piece = zone == SiteZone.Site && tissue.PieceOf(tissue.GrippedParticle(toolUid)).Count > 0;
            var contact = Body.SitePoint(uv, Body.SkinHeight(uv));
            var offset = contact - tissue.Pos[tissue.GrippedParticle(toolUid)];
            return new SkinHold(wound?.Id ?? 0, uv, skinOnly, piece, Offset: offset, Origin: contact, Clamps: clamps);
        }
        // Deeper down, under the lip, they hold only the clamped edge.
        return clamps ? new VesselHold(wound!.Id) : null;
    }

    /// <summary>
    /// Called when a spreader (the Gelpi retractor) is set into the skin with its jaws' tips at
    /// <paramref name="tips"/> (world): each jaw takes hold of the edge on its own side of the middle. Null when a tip
    /// isn't on the site or there's no cut between them to go into.
    /// </summary>
    public SpreadHold? SetSpreader(int toolUid, IReadOnlyList<Vector3> tips, float spread) {
        var uvs = new List<Vector2>();
        foreach (var tip in tips) {
            var probe = Body.Probe(tip);
            if (probe.Zone is not (SiteZone.Site or SiteZone.Cavity)) {
                return null;
            }
            uvs.Add(probe.Uv);
        }
        var middle = (uvs[0] + uvs[1]) * 0.5f;
        var depth = Body.OpeningDepth(middle, uvs[0].DistanceTo(uvs[1]) * 0.5f + Body.MetersToUv(0.003f));
        if (depth <= 0f) {
            return null;
        }
        var keys = new int[2];
        var starts = new Vector3[2];
        for (var side = 0; side < 2; side++) {
            keys[side] = SpreaderKey(toolUid, side);
            var held = Body.Tissue.GripBeside(keys[side], uvs[side], middle, uvs[side] - middle);
            if (held < 0) {
                for (var k = 0; k < side; k++) {
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
    public void OpenSpreader(SpreadHold hold, float spread) {
        var move = hold.Axis * (spread - hold.Spread) * 0.5f;
        Body.Tissue.MoveGrip(hold.Keys[0], hold.Starts[0] - move);
        Body.Tissue.MoveGrip(hold.Keys[1], hold.Starts[1] + move);
    }

    /// <summary>The tissue grip key of a spreader's jaw (side 0 or 1). Negative, so it never meets a clamp's, which is
    /// its uid.</summary>
    public static int SpreaderKey(int toolUid, int side) => -(toolUid * 2 + side);

    /// <summary>Moves what a tool holds with its tip. Returns what it holds now: a target pulled free is carried, a piece
    /// of skin lifted away comes off (null). <paramref name="heldAt"/>: where the tip ends up (world), at
    /// <paramref name="tip"/> unless skin held stops giving and holds it short of there (<see cref="TissueSim.Restrain"/>).
    /// </summary>
    public ToolHold? UpdateGrip(int toolUid, ToolHold hold, Vector3 tip, float power, float dt, float speed, out Vector3 heldAt) {
        heldAt = tip;
        switch (hold) {
            case TargetHold grip:
                var target = Targets[grip.Target];
                var local = Body.Site.ToLocal(tip);
                // Over the skin there, as a target's depth is under it: a limb's skin lies well below the site's plane.
                var height = local.Y - Body.SurfaceHeight(Body.LocalToUv(local));
                if (target.Anchor > 0f) {
                    var pulled = -target.Depth - height;
                    if (pulled < -0.02f) {
                        if (speed < 0.06f) {
                            target.Anchor = Mathf.Max(target.Anchor - dt * 0.3f, 0f);
                        }
                        if (speed > 0.25f || pulled < -0.05f) {
                            Session.Scoring.Add("forced_extraction");
                            Session.Announce("Ripped it out!");
                            target.Anchor = 0f;
                        }
                    }
                    return hold;
                }
                target.Position = local + grip.Offset;
                target.Uv = Body.LocalToUv(target.Position);
                target.Depth = -height;
                if (!target.IsFragment && height > 0.03f) {
                    Extract(target);
                    return new CarryHold(target.Index, grip.Offset);
                }
                return hold;
            case CarryHold carry:
                Targets[carry.Target].GlobalPosition = tip + (Body.Site.GlobalBasis * carry.Offset);
                return hold;
            case SkinHold skin:
                var to = Body.Site.ToLocal(tip);
                // Skin is pulled aside or up, never down after the tip into the opening: jaws closed on a lip from
                // inside the cut hold it at the surface, where the skin around would tear if the lip went down with
                // the tip.
                to.Y = Mathf.Max(to.Y, Body.SurfaceHeight(Body.LocalToUv(to)));
                // A drawn skin contact can lie between grid particles and above their settled model fit.
                // Preserve that offset so taking hold doesn't spend the skin's give before the hand pulls.
                to -= skin.Offset;
                var allowed = Body.Tissue.Restrain(toolUid, to);
                heldAt = Body.Site.ToGlobal(allowed + skin.Offset);
                Body.Tissue.MoveGrip(toolUid, allowed);
                if (skin.Piece && Body.Site.ToLocal(tip).Y - Body.SurfaceHeight(skin.Anchor) > PieceLift) {
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
    public void ReleaseGrip(int toolUid, ToolHold? hold, bool selfRetaining) {
        Body.Tissue.Release(toolUid);
        switch (hold) {
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
                foreach (var key in spread.Keys) {
                    Body.Tissue.Release(key);
                }
                break;
            case VesselHold vessel when !selfRetaining:
                Unclamp(vessel.Wound, toolUid);
                break;
            case SkinHold { Clamps: true } skin when !selfRetaining:
                Unclamp(skin.Wound, toolUid);
                break;
        }
    }

    /// <summary>The wound <paramref name="woundId"/> bleeds again once the tool <paramref name="toolUid"/> clamping it
    /// lets go, unless another still clamps it (a hemostat left locked on it).</summary>
    private void Unclamp(int woundId, int toolUid) {
        if (WoundWithId(woundId) is not { } wound) {
            return;
        }
        foreach (var tool in Session.Tools.Tools.Values) {
            if (tool.Uid != toolUid && tool.Hold is VesselHold { Wound: var vessel } && vessel == woundId
                || tool.Uid != toolUid && tool.Hold is SkinHold { Clamps: true, Wound: var edge } && edge == woundId) {
                return;
            }
        }
        wound.Clamped = 0f;
    }

    /// <summary>Host: the piece of skin cut out all round at uv comes off in the forceps that lifted it, as a skin graft
    /// for a burn.</summary>
    private void TakePiece(int toolUid, Vector2 uv) {
        Body.Tissue.Release(toolUid);
        Rpc(MethodName.TissueExcise, Body.Tissue.Nearest(uv));
        Session.Tools.GiveGraft(toolUid);
        AddFlag("graft_taken");
        Session.Announce("The skin comes away in one piece.");
    }

    /// <summary>The corners of an embedded target's glass that stick out of the skin (site space).</summary>
    internal IEnumerable<Vector3> GlassShowing(CavityTarget target) => target
        .FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>()
        .SelectMany(mesh => mesh.Mesh.GetFaces().Select(corner => Body.Site.ToLocal(mesh.GlobalTransform * corner)))
        .Where(local => local.Y > Body.SurfaceHeight(Body.LocalToUv(local)));

    /// <summary>Whether <paramref name="local"/> (site space) lies over the glass showing of
    /// <paramref name="target"/> across the skin, or at most <see cref="GlassReach"/> beside it.</summary>
    private bool OverGlass(CavityTarget target, Vector3 local) {
        var hull = Geometry2D.ConvexHull(GlassShowing(target).Select(corner => new Vector2(corner.X, corner.Z)).ToArray());
        var point = new Vector2(local.X, local.Z);
        return hull.Length > 2 && (Geometry2D.IsPointInPolygon(point, hull) || hull.Zip(hull.Skip(1))
            .Any(side => Geometry2D.GetClosestPointToSegment(point, side.First, side.Second).DistanceTo(point) <= GlassReach));
    }

    /// <summary>An organ lies over the target, between it and the opening.</summary>
    public bool Covered(CavityTarget target) => Body.Organs.Any(organ =>
        Body.LocalToUv(organ.Position).DistanceTo(target.Uv) < 0.07f
        && organ.Position.Y > Body.SurfaceHeight(target.Uv) - target.Depth);

    private void Extract(CavityTarget target) {
        if (target.Extracted) {
            return;
        }
        target.Extracted = true;
        if (target.IsSuctionTarget) {
            target.SetAside();
        } else {
            target.UpdateLook();
        }
        if (target.Plug is { } track) {
            track.Held = 0f;
            Session.Announce("The cut it came out of bleeds.");
        }
        if (target.Surge > 0f) {
            NewWound(WoundKind.Internal, target.Uv, Mathf.Clamp(0.4f + target.Surge * 0.15f, 0f, 1f)).DepthM = target.Depth;
            Session.Announce("Blood wells up where it came out!");
            Session.Sound("blood_spurt", Body.UvToWorld(target.Uv));
        }
        Session.Announce($"{target.Kind.Capitalize()}: done.");
    }
}
