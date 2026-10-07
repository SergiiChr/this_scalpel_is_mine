namespace Scalpel.Operation;

/// <summary>
/// Escalation, host only. Rolls random events from the scenario's list, more often as time runs out, and fires
/// scripted ones on schedule. Event tuning is in data/events.cfg.
/// </summary>
public partial class EventDirector : Node
{
    private const float CalmInterval = 75f;
    private const float FranticInterval = 25f;
    private const float RambleInterval = 9f;
    /// <summary>The flickering lights event is always possible; run modifiers give it its weight.</summary>
    private const string LightsFlicker = "lights_flicker";

    private readonly Dictionary<string, float> _cooldowns = [];
    /// <summary>Indices of the scripted events that already fired.</summary>
    private readonly HashSet<int> _fired = [];
    private readonly RandomNumberGenerator _rng = new();
    private IReadOnlyList<string> _pool = [];
    private IReadOnlyList<ScriptedEvent> _scripted = [];
    private float _nextRoll = 40f;
    private float _rambleTimer = 6f;
    private int _rambleIndex;
    private float _timeLimit = 1200f;
    private float _chartUpdateAt = float.PositiveInfinity;

    public void Setup(ScenarioDef scenario, uint seed)
    {
        _chartUpdateAt = (scenario.TimeLimit > 0f ? scenario.TimeLimit : 900f) * (float)GD.RandRange(0.25, 0.5);
        _pool = scenario.Events;
        _scripted = scenario.ScriptedEvents;
        _timeLimit = scenario.TimeLimit > 0f ? scenario.TimeLimit : 1200f;
        _rng.Seed = seed + 7;
    }

    public void Tick(float delta, Surgery surgery)
    {
        var elapsed = surgery.Elapsed;
        if (surgery.RunMods.Flag("card_error") && !surgery.ChartCorrected && elapsed >= _chartUpdateAt)
        {
            surgery.CorrectChart();
        }
        for (var i = 0; i < _scripted.Count; i++)
        {
            if (elapsed >= _scripted[i].At && _fired.Add(i))
            {
                Fire(_scripted[i].Id, surgery);
            }
        }
        foreach (var id in _cooldowns.Keys.ToList())
        {
            _cooldowns[id] -= delta;
        }
        var patient = surgery.Patient;
        if (_pool.Contains("panic_flail") && patient.Vitals.Panic > 0.8f && OffCooldown("panic_flail"))
        {
            Fire("panic_flail", surgery);
        }
        if (_pool.Contains("ramble"))
        {
            _rambleTimer -= delta;
            if (_rambleTimer <= 0f && patient.Ramble(_rambleIndex))
            {
                _rambleIndex++;
                _rambleTimer = RambleInterval;
            }
        }
        _nextRoll -= delta;
        if (_nextRoll > 0f)
        {
            return;
        }
        var intensity = Mathf.Clamp(elapsed / _timeLimit, 0f, 1f);
        _nextRoll = Mathf.Lerp(CalmInterval, FranticInterval, intensity) * _rng.RandfRange(0.7f, 1.3f);
        if (Pick(elapsed, surgery) is { } picked)
        {
            Fire(picked, surgery);
        }
    }

    public void Fire(string id, Surgery surgery)
    {
        var config = new ConfigReader(Db.Events, id);
        _cooldowns[id] = config.Float("cooldown", 30f);
        var text = config.String("text");
        var patient = surgery.Patient;
        switch (id)
        {
            case "arrest":
                patient.Arrest();
                break;
            case "wake_up":
                if (patient.Vitals.Anesthesia > 0.5f)
                {
                    patient.WakeUp();
                    surgery.Announce(text);
                }
                break;
            case "panic_flail":
                surgery.JoltAll(0.5f, text);
                break;
            case "cough":
                surgery.JoltAll(0.3f, text);
                break;
            case "pothole":
                surgery.JoltAll(0.6f, text);
                break;
            case "pedestrian_bump":
                var peers = surgery.Surgeons.Keys.ToList();
                surgery.JoltPeer(peers[_rng.RandiRange(0, peers.Count - 1)], 0.9f);
                surgery.Announce(text);
                break;
            case LightsFlicker:
                surgery.FlickerLights();
                break;
        }
    }

    private bool OffCooldown(string id) => _cooldowns.GetValueOrDefault(id) <= 0f;

    /// <summary>Whether the patient is in a state the event needs (requires in data/events.cfg).</summary>
    private static bool ReadyFor(string requirement, Patient patient) => requirement switch
    {
        "" => true,
        "awake" => patient.Vitals.IsAwake,
        _ => patient.Mods.Num(requirement) != 0f,
    };

    /// <summary>A random event that may happen now, by weight, or null.</summary>
    private string? Pick(float elapsed, Surgery surgery)
    {
        var total = 0f;
        var options = new List<(string Id, float Weight)>();
        foreach (var id in _pool.Append(LightsFlicker))
        {
            var config = new ConfigReader(Db.Events, id);
            var weight = config.Float("weight");
            if (id == LightsFlicker)
            {
                weight += surgery.RunMods.Num("flicker_weight");
            }
            if (weight <= 0f || !OffCooldown(id) || elapsed < config.Float("min_time"))
            {
                continue;
            }
            if (!ReadyFor(config.String("requires"), surgery.Patient))
            {
                continue;
            }
            total += weight;
            options.Add((id, weight));
        }
        var roll = _rng.Randf() * total;
        foreach (var (id, weight) in options)
        {
            roll -= weight;
            if (roll <= 0f)
            {
                return id;
            }
        }
        return null;
    }
}
