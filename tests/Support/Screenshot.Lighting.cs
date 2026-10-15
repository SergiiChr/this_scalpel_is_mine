namespace Scalpel.Tests.Support;

public partial class Screenshot {
    /// <summary>Repeatable room views and uncapped, warmed whole-frame timings for lighting comparisons.
    /// Run the same command at each revision; software-rendered results compare cost, not GPU budget compliance.</summary>
    private async Task LightingViews(Surgery surgery) {
        // Request a consistent window; record the actual pixel dimensions below because desktop scaling can differ.
        DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        DisplayServer.WindowSetSize(new Vector2I(1920, 1080));
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = 0;
        await Frames(60);
        var sampleCount = Math.Max(30, int.Parse(Arg("lighting-frames", "120"), System.Globalization.CultureInfo.InvariantCulture));
        var warmup = Math.Max(30, int.Parse(Arg("lighting-warmup", "30"), System.Globalization.CultureInfo.InvariantCulture));
        var viewports = surgery.FindChildren("*", nameof(SubViewport), true, false).Cast<Viewport>()
            .Prepend(GetViewport()).Select(viewport => viewport.GetViewportRid()).ToArray();
        foreach (var viewport in viewports) {
            RenderingServer.ViewportSetMeasureRenderTime(viewport, true);
        }
        surgery.Hud.RemoveFilmGrain();
        if (Has("--lighting-baseline")) {
            // Reproduce main at 5876605 for A/B runs without rebuilding or changing the patient/camera fixture.
            foreach (var light in surgery.Room.GetChildren().OfType<SpotLight3D>()
                .Where(light => light.Name.ToString().StartsWith("CeilingLight"))) {
                light.Visible = false;
                surgery.Room.AddChild(new OmniLight3D {
                    Position = light.Position with { Y = 2.6f },
                    OmniRange = 9f,
                    LightColor = Materials.TheatreLight,
                    LightEnergy = 0.2f,
                });
            }
            surgery.Room.AddChild(new SpotLight3D {
                Position = new Vector3(0f, 2.72f, 0f),
                Rotation = new Vector3(-Mathf.Pi / 2f, 0f, 0f),
                SpotRange = 12f,
                SpotAngle = 70f,
                SpotAttenuation = 0.3f,
                LightEnergy = 0.55f,
                LightColor = Materials.TheatreLight,
                ShadowEnabled = true,
                ShadowBlur = 2.5f,
            });
        }
        var camera = NewCamera();
        var views = new (string Name, Vector3 From, Vector3 At)[] {
            ("room", new(-2.3f, 1.8f, 1.9f), new(0.1f, 1.25f, 0f)),
            ("table", new(-1.6f, 2.1f, 1.4f), new(0f, 0.9f, 0f)),
            ("ceiling", new(-2.1f, 1.3f, 1.7f), new(0f, 2.6f, 0f)),
        };
        var report = new List<string> { $"Renderer: {RenderingServer.GetCurrentRenderingMethod()}",
            $"Adapter: {RenderingServer.GetVideoAdapterName()}", $"Logical viewport: {GetViewport().GetVisibleRect().Size}",
            $"Samples: {sampleCount} per view, warmup: {warmup}" };
        foreach (var (name, from, at) in views) {
            camera.Position = from;
            camera.LookAt(at);
            await Frames(warmup);
            var samples = new List<double>();
            var gpu = new List<double>();
            var previous = Time.GetTicksUsec();
            for (var i = 0; i < sampleCount; i++) {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                var now = Time.GetTicksUsec();
                samples.Add((now - previous) / 1000d);
                gpu.Add(viewports.Sum(RenderingServer.ViewportGetMeasuredRenderTimeGpu));
                previous = now;
            }
            samples.Sort();
            gpu.Sort();
            var median = sampleCount / 2;
            var p95 = (int)Math.Ceiling(sampleCount * 0.95d) - 1;
            report.Add($"{name}: median={samples[median]:F2} ms, p95={samples[p95]:F2} ms, max={samples[^1]:F2} ms; "
                + $"GPU median={gpu[median]:F2} ms, p95={gpu[p95]:F2} ms, max={gpu[^1]:F2} ms; "
                + $"draw calls={Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)}");
            GD.Print(report[^1]);
            // Keep the normal HUD in the timing samples; hide it only for the room photographs.
            surgery.Hud.Visible = false;
            await Frames(6);
            using var image = GetViewport().GetTexture().GetImage();
            report.Add($"{name} output pixels: {image.GetSize()}");
            image.SavePng(_out.PathJoin(name + ".png"));
            surgery.Hud.Visible = true;
        }
        var result = string.Join("\n", report);
        GD.Print(result);
        using var file = Godot.FileAccess.Open(_out.PathJoin("performance.txt"), Godot.FileAccess.ModeFlags.Write);
        file.StoreString(result + "\n");
    }
}
