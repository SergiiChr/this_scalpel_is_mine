namespace Scalpel.Tests.Support;

/// <summary>
/// A tool test's surgery: started with the patient asleep (the tool doesn't make them flinch: the setting, not what's
/// tested), rendering off between key frames and the frame budget counting from the first step. In a run with key
/// frames each driver key frame saves the site from above and obliquely (along <see cref="Along"/> when set), and with
/// <c>views</c> also what the surgeon sees, starting with the untouched site.
/// </summary>
public sealed class ToolSession
{
    public SurgeryDriver Driver { get; }
    public KeyFrames? Shots { get; }
    /// <summary>The oblique key frames look along this (world) when set: along a cut, what lies across it stands across
    /// it in view.</summary>
    public Vector3? Along { get; set; }

    private ToolSession(SurgeryDriver driver, KeyFrames? shots)
    {
        Driver = driver;
        Shots = shots;
    }

    /// <summary>Starts <paramref name="scenarioId"/> for a case whose key frames go under
    /// <paramref name="folder"/>.</summary>
    public static async Task<ToolSession> Start(string scenarioId, string folder, bool views = false)
    {
        RenderingServer.RenderLoopEnabled = false;
        var driver = SurgeryDriver.Create();
        await driver.Start(scenarioId);
        SurgeryState.PatientIsAsleep(driver.Patient);
        KeyFrames? shots = null;
        if (KeyFrames.Wanted())
        {
            shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(driver.Surgery, folder);
        }
        var session = new ToolSession(driver, shots);
        if (shots is not null)
        {
            driver.OnKeyFrame = async keyFrame =>
            {
                AssertBool(await shots.Capture(keyFrame, session.Along))
                    .OverrideFailureMessage($"{folder}: saved key frame {keyFrame}").IsTrue();
                if (views)
                {
                    AssertBool(await shots.CaptureView(keyFrame))
                        .OverrideFailureMessage($"{folder}: saved the surgeon's view {keyFrame}").IsTrue();
                }
            };
            await driver.Capture("untouched");
        }
        // Loading the room isn't gameplay: the frame budget counts from here.
        driver.Budget.Clear();
        return session;
    }

    /// <summary>Checks the frame budget (<paramref name="budgetBroken"/>: why it's known to go over) and ends the
    /// surgery.</summary>
    public async Task Finish(string budgetBroken = "")
    {
        Driver.Budget.Check(Shots is not null, "", budgetBroken);
        if (Shots is not null)
        {
            GD.Print($"key frames: {Shots.OutDir}");
            Shots.End();
        }
        await Driver.Stop();
        RenderingServer.RenderLoopEnabled = true;
    }
}
