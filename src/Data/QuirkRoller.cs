namespace Scalpel.Data;

/// <summary>Random quirk rolls for surgeons and the patient.</summary>
public static class QuirkRoller
{
    private const int MaxAttempts = 64;
    private static readonly QuirkRoll Fallback = new("normal_dude", "");

    /// <summary>1-3 surgeon quirks. Three have at least one positive and one negative (mixed counts as both),
    /// exclusive quirks come alone.</summary>
    public static List<QuirkRoll> RollSurgeon(RandomNumberGenerator rng)
    {
        var table = Db.SurgeonQuirks;
        var count = rng.RandiRange(1, 3);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var rolls = Pick(table, [.. table.Keys], count, rng);
            if (rolls.Count == 1 && table[rolls[0].Id].IsExclusive)
            {
                return rolls;
            }
            if (rolls.Any(roll => table[roll.Id].IsExclusive))
            {
                continue;
            }
            if (count < 3 || IsBalanced(rolls, table))
            {
                return rolls;
            }
        }
        return [Fallback];
    }

    /// <summary>The scenario's fixed patient quirks plus random ones from its pool that fit its site.</summary>
    public static List<QuirkRoll> RollPatient(ScenarioDef scenario, RandomNumberGenerator rng)
    {
        var table = Db.PatientQuirks;
        var rolls = scenario.FixedPatientQuirks.ToList();
        var pool = (scenario.PatientQuirkPool.Count > 0 ? scenario.PatientQuirkPool : table.Keys)
            .Where(id => rolls.All(roll => roll.Id != id) && table[id].FitsSite(scenario.Site))
            .ToList();
        var count = rng.RandiRange(scenario.PatientQuirksMin, scenario.PatientQuirksMax) - rolls.Count;
        rolls.AddRange(Pick(table, pool, count, rng));
        return rolls;
    }

    private static List<QuirkRoll> Pick(
        IReadOnlyDictionary<string, QuirkDef> table, List<string> pool, int count, RandomNumberGenerator rng)
    {
        var ids = pool.ToList();
        var rolls = new List<QuirkRoll>();
        for (var i = 0; i < Math.Min(count, pool.Count); i++)
        {
            var index = rng.RandiRange(0, ids.Count - 1);
            var id = ids[index];
            ids.RemoveAt(index);
            var variants = table[id].Variants;
            var variant = variants.Count == 0 ? "" : variants[rng.RandiRange(0, variants.Count - 1)];
            rolls.Add(new QuirkRoll(id, variant));
        }
        return rolls;
    }

    private static bool IsBalanced(List<QuirkRoll> rolls, IReadOnlyDictionary<string, QuirkDef> table)
    {
        var polarities = rolls.Select(roll => table[roll.Id].Polarity(roll.Variant)).ToList();
        return (polarities.Contains("positive") || polarities.Contains("mixed"))
            && (polarities.Contains("negative") || polarities.Contains("mixed"));
    }
}
