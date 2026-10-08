namespace Scalpel.Tests.Support;

/// <summary>The syringe, IV catheter and sedation views.</summary>
public partial class Screenshot
{
    private static readonly string[] AimedFirst = ["vial", "dish", "drip"];

    private async Task SyringeViews()
    {
        var bench = SyringeBench.Create();
        await bench.Start();
        var me = bench.Surgery.LocalSurgeon!;
        var zoomedIn = Surgeon.ZoomFov.Length - 1;
        foreach (var @case in SyringeBench.Cases.Where(@case => _only.Length == 0 || @case.Name == _only))
        {
            await bench.Stage(@case);
            me.Zoom = zoomedIn;
            if (AimedFirst.Contains(@case.Target))
            {
                // Aimed zoomed in, the hands see-through and the camera at the eyes. Then Use tool puts the needle in
                // and the needle view frames it (the patient and surgeon cases are only shown with it in).
                await SyringeBench.Release();
                me.Zoom = zoomedIn;
                await Frames(40);
                await Shot($"40_{@case.Name}_0_aimed");
                await SyringeBench.Press();
            }
            await Frames(40);
            await Shot($"40_{@case.Name}_1_needle_in");
            var notches = Math.Abs(@case.Notches);
            for (var i = 0; i < notches; i++)
            {
                if (i == notches / 2)
                {
                    await Shot($"40_{@case.Name}_2_halfway");
                }
                await SyringeBench.Notch(@case.Notches > 0);
            }
            await Frames(10);
            await Shot($"40_{@case.Name}_3_done");
            await SyringeBench.Release();
            me.Zoom = 0;
            if (@case.Name == "vial_pull")
            {
                // Held up to read, the printed scale toward the eyes.
                PlayerInput.Action(InputActions.Inspect);
                await Frames(30);
                await Shot("41_syringe_held_up");
                PlayerInput.Action(InputActions.Inspect, false);
            }
        }
        foreach (var (name, miss) in SyringeBench.CatheterCases.Where(@case => _only.Length == 0 || @case.Name == _only))
        {
            await CatheterViews(bench, name, miss);
        }
        if (_only.Length == 0 || _only == "sedation")
        {
            await Sedation(bench);
        }
    }

    /// <summary>The IV catheter aimed, in (from the side, the hands faded), the line from the stand and the dressing
    /// close up.</summary>
    private async Task CatheterViews(SyringeBench bench, string name, float miss)
    {
        var me = bench.Surgery.LocalSurgeon!;
        await bench.StageCatheter(miss);
        me.Zoom = Surgeon.ZoomFov.Length - 1;
        await Frames(40);
        await Shot($"42_{name}_1_aimed");
        var view = me.NeedleView(bench.Catheter!);
        await SyringeBench.Press();
        // Once it's in, the catheter is used up and the hands go solid: look at it from the side, as a syringe's needle
        // view would, the hands faded.
        var camera = NewCamera();
        camera.Fov = Surgeon.ZoomFov[^1];
        camera.GlobalTransform = view;
        await Frames(30);
        foreach (var hand in me.Hands)
        {
            hand.SetSeeThrough(Surgeon.ZoomSeeThrough);
        }
        await Shot($"42_{name}_2_in");
        foreach (var hand in me.Hands)
        {
            hand.SetSeeThrough(0f);
        }
        // The tubing from the stand's drip chamber down to the arm.
        var stand = bench.Surgery.Tools.DripBag()!.TipPosition();
        var arm = bench.VeinPoint();
        var middle = stand.Lerp(arm, 0.5f);
        camera.Fov = 60f;
        camera.GlobalTransform = new Transform3D(Basis.Identity,
            middle + ((arm - stand).Cross(Vector3.Up).Normalized() * 1.1f) + (Vector3.Up * 0.4f)).LookingAt(middle);
        await Shot($"42_{name}_3_line");
        // Close up on the catheter taped to the arm: film, hub, tape and the tubing leaving it.
        var dressing = bench.Surgery.Room.IvLine.Dressing!;
        camera.Fov = 35f;
        camera.GlobalTransform = new Transform3D(Basis.Identity, dressing.GlobalTransform * new Vector3(-0.03f, 0.13f, 0.07f))
            .LookingAt(dressing.GlobalTransform * new Vector3(-0.03f, 0f, 0f), dressing.GlobalBasis.X);
        foreach (var hand in me.Hands)
        {
            hand.Visible = false;
        }
        await Shot($"42_{name}_4_dressing");
        foreach (var hand in me.Hands)
        {
            hand.Visible = true;
        }
        camera.QueueFree();
        me.Camera.Current = true;
        me.Zoom = 0;
    }

    /// <summary>A sedated surgeon at the table: afterimages behind a moving hand and a blurred view (43), too much and
    /// the view darkens (44), twice the dose and they lie on the floor, looking at the table (45). Then the partner
    /// knocked out, seen from the room (46).</summary>
    private async Task Sedation(SyringeBench bench)
    {
        var surgery = bench.Surgery;
        var me = surgery.LocalSurgeon!;
        var status = me.Status;
        var right = 0.2f * status.WeightKg;
        bench.PlacePartner(SyringeBench.PartnerPark, 0f);
        PlayerInput.Action(InputActions.Crouch, false);
        me.GlobalTransform = surgery.Room.SpawnTransform(0);
        me.Pitch = -0.8f;
        for (var i = 0; i < 2; i++)
        {
            me.Hands[i].LocalTarget = new Vector3(i == 0 ? -0.17f : 0.17f, 1.18f, -0.45f);
        }
        status.Drugs.Clear();
        status.Administer("diazepam", right);
        await Frames(200);
        // The hand keeps sweeping across while the shot is taken: rendering here is slow, a hand at rest leaves no
        // trail.
        void Sweep() => me.Hands[1].LocalTarget = me.Hands[1].LocalTarget with
        {
            X = 0.17f + (0.15f * Mathf.Sin(Time.GetTicksMsec() * 0.006f)),
        };
        GetTree().PhysicsFrame += Sweep;
        await Frames(30);
        await Shot("43_sedated_trail");
        GetTree().PhysicsFrame -= Sweep;
        status.Drugs.Clear();
        status.Administer("diazepam", right * 1.8f);
        await Frames(200);
        await Shot("44_overdose");
        status.Administer("diazepam", right);
        await Frames(240);
        await Shot("45_knocked_out");
        status.Administer("flumazenil", 0.01f * status.WeightKg);
        await Frames(200);
        var partner = bench.Partner!;
        bench.PlacePartner((surgery.Patient.GlobalPosition * new Vector3(1f, 0f, 1f)) + new Vector3(0f, me.GlobalPosition.Y, 1.2f), 0f);
        partner.FallSide = 1f;
        await Frames(60);
        for (var i = 0; i < 2; i++)
        {
            partner.Hands[i].Target = partner.ToGlobal(Surgeon.LyingHand + new Vector3(-0.25f * i, 0f, 0f));
        }
        var camera = NewCamera();
        camera.GlobalPosition = partner.ToGlobal(new Vector3(-0.8f, 1.7f, -0.8f));
        camera.LookAt(partner.ToGlobal(new Vector3(-0.8f, 0.2f, 0f)));
        await Frames(10);
        await Shot("46_partner_down");
        camera.QueueFree();
        me.Camera.Current = true;
    }
}
