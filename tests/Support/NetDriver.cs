namespace Scalpel.Tests.Support;

/// <summary>
/// Two players in one surgery: the client crouches, cuts with the scalpel and hands it across the table to the host.
/// Each side then prints the state that must come out the same on both (the painted wound map, cut, stitched and torn
/// springs, the site's shape) for the network runner to compare. The host also drops a tool and sends the client where
/// it came to rest and where the cut bleeds, which the client checks against its own copies: both cross the network
/// packed, so a wrong stride or column order shows here and nowhere else.
/// Neither side waits a fixed time for the other: it waits until it sees the other's state or hears it's done (Tell).
/// </summary>
public partial class NetDriver : NetSession {
    private const int Port = 24599;
    /// <summary>Game seconds the cut settles before its state is reported. Without it, a freshly imported project can
    /// catch the same transient raised seam on both peers.</summary>
    private const float Settle = 2f;
    /// <summary>On the floor beyond the table's foot, out of everyone's way.</summary>
    private static readonly Vector3 DropFrom = new(Room.TableFoot - 0.4f, 1f, 0f);
    private readonly HashSet<string> _heard = [];
    private string? _hostState;
    private (string Blood, int DroppedUid, Transform3D Resting)? _hostComparison;

    /// <summary>The other side has done <paramref name="step"/>.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Told(string step) => _heard.Add(step);

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void HostReported(string state) => _hostState = state;

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void CompareWithHost(string blood, int droppedUid, Transform3D rest) =>
        _hostComparison = (blood, droppedUid, rest);

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
        } else {
            await SquatCutAndHandOver(surgery);
        }
        await ReportState(surgery);
        if (dropped is not null) {
            await SendComparison(surgery, dropped);
        } else {
            await CompareWithHost(surgery);
        }
        GetTree().Quit();
    }

    /// <summary>Host: once the dropped tool has come to rest, where it lies and where the patient bleeds, to the client.
    /// </summary>
    private async Task SendComparison(Surgery surgery, SurgicalTool dropped) {
        await Until(() => dropped.Sleeping, "the dropped tool coming to rest",
            () => $"at {dropped.GlobalPosition} moving {dropped.LinearVelocity.Length()} m/s");
        RpcId(Partner(surgery).PeerId, MethodName.CompareWithHost, BloodSummary(surgery), dropped.Uid, dropped.GlobalTransform);
        // Until the client has compared: what it compares with reaches it only while the host runs.
        await Heard("compared");
    }

    /// <summary>Client: its own bleed sources, cut pools and dropped tool once they have caught up with the host's.
    /// </summary>
    private async Task CompareWithHost(Surgery surgery) {
        await Until(() => _hostComparison is not null, "the host's blood and dropped tool arriving", () => "nothing yet");
        var (blood, droppedUid, rest) = _hostComparison!.Value;
        Check(blood.Contains("sources=[("), "the host's cut bleeds, so sources crossed the network");
        await Until(() => BloodSummary(surgery) == blood, "the bleed sources and cut pools matching the host's",
            () => $"host {blood}, here {BloodSummary(surgery)}");
        Check(true, $"bleed sources and cut pools match the host's: {blood}");
        // The last sync precedes the rest by up to a sync interval of creeping; a wrong column lands meters off.
        bool rests() => surgery.Tools.ByUid(droppedUid)?.GlobalTransform is { } here
            && here.Origin.DistanceTo(rest.Origin) < 0.05f && here.Basis.X.Dot(rest.Basis.X) > 0.9f
            && here.Basis.Y.Dot(rest.Basis.Y) > 0.9f;
        await Until(rests, "the dropped tool resting where it does on the host",
            () => $"host {rest.Origin}, here {surgery.Tools.ByUid(droppedUid)?.GlobalTransform.Origin}");
        Check(true, $"the dropped tool rests where it does on the host: {rest.Origin}");
        Tell(surgery, "compared");
        // Long enough to send it before quitting: nothing here waits on the host any more.
        await Wait(1.0);
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

    private void Tell(Surgery surgery, string step) => RpcId(Partner(surgery).PeerId, MethodName.Told, step);

    private Task Heard(string step) => Until(() => _heard.Contains(step), $"the partner telling '{step}'",
        () => $"heard [{string.Join(", ", _heard)}]");

    /// <summary>
    /// Both start reading their own copy of the patient card, each seeing the other's at their face and none on the
    /// hook. Both put theirs back and they hang there as one. Then the client takes it: the host sees it in the
    /// client's hands, not on the hook, and can't take it, until the client puts it back.
    /// </summary>
    private async Task PassCardAround(Surgery surgery) {
        var card = surgery.Room.Card;
        var me = surgery.LocalSurgeon!;
        var partner = Partner(surgery);
        var peer = partner.PeerId;
        var hook = surgery.Room.FindChildren("*", "", true, false).OfType<Interactable>()
            .First(spot => spot.Prompt == "Read the patient card");
        string seen() => $"states received={partner.HandStates}, reading={partner.ReadingCard}, "
            + $"travel={card.TravelOf(peer)}, paper={card.PaperOf(peer)}, eyes={partner.Camera.GlobalPosition}, "
            + $"on hook={card.OnHook}";
        // The partner's copy has got where their last state says it goes: at their face reading, else on the hook.
        bool caughtUp() => card.TravelOf(peer) == (partner.ReadingCard ? 1f : 0f);
        // Until their first state arrives, the partner stands at their spawn defaults: nothing to check yet.
        await Until(() => partner.HandStates > 0 && caughtUp(), "the partner's first state", seen);
        Check(!card.OnHook && card.PaperOf(peer)?.DistanceTo(partner.Camera.GlobalPosition) < 0.5f,
            "at the start the partner reads their own copy and none hangs on the hook", seen);
        // Not put back before the partner has seen this one read too.
        Tell(surgery, "card read");
        await Heard("card read");
        await SurgeryDriver.PlayerPutsCardBack(surgery);
        await Until(() => !partner.ReadingCard && caughtUp(), "the partner putting their card back", seen);
        Check(card.OnHook && card.PaperOf(me.PeerId) is null && card.PaperOf(peer) is null,
            "both copies put back hang on the hook as one", seen);
        if (IsHost) {
            Tell(surgery, "card back");
            await Until(() => partner.ReadingCard && caughtUp(), "the client taking the card", seen);
            Check(!card.OnHook && !hook.OfferedTo(me)
                && card.PaperOf(peer)?.DistanceTo(partner.Camera.GlobalPosition) < 0.5f,
                "the client's card is in their hands, not on the hook", seen);
            Tell(surgery, "card taken");
            await Until(() => !partner.ReadingCard && caughtUp(), "the client putting the card back", seen);
            Check(card.OnHook && hook.OfferedTo(me), "the client's card is back on the hook", seen);
        } else {
            // Not before the host has seen both copies on the hook, nor put back before it has seen this one taken.
            await Heard("card back");
            hook.Interact(me);
            await Heard("card taken");
            await SurgeryDriver.PlayerPutsCardBack(surgery);
        }
    }

    /// <summary>Client: a real crouch the host must see as a grounded squat, then a cut with the scalpel and the
    /// scalpel handed across the table.</summary>
    private async Task SquatCutAndHandOver(Surgery surgery) {
        var me = surgery.LocalSurgeon!;
        var partner = Partner(surgery);
        // Deliberately finish the client's startup after the host's former 1.2-second window. The acknowledged pose
        // must still be checked: this exercises the loading-delay race deterministically on every network run.
        await Wait(2.0);
        // A real input crouch must reach the host as a grounded, articulated squat before continuing surgery. Kept until
        // the host has actually checked it: loading and sync can take longer than any fixed hold.
        PlayerInput.Action(InputActions.Crouch);
        await Heard("squat seen");
        PlayerInput.Action(InputActions.Crouch, false);
        await Until(() => me.Crouch == 0f, "the client standing up", () => $"crouch={me.Crouch}");
        // Like a player, wait for the scalpel on the tray: it may still be filling in. The scalpel specifically, so
        // this can't silently switch to the switchblade's different cutting.
        SurgicalTool? scalpel() =>
            surgery.Tools.Tools.Values.FirstOrDefault(tool => tool.State == ToolState.Free && tool.Def.Id == "scalpel");
        await Until(() => scalpel() is not null, "the starter scalpel on the tray",
            () => string.Join(", ", surgery.Tools.Tools.Values.Select(tool => $"{tool.Def.Id}: {tool.State}")));
        var blade = scalpel()!;
        var hand = me.Hands[1];
        surgery.Tools.RequestGrab(blade, 1);
        // Holding: the hand has taken the new tool up, which resets its effort level, so the cut's level isn't lost.
        await Until(() => me.HeldTool(1) == blade && hand.Holding, "the client holding the scalpel",
            () => $"holding {me.HeldTool(1)?.Def.Id ?? "nothing"}, scalpel {blade.State}, hand holding {hand.Holding}");
        GD.Print($"[client] holding: {me.HeldTool(1)?.Def.Id ?? "nothing"}");
        var site = surgery.Patient.Body.Site.GlobalPosition;
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
        // The host works the scalpel: it must see the blade out before the hand swings across the table.
        await Heard("blade out");
        // Hand the tool across the table: both surgeons reach over the patient. 12 cm apart: close enough to pass
        // (Surgeon.PassDistance), not so close the hands bump and drop it.
        hand.Level = 0;
        hand.LocalTarget = me.ToLocal(new Vector3(0f, 1.3f, -0.06f));
        // The host decides the pass from where it sees both hands: until they hold still there, it's fumbled.
        await Heard("in reach");
        me.Active = 1;
        PlayerInput.Tap(InputActions.Grab);
        await Until(() => me.HeldTool(1) is null, "the scalpel leaving the client's hand",
            () => $"holding {me.HeldTool(1)?.Def.Id ?? "nothing"}");
        GD.Print($"[client] after handoff, holding: {me.HeldTool(1)?.Def.Id ?? "nothing"}");
    }

    /// <summary>Host: the client's crouch arrives as bent knees and grounded heels; then the left hand waits over the
    /// table for the scalpel.</summary>
    private async Task CheckSquatAndTakeTool(Surgery surgery) {
        var me = surgery.LocalSurgeon!;
        var partner = Partner(surgery);
        var cutting = partner.Hands[1];
        await Until(() => partner.Crouch >= 0.99f, "the client's squat", () => $"crouch={partner.Crouch}");
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        var hip = partner.Joint("LegL").GlobalPosition;
        var knee = partner.Joint("ShinL").GlobalPosition;
        var shoe = partner.Joint("ShoeL");
        var squat = partner.Crouch >= 0.99f && hip.Y < knee.Y
            && Mathf.Abs(shoe.GlobalPosition.Y - Surgeon.AnkleHeight) <= 0.002f && shoe.GlobalBasis.Y.Dot(Vector3.Up) >= 0.999f;
        Check(squat, squat ? "client squat has bent knees and grounded heels"
            : "client squat lost its bent knees or grounded heels");
        Tell(surgery, "squat seen");
        me.Hands[0].LocalTarget = me.ToLocal(new Vector3(0f, 1.3f, 0.06f));
        await Until(() => cutting.Lowered, "the client lowering the scalpel", () => $"lowered={cutting.Lowered}");
        await Until(() => !cutting.Lowered, "the client lifting the scalpel", () => $"lowered={cutting.Lowered}");
        Tell(surgery, "blade out");
        // What the host checks when the client lets go (ToolManager.PassOnHost): the hands in reach and still.
        await Until(() => partner.PassTarget(1)?.Surgeon == me
            && cutting.Speed <= Surgeon.FumbleSpeed && me.Hands[0].Speed <= Surgeon.FumbleSpeed,
            "the client's scalpel held still within reach",
            () => $"my hand at {me.Hands[0].GlobalPosition}, theirs at {cutting.GlobalPosition} moving {cutting.Speed} m/s");
        Tell(surgery, "in reach");
        await Until(() => me.HeldTool(0) is not null, "the client handing me the scalpel",
            () => $"client holds {partner.HeldTool(1)?.Def.Id ?? "nothing"}, my hand at {me.Hands[0].GlobalPosition}, "
                + $"theirs at {cutting.GlobalPosition}");
        GD.Print($"[host] partner handed me: {me.HeldTool(0)?.Def.Id ?? "nothing"}");
    }

    /// <summary>Prints what both peers must agree on, once the cut has settled on both, and checks the skin meets the
    /// body model. The client reports once its state has caught up with the host's, which hands it across.</summary>
    private async Task ReportState(Surgery surgery) {
        await Frames.Seconds(Settle);
        var body = surgery.Patient.Body;
        if (IsHost) {
            var state = SyncedState(body);
            RpcId(Partner(surgery).PeerId, MethodName.HostReported, state);
            // Until the client has its own report: the host's tissue changes reach it only while the host runs.
            await Heard("reported");
            Report(surgery, state);
        } else {
            await Until(() => _hostState is not null && SyncedState(body) == _hostState, "the host's state arriving",
                () => $"host {_hostState ?? "not reported"}, client {SyncedState(body)}", every: 0.1);
            Report(surgery, SyncedState(body));
            Tell(surgery, "reported");
        }
    }

    private void Report(Surgery surgery, string state) {
        var body = surgery.Patient.Body;
        var surgeonWounds = surgery.Patient.Wounds.Count(wound => wound.MadeBySurgeon);
        GD.Print($"[{Role}] surgeon wounds (host state)={surgeonWounds}, {state}, vitals hr={(int)surgery.Patient.Vitals.HeartRate}");
        if (IsHost) {
            Check(surgeonWounds > 0, "the client's scalpel cut the skin");
        }
        // Each player's simulated skin meets the body model where the cut opened it, without a step.
        var seam = SkinSeam.Measure(body);
        if (seam >= SkinSeam.Max) {
            GD.Print($"FAIL: [{Role}] the simulated skin doesn't meet the body model: {seam * 1000f:0.00} mm off at its edge");
        }
    }

    /// <summary>What must come out the same on both peers: the painted wound map, cut springs, topology and the site's
    /// shape.</summary>
    private static string SyncedState(PatientBody body) {
        var tissue = body.Tissue;
        var map = body.WoundMap;
        var painted = 0;
        for (var y = 0; y < map.Size; y += 4) {
            for (var x = 0; x < map.Size; x += 4) {
                var uv = new Vector2(x, y) / map.Size;
                painted += map.Value(WoundMap.Layer.Wounds, WoundMap.Cut, uv) > 0.1f ? 1 : 0;
            }
        }
        var severed = Enumerable.Range(0, tissue.SpringCount).Count(s => !tissue.SpringAt(s).Active);
        return $"painted texels={painted}, severed springs={severed}, topology={tissue.TopologyHash()}, site shape={body.ShapeHash()}";
    }
}
