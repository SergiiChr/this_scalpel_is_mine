namespace Scalpel.Tests.Tools;

/// <summary>Every tool a hand can hold, used like a player: taken off the instrument tray, brought to the patient and
/// lowered onto the site with Use tool, then put back on the tray. What each tool then does is tested with its feature
/// (tissue, liquids, the scenario flows).</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("slow"), TestCategory("smoke"), TestCategory("tool_all")]
[GodotArgs("--fixed-fps", "60")]
public class ToolLifecycleTest
{
    [TestCase(Timeout = Limits.Slow)]
    public async Task EveryHandheldToolIsPickedUpLoweredOntoTheSiteAndPutBack()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var surgery = driver.Surgery;
        SurgeryState.PatientIsAsleep(driver.Patient);
        var site = driver.SitePoint(new Vector2(0.5f, 0.5f));
        foreach (var id in Db.Tools.Keys.Where(id => !Db.Tool(id)!.Fixed).Order(StringComparer.Ordinal))
        {
            // A fully stocked tray: whatever isn't on it is laid in its clear strip, and taken away again after.
            var extra = driver.FreeTools(id).Count == 0;
            if (extra)
            {
                SurgeryState.ToolIsOnTray(surgery, id);
                await Frames.Physics(10);
            }
            var hand = driver.Me.Hands[driver.Me.Active];
            // Aim the empty hand down before selecting a tool, as a player can.
            var aim = new Vector2(hand.Turn - 0.12f, hand.Tilt - (SurgeonHand.RestTilt + 0.05f))
                / (Surgeon.AimSensitivity * Settings.MouseSensitivity);
            await driver.PlayerAims(aim, 1);
            SurgeryDriver.PlayerLetsGoOfAim();
            await Frames.Physics(10);
            var tilt = hand.Tilt;
            var turn = hand.Turn;
            var tool = await driver.PlayerRequestsItem(id);
            AssertBool(tool?.Def.Id == id && tool.State == ToolState.Held)
                .OverrideFailureMessage($"{id} is picked up\n{driver.Recent(4)}").IsTrue();
            if (tool is null || tool.Def.Id != id)
            {
                continue;
            }
            // A needle holder and a syringe come up in their own carry pose (SurgeonHand.DefaultTilt(), FaceSyringe()).
            if (tool.Def.Grip is not ("needle" or "syringe"))
            {
                if (tool.Def.Action != "spread")
                {
                    AssertFloat(hand.Tilt).OverrideFailureMessage($"{id} pickup preserves the player's tilt").IsEqual(tilt);
                }
                else
                {
                    AssertFloat(hand.Tilt).OverrideFailureMessage("the spreader is held tipped toward the skin, its points down")
                        .IsEqual(SurgeonHand.SpreaderTilt);
                }
                AssertFloat(hand.Turn).OverrideFailureMessage($"{id} pickup preserves the player's turn").IsEqual(turn);
            }
            await driver.PlayerWalksTo(site);
            await driver.PlayerReaches(site);
            SurgeryDriver.Use();
            var touched = false;
            for (var i = 0; i < 30; i++)
            {
                await Frames.Physics(1);
                touched |= driver.Body.Probe(tool.TipPosition()).Zone is SiteZone.Site or SiteZone.Cavity or SiteZone.Body;
            }
            SurgeryDriver.Use(false);
            AssertBool(touched).OverrideFailureMessage($"{id} comes down onto the site").IsTrue();
            if (tool.State == ToolState.Held)
            {
                await driver.PlayerPutsDown();
                AssertBool(driver.LiesOnTray(tool))
                    .OverrideFailureMessage($"{id} is put back on the tray ({tool.Middle()})\n{driver.Recent(4)}").IsTrue();
            }
            if (extra)
            {
                surgery.Tools.Consume(tool);
                await Frames.Physics(2);
            }
        }
        await driver.Stop();
    }

    [TestCase]
    public async Task AHeldToolMovesWithTheHandWhileWalking()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var scalpel = (await driver.PlayerRequestsItem("scalpel"))!;
        // Between the table and the delivery tray, walking sideways along the table over clear floor.
        await driver.PlayerWalksTo(new Vector3(0f, 0f, 1.6f), 0.4f);
        var hand = driver.Me.Hands[driver.Me.Active];
        var start = driver.Me.GlobalPosition;
        var worst = 0f;
        await driver.PlayerHoldsWalkKey(InputActions.MoveRight, 0.5f,
            () => worst = Mathf.Max(worst, scalpel.GlobalPosition.DistanceTo(hand.GripTransform().Origin)));
        AssertFloat(driver.Me.GlobalPosition.DistanceTo(start)).OverrideFailureMessage("the surgeon walks").IsGreater(0.5f);
        AssertFloat(worst)
            .OverrideFailureMessage($"the scalpel stays in the hand every frame of the walk ({worst * 1000f:0.0} mm off at worst)")
            .IsLess(0.001f);
        await driver.Stop();
    }

    [TestCase]
    public async Task DrinksAndCigarettesAreUsedUpOneAtATime()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        foreach (var def in Db.Tools.Values.Where(def => def.Drinkable || def.Id == "cig_pack"))
        {
            SurgeryState.ToolIsOnTray(driver.Surgery, def.Id);
            await Frames.Physics(10);
            var tool = (await driver.PlayerRequestsItem(def.Id))!;
            var before = tool.Charges;
            if (def.Drinkable)
            {
                SurgeryDriver.Press(InputActions.Drink);
            }
            else
            {
                await driver.PlayerInteracts("Smoke a cigarette");
            }
            await Frames.Seconds(1f);
            AssertInt(tool.Charges).OverrideFailureMessage($"{def.Id} is used once").IsEqual(before - 1);
            await driver.PlayerPutsDown();
        }
        await driver.Stop();
    }

    [TestCase]
    public void FixedToolsHaveAStationInteractionInsteadOfAHandLifecycle()
    {
        var fixedTools = Db.Tools.Values.Where(def => def.Fixed).ToList();
        AssertArray(fixedTools.Select(def => def.Id).ToList()).OverrideFailureMessage("only the IV drip is fixed")
            .IsEqual(new List<string> { "iv_drip" });
        AssertString(fixedTools[0].Action)
            .OverrideFailureMessage("the fixed IV bag has the line interaction covered by the liquids suite").IsEqual("drip");
    }

    [TestCase]
    public async Task AToolDroppedOnTheFloorIsSoiled()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var scalpel = (await driver.PlayerRequestsItem("scalpel"))!;
        // Away from the table and the tray, over bare floor.
        var floorSpot = new Vector3(0f, 0f, 1.6f);
        await driver.PlayerWalksTo(floorSpot, 0.4f);
        await driver.PlayerReaches(floorSpot);
        SurgeryDriver.Press(InputActions.Grab);
        await Frames.Seconds(2f);
        SurgeryDriver.Release(InputActions.Grab);
        AssertThat(scalpel.State).OverrideFailureMessage("the scalpel is let go").IsEqual(ToolState.Free);
        AssertBool(scalpel.Soiled && !scalpel.Sterile)
            .OverrideFailureMessage("a scalpel dropped on the floor is soiled and no longer sterile").IsTrue();
        await driver.Stop();
    }
}
