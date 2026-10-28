namespace Scalpel.Tests.UI;

/// <summary>
/// The Skin Lab sandbox, offered only in debug mode: picked in the lobby with its debug checkbox ticked, the run has no
/// surgeon quirks, run modifiers or patient quirks, and the surgery starts with the patient already under and a
/// scalpel, forceps, three retractors and a Gelpi on the tray, ready to cut.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
public class SkinLabLobbyTest {
    [TestCase]
    public async Task InDebugModeTheSkinLabStartsReadyToCutWithoutQuirks() {
        AssertBool(Db.Offered.Any(scenario => scenario.Id == "skin_lab"))
            .OverrideFailureMessage("out of debug mode the Skin Lab isn't offered").IsFalse();
        Settings.Debug = true;
        try {
            AssertBool(Db.Offered.Any(scenario => scenario.Id == "skin_lab"))
                .OverrideFailureMessage("in debug mode the Skin Lab is offered").IsTrue();
            var net = Net.Instance;
            net.PlaySolo("skin_lab");
            AssertBool(await Frames.Until(() => Frames.Tree.CurrentScene is Lobby, 10f))
                .OverrideFailureMessage("Play solo opens the lobby").IsTrue();
            await Frames.Process(2);
            var lobby = Frames.Tree.CurrentScene;
            var skip = lobby.FindChildren("*", "", true, false).OfType<CheckBox>()
                .FirstOrDefault(box => box.Text.StartsWith("Skip quirks", StringComparison.Ordinal));
            AssertObject(skip).OverrideFailureMessage("debug mode adds the skip checkbox to the lobby").IsNotNull();
            await Click(skip!);
            AssertBool(net.Roster.Values.All(player => player.Quirks.Count == 0) && net.RunModifiers.Count == 0)
                .OverrideFailureMessage("ticked, the run has no surgeon quirks or run modifiers").IsTrue();
            await Click(Find(lobby, "Ready"));
            await Click(Find(lobby, "Start surgery"));
            AssertBool(await Frames.Until(() => Surgery.Current is { Running: true }, 45f))
                .OverrideFailureMessage("the surgery starts").IsTrue();
            var surgery = Surgery.Current!;
            AssertInt(net.PatientQuirks.Count).OverrideFailureMessage("nor patient quirks").IsEqual(0);
            AssertBool(await Frames.Until(() => surgery.Patient.Vitals.IsUnder, 5f))
                .OverrideFailureMessage("the patient is already under").IsTrue();
            var tools = surgery.Tools.Tools.Values.Select(tool => tool.Def.Id).ToList();
            foreach (var (id, count) in ((string, int)[])[("scalpel", 1), ("forceps", 1), ("retractor", 3), ("gelpi", 1)]) {
                AssertInt(tools.Count(tool => tool == id)).OverrideFailureMessage($"{count} {id} on the tray").IsGreaterEqual(count);
            }
            net.BackToMenu();
        } finally {
            Settings.Debug = false;
        }
    }

    private static Button Find(Node lobby, string text) {
        var found = lobby.FindChildren("*", "", true, false).OfType<Button>()
            .FirstOrDefault(button => !button.IsQueuedForDeletion() && button.Text.StartsWith(text, StringComparison.Ordinal));
        AssertObject(found).OverrideFailureMessage($"the lobby shows \"{text}\"").IsNotNull();
        return found!;
    }

    private static async Task Click(Control control) {
        PlayerInput.Click(control);
        await PlayerInput.Delivered();
        await Frames.Process(2);
    }
}
