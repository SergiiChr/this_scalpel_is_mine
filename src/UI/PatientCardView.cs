namespace Scalpel.UI;

/// <summary>The patient record clipped to the bed, laid out like a hospital form. Mixes real conditions with
/// unrelated history, so reading it carefully matters.</summary>
public static class PatientCardView
{
    private static readonly string[] FirstNames =
        ["Dale", "Mira", "Tobias", "Irene", "Vic", "Noor", "Gus", "Lena", "Otto", "June", "Rafe", "Ada"];
    private static readonly string[] LastNames =
        ["Kowalski", "Hart", "Mendez", "Voss", "Okafor", "Lind", "Brandt", "Szabo", "Moreau", "Reyes"];
    private static readonly string[] RedHerrings =
    [
        "Allergic to cat dander.", "Vegetarian.", "Tonsillectomy in childhood.", "Recurrent tension headaches.",
        "Occasional cannabis use.", "Lactose intolerant.", "Wears contact lenses.", "Travelled abroad last month.",
        "Night shift worker.", "Takes fish oil supplements.", "Mild seasonal allergies.", "Former smoker, quit last year.",
    ];
    private static readonly Color Paper = new(0.96f, 0.95f, 0.92f);
    private static readonly Color Ink = new(0.1f, 0.12f, 0.17f);
    private static readonly Color Faint = new(0.42f, 0.46f, 0.52f);
    /// <summary>The red of a hospital stamp.</summary>
    private static readonly Color Stamp = new(0.6f, 0.12f, 0.1f);
    /// <summary>A mixed-up chart names one of these as the patient's allergy.</summary>
    private static readonly string[] WrongAllergies = ["cefazolin", "morphine", "lidocaine"];

    public static Control Build(Patient patient, uint seed, Action close)
    {
        var surgery = Surgery.Current!;
        var rng = new RandomNumberGenerator { Seed = seed + 5 };
        var age = patient.Age switch
        {
            "child" => rng.RandiRange(6, 11),
            "elderly" => rng.RandiRange(78, 94),
            _ => rng.RandiRange(22, 64),
        };
        var lines = patient.RevealedCardLines();
        var blood = patient.BloodType;
        if (surgery.RunMods.Flag("card_error") && !surgery.ChartCorrected)
        {
            // Chart mix-up: someone else's blood type and allergy, and one real condition missing. Its own RNG so the
            // rest of the card (name, age) stays the same once it's corrected.
            var errorRng = new RandomNumberGenerator { Seed = seed + 9 };
            var types = Patient.BloodTypes;
            blood = types[(types.ToList().IndexOf(blood) + 2 + types.Count) % types.Count];
            if (lines.Count > 0)
            {
                lines.RemoveAt(errorRng.RandiRange(0, lines.Count - 1));
            }
            lines.Add($"Known allergy: {Db.Drug(WrongAllergies[errorRng.RandiRange(0, 2)])!.Name}.");
        }
        lines.AddRange(RedHerrings.OrderBy(_ => rng.Randi()).Take(rng.RandiRange(2, 3)));
        for (var i = lines.Count - 1; i > 0; i--)
        {
            var j = rng.RandiRange(0, i);
            (lines[i], lines[j]) = (lines[j], lines[i]);
        }
        var name = $"{FirstNames[rng.RandiRange(0, FirstNames.Length - 1)]} {LastNames[rng.RandiRange(0, LastNames.Length - 1)]}";
        var bloodText = rng.Randf() < 0.3f ? "Pending" : blood;
        var record = $"{rng.RandiRange(100, 999)}-{rng.RandiRange(1000, 9999)}";

        var page = Ui.VBox(14);
        var heading = Ui.HBox(16);
        var titles = Ui.VBox(2);
        titles.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        titles.AddChild(Ui.Label("DEPARTMENT OF SURGERY", 16, Faint));
        titles.AddChild(Ui.Label("Patient Record", 34, Ink));
        heading.AddChild(titles);
        var marks = Ui.VBox(2);
        marks.AddChild(Ui.Label($"MRN {record}", 18, Ink));
        if (surgery.ChartCorrected)
        {
            marks.AddChild(Ui.Label("CORRECTED COPY", 16, Stamp));
        }
        heading.AddChild(marks);
        page.AddChild(heading);
        page.AddChild(Rule(2f));
        var fields = new GridContainer { Columns = 4 };
        fields.AddThemeConstantOverride("h_separation", 40);
        foreach (var (label, value) in ((string, string)[])
            [("Name", name), ("Age", $"{age}"), ("Weight", $"{(int)patient.WeightKg} kg"), ("Blood group", bloodText)])
        {
            var field = Ui.VBox(0);
            field.AddChild(Ui.Label(label.ToUpperInvariant(), 14, Faint));
            field.AddChild(Ui.Label(value, 24, Ink));
            fields.AddChild(field);
        }
        page.AddChild(fields);
        page.AddChild(Section("Presenting complaint", Ui.Label(patient.Scenario.Complaint, 22, Ink, true)));
        var history = Ui.VBox(4);
        foreach (var line in lines)
        {
            history.AddChild(Ui.Label("•  " + line, 22, Ink, true));
        }
        page.AddChild(Section("Medical history", history));

        var paper = new StyleBoxFlat { BgColor = Paper };
        paper.SetContentMarginAll(48);
        var sheet = new PanelContainer { CustomMinimumSize = new Vector2(900f, 700f) };
        sheet.AddThemeStyleboxOverride("panel", paper);
        sheet.AddChild(page);
        var box = Ui.VBox(16);
        box.Alignment = BoxContainer.AlignmentMode.Center;
        var center = new CenterContainer();
        center.AddChild(sheet);
        box.AddChild(center);
        var closeButton = Ui.Button("Put it back  [Esc]", close);
        closeButton.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        box.AddChild(closeButton);
        return Ui.Fullscreen(box);
    }

    /// <summary>A titled part of the page, under a thin rule.</summary>
    private static VBoxContainer Section(string title, Control body)
    {
        var section = Ui.VBox(6);
        section.AddChild(Ui.Label(title.ToUpperInvariant(), 15, Faint));
        section.AddChild(Rule(1f));
        section.AddChild(body);
        return section;
    }

    private static ColorRect Rule(float thickness) =>
        new() { Color = Faint, CustomMinimumSize = new Vector2(0f, thickness) };
}
