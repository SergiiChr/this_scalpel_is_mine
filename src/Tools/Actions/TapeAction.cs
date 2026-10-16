namespace Scalpel.Tools;

/// <summary>
/// A roll of tape, worked like the needle: a click (Use tool let go before ToolActions.SutureTieHold) where it rests on
/// the site presses the strip down there, starting one off the roll at the first, and every later click sticks the
/// strip down all the way from the one before. A longer hold presses its last point down there and cuts it off the
/// roll. No wheel: it's always pulled as tight as skin closes (Patient.PlaceTapePoint()).
/// On the forearm, over the tear a ripped-out IV catheter left, a strip is short: the first press starts it, the next
/// sticks it down from there and cuts it off (Patient.TapeIvTear()).
/// </summary>
public sealed class TapeAction : ToolAction {
    /// <summary>How near (meters) the tear a press on the forearm counts as taping it.</summary>
    private const float TearReach = 0.04f;

    public override void Apply(ToolStep step) {
        var tool = step.Tool;
        var patient = step.Patient;
        var tape = tool.Suture;
        if (step.Trigger) {
            // Each press starts unplaced, then keeps the latest spot on the site until it's let go: the roll rising off
            // the skin as the hand lets go doesn't lose a click that already landed.
            if (tape.Hold == 0f) {
                tape.At = new Vector2(-1, -1);
                tape.ArmAt = null;
            }
            tape.Hold += step.Dt;
            if (step.Lowered && step.Zone == SiteZone.Site) {
                tape.At = step.Uv;
            } else if (step.Lowered && step.Zone == SiteZone.Body && tape.Thread == 0
                && step.Surgery.Room.IvLine.Tear is { Taped: false } tear && tear.Across(0f).DistanceTo(step.Tip) < TearReach) {
                tape.ArmAt = step.Tip;
            }
            if (tape.Hold >= ToolActions.SutureTieHold && !tape.PressUsed && tape.Thread != 0) {
                tape.PressUsed = true;
                Stick(step);
                patient.CutTape(tape.Thread);
                step.Surgery.Sound("tape_rip", step.Tip);
            }
        } else if (step.Released) {
            if (!tape.PressUsed) {
                Stick(step);
            }
            tape.Hold = 0f;
            tape.PressUsed = false;
        }
        // Cut off, or come off the skin while still on the roll: the next click starts a new strip.
        if (tape.Thread != 0 && patient.TapeDone(tape.Thread)) {
            tape.Thread = 0;
            Surgery.Current!.Tools.SyncSuture(tool);
        }
    }

    /// <summary>Presses the strip down where the roll last rested on the site, starting one if it has none. Each point
    /// takes a charge.</summary>
    private static void Stick(ToolStep step) {
        var tool = step.Tool;
        var tape = tool.Suture;
        if (tool.Charges == 0) {
            return;
        }
        if (tape.At.X < 0f) {
            StickOnArm(step);
            return;
        }
        tape.ArmFrom = null;
        if (tape.Thread == 0) {
            tape.Thread = step.Patient.NewTape();
            step.Tools.SyncSuture(tool);
        }
        if (step.Patient.PlaceTapePoint(tape.Thread, tape.At, tool.Def)) {
            ToolActions.SpendCharge(tool);
            step.Surgery.Sound("tape_rip", step.Tip);
        }
    }

    /// <summary>Starts a strip over the IV tear where the press rested, or sticks the one started down from there and
    /// cuts it off.</summary>
    private static void StickOnArm(ToolStep step) {
        var tool = step.Tool;
        var tape = tool.Suture;
        if (tape.ArmAt is not { } at) {
            return;
        }
        if (tape.ArmFrom is not { } from) {
            tape.ArmFrom = at;
            step.Surgery.Sound("tape_rip", step.Tip);
            return;
        }
        tape.ArmFrom = null;
        if (step.Patient.TapeIvTear(from, at, tool.Def)) {
            ToolActions.SpendCharge(tool);
        }
        step.Surgery.Sound("tape_rip", step.Tip);
    }
}
