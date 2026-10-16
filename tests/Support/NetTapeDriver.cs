namespace Scalpel.Tests.Support;

/// <summary>
/// Two surgeons tape gauze down together: the host holds gauze over a cut in the left hand, the client lays a strip of
/// tape across it. The gauze goes down flat on the cut and stays in the host's hand, which stays on it, pressing, until
/// the host lets go (right click): then it stays taped where it was, both sides see it there, and the host's hand is
/// free.
/// </summary>
public partial class NetTapeDriver : NetSession {
    private const int Port = 24595;

    /// <summary>Where the cut starts, site uv: the client goes there before the host holds gauze over it.</summary>
    private static readonly Vector2 CutFrom = new(0.4f, 0.5f);

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

    /// <summary>Host: a cut, the roll on the tray for the client, then, with the client across the table, gauze held over
    /// the cut in the left hand until the client tapes it down, then let go of.</summary>
    private async Task HoldGauze(SurgeryDriver driver) {
        var surgery = driver.Surgery;
        var body = driver.Body;
        SurgeryState.PatientIsAsleep(driver.Patient);
        var cut = SurgeryState.SkinIsCut(driver.Patient, CutFrom, CutFrom + new Vector2(body.MetersToUv(0.03f), 0f), 0.3f);
        SurgeryState.ToolIsOnTray(surgery, "surgical_tape");
        var client = Partner(surgery);
        await UntilStill(() => client.GlobalPosition,
            () => surgery.Tools.Tools.Values.Any(tool => tool is { State: ToolState.Held, Def.Id: "surgical_tape" } && tool.Holder == client.PeerId),
            "the client with the roll, across the table", () => $"client at {client.GlobalPosition}");
        await driver.SwitchTo(SurgeryDriver.Left);
        var gauze = (await driver.PlayerRequestsItem("gauze"))!;
        var on = driver.SitePoint(cut.Midpoint);
        await driver.PlayerWalksTo(on with { Z = Side(driver, 1f) });
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

    /// <summary>Client: the roll from the tray in the right hand, across the table from the host, a strip along the body
    /// over the gauze the host holds, cut off; then the gauze lying taped where it was once the host lets go.</summary>
    private async Task TapeAcross(SurgeryDriver driver) {
        var surgery = driver.Surgery;
        var body = driver.Body;
        var host = Partner(surgery);
        // Cuts live on the host: the client goes by where the host holds the gauze, once it's held still over the site.
        SurgicalTool? Held() => surgery.Tools.Tools.Values.FirstOrDefault(tool =>
            tool is { State: ToolState.Held, Def.Id: "gauze" } && tool.Holder == host.PeerId && body.OnBody(body.WorldToUv(tool.Middle())));
        await Until(() => surgery.Tools.Tools.Values.Any(tool => tool is { State: ToolState.Free, Def.Id: "surgical_tape" }),
            "the roll on the tray", () => "no roll");
        await driver.PlayerRequestsItem("surgical_tape");
        // Across the table, and the strip along the body: a hand that comes near the host's bumps it away, gauze and all.
        await driver.PlayerWalksTo(driver.SitePoint(CutFrom) with { Z = Side(driver, -1f) });
        // The hand that reached for the roll on the tray stays as far out from the body, over the host's side.
        await driver.PlayerReaches(driver.SitePoint(CutFrom) with { Z = Side(driver, -2f) });
        await UntilStill(() => Held()?.Middle(), () => true, "the host holding gauze still over the site",
            () => Held()?.State.ToString() ?? "no gauze held");
        var gauze = Held()!;
        var over = body.WorldToUv(gauze.Middle());
        var across = new Vector2(body.MetersToUv(0.1f), 0f);
        foreach (var (point, last) in new[] { (over - across, false), (over + across, true) }) {
            // Up to each end from this side of the table, not along the strip past the host's hand.
            await driver.PlayerReaches(driver.SitePoint(point) with { Z = Side(driver, -1.5f) });
            await driver.PlayerSticksTape(point, last);
        }
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

    /// <summary>A point <paramref name="side"/> times 10 cm off the table's middle, on its side (+ or -): walking to it
    /// stands there.</summary>
    private static float Side(SurgeryDriver driver, float side) => driver.Patient.GlobalPosition.Z + (side * 0.1f);

    /// <summary>Waits until <paramref name="ready"/> holds and <paramref name="at"/> has stayed put for a second.</summary>
    private async Task UntilStill(Func<Vector3?> at, Func<bool> ready, string what, Func<string> seen) {
        var still = 0;
        Vector3? last = null;
        await Until(() => {
            var now = at();
            still = now is { } n && last is { } l && n.DistanceTo(l) < 0.001f ? still + 1 : 0;
            last = now;
            return still > 60 && ready();
        }, what, seen);
    }
}
