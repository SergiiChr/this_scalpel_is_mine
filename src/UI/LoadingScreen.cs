namespace Scalpel.UI;

/// <summary>
/// Between "Start surgery" and the operating room: a black screen with a bar while the surgery scene and the tool
/// models load and the runtime prepares the game's code (<see cref="ManagedRuntime"/>), so none of it stalls the
/// surgery. Once everyone in the session is done, <see cref="Net"/> lets them all in.
/// </summary>
public partial class LoadingScreen : Control
{
    private readonly List<string> _files = [Net.SurgeryScene];
    private ProgressBar _bar = null!;
    private bool _done;

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
        _files.AddRange(ModelSlot.ToolModelFiles());
        foreach (var file in _files)
        {
            ResourceLoader.LoadThreadedRequest(file);
        }
        Net.Instance.AllLoaded += Enter;
    }

    public override void _ExitTree() => Net.Instance.AllLoaded -= Enter;

    public override void _Process(double delta)
    {
        var loaded = _files.Count(file => ResourceLoader.LoadThreadedGetStatus(file)
            != ResourceLoader.ThreadLoadStatus.InProgress);
        _bar.Value = ((float)loaded / _files.Count + ManagedRuntime.Progress) / 2f;
        if (_done || loaded < _files.Count || !ManagedRuntime.WarmUp.IsCompleted)
        {
            return;
        }
        _done = true;
        Net.Instance.FinishedLoading();
    }

    private void Enter()
    {
        foreach (var file in _files.Skip(1))
        {
            if (ResourceLoader.LoadThreadedGet(file) is PackedScene model)
            {
                ModelSlot.Keep(file, model);
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
