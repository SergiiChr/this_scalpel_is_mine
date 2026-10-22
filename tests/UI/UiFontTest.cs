using System.IO;

namespace Scalpel.Tests.UI;

/// <summary>Menus, HUD and in-world screens draw in Barlow, with Inter for the symbols Barlow lacks, and together they
/// have every symbol the game writes, so no text stalls a frame looking for a system font.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
public class UiFontTest {
    [TestCase]
    public void GameTextNeedsNoSystemFont() {
        var font = ThemeDB.FallbackFont;
        AssertString(font.GetFontName()).OverrideFailureMessage("menus and screens use Barlow").IsEqual("Barlow Medium");
        AssertObject(ThemeDB.GetDefaultTheme().DefaultFont).OverrideFailureMessage("the default theme uses Barlow too")
            .IsSame(font);
        // The manual keeps its own fonts, so its pages and view are left out.
        var manual = Path.DirectorySeparatorChar + "manual" + Path.DirectorySeparatorChar;
        var sources = ((string[])["res://src", "res://data"])
            .SelectMany(dir => Directory.EnumerateFiles(ProjectSettings.GlobalizePath(dir), "*", SearchOption.AllDirectories))
            .Where(path => !path.Contains(manual, StringComparison.Ordinal) && Path.GetFileName(path) != "ManualView.cs");
        foreach (var path in sources) {
            foreach (var symbol in File.ReadAllText(path).Where(c => c > 127).Distinct()) {
                AssertBool(font.HasChar(symbol))
                    .OverrideFailureMessage($"Barlow or Inter draws '{symbol}' (U+{(int)symbol:X4}) from {Path.GetFileName(path)}").IsTrue();
            }
        }
    }
}
