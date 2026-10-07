using GodotDictionary = Godot.Collections.Dictionary;

namespace Scalpel.Operation;

/// <summary>What the host tells every peer twice a second, for the HUD: objectives, score, the scored log, the
/// nurse's and the lab's state, and the surgery's clock.</summary>
public sealed record SurgeryStatus(
    IReadOnlyList<ObjectiveView> Objectives,
    int Score,
    IReadOnlyList<(string Text, int Points)> Log,
    float NurseCooldown,
    NurseOrder? Order,
    float LabCooldown,
    float Elapsed)
{
    public static readonly SurgeryStatus Empty = new([], 0, [], 0f, null, 0f, 0f);

    /// <summary>What the board over the nurse's bell shows: the order on its way with its progress, or her cooldown.
    /// </summary>
    public string NurseBoard
    {
        get
        {
            if (Order is { } order)
            {
                var done = Mathf.RoundToInt(order.Progress * 10f);
                return $"{order.Text}\n{new string('■', done)}{new string('□', 10 - done)}  {Mathf.CeilToInt(order.SecondsLeft)} s";
            }
            return NurseCooldown > 0f ? $"Nurse is busy for {Mathf.CeilToInt(NurseCooldown)} s" : "Nurse ready";
        }
    }

    /// <summary>Whether the nurse takes an order now.</summary>
    public bool NurseReady => Order is null && NurseCooldown <= 0f;

    public GodotDictionary ToVariant() => new()
    {
        ["objectives"] = new Godot.Collections.Array(Objectives.Select(view => (Variant)view.ToVariant())),
        ["score"] = Score,
        ["log"] = new Godot.Collections.Array(Log.Select(entry => (Variant)new Godot.Collections.Array { entry.Text, entry.Points })),
        ["nurse"] = NurseCooldown,
        ["order"] = Order is { } order ? new Godot.Collections.Array { order.Text, order.SecondsLeft, order.Progress } : [],
        ["lab"] = LabCooldown,
        ["elapsed"] = Elapsed,
    };

    public static SurgeryStatus FromVariant(GodotDictionary data)
    {
        var order = data["order"].AsGodotArray();
        return new SurgeryStatus(
            [.. data["objectives"].AsGodotArray().Select(ObjectiveView.FromVariant)],
            data.Int("score"),
            [.. data["log"].AsGodotArray().Select(entry => entry.AsGodotArray()).Select(entry => (entry[0].AsString(), entry[1].AsInt32()))],
            data.Float("nurse"),
            order.Count == 3 ? new NurseOrder(order[0].AsString(), order[1].AsSingle(), order[2].AsSingle()) : null,
            data.Float("lab"),
            data.Float("elapsed"));
    }
}
