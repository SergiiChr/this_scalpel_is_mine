namespace Scalpel.Tests.Surgeons;

/// <summary>Drugs in a surgeon, in the room: morphine closes the eyelids now and then, a solo surgeon who snorts
/// cocaine on top of adrenaline races until their heart stops and, with nobody to shock it, the operation ends, and a
/// partner whose heart stopped is brought back with the defibrillator. The status checks themselves live in
/// SyringeTest.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("slow"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class SurgeonDrugsTest {
    private SurgeryDriver? _driver;
    private KeyFrames? _frames;
    private readonly List<string> _toasts = [];
    private readonly List<string> _sounds = [];

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
        _sounds.Clear();
        Sfx.Played += _sounds.Add;
        return driver;
    }

    [AfterTest]
    public async Task End() {
        try {
            Sfx.Played -= _sounds.Add;
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
    public async Task MorphineClosesTheSoloSurgeonsEyelids() {
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
        var hud = driver.Surgery.Hud;
        AssertFloat(hud.PostEffect(ShaderParam.Eyelids))
            .OverrideFailureMessage("morphine: the view is drawn with the eyelids half shut")
            .IsEqualApprox(me.Status.Eyelids, 0.001f);
        AssertBool(await Frames.Until(() => me.Status.Eyelids >= 1f, 1f) && hud.PostEffect(ShaderParam.Eyelids) >= 1f)
            .OverrideFailureMessage("morphine: the eyelids shut, and the view is drawn with them shut").IsTrue();
        await driver.Capture("eyelids_shut", "the eyelids shut: the whole view dark");
        var shutAt = driver.Surgery.Elapsed;
        AssertBool(await Frames.Until(() => me.Status.Eyelids < 1f, SurgeonStatus.NodDarkMax + 1f))
            .OverrideFailureMessage("morphine: the eyelids open again").IsTrue();
        var dark = driver.Surgery.Elapsed - shutAt;
        AssertFloat(dark).OverrideFailureMessage($"morphine: shut for 1 to 2 s ({dark:0.00} s)")
            .IsBetween(SurgeonStatus.NodDarkMin - 0.05f, SurgeonStatus.NodDarkMax + 0.05f);
        AssertBool(await Frames.Until(() => me.Status.Eyelids == 0f, SurgeonStatus.LidOpen + 0.5f) && !me.Status.IsOut
                && hud.PostEffect(ShaderParam.Eyelids) == 0f)
            .OverrideFailureMessage("morphine: the eyes wide open again, the surgeon still on their feet").IsTrue();
    }

    /// <summary>Alone at the table, the surgeon snorts cocaine with adrenaline in their blood: the heart races, the
    /// hands and feet speed up, and three minutes on the heart stops. The surgeon hits the floor with a thunk and sees
    /// how long is left for a defibrillator, the heart still pounding in their ears. Nobody is there to shock it: half a
    /// minute later they die, the view goes black and the operation fails.</summary>
    [TestCase(Timeout = Limits.Slow)]
    public async Task CocaineWithAdrenalineKillsTheSoloSurgeon() {
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
        AssertFloat(me.Status.MoveSpeed()).OverrideFailureMessage("racing: the feet speed up too")
            .IsEqualApprox(SurgeonStatus.RaceSpeed, 0.001f);
        AssertBool(await Frames.Until(() => me.Status.IsArrested, SurgeonStatus.RaceTime + 2f))
            .OverrideFailureMessage("racing: three minutes on, the heart stops").IsTrue();
        var hud = driver.Surgery.Hud;
        AssertBool(me.HeldTool(me.Active) is null && !driver.Surgery.Finished
                && hud.Countdown == $"Your heart has stopped\n{SurgeonStatus.ArrestTime:0} s")
            .OverrideFailureMessage($"arrest: the baggie dropped, the operation goes on, the surgeon sees the time left "
                + $"({hud.Countdown.Replace('\n', ' ')})")
            .IsTrue();
        _sounds.Clear();
        AssertBool(await Frames.Until(() => _sounds.Contains("surgeon_collapse"), 2f) && me.IsDown)
            .OverrideFailureMessage($"arrest: the surgeon hits the floor with a thunk ({string.Join(", ", _sounds)})")
            .IsTrue();
        await Frames.Seconds(10f);
        var beats = _sounds.Count(sound => sound == "heartbeat");
        var left = Mathf.CeilToInt(me.Status.ArrestLeft);
        AssertBool(beats >= 30 && left <= SurgeonStatus.ArrestTime - 10f && hud.Countdown.EndsWith($"\n{left} s"))
            .OverrideFailureMessage($"arrest: ten seconds on, the heart still pounding ({beats} beats), the seconds left "
                + $"counting down ({hud.Countdown.Replace('\n', ' ')}, {left} s left)")
            .IsTrue();
        await driver.Capture("heart_stopped",
            "the surgeon's view from the floor, hazy: in the middle in red \"Your heart has stopped\" and the seconds "
                + "left under it");
        AssertBool(await Frames.Until(() => driver.Surgery.Finished, SurgeonStatus.ArrestTime))
            .OverrideFailureMessage("arrest: half a minute on with nobody to shock the heart, the operation ends")
            .IsTrue();
        var report = driver.Surgery.Report!;
        AssertBool(!report.Success && report.Reason == Surgery.SurgeonDied && me.Status.Dead && me.IsDown)
            .OverrideFailureMessage($"arrest: it fails because the surgeon died ({report.Reason}), lying on the floor")
            .IsTrue();
        await driver.AssertAndCapture(hud.PostEffect(ShaderParam.Blackout) == 1f,
            "the dead surgeon's view: black, the report of the failed operation over it", "heart_attack");
    }

    /// <summary>A partner's heart has stopped and they lie on the floor: the surgeon takes the defibrillator, crouches
    /// beside them and shocks their chest until the heart is back, each shock a coin toss. The thunk of the fall is
    /// heard here too; the countdown is the partner's alone.</summary>
    [TestCase]
    public async Task DefibrillatorRestartsAFellowSurgeonsHeart() {
        var driver = await Begin();
        var surgery = driver.Surgery;
        var partner = SurgeryState.PartnerIsAtTheTable(surgery);
        partner.PlaceAt((surgery.Patient.GlobalPosition with { Y = driver.Me.GlobalPosition.Y }) + new Vector3(0f, 0f, 1.2f),
            0f);
        SurgeryState.SurgeonsHeartHasStopped(surgery, partner);
        AssertBool(await Frames.Until(() => _sounds.Contains("surgeon_collapse"), 2f))
            .OverrideFailureMessage("the partner hits the floor with a thunk, heard here too").IsTrue();
        AssertBool(_toasts.Any(toast => toast.Contains("Their heart has stopped")) && surgery.Hud.Countdown == "")
            .OverrideFailureMessage($"the surgeon hears the partner's heart stopped, and sees no countdown "
                + $"({string.Join(" | ", _toasts)}, \"{surgery.Hud.Countdown}\")")
            .IsTrue();
        var shocks = await driver.PlayerDefibrillates(partner, 10);
        var (hips, neck) = partner.Spine();
        var missed = _toasts.Count(toast => toast.EndsWith("Still no pulse."));
        AssertBool(shocks > 0 && missed == shocks - 1 && _toasts.Any(toast => toast.EndsWith("Their heart is beating again."))
                && surgery.ArrestedAt(hips.Lerp(neck, 0.7f), driver.Me.PeerId) is null)
            .OverrideFailureMessage($"a shock brings the partner's heart back ({shocks} shocks, {missed} missed: "
                + $"{string.Join(" | ", _toasts)})")
            .IsTrue();
    }
}
