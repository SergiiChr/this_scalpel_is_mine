namespace Scalpel.Tests.Surgeons;

/// <summary>The arms the player sees stay out of the way: the elbows bend out to the sides, so the arms come into the
/// view from its bottom corners, empty or holding a tool at the site. A hand that lets go of a tool it twisted rolls
/// back to palm down.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class SurgeonArmsTest {
    private SurgeryDriver? _driver;
    private KeyFrames? _frames;

    /// <summary>A surgery with a steady surgeon at the site, its key frames (when wanted) going to surgeon_arms.
    /// </summary>
    private async Task<SurgeryDriver> Begin() {
        FrameBudget.RenderingOff();
        var driver = _driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var frames = _frames = new KeyFrames();
        driver.AddChild(frames);
        frames.Begin(driver.Surgery, "surgeon_arms");
        driver.OnKeyFrame = async (name, description) => {
            if (KeyFrames.Wanted()) {
                await frames.CaptureView(name, description);
            }
        };
        SurgeryState.SurgeonIsSteady(driver.Me);
        return driver;
    }

    [AfterTest]
    public async Task End() {
        try {
            _frames?.End();
            if (_driver is { } driver) {
                await driver.Stop();
            }
        } finally {
            _frames = null;
            _driver = null;
            RenderingServer.RenderLoopEnabled = true;
        }
    }

    [TestCase]
    public async Task ArmsComeIntoTheViewFromItsBottomCorners() {
        var driver = await Begin();
        var me = driver.Me;
        await Frames.Physics(10);
        CheckArmsFromCorners(me, "empty hands at rest");
        await driver.Capture("empty_rest", "both empty hands at rest over the patient, apart toward the sides, each arm "
            + "coming in from its bottom corner of the view with the elbow out to the side");
        await driver.PlayerRequestsItem("scalpel");
        await driver.PlayerWalksTo(driver.SitePoint(new Vector2(0.5f, 0.5f)));
        await driver.PlayerReaches(driver.SitePoint(new Vector2(0.5f, 0.5f)));
        await Frames.Physics(10);
        CheckArmsFromCorners(me, "a scalpel over the middle of the site");
        await driver.Capture("scalpel_at_site", "the scalpel's tip over the middle of the site, its arm coming in from "
            + "the bottom right corner with the elbow out to the right, not up the middle of the view");
    }

    [TestCase]
    public async Task HandUntwistsAfterLettingGoOfItsTool() {
        var driver = await Begin();
        var me = driver.Me;
        var hand = me.Hands[me.Active];
        await driver.PlayerRequestsItem("scalpel");
        await driver.PlayerWalksTo(driver.SitePoint(new Vector2(0.5f, 0.5f)));
        await driver.PlayerReaches(driver.SitePoint(new Vector2(0.5f, 0.5f)));
        SurgeryDriver.Press(InputActions.TwistRight);
        await Frames.Physics(Mathf.RoundToInt(Mathf.Pi / 2f / Surgeon.TwistSpeed * Engine.PhysicsTicksPerSecond));
        SurgeryDriver.Release(InputActions.TwistRight);
        await PlayerInput.Delivered();
        AssertFloat(hand.Twist).OverrideFailureMessage("C/V roll the held scalpel a quarter turn").IsGreater(1.3f);
        await driver.Capture("twisted", "the scalpel rolled a quarter turn about its length, the hand rolled with it");
        SurgeryDriver.Tap(InputActions.Grab);
        AssertBool(await Frames.Until(() => me.HeldTool(me.Active) is null, 1f))
            .OverrideFailureMessage("Grab lets go of the scalpel").IsTrue();
        // The hand is posed empty in the next frame.
        await Frames.NextPhysics();
        AssertFloat(Up(hand)).OverrideFailureMessage("just let go, the empty hand is still rolled with the twist")
            .IsLess(0.5f);
        await driver.Capture("let_go", "the scalpel dropped, the empty hand still rolled on its side as it held it");
        var rolled = Mathf.Abs(hand.Twist);
        await Frames.Seconds((rolled / SurgeonHand.UntwistSpeed) + 0.1f);
        AssertFloat(hand.Twist).OverrideFailureMessage("the empty hand rolls back from the scalpel's twist")
            .IsEqualApprox(0f, 0.001f);
        await driver.AssertAndCapture(Up(hand) > 0.9f,
            "the empty hand back palm down, the back of the hand up, fingers along the forearm", "untwisted");
        SurgeryDriver.Press(InputActions.TwistRight);
        await Frames.Physics(30);
        SurgeryDriver.Release(InputActions.TwistRight);
        await PlayerInput.Delivered();
        AssertFloat(hand.Twist).OverrideFailureMessage("C/V don't roll an empty hand").IsEqual(0f);
    }

    /// <summary>How much the back of the glove faces up (1 straight up).</summary>
    private static float Up(SurgeonHand hand) => hand.Glove.GlobalBasis.Y.Normalized().Dot(Vector3.Up);

    /// <summary>Each arm comes into the view from its own bottom corner: the shoulder is off the view, and the elbow is
    /// further out to the hand's side and lower than the wrist, so the arm never rises up the middle in front of the
    /// work.</summary>
    private static void CheckArmsFromCorners(Surgeon me, string what) {
        foreach (var hand in me.Hands) {
            var name = hand.Index == 0 ? "left" : "right";
            var shoulder = OnScreen(me, hand, me.SteadyShoulder(hand.Index));
            var elbow = OnScreen(me, hand, hand.Elbow);
            var wrist = OnScreen(me, hand, hand.Glove.GlobalPosition);
            AssertBool(shoulder.X > 1f || shoulder.Y > 1f)
                .OverrideFailureMessage($"{what}: the {name} shoulder is off the view (at {shoulder})").IsTrue();
            AssertBool(elbow.X > wrist.X + 0.1f && elbow.Y > wrist.Y)
                .OverrideFailureMessage($"{what}: the {name} elbow is out to the side of and below the wrist (elbow "
                    + $"{elbow}, wrist {wrist})")
                .IsTrue();
        }
    }

    /// <summary>Where <paramref name="point"/> is in the view: across, out from the middle toward the hand's own side
    /// (1 at the edge), and down from the top (1 at the bottom).</summary>
    private static Vector2 OnScreen(Surgeon me, SurgeonHand hand, Vector3 point) {
        var size = me.Camera.GetViewport().GetVisibleRect().Size;
        var screen = me.Camera.UnprojectPosition(point);
        var side = hand.Index == 0 ? -1f : 1f;
        return new Vector2((screen.X - (size.X / 2f)) / (size.X / 2f) * side, screen.Y / size.Y);
    }
}
