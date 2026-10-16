namespace Scalpel.Tests.Scenarios;

/// <summary>Anesthesia given through player controls: the preinstalled IV and its objective, and alternative
/// anesthesia paths through complete surgeries.</summary>
[TestSuite, RequireGodotRuntime, IsolateCases]
[TestCategory("scenario"), TestCategory("smoke"), TestCategory("slow"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class AnesthesiaPathsTest {
    [TestCase(Timeout = Limits.Slow)]
    public async Task PreinstalledIvDeliversAnesthesiaAndCompletesTheObjective() {
        using var debug = SurgeryState.DebugHudIsEnabled();
        var driver = SurgeryDriver.Create();
        const uint Seed = 1;
        await driver.Start("bullet_muscle", seed: Seed);
        var patient = driver.Patient;
        var dressing = driver.Surgery.Room.IvLine.Dressing!;
        var forearm = dressing.GetParent<Forearm>();
        var elbow = forearm.GlobalPosition;
        var wrist = forearm.ToGlobal(forearm.Wrist);
        var axis = (wrist - elbow).Normalized();
        var length = elbow.DistanceTo(wrist);
        var fromElbow = dressing.GlobalPosition - elbow;
        var along = fromElbow.Dot(axis) / length;
        AssertFloat(along).OverrideFailureMessage("the catheter sits on the proximal forearm, clear of the elbow joint")
            .IsBetween(0.1f, 0.3f);
        var outward = fromElbow.Slide(axis).Normalized();
        AssertFloat(outward.Dot(driver.Body.Root.GlobalBasis.Y.Normalized()))
            .OverrideFailureMessage("the catheter lies on the upper side of the forearm").IsGreater(0.8f);
        AssertBool(driver.Body.VeinAt(dressing.GlobalPosition))
            .OverrideFailureMessage("the anatomical insertion also reaches a vein").IsTrue();
        AssertFloat(dressing.GlobalBasis.X.Normalized().Dot(-axis))
            .OverrideFailureMessage("the catheter points along the forearm toward the elbow").IsGreater(0.99f);
        AssertFloat(dressing.GlobalBasis.Y.Normalized().Dot(outward))
            .OverrideFailureMessage("the dressing faces outward from the arm").IsGreater(0.99f);
        AssertBool(patient.Vitals.IsAwake).OverrideFailureMessage("the patient starts awake").IsTrue();
        AssertBool(driver.Surgery.Objectives.States[0].Done).OverrideFailureMessage("anesthesia is initially incomplete").IsFalse();
        KeyFrames? shots = null;
        if (KeyFrames.Wanted()) {
            shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(driver.Surgery, $"preop_anesthesia/{Seed}");
            await shots.CaptureAt("installed_iv", "the preinstalled IV in the arm under its dressing, the line attached", dressing.GlobalPosition, 0.24f);
            await shots.CaptureView("awake", "the patient awake, eyes open, \"Put the patient under\" not checked off");
        }
        await driver.PlayerGivesDrug("vial_propofol", driver.DoseMl("vial_propofol"), SurgeryDriver.Route.Drip);
        await Frames.Until(() => patient.Vitals.IsUnder && patient.AnestheticWorks, 30f);
        await Frames.Seconds(Surgery.StatusInterval + 0.1f);
        AssertBool(patient.Vitals.IsUnder)
            .OverrideFailureMessage($"the chart dose puts the patient under: anesthesia {patient.Vitals.Anesthesia}").IsTrue();
        AssertBool(driver.Surgery.Objectives.States[0].Done)
            .OverrideFailureMessage("Put the patient under checks off after the dose works").IsTrue();
        AssertBool(driver.Surgery.Status.Objectives[0].Done)
            .OverrideFailureMessage("the HUD receives the completed anesthesia objective").IsTrue();
        if (shots is not null) {
            await shots.CaptureView("anesthetized", "the patient under after the chart dose, \"Put the patient under\" checked off");

            shots.End();
        }
        await driver.Stop();
    }

    /// <summary>
    /// Expired stock (every drug 30% weaker) and propofol down the drip in two parts: the first part alone leaves the
    /// patient awake, both together put them under though the anesthesia stays shallower than with fresh stock, and
    /// that checks off the objective.
    /// </summary>
    [TestCase(Timeout = Limits.Slow)]
    public async Task SplitDoseOfWeakDrugsPutsThePatientUnder() {
        // No key frames here: under a display a software renderer would draw every frame of both infusions.
        await WithoutRendering(async () => {
            var driver = SurgeryDriver.Create();
            await driver.Start("bullet_muscle", runModifiers: ["expired_drugs"]);
            var patient = driver.Patient;
            var objective = driver.Surgery.Objectives.States[0];
            var full = Mathf.Round(driver.DoseMl("vial_propofol"));
            var first = Mathf.Round(full * 0.5f);
            var bag = driver.Surgery.Tools.DripBag()!;
            var propofol = Db.Drug("propofol")!;
            float Given() => patient.Drugs.Find(propofol.Id) is { } entry ? entry.Level + entry.Depot : 0f;
            await driver.PlayerGivesDrug("vial_propofol", first, SurgeryDriver.Route.Drip);
            // Run down the line and soaked in.
            await Frames.Until(() => bag.Bolus <= 0f && patient.Drugs.Find(propofol.Id)?.Depot < 0.01f, 60f);
            AssertFloat(Given()).OverrideFailureMessage($"the first part is half the dose: {Given()}").IsBetween(0.45f, 0.55f);
            AssertBool(patient.Vitals.IsAwake && !objective.Done)
                .OverrideFailureMessage($"{first} of {full} ml leaves the patient awake and the objective open").IsTrue();
            await driver.PlayerGivesDrug("vial_propofol", full - first, SurgeryDriver.Route.Drip);
            // Everything run down the line, not just enough of it to put them under.
            await Frames.Until(() => objective.Done && bag.Bolus <= 0f, 30f);
            AssertFloat(Given())
                .OverrideFailureMessage($"both parts add up to the right dose: {Given()}").IsBetween(0.95f, 1.05f);
            var fresh = propofol.Peak.Anesthesia;
            var weak = fresh * driver.Surgery.RunMods.Mult("drug_strength_mult");
            AssertFloat(patient.Vitals.Anesthesia)
                .OverrideFailureMessage($"weak drugs leave the anesthesia at {weak}, shallower than fresh ones ({fresh})")
                .IsEqualApprox(weak, 0.02f);
            AssertBool(objective.Done && patient.Vitals.IsUnder)
                .OverrideFailureMessage("the patient goes under and Put the patient under checks off").IsTrue();
            await driver.Stop();
        });
    }

    /// <summary>
    /// A patient allergic to lidocaine, put under instead as the manual (17_conditions/allergy.txt) says: "Numb the
    /// area, or put the patient under" counts general anesthesia, and signed out, the surgery ends in success.
    /// The lidocaine path is <see cref="ScenarioFlowsTest.PlaysThrough"/>.
    /// </summary>
    [TestCase("hand_stitch", Timeout = Limits.Slow)]
    [TestCase("hand_stitch_child", Timeout = Limits.Slow)]
    public async Task HandStitchLidocaineAllergyUnderGeneralAnesthesia(string scenario) {
        // No key frames here: under a display a software renderer would draw every frame of the whole surgery.
        await WithoutRendering(async () => {
            var driver = SurgeryDriver.Create();
            await driver.Start(scenario, patientQuirks: [new QuirkRoll("allergy", "lidocaine")]);
            var surgery = driver.Surgery;
            await driver.PlayerSetsIv();
            await driver.PlayerSanitizesSite(0.5f);
            await driver.PlayerAnesthetizes();
            await driver.PlayerClosesWounds();
            await driver.PlayerStopsBleeding(0.2f);
            AssertBool(surgery.Scoring.Entries.ContainsKey("unneeded_alternative"))
                .OverrideFailureMessage("general anesthesia for a lidocaine allergy costs no points").IsFalse();
            await Frames.Until(() => surgery.Objectives.AllDone, 120f);
            await driver.PlayerDischarges();
            AssertBool(surgery.Report?.Success == true)
                .OverrideFailureMessage($"{scenario} ends in success under general anesthesia: "
                    + $"{string.Join(", ", surgery.Objectives.Snapshot())}. Last steps:\n{driver.Recent()}")
                .IsTrue();
            await driver.Stop();
        });
    }

    /// <summary>Runs <paramref name="run"/> with rendering off, as a CPU frame budget run has it, and back as it was
    /// after.</summary>
    private static async Task WithoutRendering(Func<Task> run) {
        var rendering = RenderingServer.RenderLoopEnabled;
        FrameBudget.RenderingOff();
        try {
            await run();
        } finally {
            RenderingServer.RenderLoopEnabled = rendering;
        }
    }
}
