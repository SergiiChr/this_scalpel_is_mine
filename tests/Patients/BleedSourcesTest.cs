namespace Scalpel.Tests.Patients;

/// <summary>
/// Every bleed shows where it comes from: a cut bleeding however little puddles on the skin at itself, and a vessel
/// inside an incision wells up where it is, however many there are. Under a narrow incision its blood wells up the slit
/// as far as one can see down it, and stays on top of the blood pooled in the cavity once that rises. With key frames
/// review the puddle on the tiny cut and a dome standing in the incision over each vessel.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("liquids"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class BleedSourcesTest
{
    [TestCase]
    public async Task EveryBleedShowsWhereItComesFrom()
    {
        var session = await ToolSession.Start("appendectomy", "bleed_sources");
        var driver = session.Driver;
        var patient = driver.Patient;
        var body = driver.Body;
        var tiny = SurgeryState.SkinIsCut(patient, new Vector2(0.3f, 0.3f), new Vector2(0.3f, 0.3f) + new Vector2(body.MetersToUv(0.003f), 0f), 0.1f);
        await Frames.Seconds(2f);
        AssertFloat(tiny.Bleeding).OverrideFailureMessage($"a tiny shallow cut bleeds a little: {driver.Bleeders()}")
            .IsBetween(0.001f, 0.05f);
        AssertFloat(body.WoundMap.Value(WoundMap.Layer.Fluids, WoundMap.Blood, tiny.BleedPoint))
            .OverrideFailureMessage("however little it bleeds, it puddles on the skin at itself").IsGreater(0.5f);
        await driver.Capture("tiny_cut");

        var at = new Vector2(0.5f, 0.62f);
        SurgeryState.SkinIsCut(patient, at - new Vector2(0.15f, 0f), at + new Vector2(0.15f, 0f), 1f);
        await Frames.Seconds(1f);
        AssertBool(body.Tissue.IsOpen(at)).OverrideFailureMessage("the vessel lies in the opening").IsTrue();
        var vessel = SurgeryState.VesselBleeds(patient, at, body.CavityDepth() * 0.5f);
        await Frames.Seconds(1f);
        AssertString(Wells(driver, vessel)).OverrideFailureMessage("the vessel wells up where it is").IsEqual("at the vessel");
        var seen = Mathf.Max(body.Tissue.GapAt(at, 0.02f) * Patient.SlitView, PatientBody.SkinThickness);
        var shown = -body.HeightAboveSite(body.Blood.Wells.MinBy(well => body.WorldToUv(well).DistanceTo(at)));
        AssertFloat(shown).OverrideFailureMessage($"deep under a narrow incision, it wells up the slit to where it can be seen ({seen * 100f:0.0} cm)")
            .IsLessEqual(seen + 0.002f);
        await driver.Capture("vessel");

        var risen = await Frames.Until(() => body.CavityPoolHeight > body.Site.ToLocal(driver.SitePoint(at, vessel.DepthM)).Y, 60f);
        AssertBool(risen).OverrideFailureMessage($"the cavity pool rises over the vessel ({patient.CavityBloodMl:0} ml)").IsTrue();
        await Frames.Seconds(0.5f);
        AssertString(Wells(driver, vessel)).OverrideFailureMessage("covered by the pool, it still wells up where it is")
            .IsEqual("at the vessel");
        var well = body.Blood.Wells.MinBy(well => body.WorldToUv(well).DistanceTo(at));
        // Placed on the frame before, while the pool keeps rising.
        AssertFloat(body.Site.ToLocal(well).Y).OverrideFailureMessage($"on the pool's surface ({body.CavityPoolHeight:0.0000} m)")
            .IsGreaterEqual(body.CavityPoolHeight - 0.002f);
        await driver.Capture("vessel_under_pool");

        // Many vessels at once along the incision, more than the domes first made room for: each shows, welling up
        // where the incision gapes, seeping onto the skin where it's shut.
        var vessels = Enumerable.Range(0, 20)
            .Select(i => SurgeryState.VesselBleeds(patient, at + new Vector2((i - 10) * 0.012f, 0f), vessel.DepthM)).ToList();
        await Frames.Seconds(1f);
        var unseen = vessels.Where(v => Wells(driver, v) != "at the vessel"
            && body.WoundMap.Value(WoundMap.Layer.Fluids, WoundMap.Blood, v.Points[0]) < 0.5f).Select(v => v.Points[0]).ToList();
        AssertBool(unseen.Count == 0).OverrideFailureMessage($"every vessel shows where it bleeds (unseen at {string.Join(", ", unseen)})")
            .IsTrue();
        await driver.Capture("many_vessels");
        await session.Finish();
    }

    /// <summary>"at the vessel" when a welling dome stands over it (within a few millimeters across the site), else
    /// where the domes are.</summary>
    private static string Wells(SurgeryDriver driver, Wound vessel)
    {
        var wells = driver.Body.Blood.Wells;
        var over = wells.Any(well => driver.Body.UvToMeters(driver.Body.WorldToUv(well).DistanceTo(vessel.Points[0])) < 0.003f);
        return over ? "at the vessel" : $"wells at {string.Join(", ", wells.Select(driver.Body.WorldToUv))}";
    }
}
