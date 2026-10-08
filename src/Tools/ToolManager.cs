namespace Scalpel.Tools;

/// <summary>A tool that sits on its own station (the IV drip on its stand), not on the tray.</summary>
public readonly record struct StationTool(string Id, Transform3D Transform);

/// <summary>
/// Owns every tool in the room. Peers send requests (grab, release, belt), the host decides and broadcasts.
/// Initial tools spawn deterministically on every peer, later ones (nurse deliveries) come from the host.
/// </summary>
public partial class ToolManager : Node3D
{
    public const float SyncInterval = 0.1f;
    public const float GrabRadius = 0.09f;
    /// <summary>Everyone but the host sees iodine levels in steps this fine (syringes, vials and the kidney dish are
    /// exact).</summary>
    private const float FillSteps = 50f;
    /// <summary>How close a syringe's needle has to be to a vial's middle to be in it. A syringe brought over a vial or
    /// the hung bag snaps its needle into it (see Surgeon.SnapSpot()).</summary>
    public const float VialReach = 0.05f;
    /// <summary>How close to a dish's middle (a share of its length) a needle has to be to be in it.</summary>
    private const float DishReach = 0.4f;
    /// <summary>How close to a hung bag's middle (a share of its length) a needle has to be to be in it.</summary>
    private const float DripReach = 0.75f;
    /// <summary>How far apart (meters) bottles delivered standing are set, so a new one doesn't stand on an earlier
    /// one.</summary>
    private const float StandingRoom = 0.04f;
    /// <summary>How high (meters) over the body at rest a tool lying on it rests at its tip, about half its thickness.
    /// Elsewhere it rests right on the body: breathing lifts a belly into it a little, never enough to stand it up.
    /// </summary>
    private const float LyingClearance = 0.0025f;
    /// <summary>Where along a lying tool (a share of its length from the tip) it's checked against the body.</summary>
    private static readonly float[] LyingSamples = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f, 0.7f, 0.8f, 0.9f, 1f];
    /// <summary>How close to a dish's middle (a share of its length) a bottle has to be to pour into it, or a cotton
    /// pad to dip in it.</summary>
    private const float PourReach = 0.7f;
    /// <summary>A tool lying lower than this (meters) is on the floor: one that lands on it lands on the floor too.
    /// </summary>
    private const float FloorPile = 0.1f;
    /// <summary>The group the room puts its floors in, which soil what lands on them.</summary>
    public const string FloorGroup = "floor";

    private readonly Dictionary<int, SurgicalTool> _tools = [];
    /// <summary>Host: syringes, vials, dishes and bags whose liquid changed since it was last sent.</summary>
    private readonly HashSet<int> _liquidChanged = [];
    private int _nextUid = 1;
    private float _syncAcc;
    private float _liquidAcc;

    /// <summary>Every tool, by uid.</summary>
    public IReadOnlyDictionary<int, SurgicalTool> Tools => _tools;

    public SurgicalTool? ByUid(int uid) => _tools.GetValueOrDefault(uid);

    private static Surgery Session => Surgery.Current!;

    /// <summary>Spawns the tray, the station tools and each surgeon's personal items, the same on every peer. A tool the
    /// station provides is never also put on the tray.</summary>
    public void SpawnInitial(List<string> trayIds, IReadOnlyList<Vector3> traySpots,
        IReadOnlyDictionary<int, IReadOnlyList<string>> personal, IReadOnlyList<StationTool> stationTools)
    {
        foreach (var station in stationTools)
        {
            trayIds.RemoveAll(id => id == station.Id);
            var made = Create(_nextUid, station.Id, station.Transform);
            if (made.Def.Fixed)
            {
                made.SetState(ToolState.Standing, 0, -1);
            }
        }
        var spot = 0;
        var groups = new Dictionary<string, List<SurgicalTool>>();
        foreach (var id in trayIds)
        {
            var tool = Create(_nextUid, id, new Transform3D(Basis.Identity, traySpots[spot % traySpots.Count]));
            if (!groups.TryGetValue(tool.Def.Tray, out var group))
            {
                group = [];
                groups[tool.Def.Tray] = group;
            }
            group.Add(tool);
            spot++;
        }
        if (Surgery.Current?.Room is { } room)
        {
            foreach (var (name, group) in groups)
            {
                // A tool's tip is at -Z: standing, it points up.
                var basis = Room.Upright.Contains(name) ? new Basis(Vector3.Right, Mathf.Pi / 2) : Basis.Identity;
                LayOut(group, room.TrayZone(name), basis);
            }
        }
        foreach (var peer in personal.Keys.Order())
        {
            var surgeon = Session.Surgeons[peer];
            var beltSlot = 0;
            foreach (var id in personal[peer])
            {
                var tool = Create(_nextUid, id, new Transform3D(Basis.Identity, traySpots[spot % traySpots.Count]));
                if (beltSlot < surgeon.BeltCapacity())
                {
                    tool.SetState(ToolState.Belt, peer, beltSlot);
                    beltSlot++;
                }
                else
                {
                    spot++;
                }
            }
        }
    }

    /// <summary>
    /// Lays a group of tools out side by side in its spot on the tray (Room.TrayZone()), in columns from its +x side,
    /// each resting right on it: nothing overlaps, so nothing gets shoved into the tray or its neighbour when the physics
    /// starts. Longest first; what doesn't fit lies on top of the ones already there.
    /// </summary>
    private static void LayOut(List<SurgicalTool> group, Aabb zone, Basis basis)
    {
        const float Gap = 0.012f;
        var x = zone.End.X;
        var z = zone.Position.Z;
        var column = 0f;
        var layer = 0f;
        var layerHeight = 0f;
        foreach (var tool in group.OrderByDescending(tool => tool.Bounds.Size.Z))
        {
            var box = new Transform3D(basis, Vector3.Zero) * tool.Bounds;
            var size = box.Size;
            if (z + size.Z > zone.End.Z)
            {
                x -= column + Gap;
                z = zone.Position.Z;
                column = 0f;
            }
            if (x - size.X < zone.Position.X)
            {
                x = zone.End.X;
                layer += layerHeight + 0.001f;
                layerHeight = 0f;
            }
            var at = new Vector3(x - size.X - box.Position.X, zone.Position.Y + layer - box.Position.Y + 0.001f, z - box.Position.Z);
            tool.GlobalTransform = new Transform3D(basis, at);
            z += size.Z + Gap;
            column = Mathf.Max(column, size.X);
            layerHeight = Mathf.Max(layerHeight, size.Y);
        }
    }

    public SurgicalTool? ToolInHand(int peer, int hand) => ToolAt(ToolState.Held, peer, hand);

    public SurgicalTool? ToolOnBelt(int peer, int beltSlot) => ToolAt(ToolState.Belt, peer, beltSlot);

    /// <summary>A loop rather than a query: asked many times a frame, a query's closure would be garbage each time.
    /// </summary>
    private SurgicalTool? ToolAt(ToolState state, int peer, int slot)
    {
        foreach (var tool in _tools.Values)
        {
            if (tool.State == state && tool.Holder == peer && tool.Slot == slot)
            {
                return tool;
            }
        }
        return null;
    }

    /// <summary>The world-space needle tip holding the free end of a live running suture, null once it is tied or
    /// torn.</summary>
    public Vector3? SutureTip(int threadId) =>
        _tools.Values.FirstOrDefault(t => t.Suture.Thread == threadId)?.TipPosition();

    /// <summary>The loose tool nearest <paramref name="at"/> (its grip or its tip) within grabbing reach.</summary>
    public SurgicalTool? NearestGrabbable(Vector3 at)
    {
        SurgicalTool? best = null;
        var bestDistance = GrabRadius;
        foreach (var tool in _tools.Values)
        {
            if (tool.State is ToolState.Free or ToolState.Standing or ToolState.Inside && !tool.Def.Fixed)
            {
                var distance = Mathf.Min(tool.GlobalPosition.DistanceTo(at), tool.TipPosition().DistanceTo(at));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = tool;
                }
            }
        }
        return best;
    }

    /// <summary>The closest tool of this kind within reach of a point (measured to its middle), wherever it is but on a
    /// belt or used up.</summary>
    public SurgicalTool? NearestOf(string id, Vector3 at, float reach) => Nearest(at, reach, tool => tool.Def.Id == id);

    private SurgicalTool? Nearest(Vector3 at, float reach, Func<SurgicalTool, bool> wanted)
    {
        SurgicalTool? best = null;
        var bestDistance = reach;
        foreach (var tool in _tools.Values)
        {
            if (wanted(tool) && tool.State is not (ToolState.Belt or ToolState.Consumed))
            {
                var distance = tool.Middle().DistanceTo(at);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = tool;
                }
            }
        }
        return best;
    }

    /// <summary>The vial or dish a syringe's needle at <paramref name="at"/> is in, or null: anything else that holds
    /// liquid, closest first.</summary>
    public SurgicalTool? NearestContainer(Vector3 at)
    {
        SurgicalTool? best = null;
        var bestDistance = float.PositiveInfinity;
        foreach (var tool in _tools.Values)
        {
            if (tool.Def.Volume <= 0f || tool.Def.Action == "syringe" || tool.State is ToolState.Belt or ToolState.Consumed)
            {
                continue;
            }
            var distance = tool.Middle().DistanceTo(at);
            var reach = tool.Def.Action switch
            {
                "vial" => VialReach,
                "drip" => tool.Def.Length * DripReach,
                _ => tool.Def.Length * DishReach,
            };
            if (distance < reach && distance < bestDistance)
            {
                bestDistance = distance;
                best = tool;
            }
        }
        return best;
    }

    /// <summary>The bag hanging on the IV stand, null where there's none.</summary>
    public SurgicalTool? DripBag() => _tools.Values.FirstOrDefault(t => t.Def.Action == "drip");

    /// <summary>The dish <paramref name="at"/> is over (<see cref="ToolDef.IsDish"/>), nearest first, or null: what a
    /// bottle pours into and a pad dips in.</summary>
    public SurgicalTool? NearestDish(Vector3 at) =>
        Nearest(at, float.PositiveInfinity, tool => tool.Def.IsDish && tool.Middle().DistanceTo(at) < tool.Def.Length * PourReach);

    /// <summary>What this tool holds at its tip (a cotton pad in forceps), or null.</summary>
    public SurgicalTool? CarriedBy(SurgicalTool tool) =>
        _tools.Values.FirstOrDefault(other => other.State == ToolState.Carried && other.Holder == tool.Uid);

    // --- Requests (any peer) ------------------------------------------------------------------------------

    public void RequestGrab(SurgicalTool tool, int hand) => RpcId(Net.HostId, MethodName.GrabOnHost, tool.Uid, hand);

    public void RequestRelease(int hand, Vector3 velocity) => RpcId(Net.HostId, MethodName.ReleaseOnHost, hand, velocity);

    public void RequestStand(int hand) => RpcId(Net.HostId, MethodName.StandOnHost, hand);

    public void RequestPass(int hand) => RpcId(Net.HostId, MethodName.PassOnHost, hand);

    public void RequestBelt(int hand, int beltSlot) => RpcId(Net.HostId, MethodName.BeltOnHost, hand, beltSlot);

    /// <summary>One wheel notch on the syringe in this hand: notches &gt; 0 pull the plunger out, &lt; 0 push it in.
    /// </summary>
    public void RequestPlunger(int hand, int notches) => RpcId(Net.HostId, MethodName.PlungerOnHost, hand, notches);

    /// <summary>Needle wheel: direction &gt; 0 loosens, direction &lt; 0 tightens the live thread.</summary>
    public void RequestSutureTension(int hand, int direction) => RpcId(Net.HostId, MethodName.SutureTensionOnHost, hand, direction);

    /// <summary>Spreader wheel: direction &gt; 0 opens it, direction &lt; 0 closes it.</summary>
    public void RequestSpread(int hand, int direction) => RpcId(Net.HostId, MethodName.SpreadOnHost, hand, direction);

    /// <summary>The needle of the syringe in this hand tore out of the patient, dragged from <paramref name="from"/> to
    /// <paramref name="to"/> (world space).</summary>
    public void RequestNeedleTear(int hand, Vector3 from, Vector3 to) => RpcId(Net.HostId, MethodName.NeedleTearOnHost, hand, from, to);

    /// <summary>Normal withdrawal leaves a visual bead at the puncture without the damage of tearing the needle out.
    /// </summary>
    public void RequestNeedleWithdrawal(int hand, Vector3 at) => RpcId(Net.HostId, MethodName.NeedleWithdrawalOnHost, hand, at);

    public void RequestSterilize(int hand) => RpcId(Net.HostId, MethodName.SterilizeOnHost, hand);

    public void RequestWash(int hand) => RpcId(Net.HostId, MethodName.WashOnHost, hand);

    /// <summary>Host: shows every peer the needle's thread tension and layer, for the holder's HUD.</summary>
    public void SyncSuture(SurgicalTool tool)
    {
        if (Multiplayer.IsServer())
        {
            Rpc(MethodName.SetSutureState, tool.Uid, tool.Suture.Thread, tool.Suture.Tension, (int)tool.Suture.Layer);
        }
    }

    /// <summary>Host: sends a spreader's opening and whether it's set to everyone. Set just now, it eases down to
    /// <paramref name="pose"/>.</summary>
    public void SyncSpread(SurgicalTool tool, Transform3D pose = default)
    {
        if (Multiplayer.IsServer())
        {
            Rpc(MethodName.SetSpread, tool.Uid, tool.Spread, tool.Hold is not null, pose);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void GrabOnHost(int uid, int hand)
    {
        var peer = Net.Instance.Sender();
        var tool = ByUid(uid);
        if (tool is null || !Session.Surgeons.TryGetValue(peer, out var surgeon) || tool.Def.Fixed || ToolInHand(peer, hand) is not null)
        {
            return;
        }
        var ownedBelt = tool.State == ToolState.Belt && tool.Holder == peer;
        if (!(tool.State is ToolState.Free or ToolState.Standing or ToolState.Inside || ownedBelt))
        {
            return;
        }
        if (surgeon.BlockedReason(tool.Def) is { Length: > 0 } blocked)
        {
            Session.Tell(peer, blocked);
            return;
        }
        var wasStanding = tool.State == ToolState.Standing;
        Rpc(MethodName.SetToolState, uid, (int)ToolState.Held, peer, hand, tool.GlobalTransform);
        // In hand again, its tip holds the skin (see LeaveStanding()).
        if (tool.Hold is SkinHold skin)
        {
            tool.Hold = skin with { Hold = null };
        }
        if (wasStanding && tool.Hold is not null)
        {
            Session.SetAttached(peer, hand, true);
        }
        if (wasStanding && tool.Def.Action == "tourniquet")
        {
            Session.Patient.RemoveTourniquet();
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ReleaseOnHost(int hand, Vector3 velocity)
    {
        var peer = Net.Instance.Sender();
        if (ToolInHand(peer, hand) is not { } tool)
        {
            return;
        }
        Session.SetAttached(peer, hand, false);
        if (tool.Hold is not null)
        {
            if (tool.Def.SelfRetaining)
            {
                LeaveStanding(tool);
                return;
            }
            Session.Patient.ReleaseGrip(tool.Uid, tool.Hold, false);
            tool.Hold = null;
        }
        Rpc(MethodName.SetToolState, tool.Uid, (int)ToolState.Free, peer, -1, tool.GlobalTransform);
        tool.LinearVelocity = velocity;
        tool.Falling = true;
    }

    /// <summary>A bottle in the hand set down standing upright on whatever is under it.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void StandOnHost(int hand)
    {
        var peer = Net.Instance.Sender();
        if (ToolInHand(peer, hand) is not { Def.Tray: "bottles" } tool)
        {
            return;
        }
        Session.SetAttached(peer, hand, false);
        Rpc(MethodName.SetToolState, tool.Uid, (int)ToolState.Free, peer, -1, StandingOn(tool, tool.Middle()));
        // Set down on the floor, it's soiled like anything else that lands there.
        tool.Falling = true;
    }

    /// <summary>Hand-to-hand handoff. Moving hands fumble it and the tool falls.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PassOnHost(int hand)
    {
        var peer = Net.Instance.Sender();
        if (!Session.Surgeons.TryGetValue(peer, out var giver) || ToolInHand(peer, hand) is not { Hold: null } tool)
        {
            return;
        }
        if (giver.PassTarget(hand) is not { } target)
        {
            return;
        }
        var (receiver, receivingHand) = target;
        if (receiver.BlockedReason(tool.Def) is { Length: > 0 } blocked)
        {
            Session.Tell(giver.PeerId, $"{receiver.DisplayName} can't take it: {blocked}");
            return;
        }
        if (giver.Hands[hand].Speed > Surgeon.FumbleSpeed || receiver.Hands[receivingHand].Speed > Surgeon.FumbleSpeed)
        {
            Rpc(MethodName.SetToolState, tool.Uid, (int)ToolState.Free, giver.PeerId, -1, tool.GlobalTransform);
            tool.Falling = true;
            Session.Announce("Fumbled the handoff!");
            return;
        }
        Rpc(MethodName.SetToolState, tool.Uid, (int)ToolState.Held, receiver.PeerId, receivingHand, tool.GlobalTransform);
        Session.Tell(receiver.PeerId, $"{giver.DisplayName} hands you the {tool.Def.Name}.");
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void BeltOnHost(int hand, int beltSlot)
    {
        var peer = Net.Instance.Sender();
        if (!Session.Surgeons.TryGetValue(peer, out var surgeon) || beltSlot >= surgeon.BeltCapacity())
        {
            return;
        }
        var held = ToolInHand(peer, hand);
        var stored = ToolOnBelt(peer, beltSlot);
        if (held is { Hold: null } && stored is null)
        {
            Rpc(MethodName.SetToolState, held.Uid, (int)ToolState.Belt, peer, beltSlot, held.GlobalTransform);
        }
        else if (held is null && stored is not null)
        {
            Rpc(MethodName.SetToolState, stored.Uid, (int)ToolState.Held, peer, hand, stored.GlobalTransform);
        }
    }

    /// <summary>The tool of this action in the sender's hand, while the surgery runs.</summary>
    private SurgicalTool? HeldForAction(int hand, string action) =>
        Session.Running && ToolInHand(Net.Instance.Sender(), hand) is { } tool && tool.Def.Action == action ? tool : null;

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PlungerOnHost(int hand, int notches)
    {
        if (HeldForAction(hand, "syringe") is { } tool)
        {
            Syringe.Plunge(tool, notches * ToolActions.PlungerStep, Session.Patient);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SutureTensionOnHost(int hand, int direction)
    {
        if (HeldForAction(hand, "sew") is { } tool)
        {
            SewAction.AdjustTension(tool, Math.Sign(direction), Session.Patient);
            SyncSuture(tool);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SpreadOnHost(int hand, int direction)
    {
        if (HeldForAction(hand, "spread") is { } tool)
        {
            SpreadAction.Adjust(tool, Math.Sign(direction), Session.Patient);
            SyncSpread(tool);
        }
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetSpread(int uid, float spread, bool inWound, Transform3D pose)
    {
        if (ByUid(uid) is not { } tool)
        {
            return;
        }
        tool.Spread = spread;
        if (inWound && !tool.InWound)
        {
            tool.DigTo(pose);
        }
        tool.InWound = inWound;
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetSutureState(int uid, int threadId, float tension, int layer)
    {
        if (ByUid(uid) is { } tool)
        {
            tool.Suture.Thread = threadId;
            tool.Suture.Tension = tension;
            tool.Suture.Layer = (TissueDepth)layer;
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NeedleTearOnHost(int hand, Vector3 from, Vector3 to)
    {
        if (HeldForAction(hand, "syringe") is not null)
        {
            Session.Patient.NeedleTear(from, to);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void NeedleWithdrawalOnHost(int hand, Vector3 at)
    {
        if (HeldForAction(hand, "syringe") is { } tool)
        {
            Session.Effect(ToolEffect.Bead, at, 0);
            Syringe.ReportPushed(tool);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SterilizeOnHost(int hand)
    {
        var peer = Net.Instance.Sender();
        var tool = ToolInHand(peer, hand);
        if (tool is null)
        {
            Session.Tell(peer, "Nothing in your hand to dip.");
        }
        else if (tool.Soiled)
        {
            Session.Tell(peer, "Alcohol won't cut through that much dirt. Wash it first.");
        }
        else
        {
            Rpc(MethodName.SetSterile, tool.Uid, true);
            tool.Use.Reported.Remove("dirty");
            Session.Tell(peer, "Dipped in alcohol.");
        }
    }

    /// <summary>The sink takes the dirt off, it doesn't make anything sterile.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void WashOnHost(int hand)
    {
        var peer = Net.Instance.Sender();
        if (ToolInHand(peer, hand) is not { } tool)
        {
            Session.Surgeons.GetValueOrDefault(peer)?.Rpc(Surgeon.MethodName.CleanGloves);
            Session.Tell(peer, "You wash your gloves. They're still gloves.");
            return;
        }
        Rpc(MethodName.SetSoiled, tool.Uid, false);
        tool.BloodExposure = 0f;
        Rpc(MethodName.SetBlood, tool.Uid, 0f);
        Session.Tell(peer, "Scrubbed clean. Still not sterile.");
    }

    // --- Host ---------------------------------------------------------------------------------------------

    /// <summary>Host: a new tool at <paramref name="at"/>, on every peer. Returns its uid.</summary>
    public int Spawn(string id, Vector3 at)
    {
        var uid = _nextUid;
        if (Multiplayer.IsServer())
        {
            Rpc(MethodName.SpawnOnPeer, uid, id, at);
        }
        return uid;
    }

    /// <summary>Host: a new tool standing upright on whatever is under <paramref name="above"/> (a bottle left on the
    /// delivery tray), not dropped. Where something already lies there (an earlier bottle), it stands beside it instead
    /// of on top.</summary>
    public void SpawnStanding(string id, Vector3 above)
    {
        var tool = _tools[Spawn(id, above)];
        var at = above;
        for (var step = 0; step < 12; step++)
        {
            at = step > 0 ? above + new Vector3(StandingRoom * (step % 4 - 1.5f), 0f, StandingRoom * (step / 4 - 1f)) : above;
            if (SurfaceUnder(tool, at) is not { Collider: SurgicalTool })
            {
                break;
            }
        }
        Rpc(MethodName.SetToolState, tool.Uid, (int)ToolState.Free, 0, -1, StandingOn(tool, at));
    }

    /// <summary>Where a tool stands upright, its tip up, on whatever is under <paramref name="at"/> (a table, a tray,
    /// the patient's skin, another tool, the floor): resting right on it, so it doesn't drop and topple.</summary>
    public Transform3D StandingOn(SurgicalTool tool, Vector3 at)
    {
        var basis = new Basis(Vector3.Right, Mathf.Pi / 2);
        var under = SurfaceUnder(tool, at)?.Position.Y ?? at.Y;
        return new Transform3D(basis, new Vector3(at.X, under - (new Transform3D(basis, Vector3.Zero) * tool.Bounds).Position.Y + 0.001f, at.Z));
    }

    /// <summary>
    /// The height of the body at rest under <paramref name="at"/>: over the site its measured surface, elsewhere the
    /// body (or the table). Not the skin as it's drawn, which a hook's pull dips and bunches up, nor the drape, which
    /// gives: a rigid tool resting on either would stand up instead of lying along the body.
    /// </summary>
    private float BodyUnder(SurgicalTool tool, Vector3 at)
    {
        var body = Session.Patient.Body;
        var uv = body.WorldToUv(at);
        if (new Rect2(0, 0, 1, 1).HasPoint(uv) && body.OnBody(uv))
        {
            return body.UvToWorld(uv).Y;
        }
        return SurfaceUnder(tool, at, 1 | PatientBody.SurfaceLayer)?.Position.Y ?? at.Y;
    }

    /// <summary>What a ray straight down met: where, and the collider.</summary>

    /// <summary>The first thing under <paramref name="at"/> a tool could stand on (of <paramref name="mask"/>), other
    /// than the tool itself; null if nothing.</summary>
    private RayHit? SurfaceUnder(SurgicalTool tool, Vector3 at,
        uint mask = 1 | PatientBody.SurfaceLayer | Drape.DrapeLayer | SurgicalTool.ToolLayer)
        => Rays.Cast(tool, at + Vector3.Up * 0.2f, at + Vector3.Down * 2f, mask, tool.GetRid());

    /// <summary>Host: a new tool falling from <paramref name="at"/>, as if it was dropped there (the floor soils it).
    /// </summary>
    public void DropNew(string id, Vector3 at) => _tools[Spawn(id, at)].Falling = true;

    /// <summary>Host: a skin graft cut from the patient, held at the tip of the tool that lifted it off. One piece
    /// covers one spot.</summary>
    public void GiveGraft(int byUid)
    {
        if (ByUid(byUid) is not { } by)
        {
            return;
        }
        var graft = _tools[Spawn("skin_graft", by.TipPosition())];
        graft.Charges = 1;
        Carry(graft, by);
    }

    /// <summary>Host: the hand lets go of a self-retaining tool, which keeps its hold where it is. One holding the skin
    /// (a retractor's hook) lies down on the body instead (LyingFromHold()).</summary>
    public void LeaveStanding(SurgicalTool tool)
    {
        Session.SetAttached(tool.Holder, tool.Slot, false);
        var pose = tool.GlobalTransform;
        var lying = tool.Hold is SkinHold;
        if (tool.Hold is SkinHold skin)
        {
            // The skin stays held where the hand left it (in the site's space, so breathing doesn't pull it), the tool
            // lying on top of it.
            tool.Hold = skin with { Hold = Session.Patient.Body.Site.ToLocal(tool.TipPosition()) };
            pose = LyingFromHold(tool, skin);
        }
        Rpc(MethodName.SetToolState, tool.Uid, (int)ToolState.Standing, tool.Holder, -1, pose);
        if (lying)
        {
            Rpc(MethodName.RideSite, tool.Uid);
        }
    }

    /// <summary>A tool lying on the patient rises and falls with the site as the patient breathes
    /// (<see cref="SurgicalTool.Ride"/>).</summary>
    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RideSite(int uid) => ByUid(uid)?.Ride(Session.Patient.Body.Site);

    /// <summary>
    /// Where a tool holding the skin lies once let go of: its tip on the body over where it holds (the skin stays held
    /// where the hand left it, see LeaveStanding()), and the rest of it lying along the body with its handle pointing
    /// straight away from where it took hold: the way it pulled, or away from the cut if it wasn't pulled.
    /// </summary>
    public Transform3D LyingFromHold(SurgicalTool tool, SkinHold hold)
    {
        var body = Session.Patient.Body;
        var tip = tool.TipPosition();
        tip.Y = BodyUnder(tool, tip) + LyingClearance;
        var flat = new Vector3(1, 0, 1);
        var away = (tip - body.UvToWorld(hold.Anchor)) * flat;
        if (away.Length() < 0.005f && Session.Patient.WoundWithId(hold.Wound) is { } cut)
        {
            away = (body.UvToWorld(hold.Anchor) - body.UvToWorld(cut.ClosestPoint(hold.Anchor))) * flat;
        }
        if (away.Length() < 0.001f)
        {
            away = tool.GlobalBasis.Z * flat;
        }
        away = away.Normalized();
        var back = (away + Vector3.Up * SlopeAlong(tool, tip, away)).Normalized();
        var side = Vector3.Up.Cross(back).Normalized();
        return new Transform3D(new Basis(side, back.Cross(side), back), tip + back * tool.Def.Length);
    }

    /// <summary>How steeply (rise over run) a tool lying from <paramref name="tip"/> toward <paramref name="along"/> (a
    /// direction across the floor) has to slope to rest on the body without sinking into it. Negative where it slopes
    /// down. Tilted, it reaches less far across the floor than its length, so the slope is found again where it then
    /// lies.</summary>
    private float SlopeAlong(SurgicalTool tool, Vector3 tip, Vector3 along)
    {
        var slope = 0f;
        for (var i = 0; i < 3; i++)
        {
            var reach = tool.Def.Length / Mathf.Sqrt(1f + slope * slope);
            slope = LyingSamples.Max(t => (BodyUnder(tool, tip + along * reach * t) - tip.Y) / (reach * t));
        }
        return slope;
    }

    /// <summary>Host: the tool leaves the hand and wraps around a limb (a tourniquet), see
    /// <see cref="PatientBody.LimbRingAt"/>.</summary>
    public void Wrap(SurgicalTool tool, LimbRing ring)
    {
        LeaveStanding(tool);
        Rpc(MethodName.WrapOnPeer, tool.Uid, ring.Center, ring.Axis, ring.Radius);
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void WrapOnPeer(int uid, Vector3 center, Vector3 axis, float radius) => ByUid(uid)?.WrapAround(center, axis, radius);

    public void Carry(SurgicalTool item, SurgicalTool by) =>
        Rpc(MethodName.SetToolState, item.Uid, (int)ToolState.Carried, by.Uid, -1, item.GlobalTransform);

    public void DropCarried(SurgicalTool by)
    {
        if (CarriedBy(by) is { } item)
        {
            LetFall(item);
        }
    }

    private void LetFall(SurgicalTool tool)
    {
        Rpc(MethodName.SetToolState, tool.Uid, (int)ToolState.Free, 0, -1, tool.GlobalTransform);
        tool.Falling = true;
    }

    /// <summary>Host: exact amount here, everyone else sees it change in FillSteps (and the moment it runs dry).
    /// </summary>
    public void SetFill(SurgicalTool tool, float amount)
    {
        var step = Mathf.CeilToInt(tool.Fill * FillSteps);
        tool.Fill = Mathf.Clamp(amount, 0f, 1f);
        if (Mathf.CeilToInt(tool.Fill * FillSteps) != step)
        {
            Rpc(MethodName.ShowFill, tool.Uid, tool.Fill);
        }
    }

    /// <summary>Host: moves up to <paramref name="amount"/> ml of liquid out of a syringe, vial or dish, into another
    /// one or (to null) out of it. Drugs go along in proportion, so a mix stays mixed. Returns what moved: drug id ->
    /// amount in its unit.</summary>
    public Dictionary<string, float> Transfer(SurgicalTool from, SurgicalTool? to, float amount)
    {
        var moved = new Dictionary<string, float>();
        amount = Mathf.Min(amount, from.Ml);
        if (amount <= 0f)
        {
            return moved;
        }
        var share = amount / from.Ml;
        foreach (var drug in from.Contents.Keys.ToList())
        {
            moved[drug] = from.Contents[drug] * share;
            from.Contents[drug] -= moved[drug];
        }
        AddLiquid(from, -amount);
        if (to is not null)
        {
            AddLiquid(to, amount, moved);
        }
        return moved;
    }

    /// <summary>
    /// Host: adds <paramref name="ml"/> of liquid holding <paramref name="drugs"/> (drug id -> amount, "blood" in ml)
    /// and <paramref name="air"/> ml of air to a syringe, vial or dish. Negative takes away (the contents are taken out
    /// by the caller). Everyone sees the exact result: the host at once, the others within SyncInterval
    /// (see SendLiquids()).
    /// </summary>
    public void AddLiquid(SurgicalTool tool, float ml, IReadOnlyDictionary<string, float>? drugs = null, float air = 0f)
    {
        foreach (var (drug, amount) in drugs ?? new Dictionary<string, float>())
        {
            tool.Contents[drug] = tool.Contents.GetValueOrDefault(drug) + amount;
        }
        tool.Ml += ml;
        tool.Air = Mathf.Max(tool.Air + air, 0f);
        if (tool.Ml <= 0.0001f)
        {
            tool.Ml = 0f;
            tool.Contents.Clear();
        }
        tool.Fill = tool.Ml / tool.Def.Volume;
        float Share(string drug) => tool.Ml > 0f ? tool.Contents.GetValueOrDefault(drug) / tool.Ml : 0f;
        ShowLiquid(tool.Uid, tool.Ml, tool.Air, Share("blood"), Share("iodine"));
        _liquidChanged.Add(tool.Uid);
    }

    /// <summary>Host: what changed in liquids goes to everyone else at most every SyncInterval, as it is then: a pour or
    /// a drip changes it every frame. The first change after a quiet spell goes at once.</summary>
    private void SendLiquids(float delta)
    {
        _liquidAcc += delta;
        if (_liquidAcc < SyncInterval || _liquidChanged.Count == 0)
        {
            return;
        }
        _liquidAcc = 0f;
        foreach (var uid in _liquidChanged)
        {
            if (ByUid(uid) is { } tool)
            {
                Rpc(MethodName.ShowLiquid, uid, tool.Ml, tool.Air, tool.Red, tool.Iodine);
            }
        }
        _liquidChanged.Clear();
    }

    public void Consume(SurgicalTool tool)
    {
        if (tool.State == ToolState.Held)
        {
            Session.SetAttached(tool.Holder, tool.Slot, false);
        }
        Rpc(MethodName.SetToolState, tool.Uid, (int)ToolState.Consumed, tool.Holder, -1, tool.GlobalTransform);
    }

    /// <summary>Drinks from (or wears) what's in the hand. Returns its tool id, null when there's nothing to drink.
    /// </summary>
    public string? Drink(int peer, int hand) =>
        ToolInHand(peer, hand) is { Def.Drinkable: true } tool && UseCharge(tool) ? tool.Def.Id : null;

    /// <summary>Takes one use, consuming the tool on its last one. False when it was already empty.</summary>
    public bool UseCharge(SurgicalTool tool)
    {
        if (tool.Charges == 0)
        {
            return false;
        }
        tool.Charges--;
        if (tool.Charges == 0)
        {
            Consume(tool);
        }
        return true;
    }

    /// <summary>Host: a surgeon left, so everything in their hands and on their belt drops. Standing clamps keep
    /// holding.</summary>
    public void DropAll(int peer)
    {
        foreach (var tool in _tools.Values.Where(t => t.Holder == peer && t.State is ToolState.Held or ToolState.Belt).ToList())
        {
            if (tool.Hold is not null)
            {
                Session.Patient.ReleaseGrip(tool.Uid, tool.Hold, false);
                tool.Hold = null;
            }
            Rpc(MethodName.SetToolState, tool.Uid, (int)ToolState.Free, 0, -1, tool.GlobalTransform);
            tool.Falling = true;
        }
    }

    /// <summary>Tools left inside the patient.</summary>
    public int RetainedCount() => _tools.Values.Count(t => t.State == ToolState.Inside);

    /// <summary>
    /// Called by a surgeon right after it walked and moved its hands: what it holds, has on its belt or carries at a
    /// tool's tip goes there at once. Tools update before the surgeons (ToolActions works on last frame's hands), so
    /// placed only then they'd trail a frame behind their hand, plain to see while walking.
    /// </summary>
    /// <summary>Reused by <see cref="Follow"/> every frame.</summary>
    private readonly HashSet<int> _followed = [];

    public void Follow(Surgeon surgeon)
    {
        var moved = _followed;
        moved.Clear();
        foreach (var tool in _tools.Values.Where(t => t.Holder == surgeon.PeerId && t.State is ToolState.Held or ToolState.Belt))
        {
            Place(tool);
            moved.Add(tool.Uid);
        }
        foreach (var tool in _tools.Values.Where(t => t.State == ToolState.Carried && moved.Contains(t.Holder)))
        {
            Place(tool);
        }
    }

    /// <summary>Puts a held, belted or carried tool where its holder has it now.</summary>
    private void Place(SurgicalTool tool)
    {
        if (tool.State == ToolState.Carried && ByUid(tool.Holder) is { } by)
        {
            // Centered on the carrier's tip.
            tool.GlobalTransform = new Transform3D(by.GlobalBasis, by.TipPosition() + by.GlobalBasis.Z * tool.Def.Length * 0.5f);
            return;
        }
        if (Session.Surgeons.GetValueOrDefault(tool.Holder) is not { } surgeon)
        {
            return;
        }
        // A spreader set in a wound stays where it went in: the hand holds it there (Surgeon.HoldInWound()).
        if (tool.State == ToolState.Held && !tool.InWound)
        {
            tool.GlobalTransform = surgeon.Hands[tool.Slot].GripTransform();
        }
        else if (tool.State == ToolState.Belt)
        {
            tool.GlobalTransform = surgeon.BeltTransform(tool.Slot);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (Surgery.Current is not { } surgery)
        {
            return;
        }
        var dt = (float)delta;
        foreach (var tool in _tools.Values)
        {
            Place(tool);
        }
        if (!Multiplayer.IsServer())
        {
            return;
        }
        SendLiquids(dt);
        if (!surgery.Running)
        {
            return;
        }
        foreach (var tool in _tools.Values.ToList())
        {
            if (tool.State != ToolState.Held)
            {
                Syringe.ReportPushed(tool);
            }
            switch (tool.State)
            {
                case ToolState.Held:
                    // Paused, not released: a lag spike must not fire a charged defibrillator or drop a clamp's grip.
                    if (surgery.Surgeons.GetValueOrDefault(tool.Holder) is { } surgeon && !surgeon.IsStalled())
                    {
                        ToolActions.Update(tool, surgeon.HandInput(tool.Slot), surgery.Patient, dt);
                    }
                    break;
                case ToolState.Standing:
                    ToolActions.UpdateStanding(tool, surgery.Patient, dt);
                    break;
                case ToolState.Free:
                    CheckDrop(tool);
                    break;
                case ToolState.Carried:
                    if (ByUid(tool.Holder) is not { State: ToolState.Held })
                    {
                        LetFall(tool);
                    }
                    break;
            }
        }
        _syncAcc += dt;
        if (_syncAcc >= SyncInterval)
        {
            _syncAcc = 0f;
            var moving = new Godot.Collections.Array();
            foreach (var tool in _tools.Values.Where(t => t.State == ToolState.Free && !t.Sleeping))
            {
                moving.Add(new Godot.Collections.Array { tool.Uid, tool.GlobalTransform });
            }
            if (moving.Count > 0)
            {
                Rpc(MethodName.SyncFree, moving);
            }
        }
    }

    /// <summary>Where a dropped tool ended up decides what it costs you.</summary>
    private void CheckDrop(SurgicalTool tool)
    {
        if (!tool.Falling)
        {
            return;
        }
        var patient = Session.Patient;
        var probe = patient.Body.Probe(tool.GlobalPosition);
        if (probe.Zone == SiteZone.Cavity)
        {
            tool.Falling = false;
            Rpc(MethodName.SetToolState, tool.Uid, (int)ToolState.Inside, 0, -1, tool.GlobalTransform);
            Session.Scoring.Add("dropped_in_cavity");
            Session.Sound("tool_drop_flesh", tool.GlobalPosition);
            if (tool.Def.Action is "cut" or "saw")
            {
                patient.CutCavity(probe.Uv, probe.Depth, 1f, !tool.Sterile, 2f);
            }
            if (!tool.Sterile)
            {
                patient.ContaminateSite("");
            }
            return;
        }
        foreach (var body in tool.GetCollidingBodies())
        {
            var onFloor = body.IsInGroup(FloorGroup) || (body is SurgicalTool other && other.GlobalPosition.Y < FloorPile);
            if (onFloor && tool.Def.Fragile)
            {
                tool.Falling = false;
                Consume(tool);
                Session.Scoring.Add("broken_syringe");
                Session.Sound("glass_break", tool.GlobalPosition);
                Session.Announce($"The {tool.Def.Name} shatters on the floor.");
                return;
            }
            if (onFloor)
            {
                tool.Falling = false;
                Rpc(MethodName.SetSterile, tool.Uid, false);
                Rpc(MethodName.SetSoiled, tool.Uid, true);
                Session.Scoring.Add("dropped_tool");
                Session.Sound("tool_drop_metal", tool.GlobalPosition);
                return;
            }
            if (body is BodyPart)
            {
                tool.Falling = false;
                if (tool.Def.IsHeavy)
                {
                    patient.HeavyDrop(tool.GlobalPosition);
                }
                return;
            }
        }
        if (tool.Sleeping)
        {
            tool.Falling = false;
        }
    }

    // --- Everyone -----------------------------------------------------------------------------------------

    private SurgicalTool Create(int uid, string id, Transform3D transform)
    {
        var def = Db.Tool(id);
        if (def is null)
        {
            GD.PushWarning($"Unknown tool id '{id}'");
            def = Db.Tool("gauze")!;
        }
        var tool = new SurgicalTool();
        AddChild(tool);
        tool.Setup(uid, def);
        tool.GlobalTransform = transform;
        _tools[uid] = tool;
        _nextUid = Math.Max(_nextUid, uid + 1);
        if (Surgery.Current?.Scenario is { DirtyStart: true } && def.Action is not ("syringe" or "vial"))
        {
            tool.Sterile = false;
        }
        return tool;
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SpawnOnPeer(int uid, string id, Vector3 at) => Create(uid, id, new Transform3D(Basis.Identity, at));

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetToolState(int uid, int state, int holder, int slot, Transform3D transform)
    {
        if (ByUid(uid) is not { } tool)
        {
            return;
        }
        // The state first, and the transform into the physics engine too: changing how a body is frozen makes the
        // engine write its own last transform back (a retractor let go of would stand back up in its held pose).
        tool.SetState((ToolState)state, holder, slot);
        tool.GlobalTransform = transform;
        PhysicsServer3D.BodySetState(tool.GetRid(), PhysicsServer3D.BodyState.Transform, transform);
        if ((ToolState)state == ToolState.Held)
        {
            Sfx.Play("tool_pickup", transform.Origin);
        }
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetSterile(int uid, bool value)
    {
        if (ByUid(uid) is { } tool)
        {
            tool.Sterile = value;
            tool.ShowContamination(Session.LocalSurgeon?.Mods.Flag("contamination_vision") == true);
        }
    }

    /// <summary>Host: builds up blood on a tool working in blood; everyone sees it in steps of a quarter.</summary>
    public void AddBlood(SurgicalTool tool, float amount)
    {
        tool.BloodExposure = Mathf.Min(tool.BloodExposure + amount, 1f);
        var shown = Mathf.Snapped(tool.BloodExposure, 0.25f);
        if (shown > tool.Blood)
        {
            Rpc(MethodName.SetBlood, tool.Uid, shown);
        }
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetBlood(int uid, float amount) => ByUid(uid)?.SetBlood(amount);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ShowFill(int uid, float amount)
    {
        if (ByUid(uid) is not { } tool)
        {
            return;
        }
        if (!Multiplayer.IsServer())
        {
            tool.Fill = amount;
        }
        tool.ShowFill(amount);
    }

    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ShowLiquid(int uid, float ml, float air, float red, float iodine)
    {
        if (ByUid(uid) is not { } tool)
        {
            return;
        }
        tool.Ml = ml;
        tool.Air = air;
        tool.Red = red;
        tool.Iodine = iodine;
        tool.Fill = ml / tool.Def.Volume;
        tool.ShowLiquid();
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetSoiled(int uid, bool value) => ByUid(uid)?.SetSoiled(value);

    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void SyncFree(Godot.Collections.Array moving)
    {
        foreach (var entry in moving.Select(item => item.AsGodotArray()))
        {
            if (ByUid(entry[0].AsInt32()) is { State: ToolState.Free } tool)
            {
                tool.GlobalTransform = entry[1].AsTransform3D();
            }
        }
    }
}
