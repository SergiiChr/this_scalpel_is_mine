namespace Scalpel.Tests.Support;

/// <summary>
/// Deliberate key frames for visual tests: Capture(name) right after a named action saves a view from straight above
/// the site and an oblique close-up that shows depth and intersections. Rendering is on only while a key frame is saved:
/// whoever runs the game keeps it off in between.
/// </summary>
public partial class KeyFrames : Node
{
    /// <summary>Key frames are taken only in a run with this environment variable set to 1 (build.py test
    /// --with-key-frames sets it and gives the tests a display). Without it the same cases run headless and every
    /// capture is skipped.</summary>
    private const string WithKeyFrames = "WITH_KEY_FRAMES";
    /// <summary>Views of the site: from this far, above and 45° off toward the surgeon's side of the table.</summary>
    private const float Distance = 0.45f;
    private const float Fov = 35f;

    public string OutDir { get; private set; } = "";
    public List<string> Saved { get; } = [];
    /// <summary>Key frames are numbered in the order they're taken, so the folder reads as the story of the surgery.
    /// </summary>
    private int _taken;
    private Surgery _surgery = null!;
    private Camera3D _camera = null!;

    public static bool Wanted() => OS.GetEnvironment(WithKeyFrames) == "1";

    /// <summary>A folder for key frames under build/test-artifacts/screenshots (made if missing), absolute.</summary>
    public static string Folder(string relative)
    {
        var folder = ProjectSettings.GlobalizePath("res://build/test-artifacts/screenshots").PathJoin(relative);
        DirAccess.MakeDirRecursiveAbsolute(folder);
        return folder;
    }

    /// <summary>Saves what the viewport shows now, a few drawn frames on so a camera that just moved is in place.
    /// Rendering is on only for those frames. Gameplay pauses so rendering preserves the named action's pose.</summary>
    public static async Task<bool> SaveViewport(string path)
    {
        var surgery = Surgery.Current;
        var paused = Frames.Tree.Paused;
        // Pause gameplay rather than disabling nodes: disabled collision objects leave the physics space,
        // but HUD aim projection still needs to query the patient's surfaces.
        Frames.Tree.Paused = true;
        RenderingServer.RenderLoopEnabled = true;
        try
        {
            // The camera or HUD visibility may have changed since the last process frame.
            // Refresh only the overlays: the action pose and HUD timers stay frozen.
            surgery?.Hud.RefreshFrame(0f);
            await Frames.Process(3);
            await RenderingServer.Singleton.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            return Frames.Root.GetViewport().GetTexture().GetImage().SavePng(path) == Error.Ok;
        }
        finally
        {
            RenderingServer.RenderLoopEnabled = false;
            Frames.Tree.Paused = paused;
        }
    }

    /// <summary>Starts a set of key frames for <paramref name="surgery"/> into <paramref name="relative"/> (under
    /// <see cref="Folder"/>).</summary>
    public void Begin(Surgery surgery, string relative)
    {
        _surgery = surgery;
        OutDir = Folder(relative);
        _camera = new Camera3D { Fov = Fov, Near = 0.01f };
        surgery.AddChild(_camera);
    }

    public void End()
    {
        if (IsInstanceValid(_camera))
        {
            _camera.QueueFree();
        }
    }

    /// <summary>Saves both views as NN_name_top.png and NN_name_oblique.png. Returns false when one couldn't be saved.
    /// The oblique view comes from the surgeon's side, or from <paramref name="side"/> (world, across the floor) when
    /// given: along a cut, say, to see what lies across it.</summary>
    public Task<bool> Capture(string keyFrame, Vector3? side = null)
    {
        var site = _surgery.Patient.Body.Site;
        var middle = site.GlobalPosition;
        var up = site.GlobalBasis.Y.Normalized();
        var toward = (side ?? (_surgery.LocalSurgeon!.GlobalPosition - middle)).Slide(up).Normalized();
        return Views(keyFrame, middle, up, toward, Distance, false);
    }

    /// <summary>The same two views of something off the site (a bottle on a tray), from <paramref name="distance"/>
    /// away. The hands are left out of both: one just let go of it would hide it.</summary>
    public Task<bool> CaptureAt(string keyFrame, Vector3 at, float distance)
    {
        var toward = (_surgery.LocalSurgeon!.GlobalPosition - at).Slide(Vector3.Up).Normalized();
        return Views(keyFrame, at, Vector3.Up, toward, distance, true);
    }

    /// <summary>Saves what the surgeon sees, the HUD's aim included, as NN_name_view.png.</summary>
    public Task<bool> CaptureView(string keyFrame)
    {
        var path = OutDir.PathJoin($"{NextName(keyFrame)}_view.png");
        Saved.Add(path);
        return SaveViewport(path);
    }

    private string NextName(string keyFrame) => $"{_taken++:00}_{keyFrame}";

    private async Task<bool> Views(string keyFrame, Vector3 middle, Vector3 up, Vector3 toward, float distance, bool noHands)
    {
        var name = NextName(keyFrame);
        var surgeon = _surgery.LocalSurgeon!;
        var hudWas = _surgery.Hud.Visible;
        _surgery.Hud.Visible = false;
        _camera.Current = true;
        var ok = true;
        foreach (var (view, from, lookUp) in (ViewSpec[])[new("top", up, toward), new("oblique", (up + toward).Normalized(), up)])
        {
            _camera.GlobalPosition = middle + (from * distance);
            _camera.LookAt(middle, lookUp);
            // From straight above the hands would hide the site; the oblique view shows them, with the tools on it.
            foreach (var hand in surgeon.Hands)
            {
                hand.Visible = view != "top" && !noHands;
            }
            var path = OutDir.PathJoin($"{name}_{view}.png");
            ok &= await SaveViewport(path);
            Saved.Add(path);
        }
        foreach (var hand in surgeon.Hands)
        {
            hand.Visible = true;
        }
        _camera.Current = false;
        surgeon.Camera.Current = true;
        _surgery.Hud.Visible = hudWas;
        return ok;
    }

    private readonly record struct ViewSpec(string Name, Vector3 From, Vector3 Up);
}
