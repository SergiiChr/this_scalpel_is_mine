using GodotDictionary = Godot.Collections.Dictionary;

namespace Scalpel.Operation;

/// <summary>
/// Root of an operation. Builds the room, patient, surgeons and tools from the session, runs the host-side systems
/// and is the one place where gameplay RPCs go through.
///
/// Launch surgery.tscn directly (F6 in the editor) for a solo test run. Command line: `-- --scenario=appendectomy`
/// picks the scenario.
/// </summary>
public partial class Surgery : Node3D
{
    /// <summary>Host: a dose went into a surgeon (their own peer gets it through Dosed()).</summary>
    [Signal] public delegate void SurgeonDosedEventHandler(int peer, string drug, float amount);

    public const float StatusInterval = 0.5f;
    public const float QteTimeout = 10f;
    public static readonly IReadOnlyList<string> QteKeys =
        [InputActions.MoveForward, InputActions.MoveBack, InputActions.MoveLeft, InputActions.MoveRight];
    /// <summary>Tools report their sound every physics frame while in use; one per this many msec per sound is plenty.
    /// </summary>
    private const ulong SoundRepeatMsec = 90;
    private const ulong ContactRepeatMsec = 120;
    /// <summary>The quiet after a syringe pushes into the local surgeon that makes the next one news.</summary>
    private const ulong StingGapMsec = 3000;

    /// <summary>Turning the patient: who takes part, what each scored, time left and where they're turning to.
    /// </summary>
    private sealed class TurnAttempt(List<int> peers, PatientPose target)
    {
        public List<int> Peers { get; } = peers;
        public Dictionary<int, (int Hits, bool Critical)> Results { get; } = [];
        public float Timer { get; set; } = QteTimeout;
        public PatientPose Target { get; } = target;
    }

    private readonly HashSet<int> _loaded = [];
    private readonly Dictionary<string, double> _announced = [];
    private readonly Dictionary<string, ulong> _soundMsec = [];
    private readonly Dictionary<int, ulong> _contactMsec = [];
    private readonly Dictionary<ToolEffect, ulong> _effectMsec = [];
    private readonly ToolEffects _effects = new() { Name = "Effects" };
    private float _statusAcc;
    private TurnAttempt? _turn;
    private ulong _stungMsec;

    /// <summary>The surgery running now, null outside one.</summary>
    public static Surgery? Current { get; private set; }

    public Room Room => GetNode<Room>("Room");
    public Patient Patient => GetNode<Patient>("Patient");
    public ToolManager Tools => GetNode<ToolManager>("Tools");
    public Node3D SurgeonsRoot => GetNode<Node3D>("Surgeons");
    public Objectives Objectives => GetNode<Objectives>("Systems/Objectives");
    public EventDirector Director => GetNode<EventDirector>("Systems/Director");
    public Scoring Scoring => GetNode<Scoring>("Systems/Scoring");
    public Nurse Nurse => GetNode<Nurse>("Systems/Nurse");
    public Lab Lab => GetNode<Lab>("Systems/Lab");
    public Hud Hud => GetNode<Hud>("Hud");

    public ScenarioDef Scenario { get; private set; } = null!;
    /// <summary>Peer id -> their surgeon.</summary>
    public Dictionary<int, Surgeon> Surgeons { get; } = [];
    public Surgeon? LocalSurgeon { get; private set; }
    public bool Running { get; private set; }
    public bool Finished { get; private set; }
    public float Elapsed { get; private set; }
    public RandomNumberGenerator Rng { get; } = new();
    /// <summary>Last status from the host, for the HUD: objectives, score, cooldowns.</summary>
    public SurgeryStatus Status { get; private set; } = SurgeryStatus.Empty;
    /// <summary>Effects of this run's modifiers (data/run_modifiers.cfg).</summary>
    public Modifiers RunMods { get; private set; } = new();
    /// <summary>Chart mix-up modifier: false until the nurse brings the corrected patient card.</summary>
    public bool ChartCorrected { get; private set; }
    /// <summary>The post-op report once the surgery is over, as every peer got it.</summary>
    public SurgeryReport? Report { get; private set; }

    public bool IsHost => Multiplayer.IsServer();

    public override void _EnterTree() => Current = this;

    public override void _ExitTree()
    {
        if (Current == this)
        {
            Current = null;
        }
    }

    public override void _Ready()
    {
        var net = Net.Instance;
        if (net.Roster.Count == 0)
        {
            net.PrepareLocalSession(ScenarioFromCommandLine());
        }
        Scenario = net.Scenario!;
        Rng.Seed = net.SessionSeed;
        RunMods = Db.RunModifierEffects(net.RunModifiers);
        Room.Build(Scenario.Environment, this);
        Patient.Position = new Vector3(0, Room.TableHeight, 0);
        Patient.Setup(Scenario, net.PatientQuirks, net.SessionSeed);
        Patient.Died += OnPatientDied;
        _effects.Patient = Patient;
        AddChild(_effects);
        var personal = new Dictionary<int, IReadOnlyList<string>>();
        var peers = net.Roster.Keys.Order().ToList();
        for (var i = 0; i < peers.Count; i++)
        {
            var surgeon = SpawnSurgeon(peers[i], i);
            personal[peers[i]] = surgeon.Mods.List("items");
        }
        Tools.SpawnInitial(Scenario.RollTools(Rng, RunMods.Num("missing_tool_chance")), Room.TraySpots(), personal, Room.StationTools());
        Objectives.Setup(Scenario);
        Director.Setup(Scenario, net.SessionSeed);
        Hud.Setup(this);
        Multiplayer.PeerDisconnected += id => OnPeerLeft((int)id);
        RpcId(Net.HostId, MethodName.PeerReady);
    }

    /// <summary>The scenario named by `--scenario=id` after `--` on the command line, the first one otherwise.</summary>
    private static ScenarioDef ScenarioFromCommandLine()
    {
        var wanted = OS.GetCmdlineUserArgs()
            .Where(arg => arg.StartsWith("--scenario=", StringComparison.Ordinal))
            .Select(arg => arg["--scenario=".Length..])
            .FirstOrDefault();
        return (wanted is null ? null : Db.Scenario(wanted)) ?? Db.Scenarios[0];
    }

    private Surgeon SpawnSurgeon(int peer, int index)
    {
        var surgeon = new Surgeon();
        SurgeonsRoot.AddChild(surgeon);
        var info = Net.Instance.Roster[peer];
        surgeon.Setup(peer, info.Name, info.Quirks, Room.SpawnTransform(index));
        Surgeons[peer] = surgeon;
        if (surgeon.IsLocal)
        {
            LocalSurgeon = surgeon;
        }
        return surgeon;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!Running || Finished)
        {
            return;
        }
        var dt = (float)delta;
        Elapsed += dt;
        if (!IsHost)
        {
            return;
        }
        Objectives.Tick(dt, this);
        Director.Tick(dt, this);
        Nurse.Tick(dt, this);
        Lab.Tick(dt, this);
        TickTurn(dt);
        if (Scenario.TimeLimit > 0f && Elapsed >= Scenario.TimeLimit)
        {
            Finish(false, "Out of time.");
        }
        else if (Objectives.AllDone && (Patient.Alive || Patient.Flags.ContainsKey("euthanized")))
        {
            Finish(true, "");
        }
        _statusAcc += dt;
        if (_statusAcc >= StatusInterval)
        {
            _statusAcc = 0f;
            var status = new SurgeryStatus(Objectives.Snapshot(), Scoring.Points, [.. Scoring.Recent], Nurse.CooldownLeft,
                Nurse.Current, Lab.CooldownLeft, Elapsed);
            Rpc(MethodName.SyncStatus, status.ToVariant());
        }
    }

    /// <summary>A partner dropped out: their tools fall where they are and their surgeon leaves the room. Clients only
    /// ever see the host leave, and Net takes them back to the menu for that.</summary>
    private void OnPeerLeft(int peer)
    {
        if (!Surgeons.TryGetValue(peer, out var surgeon))
        {
            return;
        }
        if (IsHost)
        {
            Tools.DropAll(peer);
            if (_turn is not null)
            {
                _turn.Peers.Remove(peer);
                _turn.Results.Remove(peer);
            }
            Announce($"{surgeon.DisplayName} left the operation.");
            if (!Running)
            {
                TryStart();
            }
        }
        Surgeons.Remove(peer);
        surgeon.QueueFree();
    }

    /// <summary>Seconds left on the scenario's clock, -1 without a time limit.</summary>
    public float TimeLeft() => Scenario.TimeLimit > 0f ? Mathf.Max(Scenario.TimeLimit - Elapsed, 0f) : -1f;

    private void OnPatientDied(string reason)
    {
        if (Patient.Flags.ContainsKey("euthanized"))
        {
            Announce(reason);
            return;
        }
        Finish(false, reason);
    }

    private void Finish(bool success, string reason)
    {
        if (Finished)
        {
            return;
        }
        Finished = true;
        Rpc(MethodName.ShowReport, SurgeryReport.Build(this, success, reason).ToVariant());
    }

    // --- Host -> peers ------------------------------------------------------------------------------------

    /// <summary>Host: toast for everyone. <paramref name="throttled"/> drops repeats of the same text for a few seconds.
    /// </summary>
    public void Announce(string text, bool throttled = false)
    {
        if (!IsHost || text.Length == 0)
        {
            return;
        }
        var now = Time.GetTicksMsec() * 0.001;
        if (throttled && now - _announced.GetValueOrDefault(text, double.NegativeInfinity) < 5.0)
        {
            return;
        }
        _announced[text] = now;
        Rpc(MethodName.Toast, text);
    }

    /// <summary>Host: toast only players in debug mode see (objective progress and other things the game keeps to
    /// itself).</summary>
    public void AnnounceDebug(string text)
    {
        if (IsHost)
        {
            Rpc(MethodName.ToastDebug, text);
        }
    }

    /// <summary>Host: toast for one peer.</summary>
    public void Tell(int peer, string text) => RpcId(peer, MethodName.Toast, text);

    /// <summary>Host: play a sound on every peer, in 3D at <paramref name="at"/> or flat when it's null.</summary>
    public void Sound(string id, Vector3? at = null)
    {
        if (!Throttle(_soundMsec, id, SoundRepeatMsec))
        {
            return;
        }
        Rpc(MethodName.PlaySound, id, at ?? Vector3.Inf);
    }

    /// <summary>Continuous contact is sampled rather than retransmitting a one-shot each frame.</summary>
    public void ContactSound(int uid, string id, Vector3 at, float strength)
    {
        if (IsHost && Throttle(_contactMsec, uid, ContactRepeatMsec))
        {
            Rpc(MethodName.PlayContactSound, uid, id, at, strength);
        }
    }

    /// <summary>Host: a tool effect on every peer, at most one per kind every <paramref name="minMsec"/>.</summary>
    public void Effect(ToolEffect kind, Vector3 at, ulong minMsec = 120)
    {
        if (Throttle(_effectMsec, kind, minMsec))
        {
            Rpc(MethodName.PlayEffect, (int)kind, at);
        }
    }

    /// <summary>True (and remembers now) when <paramref name="key"/> last went through at least
    /// <paramref name="gapMsec"/> ago.</summary>
    private static bool Throttle<TKey>(Dictionary<TKey, ulong> last, TKey key, ulong gapMsec) where TKey : notnull
    {
        var now = Time.GetTicksMsec();
        if (last.TryGetValue(key, out var then) && now - then < gapMsec)
        {
            return false;
        }
        last[key] = now;
        return true;
    }

    /// <summary>The patient says something: a subtitle, the jaw moving, and the recorded line if there is one.
    /// </summary>
    public void Say(string text, string voiceId) => Rpc(MethodName.SayLine, text, voiceId);

    public void JoltAll(float strength, string text)
    {
        Rpc(MethodName.Jolt, strength);
        Announce(text, throttled: true);
    }

    public void JoltPeer(int peer, float strength) => RpcId(peer, MethodName.Jolt, strength);

    public void BroadcastStress(float amount) => Rpc(MethodName.Stress, amount * RunMods.Mult("stress_mult"));

    public void AddSickness(int peer, float amount) => RpcId(peer, MethodName.Sick, amount);

    /// <summary>Host: a syringe pushed <paramref name="amount"/> of a drug into a surgeon, given as it goes in.
    /// </summary>
    public void DoseSurgeon(int peer, string drug, float amount)
    {
        EmitSignal(SignalName.SurgeonDosed, peer, drug, amount);
        if (peer == Multiplayer.GetUniqueId() || Multiplayer.GetPeers().Contains(peer))
        {
            RpcId(peer, MethodName.Dosed, drug, amount);
        }
    }

    public void SetAttached(int peer, int hand, bool value)
    {
        if (Surgeons.TryGetValue(peer, out var surgeon) && hand >= 0)
        {
            surgeon.RpcId(peer, Surgeon.MethodName.SetHandAttached, hand, value);
        }
    }

    /// <summary>The tool in this hand of this surgeon bounced off what it was pressed onto: the hand hops up and comes
    /// back down.</summary>
    public void BounceHand(int peer, int hand)
    {
        if (Surgeons.TryGetValue(peer, out var surgeon) && hand >= 0)
        {
            surgeon.RpcId(peer, Surgeon.MethodName.BounceHand, hand);
        }
    }

    public void FlickerLights() => Rpc(MethodName.Flicker, (float)GD.RandRange(1.0, 3.0));

    /// <summary>Host: the nurse brings the corrected chart (Chart mix-up modifier).</summary>
    public void CorrectChart() => Rpc(MethodName.ChartCorrection);

    public void PublishLab(string text) => Rpc(MethodName.LabResult, text);

    /// <summary>Defibrillator discharge: anyone else touching the patient gets zapped.</summary>
    public void ShockBystanders(int sourcePeer)
    {
        foreach (var (peer, surgeon) in Surgeons)
        {
            if (peer != sourcePeer && surgeon.Hands.Any(hand => hand.Lowered && Patient.Body.Probe(hand.GlobalPosition).Zone != SiteZone.None))
            {
                RpcId(peer, MethodName.Zapped);
            }
        }
    }

    /// <summary>A surgeon walked away while holding onto tissue.</summary>
    public void Overstretched(int peer, int hand)
    {
        if (Tools.ToolInHand(peer, hand) is not { Hold: { } hold } tool)
        {
            return;
        }
        if (tool.Def.SelfRetaining)
        {
            // A self-retaining tool (a Gelpi retractor, a hooked retractor) holds by itself: walking away just leaves it.
            Tools.LeaveStanding(tool);
            Tell(peer, $"You let go of the {tool.Def.Name.ToLowerInvariant()}. It keeps its hold.");
            return;
        }
        var anchor = hold is SkinHold skin ? skin.Anchor : Patient.Body.WorldToUv(tool.TipPosition());
        Patient.Tear(anchor, new Vector2((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0)), 0.05f);
        Patient.ReleaseGrip(tool.Uid, hold, false);
        tool.Hold = null;
        SetAttached(peer, hand, false);
        Tell(peer, "You pulled away while holding on. The tissue tore.");
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Toast(string text) => Hud.Toast(text);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ToastDebug(string text)
    {
        if (Settings.Debug)
        {
            Hud.Toast("[debug] " + text);
        }
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void PlaySound(string id, Vector3 at) => Sfx.Play(id, at == Vector3.Inf ? null : at);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void PlayContactSound(int uid, string id, Vector3 at, float strength) => Sfx.Contact(uid, id, at, strength);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
    private void PlayEffect(int kind, Vector3 at) => _effects.Play((ToolEffect)kind, at);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SayLine(string text, string voiceId)
    {
        Hud.Subtitle(text);
        Patient.Body.Animator.Talk(Mathf.Clamp(text.Length / 14f, 1f, 6f));
        Sfx.PlayVoice(voiceId, Patient.GlobalPosition + new Vector3(0.7f, 0.2f, 0));
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Jolt(float strength)
    {
        Patient.Body.Tissue.Shake(strength * 0.003f);
        LocalSurgeon?.Jolt(strength);
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Stress(float amount) => LocalSurgeon?.Status.AddStress(amount);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Sick(float amount) => LocalSurgeon?.Status.AddSickness(amount);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Dosed(string drug, float amount)
    {
        if (LocalSurgeon is null)
        {
            return;
        }
        LocalSurgeon.Status.Administer(drug, amount);
        // Once for a few pushes in a row, not for every ml.
        var now = Time.GetTicksMsec();
        if (_stungMsec == 0 || now - _stungMsec > StingGapMsec)
        {
            Hud.Toast("A sharp sting. Something cold goes in.");
        }
        _stungMsec = now;
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Zapped()
    {
        if (LocalSurgeon is null)
        {
            return;
        }
        Hud.Toast("ZAP! You were touching the patient during the shock.");
        LocalSurgeon.Status.AddStress(0.4f);
        LocalSurgeon.Jolt(1f);
        LocalSurgeon.DropEverything();
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Flicker(float duration) => Room.Flicker(duration);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ChartCorrection()
    {
        ChartCorrected = true;
        Hud.Toast("Nurse: \"Corrected chart. The old one belonged to someone else. Sorry.\"");
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void LabResult(string text)
    {
        Room.Monitor.ShowLab(text);
        Hud.Toast("Lab results are on the monitor.");
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void SyncStatus(GodotDictionary data)
    {
        Status = SurgeryStatus.FromVariant(data);
        Elapsed = Status.Elapsed;
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Start()
    {
        Running = true;
        Hud.Begin();
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void ShowReport(GodotDictionary data)
    {
        var report = SurgeryReport.FromVariant(data);
        Report = report;
        Running = false;
        Finished = true;
        foreach (var roll in report.PatientQuirks)
        {
            Progress.Unlock(Db.Quirk(QuirkKind.Patient, roll.Id), roll.Variant);
        }
        if (IsHost)
        {
            Progress.RecordResult(report.Scenario, report.Stars);
        }
        Hud.ShowReport(report);
    }

    // --- Peer -> host requests ----------------------------------------------------------------------------

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PeerReady()
    {
        _loaded.Add(Net.Instance.Sender());
        TryStart();
    }

    private void TryStart()
    {
        if (!Running && Net.Instance.Roster.Keys.All(_loaded.Contains))
        {
            Rpc(MethodName.Start);
        }
    }

    public void RequestDrink(int hand) => RpcId(Net.HostId, MethodName.RequestDrinkOnHost, hand);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestDrinkOnHost(int hand)
    {
        var peer = Net.Instance.Sender();
        if (Tools.Drink(peer, hand) is { } id)
        {
            RpcId(peer, MethodName.Drank, id);
        }
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Drank(string toolId)
    {
        if (LocalSurgeon is null)
        {
            return;
        }
        LocalSurgeon.Status.Drink(toolId);
        Sfx.Play("sip", LocalSurgeon.GlobalPosition);
        Hud.Toast(toolId switch
        {
            "whiskey_flask" => "Warm. Steady.",
            "coffee_thermos" => "Bitter. Awake.",
            "surgical_cap" => "Cap on. No more drips.",
            _ => "",
        });
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestSmoke(int hand)
    {
        var peer = Net.Instance.Sender();
        var tool = Tools.ToolInHand(peer, hand);
        if (tool is null || tool.Def.Id != "cig_pack" || !Tools.UseCharge(tool))
        {
            return;
        }
        var eyes = Surgeons[peer].Camera.GlobalTransform;
        var mouth = eyes.Origin - eyes.Basis.Z * 0.15f + Vector3.Down * 0.08f;
        Sound("lighter_flick", mouth);
        Effect(ToolEffect.Smoke, mouth);
        RpcId(peer, MethodName.Smoked);
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Smoked()
    {
        LocalSurgeon?.Status.Smoke();
        Hud.Toast("Ahh. Nothing can touch you now.");
    }

    /// <summary>Something happened to the local surgeon the host scores or acts on: "vomit", "passed_out",
    /// "knocked_out", "sweat_drip".</summary>
    public void ReportIncident(string kind) => RpcId(Net.HostId, MethodName.Incident, kind);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Incident(string kind)
    {
        if (!Surgeons.TryGetValue(Net.Instance.Sender(), out var surgeon))
        {
            return;
        }
        switch (kind)
        {
            case "vomit":
                Scoring.Add("vomit");
                var at = surgeon.Hands[surgeon.Active].GlobalPosition;
                if (Patient.Body.Probe(at + Vector3.Down * 0.1f).Zone is SiteZone.Site or SiteZone.Cavity)
                {
                    var uv = Patient.Body.WorldToUv(at);
                    Patient.Paint(WoundMap.Layer.Fluids, WoundMap.Grime, uv, uv, 0.15f, 0.9f, WoundMap.Mode.Max);
                    Patient.ContaminateSite("...right into the surgical field.");
                }
                foreach (var other in Surgeons.Keys.Where(other => other != surgeon.PeerId))
                {
                    AddSickness(other, 0.3f);
                }
                break;
            case "passed_out" or "knocked_out":
                Scoring.Add("passed_out");
                break;
            case "sweat_drip":
                Patient.ContaminateSite("");
                break;
        }
    }

    /// <summary>A bag in hand takes the place of the one on the IV stand: it runs into the line (a full dose, if the
    /// line works). Drugs pushed into the old bag go with it.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestIvBag(int hand)
    {
        var peer = Net.Instance.Sender();
        var tool = Tools.ToolInHand(peer, hand);
        if (tool is null || !tool.Def.IvOnly || tool.Charges == 0)
        {
            Tell(peer, "Hold a bag to hang it on the stand.");
            return;
        }
        if (Tools.DripBag() is { } drip)
        {
            drip.Contents.Clear();
            drip.Bolus = 0f;
            drip.DrippedTotal = 0f;
            var blood = Db.Drug(tool.Def.Drug) is { IsBlood: true };
            Tools.AddLiquid(drip, SurgicalTool.DripFluid - drip.Ml,
                blood ? new Dictionary<string, float> { ["blood"] = SurgicalTool.DripFluid } : []);
        }
        Patient.Administer(tool.Def.Drug, DrugRoute.Iv);
        if (tool.Charges > 0)
        {
            tool.Charges--;
            if (tool.Charges == 0)
            {
                Tools.Consume(tool);
            }
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestComfort() => Patient.Reassure();

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestOrder(string[] toolIds) => Nurse.Request(Net.Instance.Sender(), toolIds, this);

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestLab(string kind) => Lab.Request(kind, this);

    // --- Stations (called on the interacting peer) --------------------------------------------------------

    public void OpenManual() => Hud.OpenManual();

    public void OpenCard() => Hud.OpenCard();

    public void OpenNurse() => Hud.OpenNurse();

    public void OpenLab() => Hud.OpenLab();

    public void OrderTools(IEnumerable<string> toolIds) => RpcId(Net.HostId, MethodName.RequestOrder, toolIds.ToArray());

    public void OrderLab(string kind) => RpcId(Net.HostId, MethodName.RequestLab, kind);

    public void ChangeGloves(Surgeon surgeon)
    {
        surgeon.Status.Sweat = 0f;
        surgeon.Rpc(Surgeon.MethodName.CleanGloves);
        Hud.Toast("Fresh gloves.");
    }

    public void SanitizeTool(Surgeon surgeon) => Tools.RequestSterilize(surgeon.Active);

    public void WashTool(Surgeon surgeon)
    {
        Tools.RequestWash(surgeon.Active);
        Sfx.Play("sink_water", surgeon.GlobalPosition);
    }

    public void UseIv(Surgeon surgeon) => RpcId(Net.HostId, MethodName.RequestIvBag, surgeon.Active);

    public void Smoke(Surgeon surgeon) => RpcId(Net.HostId, MethodName.RequestSmoke, surgeon.Active);

    public void ComfortPatient() => RpcId(Net.HostId, MethodName.RequestComfort);

    public void TurnPatient() => RpcId(Net.HostId, MethodName.RequestTurn);

    // --- Turning the patient (quick time event) -----------------------------------------------------------
    // Everyone near the table takes part. All hits: the patient turns one step. A few misses: they slide back, try
    // again. Too many misses or a wrong key: they hit the floor.

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void RequestTurn()
    {
        var peer = Net.Instance.Sender();
        if (_turn is not null)
        {
            return;
        }
        var center = Patient.GlobalPosition;
        var near = Surgeons.Where(pair => pair.Value.GlobalPosition.DistanceTo(center) < 1.5f).Select(pair => pair.Key).ToList();
        if (near.Count == 0)
        {
            Tell(peer, "Get closer to the table to turn them.");
            return;
        }
        if (near.Select(p => Mathf.Sign(Surgeons[p].GlobalPosition.Z)).Distinct().Count() > 1)
        {
            Tell(peer, "Get on the same side of the table to turn them.");
            return;
        }
        if (near.Any(p => Surgeons[p].Hands.Any(hand => hand.Attached)))
        {
            Tell(peer, "Let go of everything first.");
            return;
        }
        var sequence = Enumerable.Range(0, 4).Select(_ => QteKeys[Rng.RandiRange(0, QteKeys.Count - 1)]).ToArray();
        _turn = new TurnAttempt(near, NextOrientation());
        foreach (var p in near)
        {
            RpcId(p, MethodName.TurnBegin, sequence, 1.1f);
        }
    }

    private PatientPose NextOrientation()
    {
        if (Patient.Body.Pose != PatientPose.Side)
        {
            return PatientPose.Side;
        }
        var flip = Scenario.Steps.FirstOrDefault(step => step.Type == "flip");
        return flip is null ? PatientPose.FaceUp : (PatientPose)flip.Parameters.Int("orientation", (int)PatientPose.FaceDown);
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TurnBegin(string[] sequence, float window) =>
        Hud.RunQte(sequence, window * (LocalSurgeon?.Mods.Mult("qte_window_mult") ?? 1f),
            (hits, critical) => RpcId(Net.HostId, MethodName.TurnResult, hits, critical));

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TurnResult(int hits, bool critical)
    {
        if (_turn is null)
        {
            return;
        }
        _turn.Results[Net.Instance.Sender()] = (hits, critical);
        if (_turn.Results.Count >= _turn.Peers.Count)
        {
            ResolveTurn(_turn);
        }
    }

    private void TickTurn(float delta)
    {
        if (_turn is null)
        {
            return;
        }
        _turn.Timer -= delta;
        if (_turn.Timer <= 0f)
        {
            ResolveTurn(_turn);
        }
    }

    private void ResolveTurn(TurnAttempt turn)
    {
        var misses = 4 * turn.Peers.Count - turn.Results.Values.Sum(result => result.Hits);
        var critical = turn.Results.Values.Any(result => result.Critical);
        if (critical || misses > turn.Peers.Count)
        {
            Scoring.Add("patient_fell");
            Announce("The patient slides off the table and hits the floor!");
            Patient.TurnOver(Patient.Body.Pose, fell: true);
            Sound("body_fall", Patient.GlobalPosition);
        }
        else if (misses > 0)
        {
            Announce("They slide back. Try again, together this time.");
        }
        else
        {
            Scoring.Add("flip_success");
            Patient.TurnOver(turn.Target, fell: false);
            Announce("Patient turned.");
        }
        _turn = null;
    }
}
