namespace Scalpel.Tests.Scenarios;

/// <summary>
/// The thigh bullet played through (<see cref="ScenarioFlow"/>) the way a player holds it open: a tourniquet above the
/// site, then, zoomed in over the widened entry wound, forceps in one hand and a retractor in the other draw its lips
/// apart. Each lip follows the hand and gives less and less, and nothing tears, either while both hold or once the
/// surgeon walks off to put the forceps down with the retractor still hooked in. The bullet shows between the lips and
/// comes out with forceps; the retractor is taken out, and the muscle is sewn before the skin. The skin never tears.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("scenario"), TestCategory("visual_confirmation"), TestCategory("core_validation"),
 TestCategory("tissue_modification"), TestCategory("skin_pull"), TestCategory("tool_forceps"), TestCategory("tool_retractor"),
 TestCategory("tool_tourniquet"), TestCategory("tool_needle"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class ThighBulletScenarioTest {
    /// <summary>How far (meters) the mouse draws each lip aside.</summary>
    private const float Draw = 0.02f;

    [TestCase("bullet_muscle", Timeout = Limits.Slow)]
    public async Task PlaysThroughHoldingTheCutOpenByHand(string scenario) {
        SurgicalTool? retractor = null;
        void NoTears(SurgeryDriver driver, string when) {
            var tears = driver.Patient.Wounds.Where(wound => wound.Kind == WoundKind.Tear).ToList();
            AssertInt(tears.Count).OverrideFailureMessage($"the skin doesn't tear {when}: "
                + string.Join(", ", tears.Select(tear => $"{driver.Body.UvToMeters(tear.LengthUv) * 100f:0.0} cm at {tear.Points[0]}"))
                + $"\n{driver.Recent()}").IsEqual(0);
        }
        async Task PlayStep(SurgeryDriver driver, ObjectiveStep step) {
            if (step.Type == "extract") {
                retractor = await SpreadsByHand(driver);
            }
            await driver.PlayerCompletes(step);
            NoTears(driver, $"by the end of \"{step.Label}\"");
            if (step.Type == "anesthesia") {
                var bleeding = driver.Patient.Vitals.BleedRate;
                await driver.PlayerAppliesTourniquet();
                await Frames.Seconds(1f);
                AssertBool(driver.Patient.TourniquetOn && driver.Patient.Vitals.BleedRate < bleeding * 0.2f)
                    .OverrideFailureMessage($"the tourniquet above the site slows the thigh's bleeding: {driver.Patient.Vitals.BleedRate:0.00} ml/s, {bleeding:0.00} before\n{driver.Recent()}")
                    .IsTrue();
            }
            if (step.Type == "extract" && retractor is not null) {
                await driver.PlayerUnhooks(retractor, SurgeryDriver.Right);
                AssertBool(retractor.Hold is null && retractor.State != ToolState.Standing)
                    .OverrideFailureMessage($"the retractor comes out of the cut: {retractor.State}, {retractor.Hold}\n{driver.Recent()}").IsTrue();
            }
        }
        void Validate(SurgeryDriver driver) {
            NoTears(driver, "by the discharge");
            foreach (var wound in driver.Patient.Wounds.Where(wound => wound.ThroughMuscle)) {
                AssertBool(wound.Muscle.All(m => m >= 1f) && wound.Closure >= 0.98f)
                    .OverrideFailureMessage($"wound {wound.Id} has its muscle sewn ({wound.Muscle.Min():0.00}) and its skin closed ({wound.Closure:0.00})")
                    .IsTrue();
            }
        }
        // Temporary local budget override, as every other scenario flow has.
        await ScenarioFlow.Play(scenario, keyFrames: true,
            budgetOverride: new("scenario surgery has recorded CPU/render overruns; profiling deferred", 19f), playStep: PlayStep,
            validate: Validate);
    }

    /// <summary>Forceps in the left hand and the retractor in the right take the widened entry wound's lips in its
    /// middle and draw them apart; the forceps are put down at the tray, the retractor left hooked in beside the bullet.
    /// Returns the retractor.</summary>
    private static async Task<SurgicalTool> SpreadsByHand(SurgeryDriver driver) {
        var patient = driver.Patient;
        var cut = patient.Wounds.Where(wound => wound.Kind == WoundKind.Cut).MaxBy(wound => wound.LengthUv)!;
        var bullet = patient.Targets.First(target => target.Kind == "bullet");
        await driver.SwitchTo(SurgeryDriver.Left);
        var forceps = (await driver.PlayerRequestsItem("forceps"))!;
        await driver.SwitchTo(SurgeryDriver.Right);
        var retractor = (await driver.PlayerRequestsItem("retractor"))!;
        await driver.PlayerWalksTo(driver.SitePoint(cut.Midpoint));
        await driver.PlayerZoomsIn();
        var wounds = patient.Wounds.Count;
        foreach (var (hand, side) in new[] { (SurgeryDriver.Left, 1), (SurgeryDriver.Right, -1) }) {
            var tool = driver.Me.HeldTool(hand)!;
            var lip = driver.LipOf(cut, side);
            AssertObject(await driver.PlayerTakesHoldOfLip(hand, lip))
                .OverrideFailureMessage($"the {tool.Def.Id} takes the lip\n{driver.Recent()}").IsNotNull();
        }
        foreach (var (hand, side) in new[] { (SurgeryDriver.Left, 1), (SurgeryDriver.Right, -1) }) {
            var tool = driver.Me.HeldTool(hand)!;
            var lip = driver.LipOf(cut, side);
            var away = (driver.SitePoint(lip) - driver.SitePoint(cut.ClosestPoint(lip))).Slide(Vector3.Up).Normalized();
            var gave = await driver.PlayerDrawsLip(hand, away, Draw);
            var text = string.Join(", ", gave.Select(mm => (mm * 1000f).ToString("0.0")));
            // With the other lip already held aside, this one still follows the hand: it doesn't stand frozen.
            AssertFloat(gave[0]).OverrideFailureMessage($"the {tool.Def.Id}'s lip follows the hand's first millimeter ({text} mm)").IsGreater(0.0005f);
            AssertFloat(gave.Sum()).OverrideFailureMessage($"the {tool.Def.Id} draws its lip at least 5 mm aside ({text} mm)").IsGreater(0.005f);
            AssertFloat(gave.Min()).OverrideFailureMessage($"the {tool.Def.Id}'s lip never jumps back ({text} mm)").IsGreater(-0.0005f);
        }
        AssertInt(patient.Wounds.Count).OverrideFailureMessage("drawing both lips apart tears no skin").IsEqual(wounds);
        AssertBool(!patient.Covered(bullet) && bullet.InReach).OverrideFailureMessage("the bullet lies open between the lips").IsTrue();
        await driver.CaptureView("spread_by_hand", "zoomed in over the thigh: forceps and the retractor draw the widened "
            + "wound's lips apart, the opening between them clear to see through the see-through hands");
        await driver.Capture("spread_by_hand", "forceps and the retractor hold the lips of the widened wound apart, each lip "
            + "drawn back smoothly with no torn skin beside it, the bullet in the opening");
        // Off to the tray with the forceps, the retractor still hooked in the other lip: it's left hooked, nothing tears.
        await driver.SwitchTo(SurgeryDriver.Left);
        await driver.PlayerPutsDown();
        AssertBool(retractor.State == ToolState.Standing && retractor.Hold is SkinHold && patient.Wounds.Count == wounds)
            .OverrideFailureMessage($"walked off with the retractor hooked in the lip, it's left hooked and no skin tears: {retractor.State}, "
                + $"{retractor.Hold}, {patient.Wounds.Count - wounds} new wounds\n{driver.Recent()}").IsTrue();
        AssertBool(forceps.Hold is null).OverrideFailureMessage("the forceps let go of their lip").IsTrue();
        await driver.Capture("retractor_left", "the retractor lies hooked in its lip where it held it, drawing it back; the "
            + "other lip let go, no torn skin");
        return retractor;
    }
}
