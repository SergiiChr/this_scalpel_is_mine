namespace Scalpel.Tests.Tools;

/// <summary>
/// The IV bag as a player meets it: hung on the stand, ports down, its liquid inside the film up to the level its ml
/// says and the film as full as the bag (EmptyBag). A line in and the bag run dry, the film falls flat. A saline bag
/// lies spread flat on the tray (RestingFlat), still touching it; picked up it fills out, swept across it sways on its
/// rig and settles once the hand stops. Hung with "Swap IV bag" it fills the stand's bag again. With key
/// frames also the hung bag up close at each step, from its front and side, the bag lying on the tray and the new one
/// held in the hand. Review them for the film, print, ports and frosted hanger flange and seams reading as an IV bag,
/// the liquid inside the film (none when it's empty), the empty film thinner than the full one, the lying bag flat on
/// the tray, the bag held in the fist without going through the glove and hanging under its hook.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("liquids"), TestCategory("tool_saline_bag"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class IvBagTest
{
    /// <summary>How far from the hung bag the key frames look at it (meters).</summary>
    private const float View = 0.4f;
    /// <summary>Close enough to a bag in hand that the camera stays in front of the surgeon.</summary>
    private const float HeldView = 0.3f;
    /// <summary>Slack for the liquid's edges against the film's (meters).</summary>
    private const float Tolerance = 0.0005f;
    /// <summary>How far and fast the hand sweeps the bag across (meters, m/s): a brisk move.</summary>
    private const float SweepLength = 0.3f;
    private const float SweepSpeed = 0.6f;
    /// <summary>A swept bag's middle swings at least this far behind the hand (meters)...</summary>
    private const float SwingWhileCarried = 0.001f;
    /// <summary>...but a full bag is heavy and stiff: it doesn't whip about (meters).</summary>
    private const float SwingMost = 0.015f;
    /// <summary>Once the surgeon stands still, it settles back to within this (meters).</summary>
    private const float SwingSettled = 0.0002f;
    /// <summary>The parts that fall flat as the bag empties.</summary>
    private static readonly string[] FilmParts = ["Bag", "Label", "Level", "Frame"];

    [TestCase]
    public async Task HangsOnTheStandAndIsSwappedForANewBag()
    {
        RenderingServer.RenderLoopEnabled = false;
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        SurgeryState.PatientIsAsleep(driver.Patient);
        var surgery = driver.Surgery;
        var drip = surgery.Tools.DripBag()!;
        if (driver.FreeTools("saline_bag").Count == 0)
        {
            SurgeryState.ToolIsOnTray(surgery, "saline_bag");
            await Frames.Seconds(1f);
        }
        var bag = driver.FreeTools("saline_bag")[0];
        KeyFrames? shots = null;
        if (KeyFrames.Wanted())
        {
            shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(surgery, "iv_bag");
            driver.OnKeyFrame = async keyFrame => AssertBool(keyFrame switch
            {
                "lying" => await shots.CaptureAt(keyFrame, bag.Middle(), HeldView),
                "bag_in_hand" => await shots.CaptureFacing(keyFrame, bag.Middle(), TowardEyes(bag), HeldView, hands: true),
                _ => await shots.CaptureFacing(keyFrame, drip.Middle(), drip.GlobalBasis.Y, View),
            })
                .OverrideFailureMessage($"saved key frame {keyFrame}").IsTrue();
        }
        driver.Budget.Clear();

        AssertBool((-drip.GlobalBasis.Z.Normalized()).Y < -0.99f)
            .OverrideFailureMessage("the bag hangs on the stand with its ports straight down").IsTrue();
        AssertLiquidAtItsLevel(drip, "hung");
        var frame = (MeshInstance3D)drip.FindChild("Frame", true, false)!;
        AssertBool(Enumerable.Range(0, frame.GetSurfaceOverrideMaterialCount())
                .Any(i => frame.GetSurfaceOverrideMaterial(i) is BaseMaterial3D { Transparency: BaseMaterial3D.TransparencyEnum.Alpha }))
            .OverrideFailureMessage("the flange, seams and clear ports are see-through").IsTrue();
        await driver.Capture("hung");

        await driver.PlayerSetsIv();
        AssertBool(driver.Patient.IvWorking).OverrideFailureMessage("a working line is in").IsTrue();
        SurgeryState.IvBagIsEmpty(surgery);
        AssertBool(drip.LiquidPart("Level")!.Visible).OverrideFailureMessage("an empty bag shows no liquid").IsFalse();
        AssertFloat(Weight(drip, "Bag", "EmptyBag")).OverrideFailureMessage("an empty bag's film falls flat")
            .IsEqualApprox(1f, 0.001f);
        await driver.Capture("emptied");

        AssertBool(Weight(bag, "Bag", "RestingFlat") > 0.999f && Weight(bag, "Label", "RestingFlat") > 0.999f)
            .OverrideFailureMessage("a bag lying on the tray is spread flat").IsTrue();
        var gap = LowestFilm(bag) - LowestOfBox(bag);
        AssertFloat(Mathf.Abs(gap)).OverrideFailureMessage($"spread flat, it still lies on the tray ({gap * 1000f:0.0} mm off)")
            .IsLess(0.001f);
        await driver.Capture("lying");

        AssertObject(await driver.PlayerRequestsItem("saline_bag")).OverrideFailureMessage("the lying bag is in hand")
            .IsEqual(bag);
        await Frames.Seconds(0.5f);
        AssertFloat(Weight(bag, "Bag", "RestingFlat")).OverrideFailureMessage("picked up, it fills out again")
            .IsEqual(0f);
        await driver.Capture("bag_in_hand");

        var middleRest = Carrier(drip).Position;
        var hand = driver.Me.Hands[driver.Me.Active];
        var sweep = driver.PlayerSweepsTo(driver.Tip(hand) + (driver.Me.GlobalBasis.X * SweepLength), SweepSpeed);
        var swing = 0f;
        while (!sweep.IsCompleted)
        {
            swing = Mathf.Max(swing, (Carrier(bag).Position - middleRest).Length());
            await Frames.NextProcess();
        }
        await sweep;
        await Frames.Seconds(2f);
        var settled = (Carrier(bag).Position - middleRest).Length();
        AssertBool(swing > SwingWhileCarried && swing < SwingMost && settled < SwingSettled)
            .OverrideFailureMessage($"swept across, the bag sways ({swing * 1000f:0.0} mm) and settles once the hand "
                + $"stops ({settled * 1000f:0.00} mm)")
            .IsTrue();

        AssertBool(await driver.PlayerInteracts("Swap IV bag")).OverrideFailureMessage("the stand offers Swap IV bag")
            .IsTrue();
        AssertBool(bag.State == ToolState.Consumed && Mathf.IsEqualApprox(drip.Ml, SurgicalTool.DripFluid))
            .OverrideFailureMessage($"the held bag is hung in place of the empty one, full ({drip.Ml:0} ml)").IsTrue();
        AssertLiquidAtItsLevel(drip, "swapped");
        AssertFloat(Weight(drip, "Bag", "EmptyBag")).OverrideFailureMessage("the full bag's film fills out again")
            .IsEqualApprox(1f - (drip.Ml / drip.Def.Volume), 0.001f);
        await driver.Capture("swapped");

        driver.Budget.Check(shots is not null);
        shots?.End();
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }

    /// <summary>The liquid lies inside the film, at its bottom, as high as its share of what the bag holds. The film and
    /// the liquid are as flat as the bag is empty.</summary>
    private static void AssertLiquidAtItsLevel(SurgicalTool bag, string when)
    {
        var empty = 1f - (bag.Ml / bag.Def.Volume);
        AssertBool(FilmParts.All(part => Mathf.IsEqualApprox(Weight(bag, part, "EmptyBag"), empty)))
            .OverrideFailureMessage($"{when}: the film, print, seams and liquid are {empty:0.00} of the way to empty").IsTrue();
        var film = InBag(bag, (MeshInstance3D)bag.FindChild("Bag", true, false)!);
        var level = bag.LiquidPart("Level")!;
        var liquid = InBag(bag, level);
        var full = level.GetAabb().Size.Z;
        var inside = film.Grow(Tolerance).Encloses(liquid);
        AssertBool(level.Visible && inside && Mathf.Abs(liquid.Size.Z - (full * bag.Ml / bag.Def.Volume)) < Tolerance)
            .OverrideFailureMessage($"{when}: the liquid fills the film from the bottom to {bag.Ml:0} of "
                + $"{bag.Def.Volume:0} ml ({liquid.Size.Z * 100f:0.0} of {full * 100f:0.0} cm high, inside: {inside})")
            .IsTrue();
    }

    private static float Weight(SurgicalTool bag, string part, string shape)
    {
        var mesh = (MeshInstance3D)bag.FindChild(part, true, false)!;
        return mesh.GetBlendShapeValue(mesh.FindBlendShapeByName(shape));
    }

    /// <summary>The bone attachment that carries the liquid, swinging with the film's middle.</summary>
    private static Node3D Carrier(SurgicalTool bag) => (Node3D)bag.LiquidPart("Level")!.GetParent();

    /// <summary>The lowest point of the film as its blend shapes draw it (world height).</summary>
    private static float LowestFilm(SurgicalTool bag) =>
        DrawnMesh.Vertices((MeshInstance3D)bag.FindChild("Bag", true, false)!).Min(vertex => vertex.Y);

    /// <summary>The bottom of the bag's collision box, which lies on the tray (world height).</summary>
    private static float LowestOfBox(SurgicalTool bag) =>
        Enumerable.Range(0, 8).Min(i => (bag.GlobalTransform * bag.Bounds.GetEndpoint(i)).Y);

    /// <summary>The face of a bag in hand that's turned toward the surgeon's eyes.</summary>
    private static Vector3 TowardEyes(SurgicalTool bag)
    {
        var face = bag.GlobalBasis.Y;
        return face * Mathf.Sign(face.Dot(Surgery.Current!.LocalSurgeon!.Camera.GlobalPosition - bag.Middle()));
    }

    /// <summary>A part's box in the bag's own space.</summary>
    private static Aabb InBag(SurgicalTool bag, MeshInstance3D part) =>
        bag.GlobalTransform.AffineInverse() * part.GlobalTransform * part.GetAabb();
}
