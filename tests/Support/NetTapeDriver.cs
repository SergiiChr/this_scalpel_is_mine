namespace Scalpel.Tests.Support;

/// <summary>
/// Two surgeons tape gauze down together: the host holds gauze over a cut in the left hand, the client lays a strip of
/// tape across it. The gauze goes down flat on the cut and stays in the host's hand, which stays on it, pressing, until
/// the host lets go (right click): then it stays taped where it was, both sides see it there, and the host's hand is
/// free.
/// </summary>
public partial class NetTapeDriver : NetSession {
    private const int Port = 24595;

    protected override async Task Drive() {
        SurgeryState.ScenarioHasAllStarterTools("appendectomy");
        var surgery = await Join("appendectomy", Port, () => SurgeryState.NetworkSessionHasOrdinarySurgeons());
        await SurgeryDriver.PlayerPutsCardBack(surgery);
        var driver = SurgeryDriver.For(surgery);
        if (IsHost) {
            await HoldGauze(driver);
        } else {
            await TapeAcross(driver);
        }
        // Let the other side finish its checks before this one goes away.
        await Wait(3.0);
        GetTree().Quit();
    }

    /// <summary>Host: a cut, the roll on the tray for the client, gauze held over the cut in the left hand until the
    /// client tapes it down, then let go of.</summary>
    private async Task HoldGauze(SurgeryDriver driver) {
        var surgery = driver.Surgery;
        var body = driver.Body;
        SurgeryState.PatientIsAsleep(driver.Patient);
        var from = new Vector2(0.4f, 0.5f);
        var cut = SurgeryState.SkinIsCut(driver.Patient, from, from + new Vector2(body.MetersToUv(0.03f), 0f), 0.3f);
        SurgeryState.ToolIsOnTray(surgery, "surgical_tape");
        await driver.SwitchTo(SurgeryDriver.Left);
        var gauze = (await driver.PlayerRequestsItem("gauze"))!;
        var on = driver.SitePoint(cut.Midpoint);
        await driver.PlayerWalksTo(on);
        await driver.PlayerReaches(on);
        await driver.PlayerReaches(on - (gauze.Middle() - gauze.TipPosition()));
        GD.Print("[host] holds the gauze over the cut");
        var hand = driver.Me.Hands[SurgeryDriver.Left];
        await Until(() => gauze.Taped, "the client's tape on the gauze", () => $"gauze {gauze.State}");
        await Wait(0.5);
        Check(driver.Me.HeldTool(SurgeryDriver.Left) == gauze && hand.Attached && hand.Grip == "press",
            "taped down, the gauze stays in my hand, pinned flat on it", () => $"attached {hand.Attached}, grip {hand.Grip}");
        var taped = body.Site.ToLocal(gauze.GlobalPosition);
        SurgeryDriver.Tap(InputActions.Grab);
        await Until(() => gauze.State == ToolState.Standing, "the gauze left taped down", () => $"gauze {gauze.State}");
        await driver.PlayerWalksTo(on + new Vector3(0.6f, 0f, 0f));
        Check(driver.Me.HeldTool(SurgeryDriver.Left) is null && !hand.Attached
            && taped.DistanceTo(body.Site.ToLocal(gauze.GlobalPosition)) < 0.005f,
            "let go of, the gauze stays taped where it was and my hand walks away",
            () => $"attached {hand.Attached}, moved {taped.DistanceTo(body.Site.ToLocal(gauze.GlobalPosition)) * 1000f:0} mm");
    }

    /// <summary>Client: the roll from the tray in the right hand, a strip across the gauze the host holds, cut off; then
    /// the gauze lying taped where it was once the host lets go.</summary>
    private async Task TapeAcross(SurgeryDriver driver) {
        var surgery = driver.Surgery;
        var body = driver.Body;
        var host = Partner(surgery);
        // Cuts live on the host: the client goes by where the host holds the gauze, once it's held still over the site.
        SurgicalTool? Held() => surgery.Tools.Tools.Values.FirstOrDefault(tool =>
            tool is { State: ToolState.Held, Def.Id: "gauze" } && tool.Holder == host.PeerId && body.OnBody(body.WorldToUv(tool.Middle())));
        var still = 0;
        var last = Vector3.Zero;
        await Until(() => {
            var held = Held();
            still = held is not null && held.Middle().DistanceTo(last) < 0.001f ? still + 1 : 0;
            last = held?.Middle() ?? Vector3.Zero;
            return still > 60 && surgery.Tools.Tools.Values.Any(tool => tool is { State: ToolState.Free, Def.Id: "surgical_tape" });
        }, "the host holding gauze still over the site, the roll on the tray", () => Held()?.State.ToString() ?? "no gauze held");
        var gauze = Held()!;
        var over = body.WorldToUv(gauze.Middle());
        await driver.PlayerRequestsItem("surgical_tape");
        var across = new Vector2(0f, body.MetersToUv(0.055f));
        await driver.PlayerLaysTape([over - across, over + across]);
        await Until(() => gauze.Taped, "the gauze taped down in the host's hand", () => $"gauze {gauze.State}");
        var off = (body.WorldToUv(gauze.Middle()) - over).Length() / body.MetersToUv(1f);
        Check(gauze.State == ToolState.Held && off < 0.01f, "my strip tapes the host's gauze down flat where it held it, still in their hand",
            () => $"{gauze.State}, {off * 100f:0.0} cm off");
        var taped = body.Site.ToLocal(gauze.GlobalPosition);
        await Until(() => gauze.State == ToolState.Standing, "the host letting go of the gauze", () => $"gauze {gauze.State}");
        await Wait(0.5);
        Check(taped.DistanceTo(body.Site.ToLocal(gauze.GlobalPosition)) < 0.005f, "let go of, the host's gauze stays taped where it was",
            () => $"moved {taped.DistanceTo(body.Site.ToLocal(gauze.GlobalPosition)) * 1000f:0} mm");
    }
}
