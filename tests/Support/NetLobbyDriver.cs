namespace Scalpel.Tests.Support;

/// <summary>
/// After the game: the host signs the patient out, which ends the surgery for both. The client leaves the report first
/// and waits alone in an empty lobby while the host still reads the report; then the host takes everyone back to the
/// lobby. Both must land there still connected, and the host picks another scenario, which both play next without
/// joining again.
/// </summary>
public partial class NetLobbyDriver : NetSession {
    private const int Port = 24596;
    private const string First = "hand_stitch";
    private const string Next = "appendectomy";
    private readonly HashSet<string> _heard = [];

    /// <summary>The other side has done <paramref name="step"/>.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Told(string step) => _heard.Add(step);

    protected override async Task Drive() {
        var net = Net.Instance;
        var surgery = await Join(First, Port);
        await SurgeryDriver.PlayerPutsCardBack(surgery);
        if (IsHost) {
            await SignOut(surgery);
        }
        await Until(() => surgery.Report is not null, "the post-op report", () => $"finished={surgery.Finished}");
        if (IsHost) {
            await Until(() => _heard.Contains("waiting"), "the client waiting in the lobby",
                () => $"heard [{string.Join(", ", _heard)}]");
            Check(Surgery.Current == surgery && surgery.Hud.Overlay is not null && net.Roster.Count == 2,
                "the host still reads the report while the client waits", () => $"roster={net.Roster.Count}");
            SurgeryDriver.Click(ButtonIn(surgery.Hud, "Back to lobby"));
        } else {
            await WaitForHost(surgery);
        }
        string lobbySeen() => $"scene {GetTree().CurrentScene?.Name}, online={net.IsOnline}, roster={net.Roster.Count}";
        await Until(() => GetTree().CurrentScene is Lobby && !net.InSession && net.Roster.Count == 2,
            "both players back in the lobby", lobbySeen);
        Check(net.IsOnline && net.Silence(IsHost ? Partner() : Net.HostId) < 1f, "back in the lobby, still connected",
            lobbySeen);
        if (IsHost) {
            var picker = GetTree().CurrentScene.FindChildren("*", nameof(OptionButton), true, false).OfType<OptionButton>()
                .Single();
            var index = Db.Scenarios.ToList().FindIndex(scenario => scenario.Id == Next);
            picker.Select(index);
            picker.EmitSignal(OptionButton.SignalName.ItemSelected, index);
        }
        // A new pick unreadies everyone; the host may ready up again before the client hears of it.
        await Until(() => net.ScenarioId == Next && net.Roster.GetValueOrDefault(net.LocalId) is { Ready: false },
            "the host's new pick in the lobby", () => $"scenario {net.ScenarioId}");
        SurgeryDriver.Click(ButtonIn(GetTree().CurrentScene, "Ready"));
        if (IsHost) {
            await Until(() => net.AllReady, "everyone ready", () => "not all ready");
            SurgeryDriver.Click(ButtonIn(GetTree().CurrentScene, "Start surgery"));
        }
        await Until(() => Surgery.Current is { Running: true } running && running != surgery, "the next surgery running",
            () => Surgery.Current is null ? "no surgery" : "surgery not running");
        var next = Surgery.Current!;
        Check(next.Scenario.Id == Next && next.Surgeons.Count == 2,
            $"the next surgery is {Next} with both surgeons",
            () => $"{next.Scenario.Id} with {next.Surgeons.Count} surgeons");
        // The host stays until the client has seen it too: its surgery reaches the client only while the host runs.
        if (IsHost) {
            await Until(() => _heard.Contains("in next surgery"), "the client in the next surgery",
                () => $"heard [{string.Join(", ", _heard)}]");
        } else {
            RpcId(Net.HostId, MethodName.Told, "in next surgery");
            await Wait(1.0);
        }
        GetTree().Quit();
    }

    /// <summary>Client: leaves the report for an empty lobby that only offers to exit, until the host comes.</summary>
    private async Task WaitForHost(Surgery surgery) {
        var net = Net.Instance;
        SurgeryDriver.Click(ButtonIn(surgery.Hud, "Back to lobby"));
        await Until(() => GetTree().CurrentScene is Lobby, "the client's lobby",
            () => $"scene {GetTree().CurrentScene?.Name}");
        var lobby = GetTree().CurrentScene;
        var buttons = lobby.FindChildren("*", nameof(Button), true, false).OfType<Button>().Select(button => button.Text);
        var waiting = lobby.FindChildren("*", nameof(Label), true, false).OfType<Label>()
            .Any(label => label.Text == "Waiting for host to finish the game.");
        Check(net.WaitingForHost && net.IsOnline && waiting && buttons.SequenceEqual(["Exit lobby"]),
            "the client waits for the host in an empty lobby",
            () => $"waiting={net.WaitingForHost}, notice={waiting}, buttons [{string.Join(", ", buttons)}]");
        RpcId(Net.HostId, MethodName.Told, "waiting");
    }

    /// <summary>Host: takes the patient card off its hook and holds Interact until the signature ends the surgery.</summary>
    private async Task SignOut(Surgery surgery) {
        var me = surgery.LocalSurgeon!;
        var hook = surgery.Room.FindChildren("*", "", true, false).OfType<Interactable>()
            .First(spot => spot.Prompt == "Read the patient card");
        await Until(() => hook.OfferedTo(me), "the card back on its hook", () => $"on hook={surgery.Room.Card.OnHook}");
        hook.Interact(me);
        await Until(() => surgery.Hud.CardOpen && surgery.Hud.Overlay!.Modulate.A >= 1f, "the card's page shown",
            () => $"card open={surgery.Hud.CardOpen}");
        PlayerInput.Action(InputActions.Interact);
        await Until(() => surgery.Finished, "the signature ending the surgery", () => "still running");
        PlayerInput.Action(InputActions.Interact, false);
    }

    private static Button ButtonIn(Node parent, string text) {
        var buttons = parent.FindChildren("*", nameof(Button), true, false).OfType<Button>().ToList();
        return buttons.FirstOrDefault(button => button.Text == text) ?? throw new InvalidOperationException(
            $"no \"{text}\" button in {parent.Name}, only [{string.Join(", ", buttons.Select(button => button.Text))}]");
    }

    private int Partner() => Net.Instance.Roster.Keys.First(peer => peer != Net.Instance.LocalId);
}
