namespace Scalpel.Tests.Liquids;

/// <summary>Iodine poured from the bottle while Use tool is held, into any dish (ToolDef.IsDish), and a cotton pad
/// dipped in it: the kidney dish works like the iodine dish (the scenario flows use that one).</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("liquids"), TestCategory("tool_iodine_bottle"), TestCategory("tool_kidney_dish"),
 TestCategory("tool_cotton_pad")]
[GodotArgs("--fixed-fps", "60")]
public class PourTest
{
    [TestCase]
    public async Task TheIodineBottlePoursWhileHeldIntoAnyDish()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var kidney = SurgeryState.ToolIsOnTray(driver.Surgery, "kidney_dish");
        await Frames.Physics(10);
        var bottle = (await driver.PlayerRequestsItem("iodine_bottle"))!;
        AssertBool(driver.Me.UsesLevel(driver.Me.Active)).OverrideFailureMessage("the bottle has no pouring level on the wheel")
            .IsFalse();
        foreach (var dish in (SurgicalTool[])[driver.FreeTools("iodine_dish")[0], kidney])
        {
            await driver.PlayerPoursInto(dish, 1f);
            var poured = bottle.Def.Power * 1f;
            AssertFloat(dish.Ml)
                .OverrideFailureMessage($"a second of Use tool held pours {poured:0} ml into the {dish.Def.Name} ({dish.Ml:0.0} ml)")
                .IsEqualApprox(Mathf.Min(poured, dish.Def.Volume), 2f);
            AssertFloat(dish.Contents.GetValueOrDefault("iodine")).OverrideFailureMessage("all of it is iodine")
                .IsEqualApprox(dish.Ml, 0.001f);
            AssertFloat(dish.Iodine).OverrideFailureMessage("every peer sees the liquid is iodine").IsEqualApprox(1f, 0.001f);
            var part = dish.FindChild("Pool", true, false) as MeshInstance3D ?? (MeshInstance3D)dish.FindChild("Liquid", true, false);
            AssertBool(part.Visible).OverrideFailureMessage($"the iodine shows in the {dish.Def.Name}").IsTrue();
        }
        var pool = (MeshInstance3D)kidney.FindChild("Pool", true, false);
        var albedo = ((ShaderMaterial)pool.GetSurfaceOverrideMaterial(0)).GetShaderParameter("albedo").AsColor();
        AssertBool(albedo.IsEqualApprox(SurgicalTool.IodineColor))
            .OverrideFailureMessage($"the kidney dish's pool is iodine brown ({albedo})").IsTrue();
        await driver.PlayerPoursInto(kidney, 6f);
        AssertFloat(kidney.Ml).OverrideFailureMessage("poured on, it fills up to the rim and no further")
            .IsEqualApprox(kidney.Def.Volume, 0.001f);
        await driver.Stop();
    }

    [TestCase]
    public async Task ACottonPadDippedInTheKidneyDishSanitizesTheSite()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        SurgeryState.PatientIsAsleep(driver.Patient);
        var kidney = SurgeryState.ToolIsOnTray(driver.Surgery, "kidney_dish");
        await Frames.Physics(10);
        await driver.PlayerSanitizesSite(0.2f, "kidney_dish");
        AssertFloat(driver.Patient.SanitizedFraction())
            .OverrideFailureMessage($"pads dipped in iodine in the kidney dish sanitize the site ({driver.Patient.SanitizedFraction():0.00})\n{driver.Recent(8)}")
            .IsGreater(0.2f);
        // The driver pours for 3 s; a soaked pad takes up to ToolActions.PadMl of it.
        var poured = Db.Tool("iodine_bottle")!.Power * 3f;
        var left = kidney.Contents.GetValueOrDefault("iodine");
        AssertFloat(left).OverrideFailureMessage($"the pads took iodine out of the kidney dish ({left:0} of {poured:0} ml left)")
            .IsLess(poured - 1f);
        await driver.Stop();
    }
}
