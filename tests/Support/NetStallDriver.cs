namespace Scalpel.Tests.Support;

/// <summary>Spotty connection: the network runner freezes the client for 10 seconds, then resumes it. Both sides must
/// still be in the same surgery afterwards, and the host must have paused the silent player's input.</summary>
public partial class NetStallDriver : NetSession
{
    private const int Port = 24598;

    protected override async Task Drive()
    {
        var surgery = await Join("hand_stitch", Port);
        await SurgeryDriver.PlayerPutsCardBack(surgery);
        if (IsHost)
        {
            await WatchPartner(surgery);
        }
        else
        {
            await BeFrozen(surgery);
        }
        GetTree().Quit();
    }

    private async Task BeFrozen(Surgery surgery)
    {
        // Holding the tool down when the freeze hits: the host must not keep using it.
        surgery.LocalSurgeon!.Hands[1].Lowered = true;
        await Wait(1.0);
        GD.Print("[client] running");
        // The runner freezes this process now. A long gap between two short waits means it happened.
        var last = Time.GetTicksMsec();
        var frozen = 0.0;
        for (var i = 0; i < 240 && frozen <= 5.0; i++)
        {
            await Wait(0.25);
            var now = Time.GetTicksMsec();
            frozen = Math.Max(frozen, ((now - last) * 0.001) - 0.25);
            last = now;
        }
        await Wait(3.0);
        // Still hearing the host means the host kept us; a dropped client only notices much later.
        var connected = Surgery.Current == surgery && Net.Instance.IsOnline && Net.Instance.Silence(Net.HostId) < 1f;
        GD.Print($"[client] frozen for {frozen:0} s, still in surgery: {connected.ToString().ToLowerInvariant()}");
    }

    private async Task WatchPartner(Surgery surgery)
    {
        var partner = Partner(surgery);
        var peer = partner.PeerId;
        var longest = 0f;
        var paused = false;
        var engagedBefore = false;
        for (var i = 0; i < 240; i++)
        {
            await Wait(0.25);
            if (!IsInstanceValid(partner))
            {
                break;
            }
            var silent = Net.Instance.Silence(peer);
            engagedBefore = engagedBefore || (partner.Hands[1].Lowered && silent < 0.5f);
            longest = Mathf.Max(longest, silent);
            paused = paused || partner.IsStalled();
            if (longest > 5f && silent < 0.5f)
            {
                break;
            }
        }
        await Wait(2.0);
        var stillHere = IsInstanceValid(partner) && surgery.Surgeons.ContainsKey(peer) && Net.Instance.Roster.Count == 2;
        GD.Print($"[host] partner silent for {longest:0} s, input paused: {(paused && engagedBefore).ToString().ToLowerInvariant()}, partner still here: {stillHere.ToString().ToLowerInvariant()}");
        // Let the client report before the host goes away.
        await Wait(3.0);
    }
}
