namespace Scalpel.Tests.Support;

/// <summary>
/// Deliberate key frames for visual tests: Capture(name, description) right after a named action saves a view from
/// straight above the site and an oblique close-up that shows depth and intersections. The description says what the
/// key frame should show; it goes into the folder's keyframes.json, which the key frame review prints beside each
/// sheet. Rendering is on only while a key frame is saved: whoever runs the game keeps it off in between.
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
    /// <summary>Each folder's file name to description index (see the class summary).</summary>
    private const string Descriptions = "keyframes.json";

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

    /// <summary>Saves what the viewport shows now, a few drawn frames on so a camera that just moved is in place, with
    /// <paramref name="description"/> of what it should show. Rendering is on only for those frames. Gameplay pauses so
    /// rendering preserves the named action's pose. Fails the case when it can't be saved.</summary>
    public static async Task SaveViewport(string path, string description)
    {
        var surgery = Surgery.Current;
        var paused = Frames.Tree.Paused;
        // Pause gameplay rather than disabling nodes: disabled collision objects leave the physics space,
        // but HUD aim projection still needs to query the patient's surfaces.
        Frames.Tree.Paused = true;
        RenderingServer.RenderLoopEnabled = true;
        var saved = false;
        try
        {
            // The camera or HUD visibility may have changed since the last process frame.
            // Refresh only the overlays: the action pose and HUD timers stay frozen.
            surgery?.Hud.RefreshFrame(0f);
            await Frames.Process(3);
            await RenderingServer.Singleton.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            saved = Frames.Root.GetViewport().GetTexture().GetImage().SavePng(path) == Error.Ok;
        }
        finally
        {
            RenderingServer.RenderLoopEnabled = false;
            Frames.Tree.Paused = paused;
        }
        AssertBool(saved && Describe(path, description))
            .OverrideFailureMessage($"saved key frame {path.GetFile()}: {description}").IsTrue();
    }

    /// <summary>Records <paramref name="description"/> for the file at <paramref name="path"/> in its folder's index.
    /// </summary>
    private static bool Describe(string path, string description)
    {
        var index = path.GetBaseDir().PathJoin(Descriptions);
        var descriptions = FileAccess.FileExists(index)
            ? Json.ParseString(FileAccess.GetFileAsString(index)).AsGodotDictionary<string, string>()
            : new Godot.Collections.Dictionary<string, string>();
        descriptions[path.GetFile()] = description;
        using var file = FileAccess.Open(index, FileAccess.ModeFlags.Write);
        return file is not null && file.StoreString(Json.Stringify(descriptions, "  ") + "\n");
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

    /// <summary>Saves both views as NN_name_top.png and NN_name_oblique.png, with <paramref name="description"/> of
    /// what they should show. The oblique view comes from the surgeon's side,
    /// or from <paramref name="side"/> (world, across the floor) when given: along a cut, say, to see what lies across
    /// it.</summary>
    public Task Capture(string keyFrame, string description, Vector3? side = null)
    {
        var site = _surgery.Patient.Body.Site;
        var middle = site.GlobalPosition;
        var up = site.GlobalBasis.Y.Normalized();
        var toward = (side ?? (_surgery.LocalSurgeon!.GlobalPosition - middle)).Slide(up).Normalized();
        return Views(keyFrame, description, middle, TopAndOblique(up, toward), Distance, false);
    }

    /// <summary>The same two views of something off the site (a bottle on a tray), from <paramref name="distance"/>
    /// away. The hands are left out of both: one just let go of it would hide it.</summary>
    public Task CaptureAt(string keyFrame, string description, Vector3 at, float distance)
    {
        var toward = (_surgery.LocalSurgeon!.GlobalPosition - at).Slide(Vector3.Up).Normalized();
        return Views(keyFrame, description, at, TopAndOblique(Vector3.Up, toward), distance, true);
    }

    /// <summary>Two level views of something flat and upright (the IV bag), hung where one from above would look down a
    /// stand's pole or held in a hand: straight at its <paramref name="face"/> and 45° round to its side, from
    /// <paramref name="distance"/> away. The hands show only when <paramref name="hands"/>.</summary>
    public Task CaptureFacing(string keyFrame, string description, Vector3 at, Vector3 face, float distance, bool hands = false)
    {
        var front = face.Slide(Vector3.Up).Normalized();
        var side = front.Rotated(Vector3.Up, Mathf.Pi / 4f);
        return Views(keyFrame, description, at, [new("front", front, Vector3.Up), new("oblique", side, Vector3.Up)], distance, !hands);
    }

    /// <summary>Saves what the surgeon sees, the HUD's aim included, as NN_name_view.png.</summary>
    public Task CaptureView(string keyFrame, string description)
    {
        var path = OutDir.PathJoin($"{NextName(keyFrame)}_view.png");
        Saved.Add(path);
        return SaveViewport(path, description);
    }

    private string NextName(string keyFrame) => $"{_taken++:00}_{keyFrame}";

    private static ViewSpec[] TopAndOblique(Vector3 up, Vector3 toward) =>
        [new("top", up, toward), new("oblique", (up + toward).Normalized(), up)];

    private async Task Views(string keyFrame, string description, Vector3 middle, ViewSpec[] views, float distance, bool noHands)
    {
        var name = NextName(keyFrame);
        var surgeon = _surgery.LocalSurgeon!;
        var hudWas = _surgery.Hud.Visible;
        _surgery.Hud.Visible = false;
        _camera.Current = true;
        foreach (var (view, from, lookUp) in views)
        {
            _camera.GlobalPosition = middle + (from * distance);
            _camera.LookAt(middle, lookUp);
            // From straight above the hands would hide the site; the oblique view shows them, with the tools on it.
            foreach (var hand in surgeon.Hands)
            {
                hand.Visible = view != "top" && !noHands;
            }
            var path = OutDir.PathJoin($"{name}_{view}.png");
            await SaveViewport(path, description);
            Saved.Add(path);
        }
        foreach (var hand in surgeon.Hands)
        {
            hand.Visible = true;
        }
        _camera.Current = false;
        surgeon.Camera.Current = true;
        _surgery.Hud.Visible = hudWas;
    }

    private readonly record struct ViewSpec(string Name, Vector3 From, Vector3 Up);
}
