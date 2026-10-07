namespace Scalpel.Operation;

/// <summary>A blood panel the lab runs: its name and how long it takes (seconds).</summary>
public sealed record LabPanel(string Label, float Seconds);

/// <summary>Blood panels, host only. Narrow panels come back faster than the full one. Shared cooldown.</summary>
public partial class Lab : Node
{
    public const float Cooldown = 60f;

    public static readonly IReadOnlyDictionary<string, LabPanel> Panels = new Dictionary<string, LabPanel>
    {
        ["glucose"] = new("Glucose", 15f),
        ["coag"] = new("Clotting", 25f),
        ["counts"] = new("Blood count and type", 25f),
        ["allergy"] = new("Allergy screen", 40f),
        ["full"] = new("Full panel", 60f),
    };

    private sealed class Order(string kind, float eta)
    {
        public string Kind { get; } = kind;
        public float Eta { get; set; } = eta;
    }

    private readonly List<Order> _pending = [];

    public float CooldownLeft { get; private set; }

    public void Request(string kind, Surgery surgery)
    {
        if (!Panels.TryGetValue(kind, out var panel) || CooldownLeft > 0f)
        {
            return;
        }
        CooldownLeft = Cooldown;
        _pending.Add(new Order(kind, panel.Seconds));
        surgery.Announce($"Blood sample sent: {panel.Label} ({panel.Seconds:0} s).");
    }

    public void Tick(float delta, Surgery surgery)
    {
        CooldownLeft = Mathf.Max(CooldownLeft - delta, 0f);
        foreach (var order in _pending.ToList())
        {
            order.Eta -= delta;
            if (order.Eta <= 0f)
            {
                _pending.Remove(order);
                surgery.PublishLab(Result(order.Kind, surgery.Patient));
            }
        }
    }

    /// <summary>What a panel reports about the patient now.</summary>
    public static string Result(string kind, Patient patient)
    {
        var vitals = patient.Vitals;
        var mods = patient.Mods;
        var full = kind == "full";
        var lines = new List<string>();
        if (kind == "glucose" || full)
        {
            var reading = vitals.Glucose > 11f ? "HIGH" : vitals.Glucose < 4f ? "LOW" : "ok";
            lines.Add($"Glucose {vitals.Glucose:0.0} mmol/L {reading}");
        }
        if (kind == "coag" || full)
        {
            var bleed = mods.Mult("bleed_mult");
            var speed = bleed > 1.3f ? "slow" : bleed < 0.8f ? "fast" : "normal";
            lines.Add($"Clotting: {speed}{(mods.Num("clot_risk") > 0f ? ", clot risk" : "")}");
        }
        if (kind == "counts" || full)
        {
            lines.Add($"Hb {(int)(140f * mods.Mult("blood_ml_mult") * vitals.BloodRatio)} g/L, volume {(int)(vitals.BloodRatio * 100f)}%, type {patient.BloodType}");
        }
        if (kind == "allergy" || full)
        {
            var allergens = mods.List("allergen");
            lines.Add($"Reacts to: {(allergens.Count > 0 ? string.Join(", ", allergens) : "nothing found")}");
        }
        if (full)
        {
            var density = mods.Num("bone_hardness") >= 2f ? "very high" : mods.Flag("bone_fragile") ? "low" : "normal";
            lines.Add($"Bone density: {density}");
            lines.Add($"Temp trend: {(mods.Flag("mh_trigger") ? "rising" : "stable")}");
        }
        return "LAB: " + string.Join('\n', lines);
    }
}
