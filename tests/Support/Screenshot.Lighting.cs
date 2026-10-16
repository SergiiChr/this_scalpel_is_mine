using System.Globalization;

namespace Scalpel.Tests.Support;

public partial class Screenshot {
    /// <summary>Paired GI screenshots and GPU timestamps of identical paused scenes. Run --lighting on Forward+;
    /// --benchmark additionally warms 180 frames and samples 240 per pass, in alternating order.</summary>
    private async Task LightingViews(Surgery surgery) {
        if (RenderingServer.GetCurrentRenderingMethod() != "forward_plus") {
            throw new InvalidOperationException("--lighting requires RENDERER=forward_plus");
        }
        surgery.Hud.RemoveFilmGrain();
        var environment = surgery.Room.GetChildren().OfType<WorldEnvironment>().Single().Environment;
        var camera = NewCamera();
        var site = surgery.Patient.Body.Site.GlobalPosition;
        var views = new (string Name, Vector3 From, Vector3 At)[] {
            ("room", new(1.8f, 1.8f, 0.95f), new(0f, 1f, 0f)),
            ("patient", site + new Vector3(0.5f, 0.6f, 0.65f), site),
            ("stations", new(-1.4f, 1.6f, -0.8f), new(0.8f, 1.1f, 0.85f)),
        };
        // This capture node keeps drawing while the simulation and its HUD stay frozen.
        surgery.ProcessMode = ProcessModeEnum.Pausable;
        surgery.LocalSurgeon!.Visible = false;
        ProcessMode = ProcessModeEnum.Always;
        var wasPaused = GetTree().Paused;
        GetTree().Paused = true;
        try {
            RenderingServer.GlobalShaderParameterSet(ShaderParam.GameTime, 0f);
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            Engine.MaxFps = 0;
            var viewport = GetViewport().GetViewportRid();
            RenderingServer.ViewportSetMeasureRenderTime(viewport, true);
            using var csv = FileAccess.Open(_out.PathJoin("performance.csv"), FileAccess.ModeFlags.Write);
            csv.StoreLine("adapter,renderer,width,height,view,pass,gi,frames,gpu_median_ms,gpu_p95_ms,gpu_max_ms,frame_median_ms,frame_p95_ms,frame_max_ms");
            foreach (var (name, from, at) in views) {
                camera.GlobalPosition = from;
                camera.LookAt(at);
                var passes = Has("--benchmark") ? 4 : 2;
                for (var pass = 0; pass < passes; pass++) {
                    var enabled = pass is 1 or 2;
                    environment.SdfgiEnabled = enabled;
                    await Frames(180);
                    if (pass < 2) {
                        await Shot($"{name}_gi_{(enabled ? "on" : "off")}");
                    }
                    if (!Has("--benchmark")) {
                        continue;
                    }
                    var gpu = new List<double>();
                    var frames = new List<double>();
                    var previous = Time.GetTicksUsec();
                    for (var frame = 0; frame < 240; frame++) {
                        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                        var now = Time.GetTicksUsec();
                        frames.Add((now - previous) / 1000.0);
                        previous = now;
                        gpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport));
                    }
                    gpu.Sort();
                    frames.Sort();
                    if (gpu[0] <= 0) {
                        throw new InvalidOperationException("GPU timestamps unavailable; cannot report GPU performance");
                    }
                    var size = GetViewport().GetVisibleRect().Size;
                    var values = new[] { gpu[120], gpu[227], gpu[^1], frames[120], frames[227], frames[^1] };
                    var line = $"{RenderingServer.GetVideoAdapterName()},{RenderingServer.GetCurrentRenderingMethod()},{size.X},{size.Y},{name},{pass},{enabled},240,"
                        + string.Join(",", values.Select(value => value.ToString("F4", CultureInfo.InvariantCulture)));
                    csv.StoreLine(line);
                    GD.Print(line);
                }
            }
        } finally {
            GetTree().Paused = wasPaused;
        }
    }
}
