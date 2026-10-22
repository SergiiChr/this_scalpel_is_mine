namespace Scalpel.World;

public partial class Room {
    /// <summary>Open work compartments in the fitted side cabinets, in floor coordinates. Surgery and dressings face
    /// the +Z aisle; medication, IV supplies and suction face the other doctor. Tops match the generated fittings.</summary>
    internal static readonly IReadOnlyDictionary<string, Rect2> AmbulanceCompartments = new Dictionary<string, Rect2> {
        ["instruments"] = new(-0.65f, 0.89f, 0.55f, 0.23f),
        ["closures"] = new(-0.05f, 0.89f, 0.4f, 0.23f),
        ["swabs"] = new(0.4f, 0.89f, 0.25f, 0.23f),
        ["bottles"] = new(-0.65f, -1.12f, 0.35f, 0.23f),
        ["syringes"] = new(-0.25f, -1.12f, 0.3f, 0.23f),
        [""] = new(0.1f, -1.12f, 0.6f, 0.23f),
    };

    internal string InitialToolGroup(ToolDef tool) => EnvironmentId == "ambulance"
        ? tool.Action switch {
            "cut" or "clamp" or "cauterize" => "instruments",
            "sew" or "staple" => "closures",
            "swab" => "swabs",
            _ => tool.Tray,
        }
        : tool.Tray;

    private void BuildAmbulance() {
        var fittings = ModelSlot.Instantiate("props", "ambulance", this);
        fittings.Name = "AmbulanceFittings";
        // Fixed cabinetry leaves a 57 cm aisle on each side of the stretcher: one person wide. The end lockers
        // leave less than a capsule diameter at the head and feet, so neither doctor can walk round the patient.
        foreach (var side in (float[])[-1f, 1f]) {
            Shapes.StaticBox(this, new Vector3(2.4f, 0.915f, 0.26f), new Vector3(-0.1f, 0.4575f, side * 1.01f));
            Shapes.StaticBox(this, new Vector3(2.4f, 0.32f, 0.18f), new Vector3(-0.1f, 1.82f, side * 1.04f));
            var label = Shapes.Label(this, side > 0f ? "SURGERY  /  DRESSINGS" : "IV  /  MEDICATION  /  SUCTION",
                new Vector3(0f, 1.56f, side * 1.135f), 24);
            label.Rotation = new Vector3(0f, side > 0f ? Mathf.Pi : 0f, 0f);
            label.Modulate = new Color(0.12f, 0.2f, 0.3f);
        }
        Shapes.StaticBox(this, new Vector3(0.5f, 0.915f, 2.2f), new Vector3(1.5f, 0.4575f, 0f));
        Shapes.StaticBox(this, new Vector3(0.26f, 0.915f, 2.2f), new Vector3(-1.57f, 0.4575f, 0f));
    }

    /// <summary>Keep the surface equipment on the fitted cabinets instead of putting hospital trolleys in the aisles.
    /// The IV stand's pole and hooks become a wall mount; its drip point stays the same.</summary>
    private static void FitAmbulanceStation(string key, Node3D root) {
        string[] hidden = key switch {
            "gloves" => ["Cabinet", "Drawers", "Handles", "Steel", "Wheels", "Grip", "Organizer", "Bins", "Labels"],
            "sanitizer" => ["Cabinet", "Doors", "Handles"],
            "defib_cart" => ["Body", "Handles", "Wheels"],
            "iv" => ["Base"],
            "manual" => ["Board", "Brackets", "Books"],
            _ => [],
        };
        foreach (var part in ModelSlot.Parts(root, hidden).Values) {
            part.Visible = false;
        }
    }
}
