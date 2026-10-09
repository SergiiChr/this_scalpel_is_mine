namespace Scalpel.Tests.Patients;

/// <summary>
/// Every bleed shows where it comes from: a cut bleeding however little puddles on the skin at itself, and a vessel
/// inside an incision wells up where it is, on top of the blood pooled in the cavity once that covers it. With key
/// frames review the puddle on the tiny cut and the dome standing over the vessel, above the pool.
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
