namespace Scalpel.Tests.Data;

/// <summary>The subtasks "I'm just a baby" lists under each objective step: every step of every scenario has them,
/// worked out in full, with doses for the patient's weight, the drug a patient's allergy calls for, where to get each
/// tool and the steps that have to come first. A line with a condition names one the game knows.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
public class ObjectiveSubtasksTest {
    private const float AdultKg = 75f;

    [TestCase]
    public void EveryStepHasSubtasksWorkedOutInFull() {
        foreach (var scenario in Db.Scenarios.Concat(Db.DisabledScenarios)) {
            foreach (var step in scenario.Steps) {
                var lines = Subtasks.For(step, scenario, Facts(scenario));
                AssertInt(lines.Count).OverrideFailureMessage($"{scenario.Id}: \"{step.Label}\" has subtasks").IsGreater(0);
                foreach (var line in lines) {
                    AssertBool(line.Text.Contains('{') || line.Text.Contains('}'))
                        .OverrideFailureMessage($"{scenario.Id}: \"{line.Text}\" is worked out").IsFalse();
                    AssertBool(line.When.Length == 0 || Subtasks.Conditions.ContainsKey(line.When))
                        .OverrideFailureMessage($"{scenario.Id}: \"{line.Text}\" is listed under a known condition, not {line.When}")
                        .IsTrue();
                }
            }
            AssertBool(Subtasks.Wake(scenario, Facts(scenario)).All(line => !line.Text.Contains('{') && !line.Text.Contains('}')))
                .OverrideFailureMessage($"{scenario.Id}: waking the patient is worked out").IsTrue();
        }
    }

    [TestCase]
    public void AnesthesiaSubtasksGiveTheDoseForTheWeightFromWhereTheVialIs() {
        var (scenario, step) = Step("appendectomy", "anesthesia");
        AssertArray(Texts(Subtasks.For(step, scenario, Facts(scenario)), 0, 2))
            .OverrideFailureMessage("propofol ordered from the nurse, 2 mg/kg of a 10 mg/ml vial for 75 kg")
            .IsEqual(new List<string> {
                "Order the Propofol 10 mg/ml, 50 ml vial from the nurse (Drugs)",
                "Draw 15 ml of Propofol into the 50 ml syringe: needle in the vial, wheel down pulls the plunger",
            });
        var child = Facts(scenario) with { WeightKg = 22f, ToolsInRoom = ["vial_propofol"] };
        AssertArray(Texts(Subtasks.For(step, scenario, child), 0, 2))
            .OverrideFailureMessage("a 22 kg patient's dose in whole ml, from the vial in the room, in the smallest syringe that takes it")
            .IsEqual(new List<string> {
                "Pick up the Propofol 10 mg/ml, 50 ml vial",
                "Draw 4 ml of Propofol into the 10 ml syringe: needle in the vial, wheel down pulls the plunger",
            });
    }

    [TestCase]
    public void AnAllergyTurnsTheDrugIntoItsFirstAlternative() {
        var (scenario, step) = Step("appendectomy", "anesthesia");
        var allergic = Facts(scenario) with { Allergens = ["propofol"] };
        AssertArray(Texts(Subtasks.For(step, scenario, allergic), 0, 2))
            .OverrideFailureMessage("ketamine instead of propofol, the allergy named")
            .IsEqual(new List<string> {
                "Order the Ketamine 50 mg/ml, 10 ml vial from the nurse (Drugs)",
                "Draw 3 ml of Ketamine (allergic to Propofol) into the 3 ml syringe: needle in the vial, wheel down pulls the plunger",
            });
        var (stitch, block) = Step("hand_stitch", "local_block");
        AssertArray(Texts(Subtasks.For(block, stitch, Facts(stitch) with { Allergens = ["lidocaine"] }), 1))
            .OverrideFailureMessage("for a lidocaine allergy the patient is put under with propofol, the manual's first alternative")
            .IsEqual(new List<string> {
                "Draw 15 ml of Propofol (allergic to Lidocaine) into the 50 ml syringe: needle in the vial, wheel down pulls the plunger",
                "Push it into the IV bag on the stand, or straight into a vein (wheel up)",
                "Wait until the patient's eyes close and the monitor shows no breathing",
            });
        AssertString(Subtasks.For(step, scenario, Facts(scenario) with { AllergyUnknown = true })[1].Text)
            .OverrideFailureMessage("an allergy the chart doesn't name keeps the usual drug and sends to the lab first")
            .IsEqual("Draw 15 ml of Propofol (unknown allergy: order the allergy screen at the monitor first) into the 50 ml "
                + "syringe: needle in the vial, wheel down pulls the plunger");
    }

    [TestCase]
    public void BloodAndOrderFollowThePatientAndTheScenario() {
        var (throat, transfuse) = Step("slit_throat", "transfuse");
        AssertString(Subtasks.For(transfuse, throat, Facts(throat) with { BloodType = "Bombay" })[1].Text)
            .OverrideFailureMessage("a Bombay patient gets Bombay blood").IsEqual("Order the blood pack: Bombay from the nurse (Blood and fluids)");
        var (appendectomy, incise) = Step("appendectomy", "incise");
        AssertArray(Subtasks.OnlyAfter(incise, appendectomy.Steps).ToList())
            .OverrideFailureMessage("the steps that must hold first, by their labels")
            .IsEqual(new List<string> { "Only after: Put the patient under general anesthesia; Sanitize the abdomen with iodine" });
        AssertArray(Subtasks.OnlyAfter(incise, appendectomy.Steps.Where(step => step.Type != "anesthesia")).ToList())
            .OverrideFailureMessage("only those still open")
            .IsEqual(new List<string> { "Only after: Sanitize the abdomen with iodine" });
        AssertArray(Subtasks.OnlyAfter(incise, []).ToList()).OverrideFailureMessage("none once all are done").IsEmpty();
    }

    [TestCase]
    public void WakingThePatientGivesFlumazenilForTheWeight() {
        var appendectomy = Db.Scenario("appendectomy")!;
        var wake = Subtasks.Wake(appendectomy, Facts(appendectomy))[1].Text;
        AssertString(wake).OverrideFailureMessage($"flumazenil for 75 kg, as the anesthesia chapter says: {wake}")
            .IsEqual("Draw 2 ml of Flumazenil into the 3 ml syringe and push it into the IV bag or a vein (wheel up)");
    }

    /// <summary>The texts of the first <paramref name="count"/> lines after <paramref name="skip"/>.</summary>
    private static List<string> Texts(List<Subtask> lines, int skip, int count = int.MaxValue) =>
        [.. lines.Skip(skip).Take(count).Select(line => line.Text)];

    private static (ScenarioDef Scenario, ObjectiveStep Step) Step(string scenarioId, string type) {
        var scenario = Db.Scenario(scenarioId)!;
        return (scenario, scenario.Steps.First(step => step.Type == type));
    }

    /// <summary>An adult with no allergy, the room as the scenario lays it out: the starter kit and, with a nurse, its
    /// own tools she can't fetch.</summary>
    private static SubtaskFacts Facts(ScenarioDef scenario) => new(AdultKg, [], "O+",
        [.. Db.StarterKit.Concat(scenario.StartingTools.Where(id => !(scenario.Nurse && Db.Tool(id)!.Orderable)))],
        scenario.Nurse);
}
