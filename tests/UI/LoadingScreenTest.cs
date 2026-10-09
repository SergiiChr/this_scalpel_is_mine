using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace Scalpel.Tests.UI;

/// <summary>"Start surgery" in a solo lobby shows the loading screen, and once everything is loaded the surgery
/// starts by itself.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
public class LoadingScreenTest
{
    [TestCase("missing_file")]
    [TestCase("missing_method")]
    public void ShutdownDoesNotRethrowWarmUpFailure(string failure)
    {
        using var shutdown = new CancellationTokenSource();
        Exception error = failure == "missing_file" ? new FileNotFoundException("warm-up dependency")
            : new MissingMethodException("warm-up method");
        var work = Task.FromException(error);
        ManagedRuntime.StopWarmUp(work, shutdown);
        AssertBool(shutdown.IsCancellationRequested).IsTrue();
        AssertBool(work.IsFaulted).OverrideFailureMessage("the failure remains available to the loading screen").IsTrue();
    }

    [TestCase]
    public async Task ShutdownCancelsPendingWarmUpBeforeJoining()
    {
        using var shutdown = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = Task.Run(() =>
        {
            entered.SetResult();
            shutdown.Token.WaitHandle.WaitOne();
            shutdown.Token.ThrowIfCancellationRequested();
        }, shutdown.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            ManagedRuntime.StopWarmUp(work, shutdown);
        }
        AssertBool(work.IsCanceled).OverrideFailureMessage("shutdown cancels pending work and joins the worker").IsTrue();
    }

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
        var exited = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                await game.WaitForExitAsync(timeout.Token);
                exited = true;
            }
            catch (OperationCanceledException)
            {
                // Save the child's diagnostics below after stopping it.
            }
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
        var log = KeyFrames.Folder("startup").PathJoin("early-quit.log");
        File.WriteAllText(log, output);
        AssertBool(exited).OverrideFailureMessage("child startup/quit exceeded 45 seconds; output: " + log).IsTrue();
        var errors = output.Split('\n').Where(line => Regex.IsMatch(line, "SCRIPT ERROR|Parse Error|ERROR:|Unhandled exception")
            && !Regex.IsMatch(line, "at exit|leaked", RegexOptions.IgnoreCase)).ToList();
        AssertArray(errors).OverrideFailureMessage("child game errors; full output: " + log).IsEmpty();
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
