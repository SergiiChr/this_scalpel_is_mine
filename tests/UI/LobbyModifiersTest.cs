namespace Scalpel.Tests.UI;

/// <summary>
/// The lobby lists every run modifier in a column right of the surgeons' cards: this run's with their explanations, the
/// available ones seen in earlier runs and a count of the locked rest. The run starts with the listed modifiers, which
/// unlocks them. With key frames also the lobby.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
public class LobbyModifiersTest {
    private static readonly string[] Active = ["understaffed", "chart_error"];
    private const string Seen = "cold_room";

    [TestCase]
    public async Task LobbyListsActiveAvailableAndLockedModifiersThenTheRunUnlocksThem() {
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
        var texts = Texts(lobby);
        var order = ((string[])["RUN MODIFIERS", "ACTIVE THIS RUN", "Understaffed night shift", "Chart mix-up", "AVAILABLE",
            "Broken heating", "LOCKED"]).Select(text => texts.IndexOf(text)).ToList();
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
        var card = Find<Label>(lobby, "Doctor (you)").GetParent<Control>().GetGlobalRect();
        var column = Find<Label>(lobby, "RUN MODIFIERS").GetParent<Control>().GetGlobalRect();
        AssertBool(column.Position.X >= card.End.X && column.Position.Y < card.End.Y)
            .OverrideFailureMessage($"the modifiers column is right of the surgeon's card: {column} beside {card}").IsTrue();
        await Capture("lobby", "the pre-op briefing for Appendectomy: the surgeon's card on the left and, right of it, a "
            + "RUN MODIFIERS column with ACTIVE THIS RUN listing Understaffed night shift and Chart mix-up in yellow, "
            + "AVAILABLE with Broken heating, each with a grey explanation, then LOCKED with "
            + $"\"{locked.Count} more\" and the unlock condition; Ready, Start surgery and Leave buttons below");

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
