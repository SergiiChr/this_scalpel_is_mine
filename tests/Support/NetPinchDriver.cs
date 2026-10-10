namespace Scalpel.Tests.Support;

/// <summary>
/// Late remote input: the client presses its forceps while lifted over the bullet in the host's opened thigh, then lets
/// Lift bring them down. The host works the client's tools from hand states that come at most 30 times a second, so
/// the tip it sees comes down in steps, holding still in between. The host must see the forceps take the bullet, not
/// close on nothing, or on whatever is passed on the way, as the press counts.
/// </summary>
public partial class NetPinchDriver : NetSession
{
    private const int Port = 24597;
    /// <summary>How long (meters) the host's cut over the bullet is.</summary>
    private const float Opening = 0.05f;
    private const float Timeout = 30f;

    protected override async Task Drive()
    {
        SurgeryState.ScenarioHasAllStarterTools("bullet_muscle");
        var surgery = await Join("bullet_muscle", Port, () => SurgeryState.NetworkSessionHasOrdinarySurgeons());
        await SurgeryDriver.PlayerPutsCardBack(surgery);
        var bullet = surgery.Patient.Targets.Single(target => target.Kind == "bullet");
        if (IsHost)
        {
            SurgeryState.PatientIsAsleep(surgery.Patient);
            var half = new Vector2(surgery.Patient.Body.MetersToUv(Opening * 0.5f), 0f);
            SurgeryState.SkinIsCut(surgery.Patient, bullet.Uv - half, bullet.Uv + half, 1f);
            await WatchPinch(surgery, bullet);
        }
        else
        {
            await PinchLifted(surgery, bullet);
        }
        GetTree().Quit();
    }

    /// <summary>Client: forceps from the tray in the right hand, over the bullet once the cut has come, Lift held, Use
    /// tool pressed and held, then Lift let go of.</summary>
    private async Task PinchLifted(Surgery surgery, CavityTarget bullet)
    {
        var me = surgery.LocalSurgeon!;
        var body = surgery.Patient.Body;
        if (!await Until(() => body.IsOpen(bullet.Uv)))
        {
            GD.Print("FAIL: [client] the host's cut over the bullet never arrived");
            return;
        }
        var forceps = surgery.Tools.Tools.Values.FirstOrDefault(tool => tool.State == ToolState.Free && tool.Def.Id == "forceps");
        if (forceps is null)
        {
            GD.Print("FAIL: [client] no forceps on the tray");
            return;
        }
        surgery.Tools.RequestGrab(forceps, 1);
        if (!await Until(() => me.HeldTool(1) == forceps))
        {
            GD.Print("FAIL: [client] never got the forceps");
            return;
        }
        var spot = body.UvToWorld(bullet.Uv, bullet.Depth);
        // Along the table until the bullet is in front: the thigh is out of reach from where the client starts.
        var walk = me.ToLocal(spot).X < 0f ? InputActions.MoveLeft : InputActions.MoveRight;
        PlayerInput.Action(walk);
        await Until(() => Mathf.Abs(me.ToLocal(spot).X) < 0.1f);
        PlayerInput.Action(walk, false);
        await Wait(0.5);
        // The hand's key makes it the active one and lets the mouse move it.
        PlayerInput.Action(InputActions.MoveRightHand);
        for (var i = 0; i < 20 && ((spot - forceps.TipPosition()) with { Y = 0f }).Length() > 0.002f; i++)
        {
            PlayerInput.Mouse(PlayerInput.HandMotion(me, (spot - forceps.TipPosition()) with { Y = 0f }));
            await Physics(6);
        }
        PlayerInput.Action(InputActions.MoveRightHand, false);
        GD.Print($"[client] forceps over the bullet, {((spot - forceps.TipPosition()) with { Y = 0f }).Length() * 1000f:0.0} mm off");
        PlayerInput.Action(InputActions.Lift);
        await Wait(0.5);
        PlayerInput.Action(InputActions.UseTool);
        await Physics(5);
        PlayerInput.Action(InputActions.Lift, false);
        // Held until the host has had time to report.
        await Wait(3.0);
        PlayerInput.Action(InputActions.UseTool, false);
        await Wait(1.0);
    }

    /// <summary>Host: what the client's forceps take hold of once the client holds them.</summary>
    private async Task WatchPinch(Surgery surgery, CavityTarget bullet)
    {
        var partner = Partner(surgery);
        SurgicalTool? Held() => surgery.Tools.Tools.Values
            .FirstOrDefault(tool => tool is { State: ToolState.Held, Def.Id: "forceps" } && tool.Holder == partner.PeerId);
        if (!await Until(() => Held() is not null))
        {
            GD.Print("FAIL: [host] the client never took the forceps");
            return;
        }
        var forceps = Held()!;
        await Until(() => forceps.Hold is not null);
        await Wait(0.5);
        GD.Print(forceps.Hold is TargetHold && bullet.GrippedBy == forceps.Uid
            ? "[host] the client's forceps take the bullet"
            : $"FAIL: [host] the client's forceps, pressed while lifted, don't take the bullet: {forceps.Hold?.ToString() ?? "nothing"}");
        // Let the client finish before the host goes away.
        await Wait(4.0);
    }

    private async Task<bool> Until(Func<bool> done)
    {
        for (var waited = 0f; waited < Timeout; waited += 0.1f)
        {
            if (done())
            {
                return true;
            }
            await Wait(0.1);
        }
        return done();
    }

    private async Task Physics(int count)
    {
        for (var i = 0; i < count; i++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
    }
}
