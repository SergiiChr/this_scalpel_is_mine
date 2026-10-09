namespace Scalpel.Tests.Support;

/// <summary>
/// Starts the async work of a scene that drives itself (a dev tool, a network driver). A task started and discarded
/// (<c>_ = Render()</c>) keeps its exception to itself: nothing is printed and Godot never quits, so whoever runs it
/// waits for a timeout that doesn't say why. Here an exception is printed as an error and quits with status 1.
/// </summary>
internal static class Scripted
{
    internal static void Start(this Node node, Func<Task> work) => _ = QuitOnError(node, work);

    private static async Task QuitOnError(Node node, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception error)
        {
            GD.PushError($"{node.GetType().Name} failed: {error}");
            node.GetTree().Quit(1);
        }
    }
}
