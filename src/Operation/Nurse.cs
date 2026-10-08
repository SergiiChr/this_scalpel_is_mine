namespace Scalpel.Operation;

/// <summary>The order on its way, for the board over the bell: what, seconds left and how far along (0..1).</summary>
public readonly record struct NurseOrder(string Text, float SecondsLeft, float Progress);

/// <summary>
/// Tool requests, host only. One order at a time shared by both surgeons: a batch of up to Batch items (the same one
/// more than once too), fetched together, so it takes as long as its slowest item. Then the nurse needs a breather
/// before the next one. The first few deliveries come without one.
/// </summary>
public partial class Nurse : Node
{
    public const float Cooldown = 15f;
    public const int FreeOrders = 5;
    public const int Batch = 5;

    private sealed class Order(IReadOnlyList<string> ids, float total)
    {
        public IReadOnlyList<string> Ids { get; } = ids;
        public float Total { get; } = total;
        public float Eta { get; set; } = total;
    }

    /// <summary>The order on its way, null when there's none.</summary>
    private Order? _order;

    /// <summary>The nurse takes an order now: none on its way and her cooldown over.</summary>
    public bool Idle => _order is null && CooldownLeft <= 0f;

    public float CooldownLeft { get; internal set; }
    public int Delivered { get; internal set; }

    public void Request(int peer, IReadOnlyList<string> toolIds, Surgery surgery)
    {
        if (toolIds.Count == 0 || toolIds.Count > Batch || toolIds.Any(id => Db.Tool(id) is not { Orderable: true }))
        {
            return;
        }
        if (_order is not null)
        {
            surgery.Tell(peer, $"The nurse is still fetching the {BatchText(_order.Ids)} ({Mathf.CeilToInt(_order.Eta)} s).");
            return;
        }
        if (CooldownLeft > 0f)
        {
            surgery.Tell(peer, $"The nurse is still busy ({Mathf.CeilToInt(CooldownLeft)} s).");
            return;
        }
        var surgeon = surgery.Surgeons.GetValueOrDefault(peer);
        var delay = toolIds.Max(id => DeliveryTime(Db.Tool(id)!, surgeon, surgery));
        _order = new Order(toolIds, delay);
        surgery.Sound("nurse_bell");
        surgery.Announce($"Nurse: \"{BatchText(toolIds)}. Give me {Mathf.CeilToInt(delay)} seconds.\"");
    }

    /// <summary>How long one item takes the nurse to fetch for this surgeon (null for nobody in particular).</summary>
    public static float DeliveryTime(ToolDef def, Surgeon? surgeon, Surgery surgery)
    {
        var delay = def.Delay * (surgeon?.Mods.Mult("nurse_delay_mult") ?? 1f) * surgery.RunMods.Mult("nurse_delay_mult");
        return def.Drug.StartsWith("blood", StringComparison.Ordinal) ? delay * surgery.RunMods.Mult("blood_delay_mult") : delay;
    }

    /// <summary>"Gauze ×2, Scalpel": names in the order first asked for, repeats counted.</summary>
    public static string BatchText(IEnumerable<string> toolIds) =>
        string.Join(", ", toolIds.GroupBy(id => id).Select(group =>
            Db.Tool(group.Key)!.Name + (group.Count() == 1 ? "" : $" ×{group.Count()}")));

    /// <summary>The order on its way, null when nothing is.</summary>
    public NurseOrder? Current =>
        _order is null ? null : new NurseOrder(BatchText(_order.Ids), _order.Eta, 1f - _order.Eta / _order.Total);

    public void Tick(float delta, Surgery surgery)
    {
        CooldownLeft = Mathf.Max(CooldownLeft - delta, 0f);
        if (_order is null)
        {
            return;
        }
        _order.Eta -= delta;
        if (_order.Eta > 0f)
        {
            return;
        }
        var ids = _order.Ids;
        for (var i = 0; i < ids.Count; i++)
        {
            var spot = surgery.Room.DeliverySpot(i, ids.Count);
            // Bottles come standing, cap up, ready to draw from.
            if (Db.Tool(ids[i])!.Tray == "bottles")
            {
                surgery.Tools.SpawnStanding(ids[i], spot);
            }
            else
            {
                surgery.Tools.Spawn(ids[i], spot);
            }
        }
        surgery.Sound("nurse_delivery");
        surgery.Announce($"Nurse leaves the {BatchText(ids)} on the delivery tray.");
        _order = null;
        Delivered++;
        if (Delivered > FreeOrders)
        {
            CooldownLeft = Cooldown * surgery.RunMods.Mult("nurse_cooldown_mult");
        }
    }
}
