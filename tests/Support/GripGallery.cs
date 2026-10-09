namespace Scalpel.Tests.Support;

/// <summary>
/// Renders every tool model held in a right hand, from both sides and from the holder's eyes, for checking grips.
/// Needs a real renderer: xvfb-run godot --path . --rendering-method gl_compatibility
/// res://tests/Support/GripGallery.tscn -- --out=/tmp/grips [--left] [--only=scalpel,needle] (just those models).
/// </summary>
public partial class GripGallery : Node3D
{
    private static readonly Vector3 Eye = new(0f, 1.62f, 0f);
    private static readonly Vector3 HandAt = new(0.17f, 1.05f, -0.42f);

    public override void _Ready() => this.Start(Render);

    private async Task Render()
    {
        var args = OS.GetCmdlineUserArgs();
        var output = args.FirstOrDefault(arg => arg.StartsWith("--out="))?.Split('=', 2)[1] ?? "user://grips";
        var only = args.FirstOrDefault(arg => arg.StartsWith("--only="))?.Split('=', 2)[1].Split(',') ?? [];
        var left = args.Contains("--left");
        var side = left ? -1f : 1f;
        DirAccess.MakeDirRecursiveAbsolute(output);
        Light();
        var body = new Node3D();
        AddChild(body);
        var hand = new SurgeonHand();
        body.AddChild(hand);
        hand.Build(left ? 0 : 1, Materials.ToonUnique(new Color(0.2f, 0.36f, 0.34f)));
        var camera = new Camera3D { Fov = 40f };
        AddChild(camera);
        camera.Current = true;
        var shoulder = new Vector3(0.19f * side, 1.4f, -0.08f);
        foreach (var def in Db.Tools.Values.DistinctBy(def => def.ModelName)
            .Where(def => only.Length == 0 || only.Contains(def.ModelName)))
        {
            var holder = new Node3D();
            AddChild(holder);
            var model = ModelSlot.InstantiateTool(def, holder);
            var animator = new ToolAnimator();
            animator.Setup(model, def.Action);
            animator.Animate(false, def.ModelName == "needle", 0f);
            var syringe = def.Action == "syringe";
            hand.Holding = true;
            hand.Grip = def.Grip;
            hand.Fit = Db.GripFit(def, left ? 0 : 1);
            hand.Target = new Vector3(HandAt.X * side, HandAt.Y, HandAt.Z);
            // A syringe half full, its scale turned to the eyes, the way the game holds it.
            hand.Press = syringe ? Surgeon.SyringePress + (def.Length * Surgeon.SyringeTravel * 0.5f) : float.NaN;
            hand.Tilt = syringe ? Surgeon.SyringeTilt : SurgeonHand.RestTilt;
            hand.Turn = syringe ? Surgeon.SyringeTurn * side : 0f;
            for (var i = 0; i < 30; i++)
            {
                if (syringe)
                {
                    hand.Twist = hand.TwistFacing(Eye - hand.GlobalPosition);
                }
                hand.UpdatePose(shoulder, 1f / 30f);
                holder.GlobalTransform = hand.GripTransform();
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            // Aim between the grip and the tip, so both the hand and the working end are in view.
            var middle = hand.GlobalPosition + hand.TipOffset(def.Length * 0.5f);
            (string Name, Vector3 At, float Fov)[] views =
            [
                ("side", middle + new Vector3(0.45f * side, 0.12f, 0.2f), 38f),
                ("thumb", middle + new Vector3(-0.45f * side, 0.12f, 0.2f), 38f),
                ("eyes", Eye, 20f),
            ];
            foreach (var (name, at, fov) in views)
            {
                camera.GlobalPosition = at;
                camera.Fov = fov;
                camera.LookAt(middle);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                GetViewport().GetTexture().GetImage().SavePng($"{output}/{def.ModelName}_{name}.png");
            }
            holder.QueueFree();
            hand.Twist = 0f;
        }
        GD.Print("grip_gallery: done");
        GetTree().Quit();
    }

    private void Light()
    {
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.16f, 0.17f, 0.18f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.5f, 0.5f, 0.5f),
            },
        });
        AddChild(new DirectionalLight3D { Rotation = new Vector3(-0.9f, 0.5f, 0f) });
    }
}
