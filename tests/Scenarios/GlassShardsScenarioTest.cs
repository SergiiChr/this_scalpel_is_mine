namespace Scalpel.Tests.Scenarios;

/// <summary>
/// The glass shards removal played through (<see cref="ScenarioFlow"/>), with the checks on embedded targets: shards of
/// different sizes stick out of the skin at their own depths, one is buried under it out of reach until its cut is
/// spread, and each plugs the dirty cut it went in by, which bleeds once it's pulled out. A sedative counts only while
/// the patient stays awake.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("scenario"), TestCategory("visual_confirmation"), TestCategory("core_validation"),
 TestCategory("tissue_modification"), TestCategory("tool_forceps"), TestCategory("tool_needle"), TestCategory("frame_budget")]
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
            if (step.Type == "extract") {
                AssertBool(await driver.PlayerTakesHoldOf(shards.Single(shard => shard.Buried)))
                    .OverrideFailureMessage("forceps can't get at the buried shard through its closed cut").IsFalse();
                await driver.PlayerPutsDown();
            }
            await driver.PlayerCompletes(step);
            if (step.Type == "extract") {
                foreach (var (shard, before) in shards.Zip(plugged)) {
                    var track = shard.Plug!;
                    AssertBool(before > 0f && track.Held == 0f && track.Bleeding > before * 3f)
                        .OverrideFailureMessage($"the cut a shard came out of bleeds: held {track.Held:0.00}, {before:0.00} ml/s plugged, {track.Bleeding:0.00} ml/s out\n{driver.Recent()}")
                        .IsTrue();
                }
            }
        }
        // Temporary local budget override, as every other scenario flow has.
        await ScenarioFlow.Play(scenario, keyFrames: true,
            budgetOverride: new("scenario surgery has recorded CPU/render overruns; profiling deferred; closing a thread takes up to 37 ms", 6f), playStep: PlayStep);
    }

    /// <summary>Too much diazepam knocks the patient out. Waking up with it still working and calm, they're sedated
    /// and awake, but the step doesn't count: they were out.</summary>
    [TestCase(Timeout = Limits.Slow)]
    public async Task SedationThatKnocksThePatientOutNeverCounts() {
        var rendering = RenderingServer.RenderLoopEnabled;
        SurgeryDriver? driver = null;
        try {
            FrameBudget.RenderingOff();
            driver = SurgeryDriver.Create();
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
        } finally {
            RenderingServer.RenderLoopEnabled = rendering;
            if (driver is not null) {
                await driver.Stop();
            }
        }
    }

    /// <summary>Before anything is done: every shard's jagged top is out over the skin and its point under it, but for
    /// one buried under it. They range from a sliver to a long blade, the deepest cut through the muscle and the
    /// shallowest not, and each plugs its own dirty cut.</summary>
    private static void CheckEmbedded(Patient patient, List<CavityTarget> shards) {
        var body = patient.Body;
        AssertInt(shards.Count).OverrideFailureMessage("the arm has six shards in it").IsEqual(6);
        AssertInt(shards.Count(shard => shard.Buried)).OverrideFailureMessage("one of them is buried under the skin").IsEqual(1);
        foreach (var shard in shards) {
            var bounds = PatientBody.LocalBounds(shard, body.Site);
            var skin = body.SurfaceHeight(shard.Uv);
            // Buried, no corner of it comes up through the skin where the arm curves away.
            Vector3[] corners = [bounds.End, bounds.End with { X = bounds.Position.X }, bounds.End with { Z = bounds.Position.Z },
                bounds.Position with { Y = bounds.End.Y }];
            var top = (shard.Buried ? corners.Max(corner => corner.Y - body.SurfaceHeight(body.LocalToUv(corner))) : bounds.End.Y - skin) * 1000f;
            var under = (skin - bounds.Position.Y) * 1000f;
            AssertBool(shard.Buried ? top < -2f : top > 5f && under > 10f)
                .OverrideFailureMessage(shard.Buried
                    ? $"the buried shard lies under the skin everywhere: its top {top:0} mm from it"
                    : $"a shard sticks out of the skin and into the arm: {top:0} mm out, {under:0} mm in")
                .IsTrue();
            AssertBool(shard.Plug!.Points.All(body.OnBody))
                .OverrideFailureMessage($"a shard's cut lies on the arm, not where the site hangs off it: {shard.Plug.Points[0]}..{shard.Plug.Points[^1]}")
                .IsTrue();
            AssertBool(shard.Plug is { Kind: WoundKind.Puncture, Dirty: true } plug && Mathf.IsEqualApprox(plug.Held, CavityTarget.PlugHold, 0.01f))
                .OverrideFailureMessage($"a shard plugs the dirty cut it went in by: {shard.Plug?.Kind}, dirty {shard.Plug?.Dirty}, held {shard.Plug?.Held:0.00}")
                .IsTrue();
        }
        var sizes = shards.Select(shard => PatientBody.LocalBounds(shard, shard).Size.Length()).ToList();
        AssertFloat(sizes.Max() / sizes.Min())
            .OverrideFailureMessage($"the biggest shard is at least twice the smallest: {string.Join(", ", sizes.Select(size => $"{size * 1000f:0} mm"))}")
            .IsGreaterEqual(2f);
        var reaches = shards.OrderBy(shard => body.SurfaceHeight(shard.Uv) - PatientBody.LocalBounds(shard, body.Site).Position.Y).ToList();
        AssertBool(!reaches[0].Plug!.ThroughMuscle && reaches[^1].Plug!.ThroughMuscle)
            .OverrideFailureMessage($"the shallowest shard's cut stops above the bottom of the muscle, the deepest's goes through it: depths {string.Join(", ", reaches.Select(s => $"{s.Plug!.Depth:0.00}"))}")
            .IsTrue();
    }
}
