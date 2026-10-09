using System.Diagnostics;

namespace Scalpel.Tests.UI;

/// <summary>"Start surgery" in a solo lobby shows the loading screen, and once everything is loaded the surgery
/// starts by itself.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
public class LoadingScreenTest
{
    [TestCase]
    public async Task QuittingDuringStartupExitsCleanly()
    {
        // A fresh process quits after its first frame, while runtime preparation can still be using native bindings.
        using var game = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = OS.GetExecutablePath(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        foreach (var argument in (string[])["--headless", "--path", ProjectSettings.GlobalizePath("res://"), "--quit-after", "1"])
        {
            game.StartInfo.ArgumentList.Add(argument);
        }
        AssertBool(game.Start()).OverrideFailureMessage("starts a fresh game process").IsTrue();
        var stdout = game.StandardOutput.ReadToEndAsync();
        var stderr = game.StandardError.ReadToEndAsync();
        try
        {
            await game.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            if (!game.HasExited)
            {
                game.Kill(entireProcessTree: true);
                await game.WaitForExitAsync();
            }
        }
        var output = await stdout + await stderr;
        GD.Print(output);
        AssertInt(game.ExitCode).OverrideFailureMessage("quitting during startup exits without a native crash").IsEqual(0);
        AssertString(output).NotContains("Unhandled exception").NotContains("SCRIPT ERROR");
    }

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
