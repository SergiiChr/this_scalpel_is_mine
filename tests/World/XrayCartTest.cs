namespace Scalpel.Tests.World;

/// <summary>The X-ray cart as a player handles it: interact on the handle pushes it, the wheel raises and lowers the
/// arm, a click locks it against the floor, and looking at the tube head the wheel still moves the arm.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class XrayCartTest {
    // Temporary local budget override: recorded CPU overruns on the usual test machine remain measured.
    private static readonly BudgetAllowance BudgetOverride = new("known to go over the frame budget, not profiled yet", 4.5f);

    /// <summary>How far (meters) the cart's view is from it: the whole cart in view, under the ceiling.</summary>
    private const float CartView = 2.2f;

    [TestCase]
    public async Task PushingRaisingAndLockingTheCart() {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        KeyFrames? shots = null;
        try {
            await driver.Start("hand_stitch");
            var me = driver.Me;
            var cart = driver.Surgery.Room.Xray!;
            var spots = cart.FindChildren("*", "", true, false).OfType<Interactable>().ToList();
            var handle = spots.Single(spot => spot.Prompt.StartsWith("Push"));
            var tubeHead = spots.Single(spot => spot.Prompt == "Take an X-ray");
            if (KeyFrames.Wanted()) {
                shots = new KeyFrames();
                driver.AddChild(shots);
                shots.Begin(driver.Surgery, "xray_cart");
                await CaptureCart(shots, cart, "untouched",
                    "the upright X-ray cart: a compact base with casters in front and wheels under the generator, a tall "
                        + "column left of the middle, a level arm curving from its top round to the middle, the tube head "
                        + "hanging under its end with a screen on its back, the red pedal down");
            }
            driver.Budget.Clear();
            await driver.PlayerWalksTo(handle.GlobalPosition, 0.6f);
            await driver.PlayerLooksAt(handle.GlobalPosition);
            await Frames.Physics(2);
            AssertObject(me.Focused)
                .OverrideFailureMessage($"looking at the handle offers to push the cart: standing at {me.GlobalPosition}, "
                    + $"the handle at {handle.GlobalPosition}, focused on {me.Focused?.Prompt}")
                .IsSame(handle);
            AssertBool(SurgeryDriver.IsLit(handle) && !SurgeryDriver.IsLit(tubeHead))
                .OverrideFailureMessage("looking at the handle lights the handle up, and only that").IsTrue();
            if (shots is not null) {
                await driver.Unbudgeted(() => shots.CaptureView("handle_in_view",
                    "the cart's handle loop lit up warmer than the rest of the cart, the prompt to push it"));
            }
            PlayerInput.Tap(InputActions.Interact);
            await PlayerInput.Delivered();
            await Frames.Physics(2);
            AssertInt(cart.Pusher).OverrideFailureMessage("interact on the handle pushes the cart").IsEqual(me.PeerId);
            var start = cart.GlobalPosition;
            await driver.PlayerHoldsWalkKey(InputActions.MoveBack, 0.8f, () => { });
            await Frames.Physics(30);
            AssertFloat(cart.GlobalPosition.DistanceTo(start)).OverrideFailureMessage("the pushed cart follows the surgeon")
                .IsGreater(0.3f);
            var ahead = me.ToLocal(cart.GlobalPosition);
            AssertBool(ahead.Z < -0.6f && Mathf.Abs(ahead.X) < 0.1f)
                .OverrideFailureMessage($"the cart stays in front of its pusher: {ahead}").IsTrue();

            var head = cart.FindChild("Head", true, false) as Node3D;
            var high = cart.ToLocal(head!.GlobalPosition);
            for (var i = 0; i < 4; i++) {
                PlayerInput.Tap(InputActions.LevelDown);
                await PlayerInput.Delivered();
            }
            await Frames.Physics(40);
            var lowered = XrayCart.ArmRest - (4 * XrayCart.ArmStep);
            AssertFloat(cart.Arm).OverrideFailureMessage("four wheel steps down lower the arm four steps")
                .IsEqualApprox(lowered, 0.001f);
            var low = cart.ToLocal(head.GlobalPosition);
            AssertFloat(high.Y - low.Y).OverrideFailureMessage("the tube head comes down with the arm")
                .IsEqualApprox(4 * XrayCart.ArmStep, 0.01f);
            AssertFloat(new Vector2(high.X - low.X, high.Z - low.Z).Length())
                .OverrideFailureMessage($"the tube head comes straight down, no nearer or further: from {high} to {low}")
                .IsLess(0.001f);
            AssertFloat(Mathf.Abs(head.GlobalRotation.X)).OverrideFailureMessage("the tube head stays level")
                .IsLess(0.01f);
            if (shots is not null) {
                await driver.Unbudgeted(() => CaptureCart(shots, cart, "lowered",
                    "the cart pushed away from its corner, the arm slid down the column, the tube head still level, the pedal up"));
            }

            PlayerInput.Tap(InputActions.UseTool);
            await PlayerInput.Delivered();
            await Frames.Physics(2);
            AssertInt(cart.Pusher).OverrideFailureMessage("a click locks the cart and lets go of it").IsEqual(0);
            var locked = cart.GlobalPosition;
            await driver.PlayerHoldsWalkKey(InputActions.MoveBack, 0.5f, () => { });
            await Frames.Physics(10);
            AssertFloat(cart.GlobalPosition.DistanceTo(locked)).OverrideFailureMessage("the locked cart stays put")
                .IsLess(0.001f);

            // On the head's right, clear of the arm that curves to it from the column on its left.
            await driver.PlayerWalksTo(tubeHead.GlobalPosition + (cart.GlobalBasis.X * 0.75f), 0f);
            await driver.PlayerLooksAt(tubeHead.GlobalPosition);
            await Frames.Physics(2);
            AssertObject(me.Focused)
                .OverrideFailureMessage($"looking at the tube head offers to take an X-ray: standing at {me.GlobalPosition}, "
                    + $"the head at {tubeHead.GlobalPosition}, focused on {me.Focused?.Prompt}")
                .IsSame(tubeHead);
            AssertBool(SurgeryDriver.IsLit(tubeHead) && !SurgeryDriver.IsLit(handle))
                .OverrideFailureMessage("looking at the tube head lights the head up and the handle goes back").IsTrue();
            AssertFloat(cart.GlobalPosition.DistanceTo(locked)).OverrideFailureMessage("walking up to it doesn't move the locked cart")
                .IsLess(0.001f);
            PlayerInput.Tap(InputActions.LevelUp);
            await PlayerInput.Delivered();
            await Frames.Physics(40);
            AssertFloat(cart.Arm).OverrideFailureMessage("locked, the wheel on the tube head still raises the arm")
                .IsEqualApprox(lowered + XrayCart.ArmStep, 0.001f);
            if (shots is not null) {
                await driver.Unbudgeted(() => shots.CaptureView("head_in_view",
                    "the tube head in the middle of the view, lit up warmer than the arm it hangs from, the prompt to "
                        + "take an X-ray with the wheel to raise or lower the arm"));
                await driver.Unbudgeted(() => shots.CaptureFacing("locked",
                    "the cart from behind, its handle loop in front, the red pedal right of middle pressed down",
                    cart.ToGlobal(new Vector3(0f, 0.5f, 0.3f)), cart.GlobalBasis.Z, 1.4f));
            }
            driver.Budget.Check(localOverride: BudgetOverride);
        } finally {
            shots?.End();
            await driver.Stop();
            RenderingServer.RenderLoopEnabled = true;
        }
    }

    /// <summary>A view of the cart's side from 45° above, from the side toward the middle of the room: the column, the
    /// arm and the tube head side by side, and in its corner the view isn't inside the wall.</summary>
    private static Task CaptureCart(KeyFrames shots, XrayCart cart, string keyFrame, string description) {
        var side = cart.GlobalBasis.X * Mathf.Sign(cart.GlobalBasis.X.Dot(-cart.GlobalPosition));
        return shots.CaptureOblique(keyFrame, description, cart.ToGlobal(new Vector3(0f, 1.1f, -0.35f)), side, CartView);
    }
}
