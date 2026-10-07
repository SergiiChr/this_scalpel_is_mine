namespace Scalpel.Tests.Surgeons;

/// <summary>Gameplay runs on game time: a hitch in the frame rate, a slow machine or --fixed-fps changes nothing about
/// what happens, only how fast it's seen.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
[GodotArgs("--fixed-fps", "60")]
public class GameTimeTest
{
    /// <summary>Real time that passes between two frames, as a hitch would make it.</summary>
    private const int HitchMsec = 300;
    private const int Trials = 5;

    /// <summary>
    /// Hand tremor is driven by Time.GetTicksMsec() (Surgeon.PlaceHand()), so it follows the wall clock, not the game: a
    /// hitch jumps the hand by a whole shake, and runs under --fixed-fps shake differently from real time. That's the
    /// likely reason the colon cancer flow fails only beside another suite. The same wall clock decides other gameplay
    /// too: score throttling (Scoring.Add()), toast throttling (Surgery.Announce()), the bone scrape jolt, the tear
    /// notice, the diazepam hand delay, how long an X-ray takes to develop and sound and effect throttles. To fix: count
    /// these in game time (Surgery.Elapsed, or a time summed from delta).
    /// </summary>
    [TestCase(Description = "BROKEN: hand tremor (and other gameplay timing) follows the wall clock, not game time.")]
    [TestCategory("broken")]
    public async Task TremorFollowsGameTime()
    {
        var driver = SurgeryDriver.Create();
        await driver.Start("hand_stitch");
        var surgeon = driver.Me;
        var hand = surgeon.Hands[surgeon.Active];
        for (var trial = 0; trial < Trials; trial++)
        {
            SurgeryState.SurgeonIsStressed(surgeon);
            await Frames.Physics(1);
            var before = hand.Tremor;
            var amount = surgeon.Status.TremorAmount();
            OS.DelayMsec(HitchMsec);
            await Frames.Physics(1);
            // Over one frame (1/60 s) the shake's three sines (23, 31 and 19 rad/s) move it at most this far.
            var most = amount * new Vector3(23f, 31f, 19f).Length() / 60f;
            AssertFloat(hand.Tremor.DistanceTo(before))
                .OverrideFailureMessage($"one frame after a {HitchMsec} ms hitch the hand moved by one frame's shake")
                .IsLessEqual(most * 1.1f);
        }
        await driver.Stop();
    }
}
