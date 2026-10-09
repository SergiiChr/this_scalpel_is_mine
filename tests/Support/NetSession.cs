namespace Scalpel.Tests.Support;

/// <summary>
/// One side of a real two-process ENet session for NetworkTest (tests/Support/network_runner.sh starts both). A
/// driver scene's node moves itself under the tree root, so it survives the scene changes from lobby to surgery, and
/// plays its <see cref="Role"/> from there.
/// </summary>
public abstract partial class NetSession : Node
{
    /// <summary>"host" or "client", from the command line's --role=.</summary>
    protected string Role { get; private set; } = "host";

    protected bool IsHost => Role == "host";

    public override void _Ready()
    {
        // The scene's own node is freed with the scene: a copy named for its class drives from the root.
        if (GetParent() != GetTree().Root || Name != GetType().Name)
        {
            var driver = (NetSession)Activator.CreateInstance(GetType())!;
            driver.Name = GetType().Name;
            GetTree().Root.CallDeferred(Node.MethodName.AddChild, driver);
            return;
        }
        Role = OS.GetCmdlineUserArgs().FirstOrDefault(arg => arg.StartsWith("--role="))?.Split('=')[1] ?? "host";
        // Not from inside _Ready: the root is still adding this node, and hosting changes the scene under it.
        Callable.From(() => this.Start(Drive)).CallDeferred();
    }

    protected abstract Task Drive();

    protected Task Wait(double seconds) => ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout)
        .AsTask();

    /// <summary>Hosts <paramref name="scenario"/> on <paramref name="port"/> or joins it, readies up and waits until
    /// the surgery runs. <paramref name="beforeStart"/>: the host's last setup before it starts the session.</summary>
    protected async Task<Surgery> Join(string scenario, int port, Action? beforeStart = null)
    {
        var net = Net.Instance;
        if (IsHost)
        {
            net.Host(scenario, port);
        }
        else
        {
            await Wait(0.5);
            net.Join("127.0.0.1", port);
        }
        while (net.Roster.Count < 2)
        {
            await Wait(0.2);
        }
        net.SetReady(true);
        if (IsHost)
        {
            while (!net.AllReady)
            {
                await Wait(0.2);
            }
            beforeStart?.Invoke();
            net.StartSession();
        }
        while (Surgery.Current is not { Running: true })
        {
            await Wait(0.2);
        }
        return Surgery.Current;
    }

    /// <summary>The other player's surgeon.</summary>
    protected static Surgeon Partner(Surgery surgery) => surgery.Surgeons.Values.First(surgeon => !surgeon.IsLocal);
}

/// <summary>Async helpers for awaiting Godot signals as tasks.</summary>
internal static class SignalAwaiterExtensions
{
    public static async Task AsTask(this SignalAwaiter awaiter) => await awaiter;
}
