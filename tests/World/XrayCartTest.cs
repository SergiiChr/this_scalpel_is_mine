namespace Scalpel.Tests.World;

/// <summary>The X-ray cart as a player handles it: interact on the handle pushes it, the wheel raises and lowers the
/// arm, a click locks it against the floor, and looking at the tube head the wheel still moves the arm.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class XrayCartTest {
    // Temporary local budget override: recorded CPU overruns on the usual test machine remain measured.
    private static readonly BudgetAllowance BudgetOverride = new("known to go over the frame budget, not profiled yet", 4.5f);

    /// <summary>How far (meters) the cart's views are from it: the whole cart in view, the top one under the ceiling.
    /// </summary>
    private const float CartView = 1.7f;

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
                await shots.CaptureAt("untouched",
                    "the X-ray cart: a T base with casters in front, big wheels under the generator, the column, the arm "
                        + "reaching up to a level tube head with a screen on its back, the red pedal down",
                    cart.ToGlobal(new Vector3(0f, 0.7f, -0.2f)), CartView);
            }
            driver.Budget.Clear();
            await driver.PlayerWalksTo(handle.GlobalPosition, 0.6f);
            await driver.PlayerLooksAt(handle.GlobalPosition);
            await Frames.Physics(2);
            AssertObject(me.Focused)
                .OverrideFailureMessage($"looking at the handle offers to push the cart: standing at {me.GlobalPosition}, "
                    + $"the handle at {handle.GlobalPosition}, focused on {me.Focused?.Prompt}")
                .IsSame(handle);
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
            var high = head!.GlobalPosition.Y;
            for (var i = 0; i < 4; i++) {
                PlayerInput.Tap(InputActions.LevelDown);
                await PlayerInput.Delivered();
            }
            await Frames.Physics(40);
            var lowered = XrayCart.ArmRest - (4 * XrayCart.ArmStep);
            AssertFloat(cart.Arm).OverrideFailureMessage("four wheel steps down lower the arm four steps")
                .IsEqualApprox(lowered, 0.001f);
            var drop = 0.95f * (Mathf.Sin(XrayCart.ArmRest) - Mathf.Sin(lowered));
            AssertFloat(high - head.GlobalPosition.Y).OverrideFailureMessage("the tube head comes down with the arm")
                .IsEqualApprox(drop, 0.01f);
            AssertFloat(Mathf.Abs(head.GlobalRotation.X)).OverrideFailureMessage("the tube head stays level")
                .IsLess(0.01f);
            if (shots is not null) {
                await driver.Unbudgeted(() => shots.CaptureAt("lowered",
                    "the cart pushed away from its corner, the arm lowered, the tube head still level, the pedal up",
                    cart.ToGlobal(new Vector3(0f, 0.7f, -0.2f)), CartView));
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

            await driver.PlayerWalksTo(tubeHead.GlobalPosition, 0.7f);
            await driver.PlayerLooksAt(tubeHead.GlobalPosition);
            await Frames.Physics(2);
            AssertObject(me.Focused)
                .OverrideFailureMessage($"looking at the tube head offers to take an X-ray: standing at {me.GlobalPosition}, "
                    + $"the head at {tubeHead.GlobalPosition}, focused on {me.Focused?.Prompt}")
                .IsSame(tubeHead);
            AssertFloat(cart.GlobalPosition.DistanceTo(locked)).OverrideFailureMessage("walking up to it doesn't move the locked cart")
                .IsLess(0.001f);
            PlayerInput.Tap(InputActions.LevelUp);
            await PlayerInput.Delivered();
            await Frames.Physics(40);
            AssertFloat(cart.Arm).OverrideFailureMessage("locked, the wheel on the tube head still raises the arm")
                .IsEqualApprox(lowered + XrayCart.ArmStep, 0.001f);
            if (shots is not null) {
                await driver.Unbudgeted(() => shots.CaptureView("head_in_view",
                    "the tube head in the middle of the view, the prompt to take an X-ray with the wheel to raise or "
                        + "lower the arm"));
                await driver.Unbudgeted(() => shots.CaptureFacing("locked",
                    "the cart from behind, its handle loop in front, the red pedal by the right wheel pressed down",
                    cart.ToGlobal(new Vector3(0f, 0.5f, 0.3f)), cart.GlobalBasis.Z, 1.4f));
            }
            driver.Budget.Check(localOverride: BudgetOverride);
        } finally {
            shots?.End();
            await driver.Stop();
            RenderingServer.RenderLoopEnabled = true;
        }
    }
}
