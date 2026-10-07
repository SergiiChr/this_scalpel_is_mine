namespace Scalpel.Tests.Tools;

/// <summary>The scalpel as a player uses it: asked for and taken off the tray, a light 5 cm cut along the blade's edge,
/// the opening it leaves, and the scalpel put back on the tray.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("tool_scalpel"), TestCategory("tissue_modification"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class ScalpelTest
{
    private const float Length = 0.05f;

    [TestCase]
    public async Task ScalpelPickupFiveCentimeterCutAndTableDrop()
    {
        RenderingServer.RenderLoopEnabled = false;
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var patient = driver.Patient;
        var body = driver.Body;
        // Asleep, so the cut doesn't make them flinch: the setting, not what's tested.
        SurgeryState.PatientIsAsleep(patient);
        var scalpel = (await driver.PlayerRequestsItem("scalpel"))!;
        AssertObject(driver.Me.HeldTool(driver.Me.Active)).OverrideFailureMessage("PlayerRequestsItem(scalpel) puts it in hand")
            .IsEqual(scalpel);
        var blade = (Node3D)scalpel.FindChild("Blade", true, false);
        var handle = (Node3D)scalpel.FindChild("Handle", true, false);
        var bladeRest = handle.GlobalTransform.AffineInverse() * blade.GlobalTransform;
        var activeFrames = 0;
        var bladeDrift = 0f;
        // Every frame the blade works, how far it moved on its handle.
        void Measure()
        {
            var hand = driver.Me.Hands[scalpel.Slot];
            if (scalpel.State != ToolState.Held || !ToolActions.InUse(scalpel.Def.Action, hand.Lowered, hand.Trigger, hand.Level))
            {
                return;
            }
            activeFrames++;
            var relative = handle.GlobalTransform.AffineInverse() * blade.GlobalTransform;
            bladeDrift = Mathf.Max(bladeDrift, relative.Origin.DistanceTo(bladeRest.Origin));
        }
        Frames.Tree.ProcessFrame += Measure;

        var from = new Vector2(0.4f, 0.45f);
        var to = from + new Vector2(body.MetersToUv(Length), 0f);
        var start = driver.SitePoint(from);
        var finish = driver.SitePoint(to);
        var middle = (start + finish) * 0.5f;
        KeyFrames? shots = null;
        if (KeyFrames.Wanted())
        {
            shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(driver.Surgery, "scalpel");
            driver.OnKeyFrame = async keyFrame => AssertBool(await shots.CaptureAt(keyFrame, middle, 0.18f))
                .OverrideFailureMessage($"saved scalpel key frame {keyFrame}").IsTrue();
        }
        await driver.PlayerWalksTo(middle);
        await driver.PlayerTurnsBlade(finish - start);
        await driver.PlayerReaches(start);
        await driver.SetLevel(1);
        await driver.Capture("untouched");
        driver.Budget.Clear();
        driver.Note("presses the scalpel into skin");
        SurgeryDriver.Use();
        await Frames.Seconds(0.3f);
        driver.Note("draws the first half of the scalpel cut");
        await driver.PlayerSweepsTo(middle);
        await driver.Capture("cutting");
        driver.Note("draws the second half of the scalpel cut");
        await driver.PlayerSweepsTo(finish);
        driver.Note("releases the scalpel from skin");
        SurgeryDriver.Use(false);
        await Frames.Physics(3);
        Frames.Tree.ProcessFrame -= Measure;
        AssertInt(activeFrames).OverrideFailureMessage("blade stability is measured throughout an actual cutting stroke").IsGreater(60);
        AssertFloat(bladeDrift)
            .OverrideFailureMessage($"the blade stays fixed to its handle while cutting ({bladeDrift * 1000f:0.000} mm drift)")
            .IsLess(0.000001f);
        await driver.Capture("released");
        var cuts = patient.Wounds.Where(wound => wound.MadeBySurgeon && wound.Kind == WoundKind.Cut).ToList();
        AssertInt(cuts.Count).OverrideFailureMessage("one stroke makes one cut").IsEqual(1);
        var cut = cuts[0];
        AssertFloat(body.UvToMeters(cut.LengthUv)).OverrideFailureMessage("a light stroke along 5 cm cuts 5 cm")
            .IsEqualApprox(Length, 0.005f);
        AssertBool(cut.Depth < Wound.MuscleDepth && !body.IsOpen(cut.Midpoint)).OverrideFailureMessage("a light cut stays in the skin")
            .IsTrue();
        AssertFloat(Opening(body, from, to)).OverrideFailureMessage("the skin is open along the cut and closed past its ends")
            .IsEqualApprox(Length, 0.01f);

        driver.Note("returns the scalpel to the tray");
        await driver.PlayerPutsDown();
        AssertThat(scalpel.State).OverrideFailureMessage("the scalpel is put down").IsEqual(ToolState.Free);
        AssertBool(driver.LiesOnTray(scalpel)).OverrideFailureMessage($"the scalpel lies on the instrument tray ({scalpel.Middle()})")
            .IsTrue();
        driver.Budget.Check(shots is not null);
        shots?.End();
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }

    /// <summary>Meters of skin open along the line from <paramref name="from"/> to <paramref name="to"/>, looked for a
    /// centimeter past both ends.</summary>
    private static float Opening(PatientBody body, Vector2 from, Vector2 to)
    {
        var step = body.MetersToUv(0.001f);
        var along = (to - from).Normalized();
        var past = body.MetersToUv(0.01f);
        var steps = (int)((from.DistanceTo(to) + (past * 2f)) / step);
        var open = Enumerable.Range(0, steps)
            .Count(i => body.Tissue.IsOpen(from - (along * past) + (along * step * i), TissueDepth.Skin));
        return body.UvToMeters(open * step);
    }
}
