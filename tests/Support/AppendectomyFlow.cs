namespace Scalpel.Tests.Support;

/// <summary>
/// The appendectomy the textbook way, like a player: the line opened through every layer and drawn along once more, the
/// appendix's base cut with the scalpel put down on it and drawn a little along its edge (slid onto it from elsewhere in
/// the opening, it passes over it), the appendix lifted out, the bleeding sealed and the belly closed. Opening the
/// belly cleanly oozes and leaves the appendix alone; only cutting its base opens its vessel. The game ends the surgery
/// with a successful report and the patient loses little blood.
/// </summary>
public sealed class AppendectomyFlow : IDisposable {
    /// <summary>An appendectomy done right loses less blood than this (ml).</summary>
    private const float LittleBlood = 200f;
    /// <summary>How far (meters) the scalpel put down on the appendix's base is drawn along its edge.</summary>
    private const float Drawn = 0.006f;
    /// <summary>How far (site uv) along the incision from the appendix the scalpel is put down to slide onto it.</summary>
    private const float Aside = 0.1f;

    private Patient? _patient;
    private float _attached;
    private float _lost;

    /// <summary>Extends incision and extraction; the shared flow still handles every objective and its hold time.</summary>
    public async Task PlayStep(SurgeryDriver driver, ObjectiveStep step) {
        var patient = driver.Patient;
        var appendix = patient.TargetByKind("appendix")!;
        if (_patient is null) {
            _patient = patient;
            _attached = appendix.Anchor;
            Frames.Tree.PhysicsFrame += RecordBlood;
        }
        var attached = _attached;
        if (step.Type == "extract") {
            await driver.PlayerCutsFree(appendix, Drawn);
            AssertInt(patient.Wounds.Count(wound => !wound.IsInternal))
                .OverrideFailureMessage("drawn along inside the opening, the blade cuts nothing more of the belly wall").IsEqual(1);
            AssertFloat(appendix.Anchor)
                .OverrideFailureMessage($"put down on the base and drawn along it, the scalpel cuts the appendix free\n{driver.Recent()}")
                .IsEqual(0f);
            AssertBool(patient.Wounds.Any(wound => wound.IsInternal && wound.Bleeding > 0.5f))
                .OverrideFailureMessage($"cutting the appendix's base opens its vessel: {driver.Bleeders()}").IsTrue();
        }
        await driver.PlayerCompletes(step);
        if (step.Type == "incise") {
            var incision = patient.Wounds.Single(wound => wound.MadeBySurgeon);
            GD.Print($"before repeat {driver.Bleeders()}");
            var scalpel = (await driver.PlayerRequestsItem("scalpel"))!;
            var blade = scalpel.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>()
                .First(mesh => mesh.Name.ToString().Contains("Blade", StringComparison.Ordinal));
            var reach = blade.Mesh.GetFaces().Max(point => blade.ToGlobal(point).DistanceTo(scalpel.TipPosition()));
            var along = (incision.Points[^1] - incision.Points[0]).Normalized();
            var inset = reach / (along * driver.Body.SiteSize).Length();
            AssertFloat(incision.LengthUv).OverrideFailureMessage("the full blade fits inside the existing incision")
                .IsGreater(inset * 2f);
            // A repeat pass keeps the whole visible blade inside the opening, not the buried tip at its V ends.
            await driver.PlayerCutsSkin(SurgeryDriver.BesideWound(incision, inset, 0f),
                SurgeryDriver.BesideWound(incision, incision.LengthUv - inset, 0f), 3);
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
        }
    }

    private void RecordBlood() => _lost += _patient!.Vitals.BleedRate / Engine.PhysicsTicksPerSecond;

    /// <summary>Checks the surgery's cumulative blood loss before the shared flow stops its scene.</summary>
    public void Validate(SurgeryDriver _) {
        Dispose();
        AssertFloat(_lost).OverrideFailureMessage($"the patient loses little blood ({_lost:0} ml)").IsLess(LittleBlood);
    }

    public void Dispose() {
        if (_patient is not null) {
            Frames.Tree.PhysicsFrame -= RecordBlood;
            _patient = null;
        }
    }
}
