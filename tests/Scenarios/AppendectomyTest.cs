namespace Scalpel.Tests.Scenarios;

/// <summary>
/// The appendectomy the textbook way, like a player: the line opened through every layer and drawn along once more, the
/// appendix's base cut with the scalpel put down on it and drawn a little along its edge (slid onto it from elsewhere in
/// the opening, it passes over it), the appendix lifted out, the bleeding sealed and the belly closed. Opening the
/// belly cleanly oozes and leaves the appendix alone; only cutting its base opens its vessel. The game ends the surgery
/// with a successful report and the patient loses little blood.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("scenario"), TestCategory("tissue_modification")]
[GodotArgs("--fixed-fps", "60")]
public class AppendectomyTest
{
    /// <summary>An appendectomy done right loses less blood than this (ml).</summary>
    private const float LittleBlood = 200f;
    /// <summary>How far (meters) the scalpel put down on the appendix's base is drawn along its edge.</summary>
    private const float Drawn = 0.006f;
    /// <summary>How far (site uv) along the incision from the appendix the scalpel is put down to slide onto it.</summary>
    private const float Aside = 0.1f;

    [TestCase(Timeout = Limits.Slow)]
    public async Task TheTextbookRouteEndsWellWithLittleBloodLost()
    {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var surgery = driver.Surgery;
        var patient = driver.Patient;
        var appendix = patient.TargetByKind("appendix")!;
        var attached = appendix.Anchor;
        var lost = 0f;
        void Bleed() => lost += patient.Vitals.BleedRate / Engine.PhysicsTicksPerSecond;
        Frames.Tree.PhysicsFrame += Bleed;

        await Completes(driver, "iv");
        await Completes(driver, "anesthesia");
        await Completes(driver, "sanitize");
        await Completes(driver, "incise");
        var incision = patient.Wounds.Single(wound => wound.MadeBySurgeon);
        await driver.PlayerCutsSkin(incision.Points[0], incision.Points[^1], 3);
        await driver.PlayerPutsDown();
        await Frames.Seconds(2f);
        AssertInt(patient.Wounds.Count)
            .OverrideFailureMessage($"drawn along the incision again, the blade runs in the same cut: {driver.Bleeders()}").IsEqual(1);
        AssertBool(appendix.Anchor == attached && !appendix.Opened)
            .OverrideFailureMessage($"a blade drawn through the belly wall passes over the appendix (attached {appendix.Anchor:0.00})")
            .IsTrue();
        AssertFloat(patient.Vitals.BleedRate)
            .OverrideFailureMessage($"a clean incision through every layer only oozes: {driver.Bleeders()}").IsBetween(0.1f, 1.5f);

        // Put down elsewhere in the opening and slid over the appendix, the blade passes over it.
        await driver.PlayerRequestsItem("scalpel");
        var aside = appendix.Uv + ((incision.Points[^1] - incision.Points[0]).Normalized() * Aside);
        var onAppendix = driver.Body.UvToWorld(appendix.Uv, appendix.Depth);
        await driver.PlayerWalksTo(onAppendix);
        await driver.PlayerWorksAlong([driver.Body.UvToWorld(aside, appendix.Depth), onAppendix], 3);
        AssertBool(appendix.Anchor == attached && !appendix.Opened)
            .OverrideFailureMessage($"slid onto the appendix from elsewhere in the opening, the blade passes over it\n{driver.Recent()}")
            .IsTrue();
        await driver.PlayerPutsDown();

        await driver.PlayerCutsFree(appendix, Drawn);
        AssertInt(patient.Wounds.Count(wound => !wound.IsInternal))
            .OverrideFailureMessage("drawn along inside the opening, the blade cuts nothing more of the belly wall").IsEqual(1);
        AssertFloat(appendix.Anchor)
            .OverrideFailureMessage($"put down on the base and drawn along it, the scalpel cuts the appendix free\n{driver.Recent()}")
            .IsEqual(0f);
        AssertBool(patient.Wounds.Any(wound => wound.IsInternal && wound.Bleeding > 0.5f))
            .OverrideFailureMessage($"cutting the appendix's base opens its vessel: {driver.Bleeders()}").IsTrue();
        await Completes(driver, "extract");
        await Completes(driver, "stop_bleeding");
        await Completes(driver, "close");
        await Frames.Until(() => surgery.Finished, 10f);
        Frames.Tree.PhysicsFrame -= Bleed;
        var report = surgery.Report;
        AssertBool(report?.Success == true)
            .OverrideFailureMessage($"the appendectomy ends in success: {report?.Reason ?? "not finished"}, {surgery.Elapsed:0} s").IsTrue();
        AssertFloat(lost).OverrideFailureMessage($"the patient loses little blood ({lost:0} ml)").IsLess(LittleBlood);
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }

    /// <summary>Does the scenario's <paramref name="type"/> objective and waits for the game to count it done.</summary>
    private static async Task Completes(SurgeryDriver driver, string type)
    {
        var objectives = driver.Surgery.Objectives;
        var index = objectives.Steps.FindIndex(step => step.Type == type);
        await driver.PlayerCompletes(objectives.Steps[index]);
        var state = objectives.States[index];
        await Frames.Until(() => state.Done, 20f);
        AssertBool(state.Done)
            .OverrideFailureMessage($"\"{objectives.Steps[index].Label}\" completes. Last steps:\n{driver.Recent()}").IsTrue();
    }
}
