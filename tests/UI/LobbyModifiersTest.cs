namespace Scalpel.Tests.UI;

/// <summary>
/// The lobby sums up this run's modifiers, and its Modifiers panel lists them with their explanations beside the
/// available ones seen in earlier runs and the locked rest. The run starts with the listed modifiers, which unlocks them.
/// With key frames also the lobby and the open panel.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
public class LobbyModifiersTest {
    private static readonly string[] Active = ["understaffed", "chart_error"];
    private const string Seen = "cold_room";

    [TestCase]
    public async Task PanelListsActiveAvailableAndLockedModifiersThenTheRunUnlocksThem() {
        // A fresh save (every test process has its own user://), but for one modifier seen in an earlier run.
        Progress.UnlockModifierForSession(Seen);
        var net = Net.Instance;
        net.PlaySolo("appendectomy");
        // Before the lobby shows: its rolls are random.
        SurgeryState.NetworkSessionHasOrdinarySurgeons();
        SurgeryState.RunHasModifiers(Active);
        AssertBool(await Frames.Until(() => Frames.Tree.CurrentScene is Lobby, 10f))
            .OverrideFailureMessage("Play solo opens the lobby").IsTrue();
        await Frames.Process(2);
        var lobby = Frames.Tree.CurrentScene;
        AssertString(Find<Label>(lobby, "RUN MODIFIERS").Text)
            .OverrideFailureMessage("the lobby names this run's modifiers in one line")
            .IsEqual("RUN MODIFIERS  Understaffed night shift, Chart mix-up");
        AssertBool(Texts(lobby).Any(text => text.Contains("three floors", StringComparison.Ordinal)))
            .OverrideFailureMessage("the lobby leaves the explanations to the Modifiers panel").IsFalse();
        await Capture("00_lobby", "the pre-op briefing for Appendectomy with one yellow line, RUN MODIFIERS  Understaffed "
            + "night shift, Chart mix-up, above the surgeon's card, and Ready, Modifiers, Start surgery and Leave buttons");

        await Click(Find<Button>(lobby, "Modifiers"));
        var texts = Texts(lobby);
        var order = ((string[])["ACTIVE THIS RUN", "Understaffed night shift", "Chart mix-up", "AVAILABLE", "Broken heating",
            "LOCKED"]).Select(text => texts.IndexOf(text)).ToList();
        AssertBool(order[0] >= 0 && order.Zip(order.Skip(1)).All(pair => pair.First < pair.Second))
            .OverrideFailureMessage($"active, then available, then locked modifiers: [{string.Join(" | ", texts)}]").IsTrue();
        AssertBool(texts.Contains(Description("understaffed")) && texts.Contains(Description(Seen)))
            .OverrideFailureMessage("each named modifier has its explanation").IsTrue();
        AssertBool(Active.All(id => texts.Count(text => text == Name(id)) == 1))
            .OverrideFailureMessage("an active modifier is listed once, not again as available").IsTrue();
        var locked = Db.RunModifiers.GetSections().Except(Active.Append(Seen)).ToList();
        AssertBool(texts.Contains($"{locked.Count} more. Each unlocks the first time a run starts with it.")
                && !locked.Any(id => texts.Contains(Name(id)) || texts.Contains(Description(id))))
            .OverrideFailureMessage("the rest are locked: counted with their unlock condition, names hidden").IsTrue();
        await Capture("01_panel", "the Modifiers panel: ACTIVE THIS RUN with Understaffed night shift "
            + "and Chart mix-up in yellow, AVAILABLE with Broken heating, each with a grey explanation, then LOCKED with "
            + $"\"{locked.Count} more\" and the unlock condition, and a Close button, over the darkened lobby");

        await Click(Find<Button>(lobby, "Close"));
        AssertBool(Texts(lobby).Contains("ACTIVE THIS RUN")).OverrideFailureMessage("Close hides the panel").IsFalse();

        await Click(Find<Button>(lobby, "Ready"));
        await Click(Find<Button>(lobby, "Start surgery"));
        AssertBool(await Frames.Until(() => Surgery.Current is { Running: true }, 45f))
            .OverrideFailureMessage("the surgery starts").IsTrue();
        AssertFloat(Surgery.Current!.RunMods.Mult("nurse_delay_mult"))
            .OverrideFailureMessage("the surgery runs with the listed Understaffed night shift").IsEqual(2f);
        AssertBool(Active.All(Progress.IsModifierUnlocked))
            .OverrideFailureMessage("starting a run unlocks its modifiers").IsTrue();
        var save = new ConfigFile();
        save.Load("user://save.cfg");
        var saved = save.GetValue("codex", "unlocked").AsStringArray();
        AssertBool(Active.All(id => saved.Contains($"modifier:{id}")))
            .OverrideFailureMessage($"the unlocks are saved: [{string.Join(", ", saved)}]")
            .IsTrue();
        net.BackToMenu();
    }

    private static string Name(string id) => new ConfigReader(Db.RunModifiers, id).String("name");

    private static string Description(string id) => new ConfigReader(Db.RunModifiers, id).String("description");

    /// <summary>The texts of the visible labels and buttons, in tree order.</summary>
    private static List<string> Texts(Node lobby) => [.. lobby.FindChildren("*", "", true, false).OfType<Control>()
        .Where(control => control.IsVisibleInTree() && !control.IsQueuedForDeletion())
        .Select(control => control switch { Label label => label.Text, Button button => button.Text, _ => null })
        .OfType<string>()];

    /// <summary>The first control of type <typeparamref name="T"/> whose text starts with <paramref name="text"/>;
    /// fails the case when there's none.</summary>
    private static T Find<T>(Node lobby, string text) where T : Control {
        var found = lobby.FindChildren("*", "", true, false).OfType<T>().FirstOrDefault(control => !control.IsQueuedForDeletion()
            && (control is Button button ? button.Text : (control as Label)?.Text ?? "").StartsWith(text, StringComparison.Ordinal));
        AssertObject(found).OverrideFailureMessage($"the lobby shows \"{text}\"").IsNotNull();
        return found!;
    }

    private static async Task Click(Control control) {
        PlayerInput.Click(control);
        await PlayerInput.Delivered();
        await Frames.Process(2);
    }

    private static async Task Capture(string name, string description) {
        if (KeyFrames.Wanted()) {
            await KeyFrames.SaveViewport(KeyFrames.Folder("lobby_modifiers").PathJoin($"{name}.png"), description);
        }
    }
}
