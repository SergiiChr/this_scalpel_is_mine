namespace Scalpel.Tests.UI;

/// <summary>
/// The nurse's shop as a player uses it: the bell opens categories with their items as a list, [+] adds to the cart up
/// to five items (the same one twice too), [-] takes one back out, and Place order sends the cart as one batch. It takes
/// as long as its slowest item, and every item lands on the delivery tray. While the nurse is out, Place order waits.
/// With key frames also the shop with a full cart and the delivery tray afterwards. Review them for legible rows, the
/// picked category lit, the cart's lines and counts, and the five items lying side by side on the tray, not on or in
/// each other.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class NurseShopTest
{
    /// <summary>Two of the same, from three categories, and a sixth that doesn't fit.</summary>
    private static readonly string[] Wanted = ["gauze", "gauze", "scalpel", "hemostat", "vial_propofol", "needle"];
    private static readonly string[] Ordered = ["gauze", "gauze", "scalpel", "hemostat", "vial_propofol"];

    [TestCase]
    public async Task CartOfFiveWithARepeatIsDeliveredAsOneBatch()
    {
        RenderingServer.RenderLoopEnabled = false;
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var surgery = driver.Surgery;
        var deliveryTray = surgery.Room.Layout["delivery_tray"];
        KeyFrames? shots = null;
        if (KeyFrames.Wanted())
        {
            shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(surgery, "nurse_shop");
            // The shop as the player sees it; the delivery tray from above and obliquely.
            driver.OnKeyFrame = async keyFrame =>
            {
                var saved = keyFrame == "cart_full"
                    ? await shots.CaptureView(keyFrame)
                    : await shots.CaptureAt(keyFrame, deliveryTray + new Vector3(0f, 0.92f, 0f), 0.6f);
                AssertBool(saved).OverrideFailureMessage($"saved key frame {keyFrame}").IsTrue();
            };
        }
        driver.Budget.Clear();
        var before = surgery.Tools.Tools.Keys.ToHashSet();
        var shop = surgery.Hud.Shop;

        await driver.PlayerFillsCart(Wanted);
        AssertBool(shop.RowOf("needle").Add.Disabled)
            .OverrideFailureMessage($"[+] is off once the cart holds {Nurse.Batch}").IsTrue();
        var shown = string.Join("\n", surgery.Hud.FindChildren("*", nameof(Label), true, false)
            .OfType<Label>().Where(label => label.IsVisibleInTree()).Select(label => label.Text));
        AssertString(shown).OverrideFailureMessage("the cart counts five").Contains("Cart  5 / 5");
        AssertString(shown).OverrideFailureMessage("the same item twice shows as one line with its count").Contains("Gauze  ×2");
        AssertString(shown).OverrideFailureMessage("the sixth item didn't fit").NotContains("Suture needle  ×");
        await driver.Capture("cart_full");
        // [-] takes one back out, [+] puts it back, so a full cart can still change.
        driver.PlayerPicksCategory(Db.Tool("gauze")!.Category);
        driver.PlayerRemovesFromCart("gauze");
        AssertBool(shop.RowOf("gauze").Add.Disabled).OverrideFailureMessage("[-] makes room in a full cart").IsFalse();
        SurgeryDriver.Click(shop.RowOf("gauze").Add);
        AssertBool(shop.RowOf("gauze").Add.Disabled).OverrideFailureMessage("[+] fills it again").IsTrue();
        await driver.PlayerPlacesOrder();
        AssertBool(shop.View.Visible).OverrideFailureMessage("placing the order closes the shop").IsFalse();

        var order = surgery.Nurse.Current;
        AssertObject(order).OverrideFailureMessage("the nurse takes the cart as one order").IsNotNull();
        if (order is not { } placed)
        {
            await Stop(driver, shots);
            return;
        }
        AssertString(placed.Text).OverrideFailureMessage("the board names the batch with its repeats")
            .IsEqual("Gauze ×2, Scalpel, Hemostat clamp, Propofol 10 mg/ml, 50 ml");
        var slowest = Ordered.Max(id => Nurse.DeliveryTime(Db.Tool(id)!, driver.Me, surgery));
        AssertFloat(placed.SecondsLeft).OverrideFailureMessage("the batch takes as long as its slowest item")
            .IsEqualApprox(slowest, 0.1f);
        // Back at the bell while she's out: the next cart waits for her, it isn't sent and lost.
        await driver.PlayerFillsCart(["gauze"]);
        await Frames.Seconds(Surgery.StatusInterval + 0.1f);
        AssertBool(shop.PlaceButton.Disabled).OverrideFailureMessage("Place order waits while the nurse is fetching").IsTrue();
        await Frames.Until(() => surgery.Nurse.Current is null, slowest + 5f);
        await Frames.Seconds(Surgery.StatusInterval + 0.1f);
        AssertBool(shop.PlaceButton.Disabled).OverrideFailureMessage("Place order comes back once she's delivered").IsFalse();
        surgery.Hud.CloseOverlay();
        await Frames.Seconds(2f);
        var delivered = surgery.Tools.Tools.Where(entry => !before.Contains(entry.Key)).Select(entry => entry.Value).ToList();
        AssertArray(delivered.Select(tool => tool.Def.Id).Order().ToList())
            .OverrideFailureMessage("everything in the cart is delivered, both gauzes too")
            .IsEqual(Ordered.Order().ToList());
        foreach (var tool in delivered)
        {
            var off = new Vector2(tool.GlobalPosition.X - deliveryTray.X, tool.GlobalPosition.Z - deliveryTray.Z).Length();
            AssertBool(tool.GlobalPosition.Y > 0.85f && off < 0.35f)
                .OverrideFailureMessage($"the {tool.Def.Id} lands on the delivery tray ({off:0.00} m off, {tool.GlobalPosition.Y:0.00} m up)")
                .IsTrue();
        }
        // Side by side, not on each other: no two of them rest on top of one another.
        foreach (var a in delivered)
        {
            foreach (var b in delivered.Where(b => b != a))
            {
                var apart = a.Middle() - b.Middle();
                AssertBool(Mathf.Abs(apart.Y) > 0.015f && apart.Slide(Vector3.Up).Length() < 0.06f)
                    .OverrideFailureMessage($"the {a.Def.Id} doesn't lie on the {b.Def.Id}").IsFalse();
            }
        }
        await driver.Capture("delivered");
        await Stop(driver, shots);
    }

    private static async Task Stop(SurgeryDriver driver, KeyFrames? shots)
    {
        driver.Budget.Check(shots is not null);
        if (shots is not null)
        {
            GD.Print($"key frames: {shots.OutDir}");
            shots.End();
        }
        await driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }
}
