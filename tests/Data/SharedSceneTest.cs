namespace Scalpel.Tests.Data;

/// <summary>Shared setup retains only completed cases in the same group; isolation and failure discard it.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
public class SharedSceneTest
{
    [TestCase("same")]
    [TestCase("different")]
    [TestCase("incomplete")]
    [TestCase("isolated")]
    public async Task OnlyCompletedCompatibleCasesReuseTheirScene(string next)
    {
        var made = 0;
        var stopped = new List<object>();
        var scenes = new SharedScene<object>(() =>
        {
            made++;
            return Task.FromResult(new object());
        }, scene =>
        {
            stopped.Add(scene);
            return Task.CompletedTask;
        });
        var first = await scenes.Begin("first");
        if (next != "incomplete")
        {
            await scenes.Complete();
        }
        await scenes.DiscardIncomplete();
        var second = await scenes.Begin(next == "isolated" ? "" : next == "different" ? "second" : "first");
        var reused = next == "same";
        AssertBool(ReferenceEquals(first, second)).OverrideFailureMessage("only the successful, compatible case retains its scene")
            .IsEqual(reused);
        AssertInt(made).IsEqual(reused ? 1 : 2);
        await scenes.Complete();
        await scenes.Stop();
        AssertInt(stopped.Count).OverrideFailureMessage("every scene is stopped exactly once, including isolated cases")
            .IsEqual(made);
        AssertInt(stopped.Distinct().Count()).IsEqual(made);
    }
}
