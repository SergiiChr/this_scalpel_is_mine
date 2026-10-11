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
        var clean = Line(WoundKind.Cut, surgeon: true, jagged: 0f).BleedRate(SiteSize, 1f);
        var jagged = Line(WoundKind.Cut, surgeon: true, jagged: 1f).BleedRate(SiteSize, 1f);
        var tear = Line(WoundKind.Tear, surgeon: false, jagged: 0f).BleedRate(SiteSize, 1f);
        AssertFloat(jagged).OverrideFailureMessage($"a jagged cut bleeds like a tear as long and deep ({jagged:0.00} and {tear:0.00} ml/s)")
            .IsEqualApprox(tear, 0.001f);
        AssertFloat(clean).OverrideFailureMessage($"a clean incision bleeds its share of that ({clean:0.00} ml/s)")
            .IsEqualApprox(tear * Wound.CleanIncisionShare, 0.001f);
    }

    [TestCase]
    public void AJaggedStretchOfAnIncisionBleedsLikeATearOnlyThere()
    {
        var clean = Line(WoundKind.Cut, surgeon: true, jagged: 0f).BleedRate(SiteSize, 1f);
        var tear = Line(WoundKind.Tear, surgeon: false, jagged: 0f).BleedRate(SiteSize, 1f);
        var tenth = Line(WoundKind.Cut, surgeon: true, jagged: 0.1f).BleedRate(SiteSize, 1f);
        AssertFloat(tenth)
            .OverrideFailureMessage($"a tenth of it cut jagged bleeds a tear's rate there and an incision's elsewhere ({tenth:0.00} ml/s, "
                + $"clean {clean:0.00}, torn {tear:0.00})")
            .IsBetween(clean + ((tear - clean) * 0.05f), clean + ((tear - clean) * 0.2f));
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
        var fresh = SurgeryState.SkinIsCut(patient, new Vector2(0.5f, 0.3f), new Vector2(0.5f, 0.3f) + new Vector2(2f * cm, 0f), 1f);
        await Frames.Seconds(1f);
        AssertFloat(incision.Closure).OverrideFailureMessage("sewn shut along its length").IsEqual(1f);

        var recut = SurgeryState.SkinIsCut(patient, from + new Vector2(4f * cm, 0f), from + new Vector2(6f * cm, 0f), 1f);
        await Frames.Seconds(1f);
        AssertObject(recut).OverrideFailureMessage("cut again along its line, it's the same incision").IsSame(incision);
        var middle = incision.BinAt(new Vector2(0.5f, 0.5f));
        AssertFloat(incision.Bins[middle]).OverrideFailureMessage("its stitches are cut where the blade went: open there")
            .IsLess(1f);
        AssertFloat(incision.Bins[0]).OverrideFailureMessage("and still sewn shut beyond").IsEqual(1f);
        AssertFloat(incision.Bleeding)
            .OverrideFailureMessage($"neither sewn nor sealed there, it bleeds there like a fresh cut that long: {driver.Bleeders()}")
            .IsGreater(fresh.Bleeding * 0.5f);
        await driver.Stop();
    }

    [TestCase]
    public async Task AStrokeComingIntoAnIncisionEndsWhereItComesIn()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var patient = driver.Patient;
        var cm = driver.Body.MetersToUv(0.01f);
        var from = new Vector2(0.5f, 0.5f) - new Vector2(5f * cm, 0f);
        SurgeryState.SkinIsCut(patient, from, from + new Vector2(10f * cm, 0f), 1f);
        var onLine = from + new Vector2(3f * cm, 0f);
        var entry = SurgeryState.SkinIsCutAlong(patient, [onLine + new Vector2(0f, cm), onLine, onLine + new Vector2(4f * cm, 0f)], 1f);
        AssertInt(patient.Wounds.Count).OverrideFailureMessage("the stroke runs into the incision rather than over it").IsEqual(2);
        AssertFloat(driver.Body.UvToMeters(entry.LengthUv))
            .OverrideFailureMessage($"its own cut ends where it comes into the incision ({driver.Body.UvToMeters(entry.LengthUv) * 100f:0.0} cm)")
            .IsEqualApprox(0.01f, 0.003f);
        await driver.Stop();
    }

    [TestCase]
    public async Task CauteryElsewhereLeavesAStretchCutAgainBleeding()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var patient = driver.Patient;
        SurgeryState.PatientIsAsleep(patient);
        var cm = driver.Body.MetersToUv(0.01f);
        var from = new Vector2(0.5f, 0.5f) - new Vector2(5f * cm, 0f);
        var incision = SurgeryState.SkinIsCut(patient, from, from + new Vector2(10f * cm, 0f), 1f);
        SurgeryState.SkinIsCut(patient, from + new Vector2(6f * cm, 0f), from + new Vector2(8f * cm, 0f), 1f);
        await Frames.Seconds(1f);
        var unsealed = incision.Bleeding;

        // On whole skin beside its far end, away from the stretch cut again.
        var end = incision.BinPosition(0);
        var beside = Enumerable.Range(2, 12).Select(step => end + new Vector2(0f, step * 0.005f)).First(uv => !driver.Body.IsOpen(uv));
        await driver.PlayerRequestsItem("cautery");
        await driver.PlayerWalksTo(driver.SitePoint(beside));
        await driver.PlayerWorksAt(driver.SitePoint(beside), 2, 3f);
        await driver.PlayerPutsDown();
        await Frames.Seconds(1f);
        AssertFloat(incision.Cauterized).OverrideFailureMessage("the cautery seals the incision").IsGreater(0.9f);
        var share = 2f / 10f;
        AssertFloat(incision.Bleeding)
            .OverrideFailureMessage($"but not the stretch cut again, far from where it went: it bleeds as before there ({unsealed:0.00} ml/s "
                + $"before, {incision.Bleeding:0.00} now)")
            .IsGreater(unsealed * share * 0.9f);
        await driver.Stop();
    }

    [TestCase(0.006f)]
    [TestCase(0.01f)]
    public async Task AStrokeAcrossAUShapedCutIsANewCut(float apart)
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var patient = driver.Patient;
        var cm = driver.Body.MetersToUv(0.01f);
        var corner = new Vector2(0.4f, 0.5f);
        var across = new Vector2(0f, driver.Body.MetersToUv(apart));
        SurgeryState.SkinIsCutAlong(patient,
            [corner, corner + new Vector2(5f * cm, 0f), corner + new Vector2(5f * cm, 0f) + across, corner + across], 0.3f);
        var bridge = SurgeryState.SkinIsCut(patient, corner + new Vector2(2f * cm, 0f), corner + new Vector2(2f * cm, 0f) + across, 0.3f);
        AssertInt(patient.Wounds.Count)
            .OverrideFailureMessage($"a stroke between the arms of a U {apart * 1000f:0} mm apart, through whole skin, is a cut of its own")
            .IsEqual(2);
        AssertFloat(driver.Body.UvToMeters(bridge.LengthUv)).OverrideFailureMessage("as long as the stroke").IsEqualApprox(apart, 0.002f);
        await driver.Stop();
    }

    /// <summary>A 10 cm wound through the muscle, jagged along <paramref name="jagged"/> of its length (0..1) from its
    /// start.</summary>
    private static Wound Line(WoundKind kind, bool surgeon, float jagged)
    {
        var start = new Vector2(0.3f, 0.5f);
        var along = new Vector2(0.1f / SiteSize, 0f);
        var wound = new Wound(1, kind, start, 1f) { MadeBySurgeon = surgeon };
        wound.Extend(start + along);
        if (jagged > 0f)
        {
            wound.MarkJagged(start, start + (along * jagged));
        }
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
