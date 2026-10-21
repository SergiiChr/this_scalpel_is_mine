namespace Scalpel.Tests.Support;

/// <summary>Detailed running-suture checks performed during the hand-stitch scenario, before its closure objective completes.</summary>
public static class SutureChecks {
    public static async Task CloseForearm(SurgeryDriver driver) {
        var session = await ToolSession.Attach(driver, "needle/forearm_skin",
            untouchedDescription: "the cleaned forearm cut ready for stitching, no thread yet");
        var patient = driver.Patient;
        var body = driver.Body;
        var tissue = body.Tissue;
        var wound = patient.Wounds[0];
        var needle = (await driver.PlayerRequestsItem("needle"))!;
        AssertFloat((driver.Me.AimPoint() - needle.TipPosition()).Slide(Vector3.Up).Length())
            .OverrideFailureMessage("the aim point is straight under the needle tip").IsLess(0.0001f);
        AssertObject(needle.FindChild("Thread", true, false)).OverrideFailureMessage("an idle needle has no placeholder thread")
            .IsNull();
        var jawA = (Node3D)needle.FindChild("JawA", true, false);
        var jawB = (Node3D)needle.FindChild("JawB", true, false);
        AssertFloat(Mathf.Abs(jawA.Transform.Basis.GetEuler().Y) + Mathf.Abs(jawB.Transform.Basis.GetEuler().Y))
            .OverrideFailureMessage("the holder stays closed around its needle").IsLess(0.001f);
        var sutures = body.Site.GetNode("Sutures");
        AssertInt(sutures.GetChildCount()).OverrideFailureMessage("no live thread is drawn before the needle is anchored").IsEqual(0);
        await driver.PlayerWalksTo(driver.SitePoint(wound.Midpoint));
        await driver.PlayerReaches(driver.SitePoint(wound.Midpoint));
        AssertInt(sutures.GetChildCount()).OverrideFailureMessage("hovering the needle over a wound does not create thread")
            .IsEqual(0);
        AssertFloat(Mathf.Abs(needle.GlobalBasis.Z.Dot(Vector3.Up)))
            .OverrideFailureMessage("the loaded needle holder rests nearly horizontal").IsLess(0.35f);
        AssertFloat((driver.Me.AimPoint() - needle.TipPosition()).Slide(Vector3.Up).Length())
            .OverrideFailureMessage("the horizontal pose keeps the aiming point under the sharp tip").IsLess(0.0001f);
        await session.CaptureView("needle_ready", "the loaded needle holder nearly horizontal over the wound, the aim dot on the skin right under its tip, no thread yet");
        AssertBool(await driver.PlayerThreads(wound, TissueDepth.Skin, captureProgress: true, checkAim: true))
            .OverrideFailureMessage("holes go in beside the cut, each where the aim dot was").IsTrue();
        var thread = needle.Suture.Thread;
        var info = tissue.Thread(thread)!;
        AssertInt(info.Springs.Count).OverrideFailureMessage("one thread runs through every hole, a span between each two")
            .IsEqual(info.Anchors.Count - 1);
        var drawn = body.ThreadNode(thread)!;
        var live = drawn.GetNode<MeshInstance3D>("Live");
        AssertObject(live.Mesh).OverrideFailureMessage("the newest hole has a live strand").IsNotNull();
        AssertBool(live.GetAabb().Grow(0.001f).HasPoint(body.Site.ToLocal(needle.TipPosition())))
            .OverrideFailureMessage("the live strand ends at the needle tip").IsTrue();
        // Thirty artificial redraws in one frame verify accumulated sub-threshold movement, outside gameplay timing.
        await driver.Unbudgeted(() => {
            var pose = needle.GlobalTransform;
            for (var frame = 0; frame < 30; frame++) {
                needle.GlobalPosition += Vector3.Right * 0.0001f;
                body.UpdateSutures();
                AssertFloat(body.DrawnThread(thread)!.LivePath[^1].DistanceTo(body.Site.ToLocal(needle.TipPosition())))
                    .OverrideFailureMessage("slow movement keeps the strand within the redraw threshold").IsLessEqual(0.00021f);
            }
            needle.GlobalTransform = pose;
            body.UpdateSutures();
            return Task.CompletedTask;
        });
        var loose = body.DrawnThread(thread)!;
        var loosePath = loose.Routes[0];
        AssertFloat(loose.PressureAmount).OverrideFailureMessage("loose thread puts no visible compression around its holes")
            .IsEqual(0f);
        AssertObject(drawn.GetNode<MeshInstance3D>("StartKnot").Mesh)
            .OverrideFailureMessage("the beginning of the running thread has a compact anchor knot").IsNotNull();
        AssertThreadClearsSkin(body, loosePath, "loose exposed thread");
        AssertFloat(wound.Closure).OverrideFailureMessage("the thread as it comes leaves the cut open").IsLess(0.99f);
        var looseGap = tissue.GapAlong(wound.Points, 0.03f, TissueDepth.Skin);
        await driver.PlayerPullsThread("closed");
        // Live, the thread only pulls its holes: the edges between them meet once it's tied off (below).
        AssertFloat(tissue.GapAlong(wound.Points, 0.03f, TissueDepth.Skin))
            .OverrideFailureMessage("pulled until it reads closed, the thread draws the edges together").IsLess(looseGap);
        var tight = body.DrawnThread(thread)!;
        AssertFloat(PathLength(tight.Routes[0])).OverrideFailureMessage("tightening takes visible slack out of the thread")
            .IsLess(PathLength(loosePath));
        AssertFloat(tight.PressureAmount).OverrideFailureMessage("closing pressure appears around every puncture").IsGreater(0f);
        AssertInt(tight.PressureHoles).OverrideFailureMessage("every pressured hole has a skin crease sprite")
            .IsEqual(info.Anchors.Count);
        var marks = drawn.GetNode<MultiMeshInstance3D>("Pressure");
        AssertBool(marks.Visible).OverrideFailureMessage("the pressure sprites render while the thread holds").IsTrue();
        AssertInt(marks.Multimesh.InstanceCount).OverrideFailureMessage("the renderer has one pressure sprite per puncture")
            .IsEqual(info.Anchors.Count);
        AssertThreadClearsSkin(body, tight.Routes[0], "tight exposed thread");
        var map = body.WoundMap;
        await driver.PlayerTiesOff(onTied: () => {
            AssertFloat(map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, wound.Midpoint))
                .OverrideFailureMessage("tying off removes standing blood from this wound seam at the actual closure event")
                .IsLess(0.12f);
        });
        await Frames.Seconds(1f);
        AssertBool(patient.SutureDone(thread) && needle.Suture.Thread == 0).OverrideFailureMessage("a long hold ties the thread off")
            .IsTrue();
        AssertBool(body.Blood.Sources.Any(source => source.Rate > 0f && source.Uv.IsEqualApprox(wound.BleedPoint)))
            .OverrideFailureMessage("one second after tie-off the closed wound supplies no fresh blood, while existing surface liquid may cross it")
            .IsFalse();
        AssertFloat(tissue.GapAlong(wound.Points, 0.03f, TissueDepth.Skin))
            .OverrideFailureMessage("the tied off thread holds the edges together").IsLess(TissueSim.OpenGap);
        AssertFloat(wound.Closure).OverrideFailureMessage("so the cut is closed along its whole length").IsGreater(0.99f);
        AssertInt(tissue.Severed.Count(s => tissue.CutDepth(s) >= TissueDepth.Skin))
            .OverrideFailureMessage("no split edge is left along the seam").IsEqual(0);
        AssertFloat(tissue.SutureLip.Max())
            .OverrideFailureMessage("the pressed edges rise into a lip instead of passing through each other").IsGreater(0.0005f);
        // Retained surface blood can flow across a closed seam. Check only recessed fluid associated
        // with this wound; another wound can still own a cut-pool mesh elsewhere on the site.
        AssertFloat(wound.Bleeding).OverrideFailureMessage("the tied wound has stopped bleeding").IsEqual(0f);
        AssertFloat(wound.PooledMl).OverrideFailureMessage("no blood remains held inside the closed wound").IsEqual(0f);
        var localPool = body.Blood.Pools.Any(pool => pool.Fill > 0f && pool.Uv.IsEqualApprox(wound.BleedPoint));
        AssertBool(localPool).OverrideFailureMessage("the closed wound has no recessed blood pool of its own").IsFalse();
        var seam = map.Value(WoundMap.Layer.Wounds, WoundMap.Cut, wound.Midpoint);
        AssertFloat(seam).OverrideFailureMessage("a tied wound keeps a visible incision line").IsGreater(0.06f);
        AssertFloat(seam).OverrideFailureMessage("the incision line is healed-looking rather than an open groove").IsLess(0.16f);
        AssertFloat(map.Value(WoundMap.Layer.Seams, WoundMap.ClosedSeam, wound.Midpoint))
            .OverrideFailureMessage("the sewn incision has an explicit seam mask independent of closure quality").IsGreater(0.9f);
        AssertBool(patient.Flags.ContainsKey("neat_closure")).OverrideFailureMessage("a closed, tied off thread is a neat closure")
            .IsTrue();
        AssertObject(live.Mesh).OverrideFailureMessage("tying off releases the live end from the needle").IsNull();
        AssertObject(drawn.GetNode<MeshInstance3D>("StartKnot").Mesh)
            .OverrideFailureMessage("the tied thread keeps its starting knot").IsNotNull();
        AssertObject(drawn.GetNode<MeshInstance3D>("EndKnot").Mesh)
            .OverrideFailureMessage("the tied thread ends in a second visible knot").IsNotNull();
        var routes = body.DrawnThread(thread)!.Routes;
        AssertInt(routes.Count).OverrideFailureMessage("the running thread alternates exposed and subcutaneous spans")
            .IsEqual((info.Springs.Count + 1) / 2);
        for (var i = 0; i < routes.Count; i++) {
            AssertThreadClearsSkin(body, routes[i], $"exposed span {i}");
        }
        session.EndCaptures();
    }

    private static void AssertThreadClearsSkin(PatientBody body, Vector3[] path, string label) {
        for (var i = 1; i < path.Length - 1; i++) {
            AssertFloat(path[i].Y).OverrideFailureMessage($"{label} stays above the skin at sample {i}")
                .IsGreaterEqual(body.SkinHeight(SiteUv(body, path[i])) + PatientBody.SutureRadius);
        }
    }

    /// <summary>Where a point drawn on the site (site-local) lies in site uv.</summary>
    private static Vector2 SiteUv(PatientBody body, Vector3 local) =>
        new((local.X / body.SiteSize.X) + 0.5f, (local.Z / body.SiteSize.Y) + 0.5f);

    private static float PathLength(Vector3[] path) =>
        Enumerable.Range(1, Math.Max(path.Length - 1, 0)).Sum(i => path[i - 1].DistanceTo(path[i]));
}
