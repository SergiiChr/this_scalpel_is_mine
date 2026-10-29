using System.Globalization;

namespace Scalpel.Tests.Support;

public partial class Screenshot {
    /// <summary>An incision held wide open, seen close from above, at a cut's wall and right at the fat, with the GPU time of
    /// each view (--tissue). Swap a tissue shader between runs to compare it: the scene is paused and the same every
    /// run. Needs RENDERER=forward_plus for GPU timestamps.</summary>
    private async Task TissueViews(Surgery surgery) {
        surgery.Hud.RemoveFilmGrain();
        var patient = surgery.Patient;
        var body = patient.Body;
        var tissue = body.Tissue;
        // One long incision through to the muscle, its lips held wide apart: walls of skin and fat down to the muscle.
        patient.Cut(1, new Vector2(0.2f, 0.55f), new Vector2(0.8f, 0.55f), 1f, 1f, false, 0.1f);
        foreach (var x in (float[])[0.38f, 0.5f, 0.62f]) {
            foreach (var (y, side) in ((float, float)[])[(0.5f, -1f), (0.6f, 1f)]) {
                var at = new Vector2(x, y);
                var key = 900 + tissue.Grips().Count;
                tissue.Grip(key, at);
                tissue.MoveGrip(key, tissue.Pos[tissue.Nearest(at)] + new Vector3(0f, 0.006f, side * 0.03f));
            }
        }
        surgery.LocalSurgeon!.Visible = false;
        await Frames(60);
        var mid = body.Site.ToGlobal(tissue.Pos[tissue.Nearest(new Vector2(0.5f, 0.55f))]);
        var up = body.Site.GlobalBasis.Y;
        var across = body.Site.GlobalBasis.Z;
        var camera = NewCamera();
        var views = new (string Name, Vector3 From, Vector3 At)[] {
            ("top", mid + (up * 0.12f) + (across * 0.001f), mid),
            ("wall", mid + (up * 0.05f) + (across * 0.05f), mid - (across * 0.01f)),
            ("fat", mid + (up * 0.02f) + (across * 0.015f), mid - (across * 0.03f)),
            ("skin", mid + (up * 0.04f) + (across * 0.13f), mid + (across * 0.07f)),
            ("patient", mid + new Vector3(0.45f, 0.5f, 0.6f), mid),
        };
        surgery.ProcessMode = ProcessModeEnum.Pausable;
        ProcessMode = ProcessModeEnum.Always;
        GetTree().Paused = true;
        RenderingServer.GlobalShaderParameterSet(ShaderParam.GameTime, 0f);
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = 0;
        var viewport = GetViewport().GetViewportRid();
        RenderingServer.ViewportSetMeasureRenderTime(viewport, true);
        var benchmark = Has("--benchmark");
        using var csv = FileAccess.Open(_out.PathJoin("tissue.csv"), FileAccess.ModeFlags.Write);
        csv.StoreLine("view,gpu_median_ms,gpu_p90_ms");
        foreach (var (name, from, at) in views) {
            camera.GlobalPosition = from;
            camera.LookAt(at);
            await Frames(benchmark ? 30 : 10);
            await Shot($"tissue_{name}");
            if (!benchmark) {
                continue;
            }
            var gpu = new List<double>();
            for (var frame = 0; frame < 60; frame++) {
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                gpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport));
            }
            gpu.Sort();
            var line = string.Create(CultureInfo.InvariantCulture, $"{name},{gpu[30]:F3},{gpu[54]:F3}");
            csv.StoreLine(line);
            GD.Print(line);
        }
    }
}
