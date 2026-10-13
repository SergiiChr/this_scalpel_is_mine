namespace Scalpel.Tests.Support;

/// <summary>A suite's compatible cases share one scene. A different group or an incomplete case discards it;
/// empty group names always request isolation. Complete only after every assertion and reset succeeded.</summary>
public sealed class SharedScene<T>(Func<Task<T>> create, Func<T, Task> stop) where T : class
{
    private T? _scene;
    private string _group = "";
    private bool _complete;

    public async Task<T> Begin(string group = "")
    {
        if (!_complete || group.Length == 0 || group != _group)
        {
            await Stop();
        }
        _complete = false;
        _group = group;
        if (_scene is null)
        {
            _scene = await create();
            GD.Print($"shared scene: started {typeof(T).Name} ({group})");
        }
        return _scene;
    }

    public async Task Complete()
    {
        _complete = true;
        if (_group.Length == 0)
        {
            await Stop();
        }
    }

    /// <summary>Use after each case, including failures: a partially executed case never contaminates the next.</summary>
    public async Task DiscardIncomplete()
    {
        if (!_complete)
        {
            await Stop();
        }
    }

    /// <summary>Use after the suite and before replacing its group.</summary>
    public async Task Stop()
    {
        _complete = false;
        if (_scene is { } scene)
        {
            _scene = null;
            await stop(scene);
        }
    }
}
