namespace Scalpel.UI;

/// <summary>
/// Between "Start surgery" and the operating room: a black screen with a bar while the surgery scene, the tool models
/// and the sounds load, a model of every tool is instantiated (<see cref="ModelSlot.PrepareTool"/>) and the runtime
/// prepares the game's code (<see cref="ManagedRuntime"/>), so none of it stalls the surgery. Once everyone in the
/// session is done, <see cref="Net"/> lets them all in.
/// </summary>
public partial class LoadingScreen : Control {
    private readonly List<(string File, Action<Resource> Keep)> _loads = Ahead();
    private ProgressBar _bar = null!;
    private bool _done;
    /// <summary>How many of <see cref="_loads"/> are done loading.</summary>
    private int _loaded;
    /// <summary>The tools still to instantiate ahead, once everything is loaded.</summary>
    private readonly Queue<ToolDef> _toPrepare = new(ModelSlot.ToolsToPrepare());
    /// <summary>Files to load and tools to prepare, in all.</summary>
    private int _steps;
    /// <summary>The surgery scene once loaded.</summary>
    private PackedScene? _surgery;

    /// <summary>What's loaded ahead besides the surgery scene: each file and where it's kept once loaded.</summary>
    internal static List<(string File, Action<Resource> Keep)> Ahead() =>
    [
        .. ModelSlot.ToolModelFiles()
            .Select(file => (file, (Action<Resource>)(scene => ModelSlot.Keep(file, (PackedScene)scene)))),
        .. Sfx.Unloaded()
            .Select(sound => (sound.File, (Action<Resource>)(stream => Sfx.Keep(sound.Id, (AudioStream)stream)))),
    ];

    public override void _Ready() {
        AddChild(new ColorRect { Color = Colors.Black, AnchorRight = 1f, AnchorBottom = 1f });
        var center = new CenterContainer { AnchorRight = 1f, AnchorBottom = 1f };
        AddChild(center);
        _bar = new ProgressBar {
            MaxValue = 1,
            Step = 0,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(480f, 10f),
        };
        center.AddChild(_bar);
        _loads.Add((Net.SurgeryScene, scene => _surgery = (PackedScene)scene));
        _steps = _loads.Count + _toPrepare.Count;
        ResourceLoader.LoadThreadedRequest(_loads[0].File);
        Net.Instance.AllLoaded += Enter;
    }

    public override void _ExitTree() => Net.Instance.AllLoaded -= Enter;

    public override void _Process(double delta) {
        // One file at a time: models loaded side by side on worker threads can corrupt the headless renderer's meshes
        // (wrong or uninitialized mesh RIDs, crashes).
        if (_loaded < _loads.Count) {
            var (file, keep) = _loads[_loaded];
            if (ResourceLoader.LoadThreadedGetStatus(file) != ResourceLoader.ThreadLoadStatus.InProgress) {
                if (ResourceLoader.LoadThreadedGet(file) is { } loaded) {
                    keep(loaded);
                }
                if (++_loaded < _loads.Count) {
                    ResourceLoader.LoadThreadedRequest(_loads[_loaded].File);
                }
            }
        } else if (_toPrepare.TryDequeue(out var def)) {
            // One tool a frame, after the models are loaded and kept: the bar keeps moving.
            ModelSlot.PrepareTool(def);
        }
        var left = _loads.Count - _loaded + _toPrepare.Count;
        _bar.Value = ((float)(_steps - left) / _steps + ManagedRuntime.Progress) / 2f;
        if (_done || left > 0 || !ManagedRuntime.WarmUp.IsCompleted) {
            return;
        }
        _done = true;
        if (ManagedRuntime.WarmUp.Exception is { } error) {
            // Not fatal: what wasn't prepared compiles when it first runs.
            GD.PushWarning($"Warming up the runtime failed: {error.InnerException ?? error}");
        }
        Net.Instance.FinishedLoading();
    }

    private void Enter() {
        if (_surgery is not null) {
            GetTree().ChangeSceneToPacked(_surgery);
        } else {
            GetTree().ChangeSceneToFile(Net.SurgeryScene);
        }
    }
}
