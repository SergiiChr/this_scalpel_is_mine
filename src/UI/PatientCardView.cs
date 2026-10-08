namespace Scalpel.UI;

/// <summary>The chart clipped to the bed. Mixes real conditions with random noise, so reading it carefully matters.
/// </summary>
public static class PatientCardView
{
    private static readonly string[] FirstNames =
        ["Dale", "Mira", "Tobias", "Irene", "Vic", "Noor", "Gus", "Lena", "Otto", "June", "Rafe", "Ada"];
    private static readonly string[] LastNames =
        ["Kowalski", "Hart", "Mendez", "Voss", "Okafor", "Lind", "Brandt", "Szabo", "Moreau", "Reyes"];
    private static readonly string[] RedHerrings =
    [
        "Allergic to cats.", "Vegetarian.", "Previous appendectomy (2011).", "Reports frequent headaches.",
        "Occasional cannabis use.", "Lactose intolerant.", "Wears contact lenses.", "Recent travel abroad.",
        "Claims to be allergic to 'bad vibes'.", "Night shift worker.", "Takes fish oil supplements.",
        "Has a tattoo reading 'DO NOT RESUSCITATE (JK)'.", "Mild seasonal allergies.", "Former smoker (quit 1 week ago).",
    ];
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
        var herrings = rng.RandiRange(2, 3);
        for (var i = 0; i < herrings; i++)
        {
            lines.Add(RedHerrings[rng.RandiRange(0, RedHerrings.Length - 1)]);
        }
        for (var i = lines.Count - 1; i > 0; i--)
        {
            var j = rng.RandiRange(0, i);
            (lines[i], lines[j]) = (lines[j], lines[i]);
        }
        var name = $"{FirstNames[rng.RandiRange(0, FirstNames.Length - 1)]} {LastNames[rng.RandiRange(0, LastNames.Length - 1)]}";
        var bloodText = rng.Randf() < 0.3f ? "unknown, lab pending" : blood;
        var corrected = surgery.ChartCorrected ? "   [color=#8a1c1c][i](corrected copy)[/i][/color]" : "";

        var text = new RichTextLabel { BbcodeEnabled = true, CustomMinimumSize = new Vector2(900f, 700f) };
        text.AddThemeColorOverride("default_color", Ui.PaperInk);
        text.AddThemeFontSizeOverride("normal_font_size", 24);
        var paper = new StyleBoxFlat { BgColor = Ui.Paper };
        paper.SetContentMarginAll(48);
        text.AddThemeStyleboxOverride("normal", paper);
        text.Text = $"[font_size=36][b]PATIENT CHART[/b][/font_size]{corrected}\n\n"
            + $"[b]Name:[/b] {name}     [b]Age:[/b] {age}     [b]Weight:[/b] {(int)patient.WeightKg} kg\n"
            + $"[b]Blood type:[/b] {bloodText}\n\n"
            + $"[b]Admission:[/b] {patient.Scenario.Complaint}\n\n"
            + "[b]History and notes:[/b]\n" + string.Join("\n", lines.Select(line => "  • " + line));
        var box = Ui.VBox(16);
        box.Alignment = BoxContainer.AlignmentMode.Center;
        var center = new CenterContainer();
        center.AddChild(text);
        box.AddChild(center);
        var closeButton = Ui.Button("Clip it back  [Esc]", close);
        closeButton.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        box.AddChild(closeButton);
        return Ui.Fullscreen(box);
    }
}
