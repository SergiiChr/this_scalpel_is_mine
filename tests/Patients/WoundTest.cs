namespace Scalpel.Tests.Patients;

/// <summary>A small cut stopped for good with gauze stays dry only until it's damaged again: torn further, or a staple
/// goes through a vessel beside it. A surgeon's clean incision bleeds far less than a jagged cut or a tear.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
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
