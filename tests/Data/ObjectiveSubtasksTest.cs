namespace Scalpel.Tests.Data;

/// <summary>The subtasks "I'm just a baby" lists under each objective step: every step of every scenario has them,
/// worked out in full, with doses for the patient's weight, the drug a patient's allergy calls for, where to get each
/// tool and the steps that have to come first.</summary>
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
                    AssertBool(line.Contains('{') || line.Contains('}'))
                        .OverrideFailureMessage($"{scenario.Id}: \"{line}\" is worked out").IsFalse();
                }
            }
            AssertBool(Subtasks.Discharge(scenario, Facts(scenario)).All(line => !line.Contains('{') && !line.Contains('}')))
                .OverrideFailureMessage($"{scenario.Id}: the discharge is worked out").IsTrue();
        }
    }

    [TestCase]
    public void AnesthesiaSubtasksGiveTheDoseForTheWeightFromWhereTheVialIs() {
        var (scenario, step) = Step("appendectomy", "anesthesia");
        AssertArray(Subtasks.For(step, scenario, Facts(scenario)).Take(2).ToList())
            .OverrideFailureMessage("propofol ordered from the nurse, 2 mg/kg of a 10 mg/ml vial for 75 kg")
            .IsEqual(new List<string> {
                "Order the Propofol 10 mg/ml, 50 ml vial from the nurse (Drugs)",
                "Draw 15 ml of Propofol into the 50 ml syringe: needle in the vial, wheel down pulls the plunger",
            });
        var child = Facts(scenario) with { WeightKg = 22f, ToolsInRoom = ["vial_propofol"] };
        AssertArray(Subtasks.For(step, scenario, child).Take(2).ToList())
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
        AssertArray(Subtasks.For(step, scenario, allergic).Take(2).ToList())
            .OverrideFailureMessage("ketamine instead of propofol, the allergy named")
            .IsEqual(new List<string> {
                "Order the Ketamine 50 mg/ml, 10 ml vial from the nurse (Drugs)",
                "Draw 3 ml of Ketamine (allergic to Propofol) into the 3 ml syringe: needle in the vial, wheel down pulls the plunger",
            });
        var (stitch, block) = Step("hand_stitch", "local_block");
        AssertArray(Subtasks.For(block, stitch, Facts(stitch) with { Allergens = ["lidocaine"] }).Skip(1).ToList())
            .OverrideFailureMessage("for a lidocaine allergy the patient is put under with propofol, the manual's first alternative")
            .IsEqual(new List<string> {
                "Draw 15 ml of Propofol (allergic to Lidocaine) into the 50 ml syringe: needle in the vial, wheel down pulls the plunger",
                "Push it into the IV bag on the stand, or straight into a vein (wheel up)",
                "Wait until the patient's eyes close and the monitor shows no breathing",
            });
        AssertString(Subtasks.For(step, scenario, Facts(scenario) with { AllergyUnknown = true })[1])
            .OverrideFailureMessage("an allergy the chart doesn't name keeps the usual drug and sends to the lab first")
            .IsEqual("Draw 15 ml of Propofol (unknown allergy: order the allergy screen at the monitor first) into the 50 ml "
                + "syringe: needle in the vial, wheel down pulls the plunger");
    }

    [TestCase]
    public void BloodAndOrderFollowThePatientAndTheScenario() {
        var (throat, transfuse) = Step("slit_throat", "transfuse");
        AssertString(Subtasks.For(transfuse, throat, Facts(throat) with { BloodType = "Bombay" })[1])
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
    public void TheDischargeWakesAPatientPutUnderFirst() {
        var appendectomy = Db.Scenario("appendectomy")!;
        var wake = Subtasks.Discharge(appendectomy, Facts(appendectomy))[1];
        AssertString(wake).OverrideFailureMessage($"flumazenil for 75 kg, as the anesthesia chapter says: {wake}")
            .IsEqual("Draw 2 ml of Flumazenil into the 3 ml syringe and push it into the IV bag or a vein (wheel up)");
        var glass = Db.Scenario("glass_shards")!;
        AssertString(Subtasks.Discharge(glass, Facts(glass))[0])
            .OverrideFailureMessage("a patient never put under starts at the checks")
            .IsEqual("Finish first: the closure holds, nothing bleeds and the patient is stable");
    }

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
