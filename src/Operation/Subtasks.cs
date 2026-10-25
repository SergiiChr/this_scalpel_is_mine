using System.Text.RegularExpressions;
using GodotDictionary = Godot.Collections.Dictionary;

namespace Scalpel.Operation;

/// <summary>What the subtasks of a surgery are written for: the patient's weight, the allergies the chart names and
/// whether it claims one it doesn't name, blood group, the tools in the room at the start and whether a nurse takes
/// orders.</summary>
public sealed record SubtaskFacts(
    float WeightKg,
    IReadOnlyCollection<string> Allergens,
    string BloodType,
    IReadOnlyCollection<string> ToolsInRoom,
    bool Nurse,
    bool AllergyUnknown = false);

/// <summary>A subtask line; one with a condition (<see cref="Subtasks.Conditions"/>) is listed only while it holds.
/// </summary>
public readonly record struct Subtask(string Text, string When = "");

/// <summary>
/// The detailed steps under an objective step, for the "I'm just a baby" difficulty: the scenario step's own subtasks
/// or its type's (data/objectives.cfg), with doses and tools worked out for this patient (tokens explained there).
/// </summary>
public static partial class Subtasks {
    [GeneratedRegex(@"\{(get|dose|syringe)\.(\w+)\}")]
    private static partial Regex Token();

    [GeneratedRegex(@"^\{if\.(\w+)\}\s*")]
    private static partial Regex Condition();

    /// <summary>What a line's leading "{if.name}" lists it under, checked on the host with every status against the
    /// patient and the step's parameters.</summary>
    internal static readonly Dictionary<string, Func<Patient, GodotDictionary, bool>> Conditions = new() {
        ["muscle_cut"] = (patient, _) => patient.Wounds.Any(wound => wound.ThroughMuscle
            && wound.Points.Any(point => patient.Body.Tissue.MuscleOpenNear(point, Patient.MuscleReach))),
        ["attached"] = (patient, step) => patient.Targets.Any(target =>
            target.Kind == step.String("target") && !target.Extracted && target.Anchor > 0f),
        ["buried"] = (patient, step) => patient.Targets.Any(target =>
            target.Kind == step.String("target") && !target.Extracted && !target.InReach),
        ["no_iv"] = (patient, _) => !patient.IvWorking,
        ["iv_in"] = (patient, _) => patient.IvSet,
        ["low_pressure"] = (patient, _) => patient.Vitals.Systolic < ObjectiveChecks.StableSystolic,
        ["beating"] = (patient, _) => !patient.Vitals.IsArrested,
        ["arrested"] = (patient, _) => patient.Vitals.IsArrested,
        ["flat_line"] = (patient, _) => patient.Vitals.Rhythm == Rhythm.Asystole,
    };

    public static List<Subtask> For(ObjectiveStep step, ScenarioDef scenario, SubtaskFacts facts) {
        var defaults = new ConfigReader(Db.Objectives, Section(step, scenario));
        var drug = step.Parameters.String("drug", defaults.String("drug"));
        if (facts.Allergens.Contains(drug) && defaults.String("allergy_instead") is { Length: > 0 } instead) {
            defaults = new ConfigReader(Db.Objectives, instead);
        }
        return Worked(step.Subtasks ?? defaults.Strings("subtasks"), drug, scenario, facts, step.Parameters);
    }

    /// <summary>The subtasks of waking a patient who is under, the optional task before the discharge.</summary>
    public static List<Subtask> Wake(ScenarioDef scenario, SubtaskFacts facts) =>
        Worked(new ConfigReader(Db.Objectives, "wake").Strings("subtasks"), "", scenario, facts, []);

    /// <summary>The subtasks of taking the IV out and taping the hole, the optional cleanup before the discharge.
    /// </summary>
    public static List<Subtask> RemoveIv(ScenarioDef scenario, SubtaskFacts facts) =>
        Worked(new ConfigReader(Db.Objectives, "remove_iv").Strings("subtasks"), "", scenario, facts, []);

    /// <summary>The texts of <paramref name="lines"/> that apply to <paramref name="patient"/> now, for the step with
    /// these parameters.</summary>
    internal static IEnumerable<string> Listed(IEnumerable<Subtask> lines, Patient patient, GodotDictionary step) =>
        lines.Where(line => line.When.Length == 0 || Conditions[line.When](patient, step)).Select(line => line.Text);

    private static List<Subtask> Worked(IEnumerable<string> lines, string drug, ScenarioDef scenario, SubtaskFacts facts,
        GodotDictionary parameters) =>
        [.. lines.Select(line => {
            var when = Condition().Match(line);
            var text = Token().Replace(line[when.Length..], found => Fill(found, drug, facts, parameters));
            return new Subtask(ManualPage.FillNumbers(text, $"{scenario.Id} subtasks", parameters), when.Groups[1].Value);
        })];

    /// <summary>The line naming the steps of <paramref name="open"/> that <paramref name="step"/> has to come after,
    /// none once they're all in place.</summary>
    public static IEnumerable<string> OnlyAfter(ObjectiveStep step, IEnumerable<ObjectiveStep> open) {
        var before = open.Where(other => step.After.Contains(other.Id)).Select(other => other.Label).ToList();
        return before.Count > 0 ? ["Only after: " + string.Join("; ", before)] : [];
    }

    /// <summary>The step type's section of data/objectives.cfg; an extract step's by how its target comes out.
    /// </summary>
    private static string Section(ObjectiveStep step, ScenarioDef scenario) {
        if (step.Type != "extract") {
            return step.Type;
        }
        var kind = step.Parameters.String("target");
        return "extract_" + (scenario.Targets.FirstOrDefault(target => target.Kind == kind)?.RemoveWith ?? "clamp");
    }

    /// <summary>One token's text; <paramref name="drug"/> is the step's, and its "reason" says why that one.</summary>
    private static string Fill(Match found, string drug, SubtaskFacts facts, GodotDictionary parameters) {
        var id = found.Groups[2].Value switch {
            "step" => drug,
            "blood" => facts.BloodType == "Bombay" ? "blood_rare" : "blood_o_neg",
            var named => named,
        };
        if (Db.Tool(id) is { } item) {
            return Get(item, facts);
        }
        var (given, allergy) = Usable(id, facts);
        if (Db.Tools.Values.FirstOrDefault(tool => tool.Drug == given.Id && tool.Concentration > 0f) is not { } vial) {
            GD.PushError($"Subtasks: no vial of {id}");
            return found.Value;
        }
        // Whole notches of the plunger: the leeway around the right dose takes the rest. Data floats are a hair off
        // their decimals (0.01 * 75 / 0.5 comes to 1.4999...), so a half rounds up only after dropping that hair.
        var exact = (double)given.Dose * facts.WeightKg / vial.Concentration / ToolActions.PlungerStep;
        var notches = Math.Round(Math.Round(exact, 4), MidpointRounding.AwayFromZero);
        var ml = Math.Max(notches, 1) * ToolActions.PlungerStep;
        return found.Groups[1].Value switch {
            "get" => Get(vial, facts),
            "dose" => $"{ml:0.#} ml of {given.Name}{ReasonNote(given, drug, parameters)}{AllergyNote(given, allergy, facts)}",
            _ => "the " + (Db.Tools.Values.Where(tool => tool.Action == "syringe").OrderBy(tool => tool.Volume)
                .FirstOrDefault(tool => tool.Volume >= ml) ?? Db.Tool("syringe_50")!).Name,
        };
    }

    /// <summary>The drug, or for a patient allergic to it its first alternative they can take, with the drug they're
    /// allergic to.</summary>
    private static (DrugDef Drug, DrugDef? Allergy) Usable(string id, SubtaskFacts facts) {
        var drug = Db.Drug(id)!;
        if (!facts.Allergens.Contains(id)) {
            return (drug, null);
        }
        var alternative = drug.Alternatives.Select(Db.Drug)
            .FirstOrDefault(other => other is { Dose: > 0f } && !facts.Allergens.Contains(other.Id));
        return (alternative ?? drug, alternative is null ? null : drug);
    }

    private static string ReasonNote(DrugDef given, string drug, GodotDictionary parameters) =>
        given.Id == drug && parameters.String("reason") is { Length: > 0 } reason ? $" ({reason})" : "";

    private static string AllergyNote(DrugDef given, DrugDef? allergy, SubtaskFacts facts) =>
        allergy is not null ? $" (allergic to {allergy.Name})"
        : facts.AllergyUnknown && given.HasFlag("allergen_candidate")
            ? " (unknown allergy: order the allergy screen at the monitor first)"
            : "";

    /// <summary>Where to get a tool: in the room, from the nurse, or wherever there is one.</summary>
    private static string Get(ToolDef tool, SubtaskFacts facts) {
        var vial = tool.Concentration > 0f;
        // In a sentence, but a vial's label and an initialism (IV catheter) stay as written.
        var name = vial || char.IsUpper(tool.Name.ElementAtOrDefault(1))
            ? tool.Name
            : char.ToLowerInvariant(tool.Name[0]) + tool.Name[1..];
        var thing = vial ? name + " vial" : name;
        return facts.ToolsInRoom.Contains(tool.Id) || !(facts.Nurse && tool.Orderable)
            ? $"Pick up the {thing}"
            : $"Order the {thing} from the nurse ({tool.Category})";
    }
}
