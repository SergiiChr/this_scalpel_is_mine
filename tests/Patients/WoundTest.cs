namespace Scalpel.Tests.Patients;

/// <summary>A small cut stopped for good with gauze stays dry only until it's damaged again: torn further, or a staple
/// goes through a vessel beside it.</summary>
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
