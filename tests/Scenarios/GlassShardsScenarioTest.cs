namespace Scalpel.Tests.Scenarios;

/// <summary>
/// The glass shards removal played through (<see cref="ScenarioFlow"/>), with the checks on embedded targets: each
/// shard sticks out of the skin at its own depth and plugs the cut it went in by, which bleeds once it's pulled out. A sedative counts only while the patient stays awake.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("scenario"), TestCategory("visual_confirmation"), TestCategory("core_validation"),
 TestCategory("tissue_modification"), TestCategory("tool_forceps"), TestCategory("tool_needle")]
[GodotArgs("--fixed-fps", "60")]
public class GlassShardsScenarioTest {
    [TestCase("glass_shards", Timeout = Limits.Slow)]
    public async Task PlaysThroughWithEmbeddedShardChecks(string scenario) {
        var plugged = new List<float>();
        async Task PlayStep(SurgeryDriver driver, ObjectiveStep step) {
            var shards = driver.Patient.Targets.Where(target => target.Embedded).ToList();
            if (step.Type == "sedate") {
                CheckEmbedded(driver.Patient, shards);
                plugged.AddRange(shards.Select(shard => shard.Plug!.Bleeding));
            }
            await driver.PlayerCompletes(step);
            if (step.Type == "extract") {
                foreach (var (shard, before) in shards.Zip(plugged)) {
                    var track = shard.Plug!;
                    AssertBool(before > 0f && track.Held == 0f && track.Bleeding > before * 3f)
                        .OverrideFailureMessage($"the cut a shard came out of bleeds: held {track.Held:0.00}, {before:0.00} ml/s plugged, {track.Bleeding:0.00} ml/s out")
                        .IsTrue();
                }
            }
        }
        // Temporary local budget override, as every other scenario flow has.
        await ScenarioFlow.Play(scenario, keyFrames: true,
            budgetOverride: new("scenario surgery has recorded CPU/render overruns; profiling deferred", 4f), playStep: PlayStep);
    }

    /// <summary>Too much diazepam knocks the patient out. Waking up with it still working and calm, they're sedated
    /// and awake, but the step doesn't count: they were out.</summary>
    [TestCase(Timeout = Limits.Slow)]
    public async Task SedationThatKnocksThePatientOutNeverCounts() {
        var driver = SurgeryDriver.Create();
        await driver.Start("glass_shards");
        var patient = driver.Patient;
        var vitals = patient.Vitals;
        var objectives = driver.Surgery.Objectives;
        var index = objectives.Steps.FindIndex(step => step.Type == "sedate");
        var step = objectives.Steps[index];
        patient.Administer("diazepam", DrugRoute.Direct, Db.Drug("diazepam")!.Dose * patient.WeightKg * 2.2f);
        AssertBool(await Frames.Until(() => !vitals.IsAwake, 30f))
            .OverrideFailureMessage($"2.2 times the dose knocks the patient out: consciousness {vitals.Consciousness:0.00}").IsTrue();
        AssertBool(await Frames.Until(() => vitals.IsAwake && patient.SedativeWorks && vitals.Panic < step.Parameters.Float("max_panic"), 400f))
            .OverrideFailureMessage($"the patient comes round with it still working, calm: consciousness {vitals.Consciousness:0.00}, panic {vitals.Panic:0.00}")
            .IsTrue();
        await Frames.Seconds(2f);
        AssertBool(objectives.States[index].Done).OverrideFailureMessage("sedation that knocked the patient out doesn't count")
            .IsFalse();
        await driver.Stop();
    }

    /// <summary>Before anything is done: every shard's jagged top is out over the skin and its point under it, the
    /// deeper ones reaching further in, and each plugs its own cut.</summary>
    private static void CheckEmbedded(Patient patient, List<CavityTarget> shards) {
        var body = patient.Body;
        AssertInt(shards.Count).OverrideFailureMessage("the arm has three shards in it").IsEqual(3);
        var reaches = new List<float>();
        foreach (var shard in shards) {
            var bounds = PatientBody.LocalBounds(shard, body.Site);
            var skin = body.SurfaceHeight(shard.Uv);
            AssertBool(bounds.End.Y > skin + 0.005f && bounds.Position.Y < skin - 0.01f)
                .OverrideFailureMessage($"a shard sticks out of the skin and into the arm: {(bounds.End.Y - skin) * 1000f:0} mm out, {(skin - bounds.Position.Y) * 1000f:0} mm in")
                .IsTrue();
            reaches.Add(skin - bounds.Position.Y);
            AssertBool(shard.Plug is { Kind: WoundKind.Puncture } plug && Mathf.IsEqualApprox(plug.Held, CavityTarget.PlugHold, 0.01f))
                .OverrideFailureMessage($"a shard plugs the cut it went in by: {shard.Plug?.Kind}, held {shard.Plug?.Held:0.00}")
                .IsTrue();
        }
        AssertBool(reaches.Zip(reaches.Skip(1)).All(pair => pair.Second > pair.First + 0.004f))
            .OverrideFailureMessage($"each shard goes deeper than the one before: {string.Join(", ", reaches.Select(r => $"{r * 1000f:0} mm"))}")
            .IsTrue();
        AssertBool(!shards[0].Plug!.ThroughMuscle && shards[^1].Plug!.ThroughMuscle)
            .OverrideFailureMessage($"the shallowest shard's cut stops above the bottom of the muscle, the deepest's goes through it: depths {string.Join(", ", shards.Select(s => $"{s.Plug!.Depth:0.00}"))}")
            .IsTrue();
    }
}
