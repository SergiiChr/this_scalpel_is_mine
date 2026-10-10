using GodotArray = Godot.Collections.Array;

namespace Scalpel.Surgeons;

/// <summary>The surgeon's state streamed to every peer, the host's messages to the owner, and status events.</summary>
public partial class Surgeon
{
    private Vector3 _netPosition;
    private float _netYaw;
    private bool _remoteOut;

    /// <summary>Puts a remote copy standing at <paramref name="at"/> facing <paramref name="yaw"/>, as if its owner had
    /// streamed it there.</summary>
    internal void PlaceAt(Vector3 at, float yaw)
    {
        GlobalPosition = at;
        _netPosition = at;
        Rotation = Rotation with { Y = yaw };
        _netYaw = yaw;
    }

    /// <summary>The state remote copies follow: body, view, active hand, each hand, who's out or down and who's reading
    /// the card.</summary>
    internal GodotArray PackState()
    {
        // The inner arrays' wrappers are freed once added: the outer array holds them now. Left to the garbage
        // collector, a stream of them at 30 a second makes its pauses long.
        using var hands = new GodotArray();
        foreach (var h in Hands)
        {
            using var hand = new GodotArray
            {
                h.EffectivePosition(), h.Tilt, h.Twist, h.Lowered, h.Trigger, h.Level, h.Lifted, h.Inspecting, h.Turn,
            };
            hands.Add(hand);
        }
        using var strain = new GodotArray { _strain[0], _strain[1] };
        return
        [
            GlobalPosition, Rotation.Y, Pitch, Active, hands, strain, Status.PassedOut > 0f, Crouch, _fallSide, ReadingCard,
        ];
    }

    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    internal void SyncState(GodotArray data)
    {
        _netPosition = data[0].AsVector3();
        _netYaw = data[1].AsSingle();
        Pitch = data[2].AsSingle();
        Active = data[3].AsInt32();
        _remoteOut = data[6].AsBool();
        Crouch = data[7].AsSingle();
        _fallSide = data[8].AsSingle();
        ReadingCard = data[9].AsBool();
        var hands = data[4].AsGodotArray();
        for (var i = 0; i < 2; i++)
        {
            var h = Hands[i];
            var d = hands[i].AsGodotArray();
            h.Target = d[0].AsVector3();
            h.Tilt = d[1].AsSingle();
            h.Twist = d[2].AsSingle();
            h.Lowered = d[3].AsBool();
            h.Trigger = d[4].AsBool();
            h.Level = d[5].AsInt32();
            h.Lifted = d[6].AsBool();
            h.Inspecting = d[7].AsBool();
            h.Turn = d[8].AsSingle();
        }
        if (Multiplayer.IsServer())
        {
            var strain = data[5].AsGodotArray();
            for (var i = 0; i < 2; i++)
            {
                if (strain[i].AsBool())
                {
                    Session.Overstretched(PeerId, i);
                }
            }
        }
    }

    /// <summary>Knocked out, a moan now and then, heard by everyone.</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void Moan() => Sfx.Play("surgeon_moan", _head.GlobalPosition);

    /// <summary>The sink or a fresh pair takes the blood off the gloves. The scrubs keep their stains.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true)]
    public void CleanGloves()
    {
        var sender = Net.Instance.Sender();
        if (sender != Net.HostId && sender != PeerId)
        {
            return;
        }
        foreach (var hand in Hands)
        {
            hand.SetBlood(0f);
        }
    }

    /// <summary>Host tells the owner the tool in a hand bounced off what it was pressed onto
    /// (<see cref="SurgeonHand.Bounce"/>).</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true)]
    public void BounceHand(int hand)
    {
        if (Net.Instance.Sender() == Net.HostId)
        {
            Hands[hand].Bounce();
        }
    }

    /// <summary>Host tells the owner a hand is now holding onto something (or let go). Attached hands don't follow the
    /// body.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true)]
    public void SetHandAttached(int hand, bool value)
    {
        if (Net.Instance.Sender() != Net.HostId)
        {
            return;
        }
        var h = Hands[hand];
        h.Attached = value;
        if (value && HeldTool(hand) is { } tool)
        {
            // Taken back off the patient (a retractor lying hooked), the hand goes where the tool's tip holds on, so the
            // skin isn't dragged off to wherever the hand reached for the tool.
            h.Target = tool.TipPosition() - h.TipOffset(tool.Def.Length);
            // At once: the tool is placed in the hand before the hand moves this frame.
            h.GlobalPosition = h.EffectivePosition();
        }
        if (!value)
        {
            h.LocalTarget = ToLocal(h.Target);
        }
    }

    private StatusContext CurrentStatusContext()
    {
        var partnerBreath = Session.Surgeons.Values
            .Where(other => other != this && other.GlobalPosition.DistanceTo(GlobalPosition) < 0.9f)
            .Sum(other => other.Mods.Num("bad_breath"));
        return new StatusContext(Session.Patient.Vitals.BleedRate, partnerBreath);
    }

    private void HandleStatusEvents(List<StatusEvent> events)
    {
        var hud = Session.Hud;
        foreach (var statusEvent in events)
        {
            switch (statusEvent)
            {
                case StatusEvent.PassOut:
                    DropEverything();
                    hud.Toast("Everything goes dark...");
                    Session.ReportIncident("passed_out");
                    break;
                case StatusEvent.Vomit:
                    hud.Toast("You vomit.");
                    Sfx.Play("vomit", GlobalPosition);
                    Session.ReportIncident("vomit");
                    break;
                case StatusEvent.Cough:
                    Sfx.Play("cough", GlobalPosition);
                    Jolt(0.4f);
                    break;
                case StatusEvent.Slip:
                    if (HeldTool(Active) is not null && !Hands[Active].Attached)
                    {
                        hud.Toast("Your sweaty glove slips!");
                        Session.Tools.RequestRelease(Active, Vector3.Zero);
                    }
                    break;
                case StatusEvent.Drip:
                    var zone = Session.Patient.Body.Probe(Hands[Active].GlobalPosition + (Vector3.Down * 0.1f)).Zone;
                    if (zone is SiteZone.Site or SiteZone.Cavity)
                    {
                        hud.Toast("A drop of sweat falls into the wound.");
                        Session.ReportIncident("sweat_drip");
                    }
                    break;
                case StatusEvent.Gasp:
                    hud.Toast("You gasp for air.");
                    Jolt(0.2f);
                    break;
                case StatusEvent.KnockedOut:
                    DropEverything();
                    _delayed.Clear();
                    hud.Toast("Your legs give way. The floor is very comfortable.");
                    Session.ReportIncident("knocked_out");
                    break;
                case StatusEvent.CameRound:
                    hud.Toast("You come round, groggy.");
                    break;
                case StatusEvent.Moan:
                    Rpc(MethodName.Moan);
                    break;
            }
        }
    }
}
