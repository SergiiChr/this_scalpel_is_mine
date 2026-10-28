namespace Scalpel.Tests.Support;

/// <summary>A neutral, orthographic studio for deterministic model key frames.</summary>
internal sealed class ModelStudio {
    private readonly Camera3D _camera;

    public Node3D Root { get; }

    public ModelStudio(float size) {
        FrameBudget.RenderingOff();
        Root = new Node3D();
        Frames.Root.AddChild(Root);
        var environment = new Godot.Environment {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color("bfcac7"),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color("e3eeef"),
            AmbientLightEnergy = 0.6f,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        Root.AddChild(new WorldEnvironment { Environment = environment });
        Root.AddChild(new DirectionalLight3D {
            RotationDegrees = new Vector3(-38f, -35f, 0f),
            LightEnergy = 1.1f,
            LightColor = new Color("fff2e0"),
        });
        Root.AddChild(new DirectionalLight3D {
            RotationDegrees = new Vector3(-20f, 145f, 0f),
            LightEnergy = 0.45f,
            LightColor = new Color("d6e7ff"),
        });
        _camera = new Camera3D {
            Projection = Camera3D.ProjectionType.Orthogonal,
            Size = size,
            Current = true,
            Near = 0.001f,
        };
        Root.AddChild(_camera);
    }

    public async Task Capture(Node3D model, string folder, string name, string description, float centerZ) {
        if (!KeyFrames.Wanted()) {
            return;
        }
        var at = model.ToGlobal(new Vector3(0f, 0f, centerZ));
        _camera.GlobalPosition = at + model.GlobalBasis * new Vector3(0.55f, 0.4f, 0.18f);
        _camera.LookAt(at, model.GlobalBasis.Y);
        await KeyFrames.SaveViewport(KeyFrames.Folder(folder).PathJoin(name + ".png"), description);
    }

    public async Task Finish() {
        Root.QueueFree();
        await Frames.NextProcess();
        RenderingServer.RenderLoopEnabled = true;
    }
}
