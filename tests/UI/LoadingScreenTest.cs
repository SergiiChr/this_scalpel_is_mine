namespace Scalpel.Tests.UI;

/// <summary>"Start surgery" in a solo lobby shows the loading screen, and once everything is loaded the surgery
/// starts by itself.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
public class LoadingScreenTest
{
    [TestCase]
    public async Task StartSurgeryLoadsThenEntersTheRoom()
    {
        Net.Instance.PlaySolo("appendectomy");
        AssertBool(await Frames.Until(() => Frames.Tree.CurrentScene is Lobby, 10f))
            .OverrideFailureMessage("Play solo opens the lobby").IsTrue();
        Button LobbyButton(string text) => Frames.Tree.CurrentScene.FindChildren("*", nameof(Button), true, false)
            .OfType<Button>().First(button => button.Text == text);
        SurgeryDriver.Click(LobbyButton("Ready"));
        await Frames.NextProcess();
        SurgeryDriver.Click(LobbyButton("Start surgery"));
        AssertBool(await Frames.Until(() => Frames.Tree.CurrentScene is LoadingScreen, 10f))
            .OverrideFailureMessage("Start surgery shows the loading screen").IsTrue();
        AssertBool(await Frames.Until(() => Surgery.Current is { Running: true }, 45f))
            .OverrideFailureMessage("once loaded, the surgery starts by itself").IsTrue();
        Net.Instance.BackToMenu();
    }
}
