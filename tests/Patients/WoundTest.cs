namespace Scalpel.Tests.Patients;

/// <summary>A small cut stopped for good with gauze stays dry only until it's damaged again: torn further, or a staple
/// goes through a vessel beside it. A surgeon's clean incision bleeds far less than a jagged cut or a tear. Cut again
/// along its own line, a wound is the same wound, open and bleeding again where it was cut, whatever had stopped it.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
[GodotArgs("--fixed-fps", "60")]
public class WoundTest
{
    /// <summary>Meters per site uv, about an abdomen's.</summary>
    private const float SiteSize = 0.3f;

    [TestCase]
    public void AClottedCutBleedsAgainOnceTornFurther()
    {
        var tear = SmallClottedTear();
        tear.Extend(new Vector2(0.5f + (0.02f / SiteSize), 0.5f));
        AssertBool(tear.Clotted).OverrideFailureMessage("torn further, it's no longer stopped for good").IsFalse();
        AssertFloat(tear.BleedRate(SiteSize, 1f)).OverrideFailureMessage("and bleeds again").IsGreater(0.01f);
    }

    [TestCase]
    public void AVesselNickedBesideAClottedCutBleeds()
    {
        var tear = SmallClottedTear();
        tear.Nicked = Patient.StapleNick;
        AssertFloat(tear.BleedRate(SiteSize, 1f)).OverrideFailureMessage("a vessel a staple went through bleeds regardless")
            .IsEqualApprox(Patient.StapleNick, 0.001f);
    }

    [TestCase]
    public void ACleanIncisionBleedsFarLessThanAJaggedCutOrATear()
    {
        var clean = Line(WoundKind.Cut, surgeon: true, jagged: false).BleedRate(SiteSize, 1f);
        var jagged = Line(WoundKind.Cut, surgeon: true, jagged: true).BleedRate(SiteSize, 1f);
        var tear = Line(WoundKind.Tear, surgeon: false, jagged: false).BleedRate(SiteSize, 1f);
        AssertFloat(jagged).OverrideFailureMessage($"a jagged cut bleeds like a tear as long and deep ({jagged:0.00} and {tear:0.00} ml/s)")
            .IsEqualApprox(tear, 0.001f);
        AssertFloat(clean).OverrideFailureMessage($"a clean incision bleeds its share of that ({clean:0.00} ml/s)")
            .IsEqualApprox(tear * Wound.CleanIncisionShare, 0.001f);
    }

    [TestCase]
    public async Task ACutStoppedForGoodBleedsAgainWhenCutAgain()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var patient = driver.Patient;
        var from = new Vector2(0.4f, 0.5f);
        var to = from + new Vector2(driver.Body.MetersToUv(0.006f), 0f);
        var cut = SurgeryState.SkinIsCut(patient, from, to, 0.3f);
        SurgeryState.WoundIsClotted(cut);
        AssertObject(SurgeryState.SkinIsCut(patient, from, to, 0.3f)).OverrideFailureMessage("cut again along it, it's the same cut")
            .IsSame(cut);
        await Frames.Seconds(1f);
        AssertBool(cut.Clotted).OverrideFailureMessage("cut again, it's no longer stopped for good").IsFalse();
        AssertFloat(cut.Bleeding).OverrideFailureMessage($"and bleeds again: {driver.Bleeders()}").IsGreater(0f);
        await driver.Stop();
    }

    [TestCase]
    public async Task ASewnAndSealedIncisionOpensAndBleedsWhereItsCutAgain()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var patient = driver.Patient;
        var cm = driver.Body.MetersToUv(0.01f);
        var from = new Vector2(0.5f, 0.5f) - new Vector2(5f * cm, 0f);
        var incision = SurgeryState.SkinIsCut(patient, from, from + new Vector2(10f * cm, 0f), 1f);
        SurgeryState.WoundIsClosed(patient, incision);
        SurgeryState.WoundIsCauterized(incision);
        await Frames.Seconds(1f);
        AssertFloat(incision.Closure).OverrideFailureMessage("sewn shut along its length").IsEqual(1f);

        var recut = SurgeryState.SkinIsCut(patient, from + new Vector2(4f * cm, 0f), from + new Vector2(6f * cm, 0f), 1f);
        await Frames.Seconds(1f);
        AssertObject(recut).OverrideFailureMessage("cut again along its line, it's the same incision").IsSame(incision);
        var middle = incision.BinAt(new Vector2(0.5f, 0.5f));
        AssertFloat(incision.Bins[middle]).OverrideFailureMessage("its stitches are cut where the blade went: open there")
            .IsLess(1f);
        AssertFloat(incision.Bins[0]).OverrideFailureMessage("and still sewn shut beyond").IsEqual(1f);
        AssertFloat(incision.Cauterized)
            .OverrideFailureMessage($"the cautery no longer seals the stretch cut again (sealed {incision.Cauterized:0.00})")
            .IsBetween(0.5f, 0.9f);
        AssertFloat(incision.Bleeding).OverrideFailureMessage($"it bleeds there: {driver.Bleeders()}").IsGreater(0f);
        await driver.Stop();
    }

    [TestCase]
    public async Task AStrokeAcrossAUShapedCutIsANewCut()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var patient = driver.Patient;
        var cm = driver.Body.MetersToUv(0.01f);
        var corner = new Vector2(0.4f, 0.5f);
        var across = new Vector2(0f, cm);
        SurgeryState.SkinIsCutAlong(patient,
            [corner, corner + new Vector2(5f * cm, 0f), corner + new Vector2(5f * cm, 0f) + across, corner + across], 0.3f);
        var bridge = SurgeryState.SkinIsCut(patient, corner + new Vector2(2f * cm, 0f), corner + new Vector2(2f * cm, 0f) + across, 0.3f);
        AssertInt(patient.Wounds.Count)
            .OverrideFailureMessage("a stroke from one arm of a U to the other through whole skin is a cut of its own").IsEqual(2);
        AssertFloat(driver.Body.UvToMeters(bridge.LengthUv)).OverrideFailureMessage("as long as the stroke").IsEqualApprox(0.01f, 0.002f);
        await driver.Stop();
    }

    /// <summary>A 10 cm wound through the muscle.</summary>
    private static Wound Line(WoundKind kind, bool surgeon, bool jagged)
    {
        var wound = new Wound(1, kind, new Vector2(0.3f, 0.5f), 1f) { MadeBySurgeon = surgeon, Jagged = jagged };
        wound.Extend(new Vector2(0.3f + (0.1f / SiteSize), 0.5f));
        return wound;
    }

    /// <summary>A 0.5 cm tear stopped for good with gauze.</summary>
    private static Wound SmallClottedTear()
    {
        var tear = new Wound(1, WoundKind.Tear, new Vector2(0.5f, 0.5f), 0.5f);
        tear.Extend(new Vector2(0.5f + (0.005f / SiteSize), 0.5f));
        AssertBool(tear.IsSmall(SiteSize)).OverrideFailureMessage("a 0.5 cm tear is too small to sew").IsTrue();
        tear.Clotted = true;
        AssertFloat(tear.BleedRate(SiteSize, 1f)).OverrideFailureMessage("stopped for good, it's dry").IsEqual(0f);
        return tear;
    }
}
