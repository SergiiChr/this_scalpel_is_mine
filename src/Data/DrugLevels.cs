namespace Scalpel.Data;

/// <summary>
/// How much of each drug is in a body, as a share of the right dose for its weight (<see cref="DrugDef.Dose"/>),
/// however many injections it came in: ten 1 ml shots add up to one 10 ml shot. A dose goes into a depot first and
/// soaks into the blood over its route's onset; the level in the blood then drops by one right dose every duration
/// seconds, so twice the dose lasts twice as long. From <see cref="DrugDef.DoseEffective"/> in the blood the drug
/// does its job. From <see cref="DrugDef.DoseOverdose"/> taken in (in the blood or still soaking in, so one big dose
/// counts in full though some wears off meanwhile) it's an overdose; only for drugs that are dosed, not bags of fluid
/// or blood.
/// </summary>
public sealed class DrugLevels
{
    /// <summary>Below this share of DoseEffective a drug that worked counts as worn off, so another dose makes it work
    /// again.</summary>
    public const float WornOff = 0.5f;
    /// <summary>What's left of a depot once it's this small soaks in at once.</summary>
    public const float DepotLeft = 0.0001f;

    /// <summary>One drug in the body.</summary>
    public sealed class Entry(DrugDef def, float onset)
    {
        public DrugDef Def { get; } = def;
        /// <summary>Share of the right dose still soaking in.</summary>
        public float Depot { get; set; }
        /// <summary>Share of the right dose in the blood.</summary>
        public float Level { get; set; }
        /// <summary>Seconds the last dose takes to soak in.</summary>
        public float Onset { get; set; } = onset;
        /// <summary>Seconds since it reached DoseEffective, -1 while it hasn't.</summary>
        public float Working { get; set; } = -1f;
        public bool Overdosed { get; set; }

        public bool IsWorking => Working >= 0f;
    }

    /// <summary>A drug crossing a threshold during <see cref="Update"/>.</summary>
    public enum Crossing { Works, Overdose }

    private readonly Dictionary<string, Entry> _entries = [];

    public IReadOnlyCollection<Entry> Entries => _entries.Values;

    public Entry? Find(string id) => _entries.GetValueOrDefault(id);

    /// <summary>Adds <paramref name="share"/> of the right dose, soaking in over <paramref name="onset"/> seconds.
    /// True when the drug wasn't in the body yet.</summary>
    public bool Give(DrugDef def, float share, float onset)
    {
        var fresh = !_entries.TryGetValue(def.Id, out var entry);
        if (entry is null)
        {
            entry = new Entry(def, onset);
            _entries[def.Id] = entry;
        }
        entry.Depot += share;
        entry.Onset = Mathf.Max(onset, 0.1f);
        return fresh;
    }

    /// <summary>
    /// Moves every drug on by <paramref name="dt"/> seconds: soaking in, then wearing off as fast as
    /// <paramref name="wear"/> says (1 for a dose over its duration, 0 holds it). Returns what crossed a threshold.
    /// </summary>
    public List<(DrugDef Def, Crossing Crossing)> Update(float dt, Func<DrugDef, float, float> wear)
    {
        var crossed = new List<(DrugDef, Crossing)>();
        foreach (var entry in _entries.Values.ToList())
        {
            var def = entry.Def;
            // All of it as it came in, before this step wears any off.
            var taken = entry.Level + entry.Depot;
            // Fast at first, most of it in by the onset.
            var soaked = entry.Depot < DepotLeft
                ? entry.Depot
                : entry.Depot * (1f - Mathf.Exp(-3f * dt / entry.Onset));
            entry.Depot -= soaked;
            entry.Level = Mathf.Max(
                entry.Level + soaked - dt / Mathf.Max(def.Duration, 0.01f) * wear(def, entry.Level), 0f);
            if (!entry.IsWorking && entry.Level >= DrugDef.DoseEffective)
            {
                entry.Working = 0f;
                crossed.Add((def, Crossing.Works));
            }
            else if (entry.IsWorking)
            {
                entry.Working = entry.Level < DrugDef.DoseEffective * WornOff ? -1f : entry.Working + dt;
            }
            if (!entry.Overdosed && def.Dose > 0f && taken >= DrugDef.DoseOverdose)
            {
                entry.Overdosed = true;
                crossed.Add((def, Crossing.Overdose));
            }
            else if (entry.Overdosed && taken < DrugDef.DoseHigh)
            {
                entry.Overdosed = false;
            }
            if (entry.Level <= 0f && entry.Depot <= 0f)
            {
                _entries.Remove(def.Id);
            }
        }
        return crossed;
    }

    /// <summary>The share of the right dose of this drug in the blood (0 when there's none).</summary>
    public float Level(string id) => _entries.TryGetValue(id, out var entry) ? entry.Level : 0f;

    /// <summary>Whether a drug with this flag is in at an effective level.</summary>
    public bool Working(string flag) => _entries.Values.Any(entry => entry.IsWorking && entry.Def.HasFlag(flag));

    /// <summary>Takes every drug <paramref name="gone"/> says out of the body.</summary>
    public void Remove(Func<DrugDef, bool> gone)
    {
        foreach (var id in _entries.Where(pair => gone(pair.Value.Def)).Select(pair => pair.Key).ToList())
        {
            _entries.Remove(id);
        }
    }
}
