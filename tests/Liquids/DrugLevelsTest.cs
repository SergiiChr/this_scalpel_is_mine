namespace Scalpel.Tests.Liquids;

/// <summary>Drugs add up in the body (DrugLevels): what counts is how much is in, not how many injections it came in.
/// A drug works once its level reaches DrugDef.DoseEffective and is an overdose from DrugDef.DoseOverdose, for a
/// patient and a surgeon alike.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("liquids")]
[GodotArgs("--fixed-fps", "60")]
public class DrugLevelsTest
{
    private const string Drug = "atropine";

    [TestCase]
    public void TenSmallInjectionsAddUpToOneBigOne()
    {
        var def = Db.Drug(Drug)!;
        var small = new DrugLevels();
        var big = new DrugLevels();
        static float Wear(DrugDef def, float level) => 1f;
        big.Give(def, 1f, def.Onset);
        for (var second = 0; second < 30; second++)
        {
            if (second < 10)
            {
                small.Give(def, 0.1f, def.Onset);
            }
            for (var i = 0; i < 10; i++)
            {
                small.Update(0.1f, Wear);
                big.Update(0.1f, Wear);
            }
        }
        AssertFloat(small.Level(Drug))
            .OverrideFailureMessage($"ten tenths of a dose a second apart are as much in the blood as one dose ({small.Level(Drug):0.0000}, {big.Level(Drug):0.0000})")
            .IsEqualApprox(big.Level(Drug), 0.001f);
        AssertFloat(big.Level(Drug)).OverrideFailureMessage($"a dose wears off by one right dose every {def.Duration:0} s")
            .IsEqualApprox(1f - (30f / def.Duration), 0.002f);
    }

    [TestCase]
    public async Task ADrugWorksFromAnEffectiveLevelAndOverdosesPastASafeOne()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var patient = driver.Patient;
        var def = Db.Drug(Drug)!;
        var tenth = def.Dose * patient.WeightKg * 0.1f;
        var soaked = def.Onset * DrugDef.DirectOnset * 2f;
        async Task Give(int tenths)
        {
            for (var i = 0; i < tenths; i++)
            {
                patient.Administer(Drug, DrugRoute.Direct, tenth);
            }
            await Frames.Seconds(soaked);
        }
        await Give(4);
        AssertBool(patient.Flags.ContainsKey("drug_" + Drug))
            .OverrideFailureMessage($"four tenths of a dose don't do the job (level {patient.Drugs.Level(Drug):0.00})").IsFalse();
        var faint = patient.DrugEffectsOver(0f).Hr;
        AssertBool(faint > 0f && faint < def.Peak.Hr)
            .OverrideFailureMessage($"they still have a faint effect ({faint:0.0} of {def.Peak.Hr:0.0} bpm)").IsTrue();
        await Give(2);
        AssertBool(patient.Flags.ContainsKey("drug_" + Drug))
            .OverrideFailureMessage($"two more tenths make it work (level {patient.Drugs.Level(Drug):0.00})").IsTrue();
        AssertBool(patient.Flags.ContainsKey("overdose")).OverrideFailureMessage("that's no overdose").IsFalse();
        await Give(25);
        AssertBool(patient.Flags.ContainsKey("overdose"))
            .OverrideFailureMessage($"twenty five more tenths are an overdose (level {patient.Drugs.Level(Drug):0.00}, from {DrugDef.DoseOverdose:0.0})")
            .IsTrue();
        await driver.Stop();
    }

    [TestCase]
    public void ASurgeonsSedativeAddsUpToo()
    {
        var status = new SurgeonStatus(new Modifiers());
        var right = Db.Drug("diazepam")!.Dose * status.WeightKg;
        var events = new List<StatusEvent>();
        for (var i = 0; i < 25; i++)
        {
            status.Administer("diazepam", right * 0.1f);
            for (var j = 0; j < 5; j++)
            {
                events.AddRange(status.Update(0.1f, default));
            }
        }
        AssertBool(events.Contains(StatusEvent.KnockedOut) && status.IsOut)
            .OverrideFailureMessage($"twenty five tenths of a dose knock a surgeon out like 2.5 doses at once ({status.Drugs.Level("diazepam"):0.00} in)")
            .IsTrue();
    }

    [TestCase]
    public async Task ThresholdsForAnestheticsBagsLethalDrugsAndDangerousPairs()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var patient = driver.Patient;
        var toasts = new List<string>();
        driver.Surgery.Hud.Toasted += toasts.Add;
        float Right(string id) => Db.Drug(id)!.Dose * patient.WeightKg;
        void Fresh()
        {
            patient.Drugs.Clear();
            patient.Flags.Remove("overdose");
        }
        // An anesthetic tops up to the right dose: a second one deepens it for a while, then it's back to one dose.
        Fresh();
        patient.Administer("propofol", DrugRoute.Vein, Right("propofol"));
        Run(patient, 20f);
        patient.Administer("propofol", DrugRoute.Vein, Right("propofol"));
        Run(patient, 20f);
        var deeper = patient.Drugs.Level("propofol");
        Run(patient, Db.Drug("propofol")!.Duration * 1.2f);
        var held = patient.Drugs.Level("propofol");
        AssertBool(deeper > 1.5f && Mathf.Abs(held - 1f) < 0.02f && !patient.Flags.ContainsKey("overdose"))
            .OverrideFailureMessage($"a second dose of propofol deepens it ({deeper:0.00}), then it holds at one dose ({held:0.00}), no overdose")
            .IsTrue();
        // One dose of 2.5 times the right one is an overdose, though some wears off while it soaks in.
        Fresh();
        patient.Administer(Drug, DrugRoute.Vein, Right(Drug) * DrugDef.DoseOverdose);
        Run(patient, 20f);
        AssertBool(patient.Flags.ContainsKey("overdose"))
            .OverrideFailureMessage($"a single dose of {DrugDef.DoseOverdose:0.0} times the right one is an overdose").IsTrue();
        // Bags have no dose to overdo: three blood bags in a row are just a lot of blood.
        Fresh();
        for (var i = 0; i < 3; i++)
        {
            patient.Administer("blood_o_neg", DrugRoute.Vein);
        }
        Run(patient, 20f);
        AssertBool(patient.Flags.ContainsKey("overdose")).OverrideFailureMessage("three blood bags in a row are no overdose")
            .IsFalse();
        // Two drugs that react, working from the same moment, react once.
        patient.Drugs.Clear();
        toasts.Clear();
        patient.Administer("adrenaline", DrugRoute.Direct, Right("adrenaline"));
        patient.Administer("cocaine", DrugRoute.Direct, Db.Drug("cocaine")!.Dose > 0f ? Right("cocaine") : -1f);
        Run(patient, 20f);
        await Frames.Physics(2);
        AssertInt(toasts.Count(toast => toast.Contains("spikes")))
            .OverrideFailureMessage($"adrenaline and cocaine working together react once ({string.Join(" | ", toasts)})")
            .IsEqual(1);
        // A lethal drug that worked ends it, though the right dose wears off below working before the end.
        patient.Drugs.Clear();
        patient.Vitals.Rhythm = Rhythm.Sinus;
        var reasons = new List<string>();
        patient.Died += reasons.Add;
        patient.Administer("pentobarbital", DrugRoute.Vein, Right("pentobarbital"));
        for (var i = 0; i < (int)(Db.Drug("pentobarbital")!.Duration * 10f) && patient.Alive; i++)
        {
            patient.Simulate(0.1f);
        }
        AssertArray(reasons).OverrideFailureMessage("the right dose of pentobarbital lets the patient pass away peacefully")
            .IsEqual(new List<string> { "Passed away peacefully." });
        await driver.Stop();
    }

    [TestCase]
    public async Task DrawingFromTheIvBagTakesWhatWasPushedInFirst()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var tools = driver.Surgery.Tools;
        var bag = tools.DripBag()!;
        var amount = 6f * Db.Tool("vial_" + Drug)!.Concentration;
        tools.AddLiquid(bag, 6f, new Dictionary<string, float> { [Drug] = amount });
        bag.Bolus = 6f;
        var syringe = SurgeryState.ToolIsOnTray(driver.Surgery, "syringe_10");
        Syringe.DrawFromBag(bag, syringe, 3f);
        AssertFloat(syringe.Contents.GetValueOrDefault(Drug))
            .OverrideFailureMessage("3 ml drawn back by the port bring half the drug pushed in").IsEqualApprox(amount * 0.5f, 0.0001f);
        AssertFloat(bag.Bolus).OverrideFailureMessage("the other 3 ml of it are still to run down the line").IsEqualApprox(3f, 0.0001f);
        AssertFloat(bag.Contents.GetValueOrDefault(Drug)).OverrideFailureMessage("with the other half of the drug")
            .IsEqualApprox(amount * 0.5f, 0.0001f);
        await driver.Stop();
    }

    /// <summary><paramref name="seconds"/> of the patient's drugs soaking in and wearing off, in steps like the
    /// patient's own.</summary>
    private static void Run(Patient patient, float seconds)
    {
        for (var i = 0; i < (int)(seconds * 10f); i++)
        {
            patient.DrugEffectsOver(0.1f);
        }
    }
}
