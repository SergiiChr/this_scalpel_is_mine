namespace Scalpel.UI;

/// <summary>
/// Between "Start surgery" and the operating room: a black screen with a bar while the surgery scene, the tool models
/// and the sounds load and the runtime prepares the game's code (<see cref="ManagedRuntime"/>), so none of it stalls
/// the surgery. Once everyone in the session is done, <see cref="Net"/> lets them all in.
/// </summary>
public partial class LoadingScreen : Control
{
    private readonly List<(string File, Action<Resource> Keep)> _loads = Ahead();
    private ProgressBar _bar = null!;
    private bool _done;

    /// <summary>What's loaded ahead besides the surgery scene: each file and where it's kept once loaded.</summary>
    internal static List<(string File, Action<Resource> Keep)> Ahead() =>
    [
        .. ModelSlot.ToolModelFiles()
            .Select(file => (file, (Action<Resource>)(scene => ModelSlot.Keep(file, (PackedScene)scene)))),
        .. Sfx.Unloaded()
            .Select(sound => (sound.File, (Action<Resource>)(stream => Sfx.Keep(sound.Id, (AudioStream)stream)))),
    ];

    public override void _Ready()
    {
        AddChild(new ColorRect { Color = Colors.Black, AnchorRight = 1f, AnchorBottom = 1f });
        var center = new CenterContainer { AnchorRight = 1f, AnchorBottom = 1f };
        AddChild(center);
        _bar = new ProgressBar
        {
            MaxValue = 1,
            Step = 0,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(480f, 10f),
        };
        center.AddChild(_bar);
        _loads.Add((Net.SurgeryScene, _ => { }));
        foreach (var (file, _) in _loads)
        {
            ResourceLoader.LoadThreadedRequest(file);
        }
        Net.Instance.AllLoaded += Enter;
    }

    public override void _ExitTree() => Net.Instance.AllLoaded -= Enter;

    public override void _Process(double delta)
    {
        var loaded = _loads.Count(load => ResourceLoader.LoadThreadedGetStatus(load.File)
            != ResourceLoader.ThreadLoadStatus.InProgress);
        _bar.Value = ((float)loaded / _loads.Count + ManagedRuntime.Progress) / 2f;
        if (_done || loaded < _loads.Count || !ManagedRuntime.WarmUp.IsCompleted)
        {
            return;
        }
        _done = true;
        if (ManagedRuntime.WarmUp.Exception is { } error)
        {
            // Not fatal: what wasn't prepared compiles when it first runs.
            GD.PushWarning($"Warming up the runtime failed: {error.InnerException ?? error}");
        }
        Net.Instance.FinishedLoading();
    }

    private void Enter()
    {
        foreach (var (file, keep) in _loads)
        {
            if (ResourceLoader.LoadThreadedGet(file) is { } loaded)
            {
                keep(loaded);
            }
        }
        if (ResourceLoader.LoadThreadedGet(Net.SurgeryScene) is PackedScene surgery)
        {
            GetTree().ChangeSceneToPacked(surgery);
        }
        else
        {
            GetTree().ChangeSceneToFile(Net.SurgeryScene);
        }
    }
}
