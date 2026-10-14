namespace Scalpel.Tests.UI;

/// <summary>The manual's chronic conditions section as a reader opens it: listed as one entry, its conditions show
/// under it once it's open, and Divine knowledge points at the patient's condition through both levels.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
public class ManualTest
{
    [TestCase]
    public void ConditionsOpenUnderTheirSectionAndDivineKnowledgePointsThrough()
    {
        var manual = new ManualView(["aneurysm"], () => { });
        AutoFree(manual.View);
        Frames.Root.AddChild(manual.View);
        Button Entry(string title) => manual.Entries.First(entry => entry.Page.Title == title).Button;
        var section = Entry("17. Chronic conditions");
        var aneurysm = Entry("Aneurysm");
        var pacemaker = Entry("Implanted pacemaker");
        AssertBool(aneurysm.Visible).OverrideFailureMessage("conditions are hidden until their section is open").IsFalse();
        AssertString(section.Text).OverrideFailureMessage("the section points at the patient's condition inside it")
            .StartsWith("☞");
        SurgeryDriver.Click(section);
        AssertBool(aneurysm.Visible && pacemaker.Visible).OverrideFailureMessage("opening the section lists its conditions")
            .IsTrue();
        AssertString(aneurysm.Text).OverrideFailureMessage("the patient's condition is pointed at").Contains("☞");
        AssertString(pacemaker.Text).OverrideFailureMessage("other conditions are not").NotContains("☞");
        SurgeryDriver.Click(aneurysm);
        var page = manual.View.FindChildren("*", nameof(RichTextLabel), true, false).OfType<RichTextLabel>().First();
        AssertString(page.Text).OverrideFailureMessage("the entry shows the chart line to look for")
            .Contains("Known history of aneurysm");
        AssertBool(aneurysm.Visible).OverrideFailureMessage("the section stays open while one of its conditions is read")
            .IsTrue();
        SurgeryDriver.Click(Entry("1. Preoperative preparation"));
        AssertBool(aneurysm.Visible).OverrideFailureMessage("opening another page closes the section").IsFalse();
    }

    /// <summary>Drug allergy lists its drugs under it once it's open, and each drug's page gives its alternatives and
    /// what its reaction looks like.</summary>
    [TestCase]
    public void AllergyDrugsOpenUnderDrugAllergy()
    {
        var manual = new ManualView([], () => { });
        AutoFree(manual.View);
        Frames.Root.AddChild(manual.View);
        Button Entry(string title) => manual.Entries.First(entry => entry.Page.Title == title).Button;
        var drugs = ((string[])["Lidocaine", "Morphine", "Propofol"]).Select(Entry).ToList();
        SurgeryDriver.Click(Entry("17. Chronic conditions"));
        AssertBool(drugs.Any(drug => drug.Visible)).OverrideFailureMessage("the drugs are hidden until Drug allergy is open")
            .IsFalse();
        SurgeryDriver.Click(Entry("Drug allergy"));
        AssertBool(drugs.All(drug => drug.Visible)).OverrideFailureMessage("opening Drug allergy lists its drugs").IsTrue();
        SurgeryDriver.Click(Entry("Lidocaine"));
        var page = manual.View.FindChildren("*", nameof(RichTextLabel), true, false).OfType<RichTextLabel>().First();
        AssertString(page.Text).OverrideFailureMessage("a drug's page lists its alternatives").Contains("Alternatives");
        AssertString(page.Text).OverrideFailureMessage("a drug's page says what its reaction looks like").Contains("Reaction");
        AssertBool(drugs.All(drug => drug.Visible) && Entry("Aneurysm").Visible)
            .OverrideFailureMessage("the other drugs and conditions stay listed while one drug is read").IsTrue();
        SurgeryDriver.Click(Entry("Aneurysm"));
        AssertBool(drugs.Any(drug => drug.Visible)).OverrideFailureMessage("opening another condition closes the drugs")
            .IsFalse();
    }
}
