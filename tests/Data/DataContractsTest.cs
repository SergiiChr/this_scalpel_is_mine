using System.Text.RegularExpressions;

namespace Scalpel.Tests.Data;

/// <summary>The data files the game reads at startup hold together.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
public partial class DataContractsTest
{
    private static IEnumerable<ScenarioDef> AllScenarios => Db.Scenarios.Concat(Db.DisabledScenarios);

    private static ManualPage ConditionsPage => Db.Manual.First(page => page.Children.Count > 0);

    [TestCase]
    public void EveryMainMenuScenarioHasObjectives()
    {
        AssertInt(Db.Scenarios.Count).OverrideFailureMessage("at least one scenario is registered").IsGreater(0);
        foreach (var scenario in AllScenarios)
        {
            AssertString(scenario.Id).OverrideFailureMessage("scenario id is present").IsNotEmpty();
            AssertInt(scenario.Steps.Count(step => !step.Optional))
                .OverrideFailureMessage($"{scenario.Id} has a required objective").IsGreater(0);
        }
    }

    [TestCase]
    public void EveryToolIsFoundByItsOwnId()
    {
        foreach (var id in Db.Tools.Keys)
        {
            AssertString(Db.Tool(id)!.Id).OverrideFailureMessage($"tool {id} knows its id").IsEqual(id);
        }
    }

    [TestCase]
    public void ScenarioIdsAreUnique()
    {
        foreach (var group in AllScenarios.GroupBy(scenario => scenario.Id))
        {
            AssertInt(group.Count())
                .OverrideFailureMessage($"scenario id {group.Key} is used once (file names differ in more than their number)")
                .IsEqual(1);
        }
    }

    [TestCase]
    public void EveryToolAScenarioListsExists()
    {
        foreach (var scenario in AllScenarios)
        {
            foreach (var id in scenario.StartingTools.Concat(scenario.RandomTools))
            {
                AssertObject(Db.Tool(id)).OverrideFailureMessage($"{scenario.Id} lists tool {id}").IsNotNull();
            }
        }
    }

    [TestCase]
    public void EveryChronicConditionHasAManualEntryQuotingItsChartLine()
    {
        var entries = ConditionsPage.Children;
        AssertInt(entries.Count).OverrideFailureMessage("the manual has a chronic conditions section").IsGreater(0);
        foreach (var quirk in Db.PatientQuirks.Values)
        {
            foreach (var variant in quirk.Variants.Count > 0 ? quirk.Variants : [""])
            {
                var card = quirk.Text("card", variant);
                // A strong heart ("Marathon runner") is on the chart but isn't a condition to manage.
                if (card.Length == 0 || quirk.IsRedHerring || quirk.Polarity(variant) == "positive")
                {
                    continue;
                }
                var key = variant.Length > 0 ? $"{quirk.Id}.{variant}" : quirk.Id;
                var entry = entries.LastOrDefault(page => page.Tags.Contains(key) || page.Tags.Contains(quirk.Id));
                AssertObject(entry).OverrideFailureMessage($"{card} has a chronic conditions entry tagged {key}").IsNotNull();
                if (entry is not null)
                {
                    AssertString(entry.Body).OverrideFailureMessage($"the {entry.Title} entry quotes the chart line")
                        .Contains($"“{card}”");
                }
            }
        }
    }

    [GeneratedRegex(@"See Chronic conditions: ([^.\n]+)\.")]
    private static partial Regex Pointer();

    [TestCase]
    public void ManualPointersNameExistingConditionEntries()
    {
        var titles = ConditionsPage.Children.Select(page => page.Title).ToList();
        foreach (var page in Db.Manual)
        {
            foreach (Match found in Pointer().Matches(page.Body))
            {
                foreach (var title in found.Groups[1].Value.Split(", "))
                {
                    AssertArray(titles).OverrideFailureMessage($"{page.Title} points to an existing condition entry")
                        .Contains(title);
                }
            }
        }
    }

    [TestCase]
    public void SiteBoundQuirksRollOnlyOnTheirSites()
    {
        var rng = new RandomNumberGenerator { Seed = 3 };
        var rolledSomewhere = false;
        foreach (var scenario in AllScenarios)
        {
            for (var i = 0; i < 60; i++)
            {
                foreach (var roll in QuirkRoller.RollPatient(scenario, rng))
                {
                    AssertBool(Db.PatientQuirks[roll.Id].FitsSite(scenario.Site))
                        .OverrideFailureMessage($"{roll.Id} rolls only on its sites, not on {scenario.Site} ({scenario.Id})")
                        .IsTrue();
                    rolledSomewhere |= roll.Id == "aneurysm";
                }
            }
        }
        AssertBool(rolledSomewhere).OverrideFailureMessage("an aneurysm rolls on a great-vessel site").IsTrue();
    }

    /// <summary>A number with a unit written straight into a page.</summary>
    [GeneratedRegex(@"(?i)(\b\d+(\.\d+)?|\b(one|two|three|four|five|six|ten|fifteen|twenty|thirty|forty|fifty|sixty)) ?((mmHg|mmol/l|°C|mg|units|ml/s|ml|seconds?|minutes?|kg|points|times)\b|%)")]
    private static partial Regex HandWrittenNumber();

    [TestCase]
    public void ManualNumbersComeFromTheGame()
    {
        var filled = ManualPage.FillNumbers(
            "{Patient.HighPressure} mmHg for {drug.diazepam.Duration / 60} min, {quirk.heart_weak.arrest_mult}x", "test");
        var arrest = Modifiers.Number(Db.PatientQuirks["heart"].Effects("weak")["arrest_mult"])!.Value;
        var expected = $"{Patient.HighPressure:0} mmHg for {Db.Drug("diazepam")!.Duration / 60f:0} min, {arrest:0.##}x";
        AssertString(filled).OverrideFailureMessage("manual numbers are worked out from constants, drugs and quirk effects")
            .IsEqual(expected);
        // A number with a unit written straight into a page would go stale when the game changes: it has to be "{...}".
        foreach (var page in Db.Manual.SelectMany(page => page.Children.Prepend(page)))
        {
            AssertString(page.Body).OverrideFailureMessage($"{page.Title} has no number left to work out").NotContains("{");
            var text = ManualPage.NumberPattern().Replace(page.Source, "").Split("[font_size=16][b]References")[0];
            foreach (Match found in HandWrittenNumber().Matches(text))
            {
                AssertBool(false).OverrideFailureMessage($"{page.Title} writes \"{found.Value}\" by hand: use a {{...}} game number")
                    .IsTrue();
            }
        }
    }
}
