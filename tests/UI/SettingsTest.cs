namespace Scalpel.Tests.UI;

/// <summary>
/// The settings' subsurface scattering box: on by default, a click turns it off and Save keeps it off for the next
/// start. What it switches is Godot's scattering pass, which only a rendered frame shows (the tissue views of
/// ./build.py shots, SHOTS_ARGS=--tissue).
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
[GodotArgs("--fixed-fps", "60")]
public class SettingsTest {
    private const string SettingsPath = "user://settings.cfg";
    private const string Label = "Subsurface scattering (soft light under skin and fat)";

    [TestCase]
    public async Task SubsurfaceScatteringSwitchesOffAndStaysOff() {
        // The settings file is the game's own, read and written as a player's: a developer's is put back after.
        var kept = FileAccess.FileExists(SettingsPath) ? FileAccess.GetFileAsBytes(SettingsPath) : null;
        Settings.IgnoreFile = false;
        var panel = new SettingsPanel();
        try {
            Frames.Root.AddChild(panel);
            await Frames.Process(2);
            var box = panel.FindChildren("*", "", true, false).OfType<CheckBox>().Single(box => box.Text == Label);
            AssertBool(box.ButtonPressed && Settings.SubsurfaceScattering).OverrideFailureMessage("on by default").IsTrue();

            PlayerInput.Click(box);
            await PlayerInput.Delivered();
            await Frames.Process(2);
            AssertBool(Settings.SubsurfaceScattering).OverrideFailureMessage("a click turns it off").IsFalse();

            PlayerInput.Click(panel.FindChildren("*", "", true, false).OfType<Button>().Single(button => button.Text == "Save"));
            await PlayerInput.Delivered();
            Settings.SubsurfaceScattering = true;
            Settings.Load();
            AssertBool(Settings.SubsurfaceScattering).OverrideFailureMessage("Save keeps it off for the next start").IsFalse();
        } finally {
            panel.QueueFree();
            Settings.SubsurfaceScattering = true;
            Settings.Apply();
            Settings.IgnoreFile = true;
            if (kept is null) {
                DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(SettingsPath));
            } else {
                using var file = FileAccess.Open(SettingsPath, FileAccess.ModeFlags.Write);
                file.StoreBuffer(kept);
            }
        }
    }
}
