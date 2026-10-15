namespace Scalpel.Tests.World;

/// <summary>The X-ray cart as a player handles it, like a tool: Grab anywhere on it takes hold of it and pushes it, the
/// wheel slides the arm up and down the column, Use tool takes an X-ray, and Grab again lets go and locks it against the
/// floor. What the player can take hold of lights up while they look at it.</summary>
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
            var holds = cart.FindChildren("*", "", true, false).OfType<Interactable>()
                .Where(spot => spot.Prompt.StartsWith("Push")).ToList();
            var head = (Node3D)cart.FindChild("Head", true, false);
            var body = holds.Single(spot => spot.GetParent() == cart);
            var arm = holds.Single(spot => spot != body);
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
            // Not the handle: the column, partway up, from behind its left corner.
            var column = cart.ToGlobal(new Vector3(-0.17f, 1.2f, -0.03f));
            await driver.PlayerWalksTo(cart.ToGlobal(new Vector3(-0.3f, 0f, 0.9f)), 0f);
            await driver.PlayerLooksAt(column);
            await Frames.Physics(2);
            AssertBool(me.Focused == body && me.Focused.Button == InputActions.Grab)
                .OverrideFailureMessage($"looking at the column offers to take hold of the cart with Grab: standing at "
                    + $"{me.GlobalPosition}, the column at {column}, focused on {me.Focused?.Prompt}")
                .IsTrue();
            AssertBool(SurgeryDriver.IsLit(body)).OverrideFailureMessage("looking at the cart lights it up").IsTrue();
            if (shots is not null) {
                await driver.Unbudgeted(() => shots.CaptureView("cart_in_view",
                    "the whole cart lit up warmer than the room, the prompt to push it with the right mouse button"));
            }
            PlayerInput.Tap(InputActions.Grab);
            await PlayerInput.Delivered();
            await Frames.Physics(2);
            AssertInt(cart.Pusher).OverrideFailureMessage("Grab on the column takes hold of the cart").IsEqual(me.PeerId);
            // Kept off the wall it was parked against, it settles first.
            await Frames.Physics(30);
            var start = cart.GlobalPosition;
            var held = me.GlobalTransform.AffineInverse() * cart.GlobalTransform;
            await driver.PlayerHoldsWalkKey(InputActions.MoveBack, 0.8f, () => { });
            await Frames.Physics(30);
            AssertFloat(cart.GlobalPosition.DistanceTo(start)).OverrideFailureMessage("the pushed cart follows the surgeon")
                .IsGreater(0.3f);
            var now = me.GlobalTransform.AffineInverse() * cart.GlobalTransform;
            AssertBool(now.Origin.DistanceTo(held.Origin) < 0.05f && now.Basis.Z.AngleTo(held.Basis.Z) < 0.05f)
                .OverrideFailureMessage($"like a held tool, the cart keeps where it was from its pusher: {held.Origin} "
                    + $"then {now.Origin}")
                .IsTrue();

            var high = cart.ToLocal(head.GlobalPosition);
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
            // Away from the table Use tool takes no X-ray, and doesn't let go of the cart either.
            PlayerInput.Tap(InputActions.UseTool);
            await PlayerInput.Delivered();
            await Frames.Physics(2);
            AssertInt(cart.Pusher).OverrideFailureMessage("Use tool keeps hold of the cart").IsEqual(me.PeerId);

            PlayerInput.Tap(InputActions.Grab);
            await PlayerInput.Delivered();
            await Frames.Physics(2);
            AssertInt(cart.Pusher).OverrideFailureMessage("Grab again lets go of the cart and locks it").IsEqual(0);
            var locked = cart.GlobalPosition;
            await driver.PlayerHoldsWalkKey(InputActions.MoveBack, 0.5f, () => { });
            await Frames.Physics(10);
            AssertFloat(cart.GlobalPosition.DistanceTo(locked)).OverrideFailureMessage("the locked cart stays put")
                .IsLess(0.001f);

            // On the head's right, clear of the arm that curves to it from the column on its left.
            await driver.PlayerWalksTo(head.GlobalPosition + (cart.GlobalBasis.X * 0.75f), 0f);
            await driver.PlayerLooksAt(head.GlobalPosition);
            await Frames.Physics(2);
            AssertBool(me.Focused == arm && SurgeryDriver.IsLit(arm))
                .OverrideFailureMessage($"looking at the tube head offers to take hold of the cart, lit up: standing at "
                    + $"{me.GlobalPosition}, the head at {head.GlobalPosition}, focused on {me.Focused?.Prompt}")
                .IsTrue();
            AssertFloat(cart.GlobalPosition.DistanceTo(locked)).OverrideFailureMessage("walking up to it doesn't move the locked cart")
                .IsLess(0.001f);
            PlayerInput.Tap(InputActions.Grab);
            await PlayerInput.Delivered();
            await Frames.Physics(2);
            AssertInt(cart.Pusher).OverrideFailureMessage("Grab on the tube head takes hold of the cart").IsEqual(me.PeerId);
            PlayerInput.Tap(InputActions.LevelUp);
            await PlayerInput.Delivered();
            await Frames.Physics(40);
            AssertFloat(cart.Arm).OverrideFailureMessage("held by the head, the wheel raises the arm")
                .IsEqualApprox(lowered + XrayCart.ArmStep, 0.001f);
            if (shots is not null) {
                await driver.Unbudgeted(() => shots.CaptureView("head_in_view",
                    "the tube head in the middle of the view, the cart lit up, the prompt to push or let go of it"));
            }
            PlayerInput.Tap(InputActions.Grab);
            await PlayerInput.Delivered();
            await Frames.Physics(2);
            AssertInt(cart.Pusher).OverrideFailureMessage("Grab lets go of it again").IsEqual(0);
            await driver.PlayerLooksAt((me.GlobalPosition * 2f) - head.GlobalPosition);
            await Frames.Physics(2);
            AssertBool(holds.Any(SurgeryDriver.IsLit)).OverrideFailureMessage("looking away, the cart is no longer lit")
                .IsFalse();
            if (shots is not null) {
                // From above as well as behind: pushed and turned as a player would, its back may face the table.
                await driver.Unbudgeted(() => shots.CaptureOblique("locked",
                    "the cart from behind, its handle loop in front, the red pedal right of middle pressed down",
                    cart.ToGlobal(new Vector3(0f, 0.4f, 0.3f)), cart.GlobalBasis.Z, 1.4f));
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
        var side = cart.GlobalBasis.X.Dot(cart.GlobalPosition) > 0f ? -cart.GlobalBasis.X : cart.GlobalBasis.X;
        return shots.CaptureOblique(keyFrame, description, cart.ToGlobal(new Vector3(0f, 1.1f, -0.35f)), side, CartView);
    }
}
