namespace Scalpel.Data;

/// <summary>One entry of data/tools.cfg. Field meaning is documented at the top of that file.</summary>
public sealed class ToolDef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Action { get; init; } = "none";
    public string Description { get; init; } = "";
    public float Sharpness { get; init; } = 1f;
    public float Quality { get; init; } = 1f;
    /// <summary>Chance per staple that it tears out through the skin.</summary>
    public float TearChance { get; init; }
    /// <summary>Chance per staple that it goes through a vessel and makes it bleed.</summary>
    public float BleedChance { get; init; }
    /// <summary>A stapler's staple width: meters between its legs.</summary>
    public float StapleSpan { get; init; }
    /// <summary>How much further out each of a stapler's legs can reach for an edge.</summary>
    public float StapleGive { get; init; }
    public float Radius { get; init; } = 0.01f;
    public float Power { get; init; } = 1f;
    public bool Sterile { get; init; }
    public string Size { get; init; } = "normal";
    public bool Improvised { get; init; }
    public bool Orderable { get; init; }
    public float Delay { get; init; } = 20f;
    public string Category { get; init; } = "Supplies";
    public int Charges { get; init; } = -1;
    public string Drug { get; init; } = "";
    public float Volume { get; init; }
    public float Concentration { get; init; }
    public bool Fragile { get; init; }
    public bool Drinkable { get; init; }
    public bool IvOnly { get; init; }
    public bool SelfRetaining { get; init; }
    /// <summary>Stays where it is: no hand picks it up (the IV drip on its stand).</summary>
    public bool Fixed { get; init; }
    public string Model { get; init; } = "";
    public float Length { get; init; } = 0.12f;
    public float Width { get; init; } = 0.012f;
    public Color Color { get; init; } = Colors.Gray;
    public string Grip { get; init; } = "pencil";
    /// <summary>Group it's laid out with on the instrument tray at the start (<see cref="Room.TrayZones"/>), "" for
    /// the space left.</summary>
    public string Tray { get; init; } = "";

    /// <summary>The model file name: shared models (vials, bags) name theirs, the rest use the tool id.</summary>
    public string ModelName => Model.Length > 0 ? Model : Id;

    /// <summary>
    /// An open dish or pot: it holds liquid without doing anything of its own (the iodine dish, the kidney dish). A
    /// bottle pours into it, a syringe squirts into it and draws from it, a cotton pad dips into iodine in it.
    /// </summary>
    public bool IsDish => Volume > 0f && Action == "none";

    public bool IsHeavy => Size == "heavy";

    public static ToolDef FromConfig(ConfigReader config)
    {
        var action = config.String("action", "none");
        var trayByAction = action switch
        {
            "syringe" => "syringes",
            "vial" or "pour" => "bottles",
            _ => "",
        };
        return new ToolDef
        {
            Id = config.Section,
            Name = config.String("name", config.Section.Capitalize()),
            Action = action,
            Description = config.String("description"),
            Sharpness = config.Float("sharpness", 1f),
            Quality = config.Float("quality", 1f),
            TearChance = config.Float("tear_chance"),
            BleedChance = config.Float("bleed_chance"),
            StapleSpan = config.Float("staple_span"),
            StapleGive = config.Float("staple_give"),
            Radius = config.Float("radius", 0.01f),
            Power = config.Float("power", 1f),
            Sterile = config.Bool("sterile"),
            Size = config.String("size", "normal"),
            Improvised = config.Bool("improvised"),
            Orderable = config.Bool("orderable"),
            Delay = config.Float("delay", 20f),
            Category = config.String("category", "Supplies"),
            Charges = config.Int("charges", -1),
            Drug = config.String("drug"),
            Volume = config.Float("volume"),
            Concentration = config.Float("concentration"),
            Fragile = config.Bool("fragile"),
            Drinkable = config.Bool("drinkable"),
            IvOnly = config.Bool("iv_only"),
            SelfRetaining = config.Bool("self_retaining"),
            Fixed = config.Bool("fixed"),
            Model = config.String("model"),
            Length = config.Float("length", 0.12f),
            Width = config.Float("width", 0.012f),
            Color = config.Color("color", Colors.Gray),
            Grip = config.String("grip", "pencil"),
            Tray = config.String("tray", trayByAction),
        };
    }
}
