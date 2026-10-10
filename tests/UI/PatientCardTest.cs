namespace Scalpel.Tests.UI;

/// <summary>
/// The patient card as a player meets it: the surgery opens with the card held up to the face and its page shown. Put
/// back with Esc, it travels in view to its hook on the table's head rail and hangs there. Looked at, it's what's in
/// focus, not the patient's head beside it. Taken with Interact, it leaves the hook and comes back up to the face before
/// the page shows. With key frames also what the surgeon sees at each step and the card on its hook up close. Review
/// them for a legible, form-like page, the card in view all the way to the hook and hanging on the rail.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class PatientCardTest
{
    [TestCase]
    public async Task OpensInHandTravelsToItsHookAndBackWhenTaken()
    {
        RenderingServer.RenderLoopEnabled = false;
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy", readingCard: true);
        var surgery = driver.Surgery;
        var card = surgery.Room.Card;
        var me = driver.Me;
        KeyFrames? shots = null;
        if (KeyFrames.Wanted())
        {
            shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(surgery, "patient_card");
            driver.OnKeyFrame = async (keyFrame, description) =>
            {
                await shots.CaptureView(keyFrame, description);
                if (keyFrame == "on_hook")
                {
                    await shots.CaptureAt("on_hook_close", "close up, the card hangs flat on its hook, not in the wall", card.HookPaper, 0.6f);
                }
            };
        }
        driver.Budget.Clear();

        AssertBool(surgery.Hud.CardOpen && me.InputLocked).OverrideFailureMessage("the surgery opens on the card's page")
            .IsTrue();
        AssertBool(card.OnHook).OverrideFailureMessage("the card isn't on its hook while it's read").IsFalse();
        AssertHeldUp(card, me, "at the start");
        var page = string.Join("\n", surgery.Hud.Overlay!.FindChildren("*", nameof(Label), true, false)
            .OfType<Label>().Select(label => label.Text));
        AssertString(page).OverrideFailureMessage("the page reads as a patient record").Contains("Patient Record");
        AssertString(page).OverrideFailureMessage("the page shows the complaint").Contains(surgery.Scenario.Complaint);
        await driver.Capture("in_hand", "the card held up in front of the surgeon, its page readable as a patient record");

        SurgeryDriver.Tap(InputActions.Pause);
        await PlayerInput.Delivered();
        AssertBool(surgery.Hud.CardOpen || me.InputLocked).OverrideFailureMessage("Esc puts the card down").IsFalse();
        await Frames.Until(() => card.TravelOf(me.PeerId) <= 0.5f, PatientCard.TravelSeconds);
        AssertBool(card.PaperOf(me.PeerId) is { } midway && me.Camera.IsPositionInFrustum(midway))
            .OverrideFailureMessage("half way the card is in view").IsTrue();
        await driver.Capture("on_its_way", "the card half way from the hand to its hook, in view");
        AssertBool(await Frames.Until(() => card.OnHook, PatientCard.TravelSeconds + 0.5f))
            .OverrideFailureMessage("the card ends up on its hook").IsTrue();
        AssertObject(card.PaperOf(me.PeerId)).OverrideFailureMessage("only the card on the hook is left").IsNull();
        await driver.Capture("on_hook", "the card on its hook, nothing left in the hand");

        // Like a player: walk up, look at it and press Interact. Next to the patient's head, the card is what's in focus.
        await driver.PlayerWalksTo(card.HookPaper, 0.6f);
        await driver.PlayerLooksAt(card.HookPaper);
        AssertString(me.Focused?.Prompt).OverrideFailureMessage("looking at the card on its hook offers it")
            .IsEqual("Read the patient card");
        SurgeryDriver.Tap(InputActions.Interact);
        await PlayerInput.Delivered();
        AssertBool(await Frames.Until(() => !card.OnHook, 0.1f)).OverrideFailureMessage("taken, the card leaves its hook")
            .IsTrue();
        AssertFloat(surgery.Hud.Overlay!.Modulate.A).OverrideFailureMessage("the page waits for the card to come up")
            .IsLess(0.5f);
        AssertBool(await Frames.Until(() => surgery.Hud.Overlay?.Modulate.A >= 1f, PatientCard.TravelSeconds + 0.5f))
            .OverrideFailureMessage("the page shows once the card is up").IsTrue();
        AssertHeldUp(card, me, "taken again");
        await driver.Capture("taken", "taken again, the card held up with its page showing");

        await SurgeryDriver.PlayerPutsCardBack(surgery);
        AssertBool(card.OnHook).OverrideFailureMessage("put back again, it hangs on its hook").IsTrue();
        driver.Budget.Check(shots is not null);
        if (shots is not null)
        {
            GD.Print($"key frames: {shots.OutDir}");
            shots.End();
        }
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }

    /// <summary>The start is the guide to where the card lives: from wherever a surgeon starts, the hook is in view, so
    /// the card is seen all the way there.</summary>
    [TestCase("appendectomy")]
    [TestCase("sidewalk_stab")]
    [TestCase("ambulance_bullet")]
    public async Task HookIsInViewFromEverySpawn(string scenario)
    {
        var driver = SurgeryDriver.Create();
        await driver.Start(scenario, readingCard: true);
        var partner = SurgeryState.PartnerIsAtTheTable(driver.Surgery);
        await Frames.Physics(2);
        var hook = driver.Surgery.Room.Card.HookPaper;
        foreach (var surgeon in (Surgeon[])[driver.Me, partner])
        {
            AssertBool(surgeon.Camera.IsPositionInFrustum(hook))
                .OverrideFailureMessage($"{scenario}: the hook is in view from spawn {surgeon.PeerId}").IsTrue();
        }
        await driver.Stop();
    }

    /// <summary>A partner who leaves without the card out takes nothing along: the card stays in the reader's hands and
    /// doesn't turn up on the hook as well.</summary>
    [TestCase]
    public async Task PartnerLeavingDoesNotDuplicateTheCard()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var surgery = driver.Surgery;
        var card = surgery.Room.Card;
        SurgeryState.PartnerIsAtTheTable(surgery);
        AssertBool(await driver.PlayerInteracts("Read the patient card")).IsTrue();
        AssertBool(await Frames.Until(() => !card.OnHook, 0.1f)).OverrideFailureMessage("the reader has the card").IsTrue();
        surgery.Multiplayer.EmitSignal(MultiplayerApi.SignalName.PeerDisconnected, 2L);
        await Frames.Process(3);
        AssertBool(surgery.Surgeons.ContainsKey(2)).OverrideFailureMessage("the partner left").IsFalse();
        AssertBool(card.OnHook).OverrideFailureMessage("no second card appears on the hook").IsFalse();
        await driver.Stop();
    }

    /// <summary>The card's paper is right in front of the eyes, facing them.</summary>
    private static void AssertHeldUp(PatientCard card, Surgeon me, string when)
    {
        var eyes = me.Camera.GlobalTransform;
        var paper = card.PaperOf(me.PeerId);
        AssertObject(paper).OverrideFailureMessage($"{when} the card is out of the hook").IsNotNull();
        if (paper is { } at)
        {
            var ahead = (at - eyes.Origin).Dot(-eyes.Basis.Z);
            AssertFloat(ahead).OverrideFailureMessage($"{when} the card is held up in front of the eyes")
                .IsBetween(0.2f, 0.4f);
            AssertFloat((at - eyes.Origin).Slide(eyes.Basis.Z).Length())
                .OverrideFailureMessage($"{when} the card is in the middle of the view").IsLess(0.02f);
        }
    }
}
