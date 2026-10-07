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
}
