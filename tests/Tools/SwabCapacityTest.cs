namespace Scalpel.Tests.Tools;

/// <summary>
/// Gauze and cotton pads hold what they soak up, iodine and blood together, up to their volume. Gauze packed into a
/// pool of blood fills up, turns red and takes no more, but still presses on the vessel under it. A pad dipped in
/// iodine with blood mixed in takes both in their share, then wiping blood fills it and it takes the color of their mix. With key frames review the gauze soaked deep
/// red in the opening and the pad in the forceps a red brown between iodine and blood.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("liquids"), TestCategory("tool_gauze"), TestCategory("tool_cotton_pad"),
 TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class SwabCapacityTest {
    // Temporary local budget overrides, measured in a cloud container: recorded CPU overruns remain measured;
    // functional assertions still fail normally.
    private static readonly BudgetAllowance BleedingOverride = new("gauze packed into a bleeding opening exceeds the recorded CPU budget", 8.5f);
    private static readonly BudgetAllowance PickupOverride = new("the scenario start and picking up forceps and the pad exceed the recorded CPU budget", 9f);

    [TestCase]
    public async Task FullGauzeTakesNoMoreBloodButStillPresses() {
        var session = await ToolSession.Start("appendectomy", "gauze_full");
        var driver = session.Driver;
        var patient = driver.Patient;
        var body = driver.Body;
        var at = new Vector2(0.5f, 0.62f);
        SurgeryState.SkinIsCut(patient, at - new Vector2(0.1f, 0f), at + new Vector2(0.1f, 0f), 1f);
        await Frames.Seconds(1f);
        var vessel = SurgeryState.VesselBleeds(patient, at, body.CavityDepth() * 0.5f);
        SurgeryState.CavityHoldsBlood(patient, 100f);

        var gauze = (await driver.PlayerRequestsItem("gauze"))!;
        var on = driver.SitePoint(at);
        await driver.PlayerWalksTo(on);
        await driver.PlayerReaches(on);
        SurgeryDriver.Use();
        var full = await Frames.Until(() => gauze.Room <= 0.01f, 10f);
        AssertBool(full).OverrideFailureMessage($"packed into a pool of blood, the gauze fills up ({gauze.Ml:0.0} of {gauze.Def.Volume} ml)")
            .IsTrue();
        AssertFloat(gauze.Contents.GetValueOrDefault("blood")).OverrideFailureMessage("all of it is blood")
            .IsEqualApprox(gauze.Def.Volume, 0.01f);
        var left = patient.CavityBloodMl;
        await Frames.Seconds(2f);
        AssertFloat(patient.CavityBloodMl)
            .OverrideFailureMessage($"full, it takes no more blood out of the opening ({left:0.0} -> {patient.CavityBloodMl:0.0} ml)")
            .IsGreaterEqual(left);
        AssertFloat(vessel.Bleeding).OverrideFailureMessage($"but it still presses on the vessel: {driver.Bleeders()}").IsLess(0.01f);
        AssertFloat(vessel.SincePressed).OverrideFailureMessage("pressed right now, not held by earlier pressure").IsLess(0.5f);
        AssertFloat(driver.Me.Hands[driver.Me.Active].Blood).OverrideFailureMessage("the soaked gauze bloodies the glove holding it")
            .IsGreater(0f);
        AssertBool(Shows(gauze, SurgicalTool.BloodColor)).OverrideFailureMessage($"full of blood, it's blood red ({Albedos(gauze)})").IsTrue();
        await driver.Capture("full", "the gauze packed in the opening soaked through deep red, the vessel not bleeding");
        SurgeryDriver.Use(false);
        await driver.PlayerPutsDown();
        await session.Finish(BleedingOverride);
    }

    [TestCase]
    public async Task APadDippedInIodineFillsWithBloodAndTakesTheirMixedColor() {
        var session = await ToolSession.Start("appendectomy", "pad_iodine_blood");
        var driver = session.Driver;
        var patient = driver.Patient;
        var pad = SurgeryState.ToolIsOnTray(driver.Surgery, "cotton_pad");
        await Frames.Physics(10);
        var dish = driver.FreeTools("iodine_dish")[0];
        SurgeryState.ToolHolds(driver.Surgery, dish, "iodine", 30f);
        SurgeryState.ToolHolds(driver.Surgery, dish, "blood", 10f);
        var spot = new Vector2(0.5f, 0.5f);
        SurgeryState.SkinIsBloody(patient, spot, 0.3f);

        var forceps = (await driver.PlayerRequestsItem("forceps"))!;
        await driver.PlayerWalksTo(pad.GlobalPosition);
        await driver.PlayerWorksAt(pad.GlobalPosition, 0, 0.2f);
        AssertBool(driver.Surgery.Tools.CarriedBy(forceps) == pad).OverrideFailureMessage("the forceps took the pad").IsTrue();
        // A short dip into iodine with blood mixed in: the pad takes both in their share and still has room.
        await driver.PlayerWalksTo(dish.GlobalPosition);
        await driver.PlayerWorksAt(dish.Middle(), 0, 0.3f);
        var dipped = pad.Contents.GetValueOrDefault("blood") / pad.Ml;
        AssertBool(pad.Ml > 0.5f && pad.Room > 1f)
            .OverrideFailureMessage($"a short dip soaks the pad part way ({pad.Ml:0.0} of {pad.Def.Volume} ml)").IsTrue();
        AssertFloat(dipped).OverrideFailureMessage($"blood comes along in its share of the dish ({dipped:0.00})").IsEqualApprox(0.25f, 0.01f);
        AssertFloat(pad.BloodExposure).OverrideFailureMessage("the blood it took counts toward bloodying the hand holding it").IsGreater(0f);
        await driver.PlayerWalksTo(driver.SitePoint(spot));
        // Rows across the blood until the pad is full. Wiping leaves its iodine on the skin, so blood keeps filling the
        // room that frees: it ends full only while it's still on fresh blood.
        for (var row = 0; row < 10 && pad.Room > 0.5f; row++) {
            var v = spot.Y - 0.09f + (row * 0.02f);
            await driver.PlayerWorksAlong([driver.SitePoint(new Vector2(spot.X - 0.1f, v)), driver.SitePoint(new Vector2(spot.X + 0.1f, v))], 0, 0.06f);
        }
        var iodine = pad.Contents.GetValueOrDefault("iodine");
        var blood = pad.Contents.GetValueOrDefault("blood");
        AssertFloat(iodine + blood).OverrideFailureMessage($"iodine and blood together fill the pad ({iodine:0.0} + {blood:0.0} ml)")
            .IsEqualApprox(pad.Def.Volume, 0.5f);
        AssertBool(iodine > 1f && blood > 1f).OverrideFailureMessage($"it holds both ({iodine:0.0} ml iodine, {blood:0.0} ml blood)").IsTrue();
        var mix = SurgicalTool.IodineColor.Lerp(SurgicalTool.BloodColor, blood / (iodine + blood));
        AssertBool(Shows(pad, mix)).OverrideFailureMessage($"it takes the color of their mix ({Albedos(pad)}, mix {mix})").IsTrue();
        await session.CaptureAt("mixed", "the pad in the forceps a red brown between iodine and blood, over the bloody skin", pad.GlobalPosition, 0.15f);
        await driver.PlayerPutsDown();
        await session.Finish(PickupOverride);
    }

    /// <summary>Every part of <paramref name="tool"/>'s model is close to <paramref name="color"/>.</summary>
    private static bool Shows(SurgicalTool tool, Color color) =>
        Colors(tool).All(albedo => new Vector3(albedo.R - color.R, albedo.G - color.G, albedo.B - color.B).Length() < 0.05f);

    private static string Albedos(SurgicalTool tool) => string.Join(", ", Colors(tool));

    private static List<Color> Colors(SurgicalTool tool) => [
        .. tool.FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>()
            .SelectMany(mesh => Enumerable.Range(0, mesh.GetSurfaceOverrideMaterialCount()).Select(mesh.GetSurfaceOverrideMaterial))
            .OfType<ShaderMaterial>()
            .Select(material => material.GetShaderParameter("albedo").AsColor()),
    ];
}
