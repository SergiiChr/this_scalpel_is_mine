namespace Scalpel.Tests.Support;

/// <summary>
/// One side of a real two-process ENet session for NetworkTest (tests/Support/network_runner.sh starts both). A
/// driver scene's node moves itself under the tree root, so it survives the scene changes from lobby to surgery, and
/// plays its <see cref="Role"/> from there.
/// </summary>
public abstract partial class NetSession : Node {
    /// <summary>"host" or "client", from the command line's --role=.</summary>
    protected string Role { get; private set; } = "host";

    protected bool IsHost => Role == "host";

    /// <summary>When (msec from this process's start) waits on the other side are given up, from --give-up= (seconds):
    /// under the case's limit, so one that never comes true fails it with what it saw instead of running into that
    /// limit. One deadline for all of them: a slow start leaves the later waits less time, as the case's limit does.
    /// </summary>
    private ulong _giveUpMsec;

    public override void _Ready() {
        // The scene's own node is freed with the scene: a copy named for its class drives from the root.
        if (GetParent() != GetTree().Root || Name != GetType().Name) {
            var driver = (NetSession)Activator.CreateInstance(GetType())!;
            driver.Name = GetType().Name;
            GetTree().Root.CallDeferred(Node.MethodName.AddChild, driver);
            return;
        }
        var args = OS.GetCmdlineUserArgs();
        Role = args.FirstOrDefault(arg => arg.StartsWith("--role="))?.Split('=')[1] ?? "host";
        _giveUpMsec = ulong.Parse(args.First(arg => arg.StartsWith("--give-up=")).Split('=')[1]) * 1000;
        // Not from inside _Ready: the root is still adding this node, and hosting changes the scene under it.
        Callable.From(() => this.Start(Drive)).CallDeferred();
    }

    protected abstract Task Drive();

    /// <summary>Waits a physics frame at a time until <paramref name="done"/>: a slow machine only makes it take longer.
    /// Past <see cref="_giveUpMsec"/> it throws, naming <paramref name="what"/> never happened and what
    /// <paramref name="seen"/> reports instead. <paramref name="every"/> (seconds) spaces out a costly
    /// <paramref name="done"/>.</summary>
    protected async Task Until(Func<bool> done, string what, Func<string> seen, double every = 0) {
        while (!done()) {
            if (Time.GetTicksMsec() > _giveUpMsec) {
                throw new TimeoutException($"[{Role}] {what} never happened, last seen: {Describe(seen)}");
            }
            await (every > 0 ? Wait(every) : ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame).AsTask());
        }
    }

    /// <summary>What <paramref name="seen"/> reports, or why it can't: the other side may have given up first and left,
    /// taking the scene's nodes for it along.</summary>
    private static string Describe(Func<string> seen) {
        try {
            return seen();
        } catch (ObjectDisposedException gone) {
            return $"nothing, its nodes are gone ({gone.ObjectName})";
        }
    }

    protected Task Wait(double seconds) => ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout)
        .AsTask();

    /// <summary>Hosts <paramref name="scenario"/> on <paramref name="port"/> or joins it, readies up and waits until
    /// the surgery runs. <paramref name="beforeStart"/>: the host's last setup before it starts the session.</summary>
    protected async Task<Surgery> Join(string scenario, int port, Action? beforeStart = null) {
        var net = Net.Instance;
        if (IsHost) {
            net.Host(scenario, port);
        } else {
            await Wait(0.5);
            net.Join("127.0.0.1", port);
        }
        await Until(() => net.Roster.Count >= 2, "the other player joining", () => $"{net.Roster.Count} in the roster");
        net.SetReady(true);
        if (IsHost) {
            await Until(() => net.AllReady, "everyone ready", () => "not all ready");
            beforeStart?.Invoke();
            net.StartSession();
        }
        await Until(() => Surgery.Current is { Running: true }, "the surgery running",
            () => Surgery.Current is null ? "no surgery" : "surgery not running");
        return Surgery.Current!;
    }

    /// <summary>The other player's surgeon.</summary>
    protected static Surgeon Partner(Surgery surgery) => surgery.Surgeons.Values.First(surgeon => !surgeon.IsLocal);
}

/// <summary>Async helpers for awaiting Godot signals as tasks.</summary>
internal static class SignalAwaiterExtensions {
    public static async Task AsTask(this SignalAwaiter awaiter) => await awaiter;
}
