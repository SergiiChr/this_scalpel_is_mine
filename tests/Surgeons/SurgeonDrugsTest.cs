namespace Scalpel.Tests.Surgeons;

/// <summary>Drugs in a surgeon, in the room: morphine closes the eyelids now and then, and a solo surgeon who snorts
/// cocaine on top of adrenaline races until a heart attack ends the operation. The status checks themselves live in
/// SyringeTest.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class SurgeonDrugsTest {
    private SurgeryDriver? _driver;
    private KeyFrames? _frames;
    private readonly List<string> _toasts = [];

    /// <summary>A solo appendectomy with a steady surgeon, its key frames (when wanted) going to surgeon_drugs.
    /// </summary>
    private async Task<SurgeryDriver> Begin() {
        FrameBudget.RenderingOff();
        var driver = _driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var frames = _frames = new KeyFrames();
        driver.AddChild(frames);
        frames.Begin(driver.Surgery, "surgeon_drugs");
        driver.OnKeyFrame = async (name, description) => {
            if (KeyFrames.Wanted()) {
                await frames.CaptureView(name, description);
            }
        };
        SurgeryState.SurgeonIsSteady(driver.Me);
        _toasts.Clear();
        driver.Surgery.Hud.Toasted += _toasts.Add;
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

    /// <summary>On morphine the eyelids close from the top and bottom, stay shut a moment and open again.</summary>
    [TestCase]
    public async Task MorphineClosesTheEyelidsNowAndThen() {
        var driver = await Begin();
        var me = driver.Me;
        await driver.Capture("eyes_open",
            "the player's view of the patient, wide open: no dark bands at the top or bottom");
        SurgeryState.SurgeonIsGiven(me, "morphine", 1f);
        var limit = (Db.Drug("morphine")!.Onset * DrugDef.DirectOnset) + SurgeonStatus.NodGapMax + 2f;
        AssertBool(await Frames.Until(() => me.Status.Eyelids is > 0.4f and < 0.7f, limit))
            .OverrideFailureMessage($"morphine: the eyelids start closing within {limit:0} s").IsTrue();
        await driver.Capture("eyelids_closing",
            "the eyelids half shut: dark from the top and bottom, the edges soft and curved, closer together at the "
                + "sides; the patient seen through the slit in the middle");
        AssertBool(await Frames.Until(() => me.Status.Eyelids >= 1f, 1f))
            .OverrideFailureMessage("morphine: the eyelids shut").IsTrue();
        await driver.Capture("eyelids_shut", "the eyelids shut: the whole view dark");
        var shutAt = driver.Surgery.Elapsed;
        AssertBool(await Frames.Until(() => me.Status.Eyelids < 1f, SurgeonStatus.NodDarkMax + 1f))
            .OverrideFailureMessage("morphine: the eyelids open again").IsTrue();
        var dark = driver.Surgery.Elapsed - shutAt;
        AssertFloat(dark).OverrideFailureMessage($"morphine: shut for 1 to 2 s ({dark:0.00} s)")
            .IsBetween(SurgeonStatus.NodDarkMin - 0.05f, SurgeonStatus.NodDarkMax + 0.05f);
        AssertBool(await Frames.Until(() => me.Status.Eyelids == 0f, SurgeonStatus.LidOpen + 0.5f) && !me.Status.IsOut)
            .OverrideFailureMessage("morphine: the eyes wide open again, the surgeon still on their feet").IsTrue();
    }

    /// <summary>Alone at the table, the surgeon snorts cocaine with adrenaline in their blood: the heart races, the
    /// hands and feet speed up, and when the heart gives out the surgeon goes down and the operation fails.</summary>
    [TestCase]
    public async Task CocaineWithAdrenalineEndsASoloOperation() {
        var driver = await Begin();
        var me = driver.Me;
        SurgeryState.SurgeonIsGiven(me, "adrenaline", 1f);
        SurgeryState.ToolIsOnTray(driver.Surgery, "cocaine");
        await Frames.Physics(10);
        await driver.PlayerRequestsItem("cocaine");
        SurgeryDriver.Press(InputActions.Drink);
        SurgeryDriver.Release(InputActions.Drink);
        AssertBool(await Frames.Until(() => me.Status.IsRacing, 5f))
            .OverrideFailureMessage($"racing: the snorted cocaine meets the adrenaline ({string.Join(" | ", _toasts)})")
            .IsTrue();
        AssertBool(_toasts.Contains("Your heart hammers. Everything is so fast.")
                && Mathf.IsEqualApprox(me.Status.HandSpeed(), SurgeonStatus.RaceSpeed))
            .OverrideFailureMessage($"racing: the surgeon feels it, hands {me.Status.HandSpeed():0.00}x").IsTrue();
        SurgeryState.SurgeonsHeartGivesOutIn(me, 0.5f);
        AssertBool(await Frames.Until(() => driver.Surgery.Finished, 3f))
            .OverrideFailureMessage("racing: the heart attack ends the solo operation").IsTrue();
        var report = driver.Surgery.Report!;
        AssertBool(!report.Success && report.Reason == Surgery.SurgeonDied && me.Status.Dead
                && me.HeldTool(me.Active) is null)
            .OverrideFailureMessage($"racing: it fails because the surgeon died ({report.Reason}), the baggie dropped")
            .IsTrue();
    }
}
