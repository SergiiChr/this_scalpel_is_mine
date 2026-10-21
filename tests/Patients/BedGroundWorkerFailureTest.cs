namespace Scalpel.Tests.Patients;

[TestSuite, RequireGodotRuntime]
[TestCategory("liquids"), TestCategory("liquid_contours"), TestCategory("frame_budget")]
[GodotArgs("--fixed-fps", "60")]
public class BedGroundWorkerFailureTest {
    [TestCase]
    public async Task FailedPoolBuildBacksOffWithoutStoppingOtherDropsOrLosingBlood() {
        var session = await ToolSession.Start("appendectomy", "blood_pool_worker_failure", captureUntouched: false);
        var driver = session.Driver;
        driver.Patient.SetPhysicsProcess(false);
        driver.Body.SetProcess(false);
        var blood = driver.Body.Blood;
        blood.Sources.Clear();
        blood.Pools.Clear();
        var poolAt = new Vector3(0.2f, BloodFlow.TableTop, 0.1f);
        var dose = BloodFlow.PoolAreaPerDrop * 0.5f;
        blood.Pool(poolAt, dose);
        var pool = blood.GroundDeposits.Single();
        pool.TopDirty = true;
        pool.Building = Task.FromException<BloodFlow.GroundDrawing>(new InvalidOperationException("injected ground mesh failure"));
        blood.Spray(new Vector3(-0.2f, 1.5f, 0f), 1, 0.3f);
        var falling = blood.Drops.Single();
        var start = falling.Position;

        await Frames.Process(2);
        AssertInt(pool.Faults).OverrideFailureMessage("a worker failure is observed exactly once").IsEqual(1);
        AssertBool(pool.Building is null).OverrideFailureMessage("the failed task handle is detached for safe future work").IsTrue();
        AssertBool(pool.TopDirty).OverrideFailureMessage("failed geometry remains scheduled for a later retry").IsTrue();
        AssertFloat(pool.Incoming).OverrideFailureMessage("failed upload keeps the unbuilt liquid accounted").IsEqualApprox(dose, dose * 0.001f);
        AssertFloat(pool.RetryIn).OverrideFailureMessage("the first retry waits instead of retrying every frame").IsGreater(0.2f);
        AssertFloat(falling.Position.DistanceTo(start)).OverrideFailureMessage("a failed pool worker does not stop another droplet's simulation").IsGreater(0f);

        var expectedDelay = 0.5f;
        for (var failure = 2; failure <= 5; failure++) {
            pool.Building = Task.FromException<BloodFlow.GroundDrawing>(new InvalidOperationException("injected persistent mesh failure"));
            await Frames.Process(2);
            AssertInt(pool.Faults).OverrideFailureMessage("persistent failures advance the bounded retry policy").IsEqual(failure);
            if (failure < 5) {
                AssertFloat(pool.RetryIn).OverrideFailureMessage("retries back off exponentially").IsGreater(expectedDelay - 0.04f);
                expectedDelay *= 2f;
            } else {
                AssertBool(float.IsPositiveInfinity(pool.RetryIn)).OverrideFailureMessage("a permanently broken pool is quarantined after bounded retries").IsTrue();
            }
            AssertFloat(pool.Incoming).OverrideFailureMessage("quarantine preserves the liquid ledger for recovery").IsEqualApprox(dose, dose * 0.001f);
            AssertBool(blood.Drops.Count == 1).OverrideFailureMessage("other falling drops continue while this pool is quarantined").IsTrue();
        }
        AssertFloat(blood.GroundLiquidArea).OverrideFailureMessage("quarantining a broken renderer cannot discard the incoming dose")
            .IsEqualApprox(dose + BloodFlow.PoolAreaPerDrop, dose * 0.001f);
        await session.Finish();
    }
}

