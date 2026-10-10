namespace Scalpel.Tests.Liquids;

/// <summary>A normal injection ends on Use tool release, before moving away: one bead at the puncture, no tear or extra
/// pain. Checks all syringe sizes on skin and a vein; key frames show the ready needle, injection, release and the bead
/// once the needle moved away.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("smoke"), TestCategory("liquids"), TestCategory("tool_syringe_3"),
 TestCategory("tool_syringe_10"), TestCategory("tool_syringe_50"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class SyringeWithdrawalTest
{
    private static List<MeshInstance3D> Beads(Node body) =>
        [.. body.FindChildren("BloodBead*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>()];

    [TestCase(Timeout = Limits.Slow)]
    public async Task ReleaseWithdrawsWithoutTraumaAndLeavesBloodAtThePuncture()
    {
        GD.Seed(42);
        var bench = SyringeBench.Create();
        await bench.Start(false, false);
        var surgery = bench.Surgery;
        var patient = surgery.Patient;
        var body = patient.Body;
        var me = surgery.LocalSurgeon!;
        var budget = new FrameBudget();
        KeyFrames? shots = null;
        RenderingServer.RenderLoopEnabled = false;
        if (KeyFrames.Wanted())
        {
            shots = new KeyFrames();
            bench.AddChild(shots);
            shots.Begin(surgery, "syringe_withdrawal");
        }
        foreach (var target in (string[])["skin", "vein"])
        {
            foreach (var size in (string[])["syringe_10", "syringe_3", "syringe_50"])
            {
                var label = $"{target}_{size}";
                var capture = shots is not null && size == "syringe_10";
                await bench.Stage(new SyringeCase(label, target, size, 1f, 0), false);
                var syringe = bench.Syringe!;
                var before = Beads(body);
                if (capture)
                {
                    await shots!.CaptureAt(label + "_ready", "the untouched injection site, the needle tip just above it", syringe.TipPosition(), 0.12f);
                }
                await SyringeBench.Press();
                await Frames.Physics(5);
                AssertObject(me.NeedleAnchor).OverrideFailureMessage(label + ": the needle is inserted").IsNotNull();
                var entered = me.NeedleAnchor ?? Vector3.Zero;
                await SyringeBench.Notch(false);
                AssertFloat(syringe.Ml).OverrideFailureMessage(label + ": the wheel injects the dose").IsEqual(0f);
                AssertInt(Beads(body).Count).OverrideFailureMessage(label + ": blood does not appear while the needle is in")
                    .IsEqual(before.Count);
                if (capture)
                {
                    await shots!.CaptureView(label + "_injected", "the needle in the skin while injecting, no blood yet");
                }
                var pain = patient.Vitals.Pain;
                var wounds = body.WoundMap.Snapshot(WoundMap.Layer.Wounds);
                // Capturing the injected view advances breathing: the puncture is measured on the skin as it is right
                // before release, not from a height kept from before the screenshot.
                var probe = body.Probe(entered);
                var puncture = probe.Zone == SiteZone.Site
                    ? body.UvToWorld(probe.Uv)
                    : entered with { Y = me.SurfaceBelow(entered).Y };
                budget.Resume();
                budget.Sample(label + ": releasing Use tool");
                await SyringeBench.Release(0);
                budget.Sample(label + ": creating the withdrawal bead");
                AssertObject(me.NeedleAnchor).OverrideFailureMessage(label + ": release frees the needle before another mouse move")
                    .IsNull();
                var after = Beads(body);
                AssertInt(after.Count).OverrideFailureMessage(label + ": release immediately creates one bead")
                    .IsEqual(before.Count + 1);
                if (after.Except(before).ToList() is not [var bead])
                {
                    continue;
                }
                AssertFloat(bead.GlobalPosition.DistanceTo(puncture))
                    .OverrideFailureMessage(label + ": the bead sits exactly on the skin at the injection point").IsLess(0.0005f);
                AssertFloat(patient.Vitals.Pain).OverrideFailureMessage(label + ": withdrawal adds no pain").IsEqual(pain);
                AssertBool(body.WoundMap.Snapshot(WoundMap.Layer.Wounds).SequenceEqual(wounds))
                    .OverrideFailureMessage(label + ": withdrawal adds no scratch").IsTrue();
                // Physics frame signals come before node updates; the process frame follows the first physics step.
                budget.Resume();
                budget.Sample(label + ": first withdrawal frame");
                await Frames.NextProcess();
                budget.Sample(label + ": needle withdrawn");
                AssertFloat(syringe.TipPosition().Y)
                    .OverrideFailureMessage(label + ": the needle clears the skin in the first release frame, without moving away")
                    .IsGreater(puncture.Y);
                AssertBool(me.NeedleTorn).OverrideFailureMessage(label + ": releasing does not trigger mishandling").IsFalse();
                if (capture)
                {
                    await shots!.CaptureAt(label + "_released", "just released: the needle out and clear, one blood bead on the skin at the puncture", bead.GlobalPosition, 0.12f);
                }
                var stuck = bead.Position;
                budget.Resume();
                for (var frame = 0; frame < 15; frame++)
                {
                    await bench.Steer(new Vector2(20f, 0f));
                    await Frames.Physics(1);
                    budget.Sample($"{label}: moving the released needle away, frame {frame}");
                }
                AssertBool(me.NeedleTorn).OverrideFailureMessage(label + ": moving away after release cannot tear the skin").IsFalse();
                AssertInt(Beads(body).Count).OverrideFailureMessage(label + ": moving away does not create another bead")
                    .IsEqual(after.Count);
                AssertBool(body.WoundMap.Snapshot(WoundMap.Layer.Wounds).SequenceEqual(wounds))
                    .OverrideFailureMessage(label + ": moving away adds no scratch").IsTrue();
                AssertThat(bead.Position).OverrideFailureMessage(label + ": the bead stays attached to the puncture").IsEqual(stuck);
                if (capture)
                {
                    await shots!.CaptureAt(label + "_moved_away", "the needle moved away: the bead stays at the puncture, no scratch", bead.GlobalPosition, 0.12f);
                }
            }
        }
        budget.Check(shots is not null, "Syringe withdrawal");
        shots?.End();
        await bench.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }
}
