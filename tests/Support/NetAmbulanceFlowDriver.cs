namespace Scalpel.Tests.Support;

/// <summary>Two real players cooperate through Ambulance Run, each taking turns on ordinary objectives and controls.
/// </summary>
public partial class NetAmbulanceFlowDriver : NetSession {
    private const int Port = 24601;

    protected override async Task Drive() {
        var surgery = await Join("ambulance_bullet", Port,
            () => SurgeryState.NetworkSessionHasOrdinarySurgeons());
        await SurgeryDriver.PlayerPutsCardBack(surgery);
        var driver = SurgeryDriver.For(surgery);
        var tableZ = surgery.Patient.GlobalPosition.Z;
        var side = IsHost ? 1f : -1f;
        driver.StandingConstraint = at => (at.Z - tableZ) * side > 0.05f;
        Check((driver.Me.GlobalPosition.Z - tableZ) * side > 0f,
            $"{Role} starts on its assigned {(IsHost ? "+Z" : "-Z")} aisle");
        var steps = surgery.Objectives.Steps;
        Check(steps.Count == 5, "the ambulance has five required objective steps");
        for (var index = 0; index < steps.Count; index++) {
            var step = steps[index];
            var thisSide = step.Type is "extract" or "close" ? IsHost : !IsHost;
            // A needle or catheter goes into the arm on the host's side of the table, out of the client's reach: the
            // host holds it still by the wrist while the client works.
            var holdsArm = step.Type is "iv" or "anesthesia" && driver.Patient.Vitals.IsAwake;
            var action = thisSide ? Work(driver, step, holdsArm) : holdsArm ? driver.PlayerHoldsForearm() : Task.CompletedTask;
            var deadline = Time.GetTicksMsec() + (ulong)(StepTimeout(step) * 1000f);
            bool Done() => surgery.Status.Objectives.Count > index && surgery.Status.Objectives[index].Done;
            string Seen() => $"status=[{string.Join(", ", surgery.Status.Objectives.Select(view => $"{view.Label}:{view.Done}"))}], "
                + $"finished={surgery.Finished}, {driver.Recent()}";
            while (!action.IsCompleted || (!Done() && !surgery.Finished)) {
                if (Time.GetTicksMsec() >= deadline) {
                    throw new TimeoutException($"[{Role}] '{step.Label}' did not complete within {StepTimeout(step):0}s; {Seen()}");
                }
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame).AsTask();
            }
            await action;
            if (!thisSide && holdsArm) {
                await driver.PlayerLetsGoWithFreeHand();
            }
            if (thisSide) {
                Check((driver.Me.GlobalPosition.Z - tableZ) * side > 0f,
                    $"{Role} completes {step.Label} from its assigned aisle");
            }
            Check(Done(), $"{step.Label} completed through player controls", () => Seen());
        }
        if (IsHost) {
            await driver.PlayerDischarges();
        } else {
            await Until(() => surgery.Finished, "the host discharging the patient", () => "still in surgery");
        }
        Check(surgery.Report?.Success == true,
            "both cooperating surgeons receive the game's successful surgery report",
            () => $"report={surgery.Report?.Reason ?? "none"}");
        Check((driver.Me.GlobalPosition.Z - tableZ) * side > 0f,
            $"{Role} remains on its assigned aisle through discharge");
        // The client goes first: left behind, its heartbeat would call RPCs on a host that's gone.
        if (IsHost) {
            await Until(() => Multiplayer.GetPeers().Length == 0, "the client leaving", () => "still connected");
        } else {
            await Wait(1.0);
        }
        GetTree().Quit();
    }

    /// <summary>Completes <paramref name="step"/>, once the other surgeon holds the patient still when
    /// <paramref name="heldStill"/>.</summary>
    private async Task Work(SurgeryDriver driver, ObjectiveStep step, bool heldStill) {
        if (heldStill) {
            await Until(() => driver.AnotherHoldsPatient, "the other surgeon holding the arm still", () => "not held");
        }
        await driver.PlayerCompletes(step);
    }

    private static float StepTimeout(ObjectiveStep step) {
        var timeout = 20f + step.Parameters.Float("seconds");
        if (step.Type == "calm") {
            timeout += 60f;
        }
        var drugId = step.Type switch {
            "anesthesia" => "ketamine",
            "inject" => step.Parameters.String("drug"),
            _ => "",
        };
        if (drugId.Length > 0 && Db.Drug(drugId) is { } drug) {
            timeout += drug.Onset * 1.5f;
        }
        return timeout;
    }
}
