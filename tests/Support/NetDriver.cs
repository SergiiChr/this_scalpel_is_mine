namespace Scalpel.Tests.Support;

/// <summary>
/// Two players in one surgery: the client crouches, cuts with the scalpel and hands it across the table to the host.
/// Each side then prints the state that must come out the same on both (the painted wound map, cut, stitched and torn
/// springs, the site's shape) for the network runner to compare. The host also drops a tool and sends the client where
/// it came to rest and where the cut bleeds, which the client checks against its own copies: both cross the network
/// packed, so a wrong stride or column order shows here and nowhere else.
/// </summary>
public partial class NetDriver : NetSession {
    private const int Port = 24599;
    private const float SquatTimeout = 15f;
    /// <summary>On the floor beyond the table's foot, out of everyone's way.</summary>
    private static readonly Vector3 DropFrom = new(Room.TableFoot - 0.4f, 1f, 0f);
    private bool _squatSeen;
    private bool _comparedWithHost;

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void AcknowledgeSquat() => _squatSeen = true;

    protected override async Task Drive() {
        // This checks synchronization, not missing starter tools or random surgeon handicaps.
        SurgeryState.ScenarioHasAllStarterTools("appendectomy");
        var surgery = await Join("appendectomy", Port, () => SurgeryState.NetworkSessionHasOrdinarySurgeons());
        GD.Print($"[{Role}] surgery running, surgeons={surgery.Surgeons.Count} tools={surgery.Tools.Tools.Count}");
        await PassCardAround(surgery);
        SurgicalTool? dropped = null;
        if (IsHost) {
            await CheckSquatAndTakeTool(surgery);
            dropped = surgery.Tools.DropNew("gauze", DropFrom);
        } else if (!await SquatCutAndHandOver(surgery)) {
            GetTree().Quit(1);
            return;
        }
        await ReportState(surgery);
        if (dropped is not null) {
            await SendComparison(surgery, dropped);
        } else {
            await WaitForComparison();
        }
        await Wait(1.0);
        GetTree().Quit();
    }

    /// <summary>Host: once the dropped tool has come to rest, where it lies and where the patient bleeds, to the client.
    /// </summary>
    private async Task SendComparison(Surgery surgery, SurgicalTool dropped) {
        for (var i = 0; i < 40 && !dropped.Sleeping; i++) {
            await Wait(0.2);
        }
        Check(dropped.Sleeping, "the dropped tool came to rest");
        RpcId(Partner(surgery).PeerId, MethodName.CompareWithHost, BloodSummary(surgery), dropped.Uid, dropped.GlobalTransform);
    }

    /// <summary>Client: the host's report arrives after its own tool has settled.</summary>
    private async Task WaitForComparison() {
        for (var i = 0; i < 75 && !_comparedWithHost; i++) {
            await Wait(0.2);
        }
        Check(_comparedWithHost, "the host reported its blood and dropped tool");
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CompareWithHost(string blood, int droppedUid, Transform3D rest) {
        var surgery = Surgery.Current!;
        var mine = BloodSummary(surgery);
        Check(mine == blood, $"bleed sources and cut pools match the host's: {blood}" + (mine == blood ? "" : $", here {mine}"));
        Check(blood.Contains("sources=[("), "the host's cut bleeds, so sources crossed the network");
        var here = surgery.Tools.ByUid(droppedUid)?.GlobalTransform;
        // The last sync precedes the rest by up to a sync interval of creeping; a wrong column lands meters off.
        var rests = here is { } t && t.Origin.DistanceTo(rest.Origin) < 0.05f && t.Basis.X.Dot(rest.Basis.X) > 0.9f
            && t.Basis.Y.Dot(rest.Basis.Y) > 0.9f;
        Check(rests, $"the dropped tool rests where it does on the host: {rest.Origin}, here {here?.Origin}");
        _comparedWithHost = true;
    }

    /// <summary>The bleed sources and cut pools by what stays put between syncs: where they are, whether inside, a
    /// point bleed, a pool's floor. Rates and fills change every tick.</summary>
    private static string BloodSummary(Surgery surgery) {
        var blood = surgery.Patient.Body.Blood;
        var sources = blood.Sources.Select(source =>
            $"({source.Uv.X:0.000},{source.Uv.Y:0.000},{(source.Inside ? "inside" : "skin")}{(source.Point ? ",point" : "")})");
        var pools = blood.Pools.Select(pool => $"({pool.Uv.X:0.000},{pool.Uv.Y:0.000},floor={pool.Floor:0.00})");
        return $"sources=[{string.Join(" ", sources)}] pools=[{string.Join(" ", pools)}]";
    }

    /// <summary>
    /// Both start reading their own copy of the patient card, each seeing the other's at their face and none on the
    /// hook. Both put theirs back and they hang there as one. Then the client takes it: the host sees it in the
    /// client's hands, not on the hook, and can't take it, until the client puts it back.
    /// </summary>
    private async Task PassCardAround(Surgery surgery) {
        var card = surgery.Room.Card;
        var me = surgery.LocalSurgeon!;
        var partner = Partner(surgery);
        var hook = surgery.Room.FindChildren("*", "", true, false).OfType<Interactable>()
            .First(spot => spot.Prompt == "Read the patient card");
        // The partner's view and card arrive with their first synced states: on a busy machine that takes a while.
        bool partnerReads() =>
            !card.OnHook && card.PaperOf(partner.PeerId)?.DistanceTo(partner.Camera.GlobalPosition) < 0.5f;
        for (var i = 0; i < 50 && !partnerReads(); i++) {
            await Wait(0.2);
        }
        Check(partnerReads(), "at the start the partner reads their own copy and none hangs on the hook");
        await SurgeryDriver.PlayerPutsCardBack(surgery);
        for (var i = 0; i < 50 && card.TravelOf(partner.PeerId) > 0f; i++) {
            await Wait(0.2);
        }
        Check(card.OnHook && card.PaperOf(me.PeerId) is null && card.PaperOf(partner.PeerId) is null,
            "both copies put back hang on the hook as one");
        if (IsHost) {
            for (var i = 0; i < 50 && card.TravelOf(partner.PeerId) < 1f; i++) {
                await Wait(0.2);
            }
            Check(!card.OnHook && !hook.OfferedTo(me)
                && card.PaperOf(partner.PeerId)?.DistanceTo(partner.Camera.GlobalPosition) < 0.5f,
                "the client's card is in their hands, not on the hook");
            for (var i = 0; i < 50 && !card.OnHook; i++) {
                await Wait(0.2);
            }
            Check(card.OnHook && hook.OfferedTo(me), "the client's card is back on the hook");
        } else {
            await Wait(1.0);
            hook.Interact(me);
            await Wait(2.0);
            await SurgeryDriver.PlayerPutsCardBack(surgery);
        }
    }

    private void Check(bool passed, string claim) => GD.Print(passed ? $"[{Role}] {claim}" : $"FAIL: [{Role}] {claim}");

    /// <summary>Client: a real crouch the host must see as a grounded squat, then a cut with the scalpel and the
    /// scalpel handed across the table. False when something didn't arrive.</summary>
    private async Task<bool> SquatCutAndHandOver(Surgery surgery) {
        var me = surgery.LocalSurgeon!;
        // Deliberately finish the client's startup after the host's former 1.2-second window. The acknowledged pose
        // must still be checked: this exercises the loading-delay race deterministically on every network run.
        await Wait(2.0);
        // A real input crouch must reach the host as a grounded, articulated squat before continuing surgery. Kept until
        // the host has actually checked it: loading and sync can take longer than any fixed hold.
        PlayerInput.Action(InputActions.Crouch);
        var deadline = Time.GetTicksMsec() + (ulong)(SquatTimeout * 1000f);
        while (!_squatSeen && Time.GetTicksMsec() < deadline) {
            await Wait(0.05);
        }
        PlayerInput.Action(InputActions.Crouch, false);
        if (!_squatSeen) {
            GD.Print("FAIL: [client] host never acknowledged the grounded squat");
            return false;
        }
        await Wait(0.4);
        // Like a player, wait a moment for the scalpel on the tray: it may still be filling in. Not long: the host only
        // waits so long for the handoff before it ends the session. The scalpel specifically, so this can't silently
        // switch to the switchblade's different cutting.
        SurgicalTool? scalpel = null;
        for (var i = 0; i < 20 && scalpel is null; i++) {
            scalpel = surgery.Tools.Tools.Values.FirstOrDefault(tool => tool.State == ToolState.Free && tool.Def.Id == "scalpel");
            if (scalpel is null) {
                await Wait(0.1);
            }
        }
        if (scalpel is null) {
            GD.Print("FAIL: [client] starter scalpel never arrived");
            return false;
        }
        surgery.Tools.RequestGrab(scalpel, 1);
        await Wait(0.5);
        GD.Print($"[client] holding: {me.HeldTool(1)?.Def.Id ?? "nothing"}");
        var site = surgery.Patient.Body.Site.GlobalPosition;
        var hand = me.Hands[1];
        // The hand sits behind the tip: start it short of the site so the blade lands in the middle.
        hand.LocalTarget = me.ToLocal(site + new Vector3(0.06f, 0.12f, -0.08f));
        hand.Level = 3;
        hand.Lowered = true;
        // Rotated a quarter turn, the blade's edge runs sideways: along the cut.
        hand.Twist = Mathf.Pi / 2f;
        for (var i = 0; i < 60; i++) {
            hand.LocalTarget += new Vector3(0.002f, 0f, 0f);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        hand.Lowered = false;
        hand.Twist = 0f;
        await Wait(1.0);
        // Hand the tool across the table: both surgeons reach over the patient. 12 cm apart: close enough to pass
        // (Surgeon.PassDistance), not so close the hands bump and drop it.
        hand.Level = 0;
        hand.LocalTarget = me.ToLocal(new Vector3(0f, 1.3f, -0.06f));
        await Wait(1.5);
        me.Active = 1;
        PlayerInput.Tap(InputActions.Grab);
        await Wait(1.0);
        GD.Print($"[client] after handoff, holding: {me.HeldTool(1)?.Def.Id ?? "nothing"}");
        return true;
    }

    /// <summary>Host: the client's crouch arrives as bent knees and grounded heels; then the left hand waits over the
    /// table for the scalpel.</summary>
    private async Task CheckSquatAndTakeTool(Surgery surgery) {
        var me = surgery.LocalSurgeon!;
        var partner = Partner(surgery);
        var deadline = Time.GetTicksMsec() + (ulong)(SquatTimeout * 1000f);
        while (partner.Crouch <= 0.99f && Time.GetTicksMsec() < deadline) {
            await Wait(0.02);
        }
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        var hip = partner.Joint("LegL").GlobalPosition;
        var knee = partner.Joint("ShinL").GlobalPosition;
        var shoe = partner.Joint("ShoeL");
        var squat = partner.Crouch >= 0.99f && hip.Y < knee.Y
            && Mathf.Abs(shoe.GlobalPosition.Y - Surgeon.AnkleHeight) <= 0.002f && shoe.GlobalBasis.Y.Dot(Vector3.Up) >= 0.999f;
        GD.Print(squat ? "[host] client squat has bent knees and grounded heels"
            : "FAIL: [host] client squat lost its bent knees or grounded heels");
        RpcId(partner.PeerId, MethodName.AcknowledgeSquat);
        me.Hands[0].LocalTarget = me.ToLocal(new Vector3(0f, 1.3f, 0.06f));
        // Until the client has cut and handed its tool across: on a slow machine that takes a while.
        for (var i = 0; i < 75 && me.HeldTool(0) is null; i++) {
            await Wait(0.2);
        }
        await Wait(1.0);
        GD.Print($"[host] partner handed me: {me.HeldTool(0)?.Def.Id ?? "nothing"}");
    }

    /// <summary>Prints what both peers must agree on, once the cut has settled on both, and checks the skin meets the
    /// body model.</summary>
    private async Task ReportState(Surgery surgery) {
        // Without the wait, a freshly imported project can catch the same transient raised seam on both peers.
        await Wait(2.0);
        var body = surgery.Patient.Body;
        var tissue = body.Tissue;
        var surgeonWounds = surgery.Patient.Wounds.Count(wound => wound.MadeBySurgeon);
        var map = body.WoundMap;
        var painted = 0;
        for (var y = 0; y < map.Size; y += 4) {
            for (var x = 0; x < map.Size; x += 4) {
                var uv = new Vector2(x, y) / map.Size;
                painted += map.Value(WoundMap.Layer.Wounds, WoundMap.Cut, uv) > 0.1f ? 1 : 0;
            }
        }
        var severed = Enumerable.Range(0, tissue.SpringCount).Count(s => !tissue.SpringAt(s).Active);
        GD.Print($"[{Role}] surgeon wounds (host state)={surgeonWounds}, painted texels={painted}, severed springs={severed}, topology={tissue.TopologyHash()}, site shape={body.ShapeHash()}, vitals hr={(int)surgery.Patient.Vitals.HeartRate}");
        // Each player's simulated skin meets the body model where the cut opened it, without a step.
        var seam = SkinSeam.Measure(body);
        if (seam >= SkinSeam.Max) {
            GD.Print($"FAIL: [{Role}] the simulated skin doesn't meet the body model: {seam * 1000f:0.00} mm off at its edge");
        }
    }
}
