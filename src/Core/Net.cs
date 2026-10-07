using GodotDictionary = Godot.Collections.Dictionary;

namespace Scalpel.Core;

/// <summary>A player in the lobby: name, rolled surgeon quirks and whether they're ready.</summary>
public sealed record LobbyPlayer(string Name, IReadOnlyList<QuirkRoll> Quirks, bool Ready)
{
    public GodotDictionary ToVariant() => new()
    {
        ["name"] = Name, ["quirks"] = QuirkRoll.ToVariant(Quirks), ["ready"] = Ready,
    };

    public static LobbyPlayer FromVariant(GodotDictionary data) =>
        new(data.String("name"), QuirkRoll.FromVariant(data["quirks"].AsGodotArray()), data.Bool("ready"));
}

/// <summary>
/// Host-client session. The host owns the simulation and the save; clients own only their surgeon.
/// Solo play uses the same code path with an offline peer, so there is no separate single player mode.
///
/// Flow: <see cref="Host"/>/<see cref="Join"/> -> lobby (quirks rolled by host, everyone readies up) ->
/// <see cref="StartSession"/> -> surgery scene.
///
/// Spotty connections: ENet only drops a peer after TimeoutMaxMsec without any answer (its default gives up after
/// about 5 s). Until then everyone keeps playing: a heartbeat tracks how long each peer has been silent, the HUD warns
/// about it, and the host stops applying a silent player's last input (see <see cref="Surgeon"/>).
/// An autoload; the rest of the game reaches it through <see cref="Instance"/>.
/// </summary>
public partial class Net : Node
{
    [Signal] public delegate void RosterChangedEventHandler();
    [Signal] public delegate void ScenarioChangedEventHandler();
    [Signal] public delegate void ConnectionFailedEventHandler(string reason);
    [Signal] public delegate void SessionStartedEventHandler();
    [Signal] public delegate void DisconnectedEventHandler();

    public const int DefaultPort = 24565;
    public const int MaxClients = 1;
    public const string LobbyScene = "res://scenes/ui/lobby.tscn";
    public const string SurgeryScene = "res://scenes/surgery.tscn";
    public const string MenuScene = "res://scenes/ui/main_menu.tscn";
    /// <summary>ENet drops a peer once a reliable packet stays unanswered this long (or TimeoutMinMsec after
    /// TimeoutLimit resends).</summary>
    public const int TimeoutLimit = 64;
    public const int TimeoutMinMsec = 15000;
    public const int TimeoutMaxMsec = 45000;
    /// <summary>Joining gives up on a host that never answers after this long, instead of waiting out the full
    /// timeout.</summary>
    public const float ConnectTimeout = 10f;
    public const float HeartbeatInterval = 0.25f;
    /// <summary>The host's peer id.</summary>
    public const int HostId = 1;

    public static Net Instance { get; private set; } = null!;

    private readonly RandomNumberGenerator _rng = new();
    /// <summary>Peer id -> Time.GetTicksMsec() of the last heartbeat from them.</summary>
    private readonly Dictionary<int, ulong> _lastHeard = [];
    private double _heartbeatAcc;
    private SceneTreeTimer? _connectTimer;

    public Dictionary<int, LobbyPlayer> Roster { get; private set; } = [];
    /// <summary>The scenario of the lobby or session. Set through its id, except for a local session.</summary>
    public ScenarioDef? Scenario { get; private set; }
    public string ScenarioId
    {
        get => Scenario?.Id ?? "";
        private set => Scenario = Db.Scenario(value);
    }
    public uint SessionSeed { get; private set; }
    public List<QuirkRoll> PatientQuirks { get; private set; } = [];
    /// <summary>Run modifier ids (data/run_modifiers.cfg), rolled in the lobby so players see them before starting.
    /// </summary>
    public List<string> RunModifiers { get; private set; } = [];
    /// <summary>True from the start of a surgery until everyone is back in the lobby. Nobody can join in the middle.
    /// </summary>
    public bool InSession { get; private set; }
    /// <summary>Why the last session ended, shown on the main menu. Empty after a normal exit.</summary>
    public string LastError { get; set; } = "";

    public override void _EnterTree() => Instance = this;

    public override void _Ready()
    {
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
        Multiplayer.ConnectedToServer += OnConnected;
        Multiplayer.ConnectionFailed += () => OnConnectFailed("Could not reach the host.");
        Multiplayer.ServerDisconnected += OnServerGone;
    }

    public override void _Process(double delta)
    {
        if (!IsOnline)
        {
            return;
        }
        _heartbeatAcc += delta;
        if (_heartbeatAcc >= HeartbeatInterval)
        {
            _heartbeatAcc = 0;
            Rpc(MethodName.Heartbeat);
        }
    }

    public bool IsHost => Multiplayer.IsServer();

    public int LocalId => Multiplayer.GetUniqueId();

    public bool IsOnline => Multiplayer.MultiplayerPeer is not OfflineMultiplayerPeer;


    public IReadOnlyList<QuirkRoll> LocalQuirks => Roster.TryGetValue(LocalId, out var player) ? player.Quirks : [];

    public bool AllReady => Roster.Count > 0 && Roster.Values.All(player => player.Ready);

    /// <summary>Seconds since anything was heard from the peer. 0 when playing solo or for yourself.</summary>
    public float Silence(int peer)
    {
        if (!IsOnline || peer == LocalId || !_lastHeard.TryGetValue(peer, out var heard))
        {
            return 0f;
        }
        return (Time.GetTicksMsec() - heard) * 0.001f;
    }

    /// <summary>The longest any other player has been silent, or (0, 0) if everyone is fine.</summary>
    public (int Peer, float Seconds) WorstSilence()
    {
        var worst = (Peer: 0, Seconds: 0f);
        foreach (var peer in _lastHeard.Keys)
        {
            if (Silence(peer) > worst.Seconds)
            {
                worst = (peer, Silence(peer));
            }
        }
        return worst;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void Heartbeat() => _lastHeard[Multiplayer.GetRemoteSenderId()] = Time.GetTicksMsec();

    private static void TolerateLag(ENetPacketPeer peer) => peer.SetTimeout(TimeoutLimit, TimeoutMinMsec, TimeoutMaxMsec);

    public void PlaySolo(string scenario)
    {
        Leave();
        StartHosting(scenario);
    }

    public Error Host(string scenario, int port = DefaultPort)
    {
        Leave();
        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateServer(port, MaxClients);
        if (error != Error.Ok)
        {
            return error;
        }
        Multiplayer.MultiplayerPeer = peer;
        StartHosting(scenario);
        return Error.Ok;
    }

    public Error Join(string ip, int port = DefaultPort)
    {
        Leave();
        var peer = new ENetMultiplayerPeer();
        var error = peer.CreateClient(ip, port);
        if (error != Error.Ok)
        {
            return error;
        }
        if (peer.GetPeer(HostId) is { } hostPeer)
        {
            TolerateLag(hostPeer);
        }
        Multiplayer.MultiplayerPeer = peer;
        // The connect attempt itself would wait out the whole lag timeout; a host that never answers fails sooner.
        var timer = GetTree().CreateTimer(ConnectTimeout);
        _connectTimer = timer;
        timer.Timeout += () => OnConnectTimeout(timer);
        return Error.Ok;
    }

    public void Leave()
    {
        if (Multiplayer.MultiplayerPeer is { } peer and not OfflineMultiplayerPeer)
        {
            peer.Close();
        }
        Multiplayer.MultiplayerPeer = new OfflineMultiplayerPeer();
        Roster.Clear();
        InSession = false;
        _lastHeard.Clear();
        _connectTimer = null;
    }

    public void BackToMenu()
    {
        Leave();
        GetTree().ChangeSceneToFile(MenuScene);
    }

    // --- Host side ---------------------------------------------------------------------------------------

    private void StartHosting(string scenario)
    {
        _rng.Randomize();
        ScenarioId = scenario;
        RunModifiers = RollRunModifiers();
        Roster = new Dictionary<int, LobbyPlayer> { [HostId] = NewPlayer(Progress.PlayerName) };
        GetTree().ChangeSceneToFile(LobbyScene);
    }

    private LobbyPlayer NewPlayer(string name) => new(name, QuirkRoller.RollSurgeon(_rng), Ready: false);

    /// <summary>Host only. Picks a new scenario and rerolls everyone (it's a new run).</summary>
    public void ChangeScenario(string id)
    {
        if (!IsHost)
        {
            return;
        }
        ScenarioId = id;
        RunModifiers = RollRunModifiers();
        foreach (var peer in Roster.Keys.ToList())
        {
            Roster[peer] = Roster[peer] with { Quirks = QuirkRoller.RollSurgeon(_rng), Ready = false };
        }
        SyncLobbyEverywhere();
    }

    /// <summary>One or two per run, never the same twice.</summary>
    private List<string> RollRunModifiers()
    {
        var ids = Db.RunModifiers.GetSections().ToList();
        Shuffle(ids);
        return [.. ids.Take(_rng.RandiRange(1, 2))];
    }

    private static void Shuffle<T>(List<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = (int)(GD.Randi() % (uint)(i + 1));
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// A solo session without the lobby, for a surgery scene launched on its own (the editor): the given scenario, a
    /// random seed, the local player with rolled quirks and the patient's quirks, no run modifiers.
    /// </summary>
    public void PrepareLocalSession(ScenarioDef scenario)
    {
        var rng = new RandomNumberGenerator();
        rng.Randomize();
        var player = new LobbyPlayer(Progress.PlayerName, QuirkRoller.RollSurgeon(rng), Ready: true);
        StartLocalSession(scenario, rng.Randi(), player, QuirkRoller.RollPatient(scenario, rng));
    }

    /// <summary>A solo session set up exactly as given, without the lobby (tests, see also
    /// <see cref="PrepareLocalSession"/>). The scenario may be a changed copy of one in Db.</summary>
    public void StartLocalSession(
        ScenarioDef scenario, uint seed, LobbyPlayer player, IReadOnlyList<QuirkRoll> patientQuirks,
        IReadOnlyList<string>? runModifiers = null)
    {
        Leave();
        Scenario = scenario;
        SessionSeed = seed;
        Roster = new Dictionary<int, LobbyPlayer> { [HostId] = player };
        PatientQuirks = [.. patientQuirks];
        RunModifiers = [.. runModifiers ?? []];
    }

    /// <summary>Host only, after everyone readied up.</summary>
    public void StartSession()
    {
        if (!IsHost || !AllReady || Scenario is not { } scenario)
        {
            return;
        }
        SessionSeed = _rng.Randi();
        PatientQuirks = QuirkRoller.RollPatient(scenario, _rng);
        Rpc(MethodName.Begin, SessionSeed, QuirkRoll.ToVariant(PatientQuirks), RosterToVariant(), ScenarioId,
            RunModifiersToVariant());
    }

    /// <summary>Host only. Back to the lobby after a report, with fresh quirks.</summary>
    public void ReturnToLobby()
    {
        if (IsHost)
        {
            ChangeScenario(ScenarioId);
            Rpc(MethodName.GoToLobby);
        }
    }

    public void SetReady(bool value) => RpcId(HostId, MethodName.RequestReady, value);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestReady(bool value)
    {
        var sender = Sender();
        if (IsHost && Roster.TryGetValue(sender, out var player))
        {
            Roster[sender] = player with { Ready = value };
            SyncLobbyEverywhere();
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Register(string playerName)
    {
        if (!IsHost)
        {
            return;
        }
        if (InSession)
        {
            Multiplayer.MultiplayerPeer.DisconnectPeer(Sender());
            return;
        }
        Roster[Sender()] = NewPlayer(playerName);
        SyncLobbyEverywhere();
    }

    // --- Everyone ----------------------------------------------------------------------------------------

    private void SyncLobbyEverywhere() =>
        Rpc(MethodName.SyncLobby, RosterToVariant(), ScenarioId, RunModifiersToVariant());

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SyncLobby(GodotDictionary roster, string scenario, string[] modifiers)
    {
        Roster = RosterFromVariant(roster);
        RunModifiers = [.. modifiers];
        if (scenario != ScenarioId)
        {
            ScenarioId = scenario;
            EmitSignal(SignalName.ScenarioChanged);
        }
        EmitSignal(SignalName.RosterChanged);
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Begin(uint seed, Godot.Collections.Array patient, GodotDictionary roster, string scenario,
        string[] modifiers)
    {
        SessionSeed = seed;
        RunModifiers = [.. modifiers];
        PatientQuirks = QuirkRoll.FromVariant(patient);
        Roster = RosterFromVariant(roster);
        ScenarioId = scenario;
        InSession = true;
        foreach (var roll in LocalQuirks)
        {
            Progress.Unlock(Db.Quirk(QuirkKind.Surgeon, roll.Id), roll.Variant);
        }
        EmitSignal(SignalName.SessionStarted);
        GetTree().ChangeSceneToFile(SurgeryScene);
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void GoToLobby()
    {
        InSession = false;
        GetTree().ChangeSceneToFile(LobbyScene);
    }

    private void OnConnected()
    {
        _connectTimer = null;
        _lastHeard[HostId] = Time.GetTicksMsec();
        RpcId(HostId, MethodName.Register, Progress.PlayerName);
        GetTree().ChangeSceneToFile(LobbyScene);
    }

    private void OnPeerConnected(long id)
    {
        _lastHeard[(int)id] = Time.GetTicksMsec();
        if (IsHost && Multiplayer.MultiplayerPeer is ENetMultiplayerPeer enet)
        {
            TolerateLag(enet.GetPeer((int)id));
        }
    }

    private void OnPeerDisconnected(long id)
    {
        Roster.Remove((int)id);
        _lastHeard.Remove((int)id);
        if (IsHost)
        {
            SyncLobbyEverywhere();
        }
    }

    private void OnServerGone()
    {
        LastError = "Lost the connection to the host.";
        EmitSignal(SignalName.Disconnected);
        BackToMenu();
    }

    private void OnConnectTimeout(SceneTreeTimer timer)
    {
        // Only the attempt this timer was started for; a later join has its own.
        if (timer == _connectTimer && Multiplayer.MultiplayerPeer is ENetMultiplayerPeer enet && LocalId != HostId
            && enet.GetConnectionStatus() == MultiplayerPeer.ConnectionStatus.Connecting)
        {
            OnConnectFailed($"The host didn't answer within {ConnectTimeout:0} seconds.");
        }
    }

    private void OnConnectFailed(string reason)
    {
        Leave();
        EmitSignal(SignalName.ConnectionFailed, reason);
    }

    /// <summary>Remote sender id, or our own id for local calls.</summary>
    public int Sender()
    {
        var id = Multiplayer.GetRemoteSenderId();
        return id != 0 ? id : LocalId;
    }

    private GodotDictionary RosterToVariant()
    {
        var roster = new GodotDictionary();
        foreach (var (peer, player) in Roster)
        {
            roster[peer] = player.ToVariant();
        }
        return roster;
    }

    private static Dictionary<int, LobbyPlayer> RosterFromVariant(GodotDictionary roster) =>
        roster.ToDictionary(pair => pair.Key.AsInt32(), pair => LobbyPlayer.FromVariant(pair.Value.AsGodotDictionary()));

    private string[] RunModifiersToVariant() => [.. RunModifiers];
}
