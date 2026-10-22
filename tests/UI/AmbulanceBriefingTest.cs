namespace Scalpel.Tests.UI;

/// <summary>The ambulance briefing explains its co-op limitation while still allowing one player to start.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class AmbulanceBriefingTest {
    [TestCase]
    public async Task WarnsAboutCoopAndStillStartsSolo() {
        FrameBudget.RenderingOff();
        try {
            Net.Instance.PlaySolo("ambulance_bullet");
            AssertBool(await Frames.Until(() => Frames.Tree.CurrentScene is Lobby, 10f)).IsTrue();
            var lobby = Frames.Tree.CurrentScene;
            var description = Db.Scenario("ambulance_bullet")!.Description;
            var label = lobby.FindChildren("*", nameof(Label), true, false).OfType<Label>()
                .Single(item => item.Text.Contains(description, StringComparison.Ordinal));
            AssertBool(label.IsVisibleInTree()).IsTrue();
            AssertString(label.Text).Contains("Intended for co-op");
            AssertString(label.Text).Contains("Solo play remains available");
            Button Button(string text) => lobby.FindChildren("*", nameof(Godot.Button), true, false)
                .OfType<Button>().Single(button => button.Text == text);
            SurgeryState.NetworkSessionHasOrdinarySurgeons();
            SurgeryDriver.Click(Button("Ready"));
            await Frames.NextProcess();
            AssertBool(Button("Start surgery").Disabled).OverrideFailureMessage("one ready player can start the ambulance")
                .IsFalse();
            if (KeyFrames.Wanted()) {
                await KeyFrames.SaveViewport(KeyFrames.Folder("ambulance_briefing").PathJoin("solo_warning.png"),
                    "the pre-op briefing explains the co-op limitation and that solo remains available; Start surgery is enabled for one ready doctor");
            }
            SurgeryDriver.Click(Button("Start surgery"));
            // Loading uses background resource threads: give it wall time even under deterministic fixed-fps runs.
            var deadline = Time.GetTicksMsec() + 45_000;
            while (Surgery.Current is not { Running: true } && Time.GetTicksMsec() < deadline) {
                await Frames.NextProcess();
            }
            AssertBool(Surgery.Current is { Running: true })
                .OverrideFailureMessage("the ambulance still loads and runs with only one doctor").IsTrue();
            AssertInt(Surgery.Current!.Surgeons.Count).IsEqual(1);
        } finally {
            Net.Instance.BackToMenu();
            await Frames.Process(2);
            RenderingServer.RenderLoopEnabled = true;
        }
    }
}
