namespace Scalpel.Tools;

/// <summary>
/// Forceps, hemostats and retractors: pressed, the jaws pinch what they come to rest on and let go anywhere. Forceps
/// also pick up a cotton pad (wiped and dipped while lowered) or carry a skin graft to a cleaned burn.
/// </summary>
public sealed class ClampAction : ToolAction {
    /// <summary>A pinch closes once the tip has stayed within RestReach (meters, up or down) for RestTime (seconds) on the
    /// patient, not when the press counts: lowered out of an aim, or by a remote hand whose moves come late,
    /// the tip is still coming down then.
    /// RestReach is more than a shaking hand moves the tip in a frame (SurgeonStatus.MaxTremor) and less than a tool
    /// settling at SurgeonHand.SettleSpeed comes down.
    /// The rest counts only once a new hand state has the tip there too: between a remote hand's states it seems to
    /// hold still wherever it is.
    /// Only height counts, so a hand moving across still closes.
    /// A tip that never comes to rest on the patient closes after MaxWait there on whatever is under it; in the air it
    /// waits as long as Use tool is held.</summary>
    internal const float RestReach = 0.005f;
    internal const float RestTime = 0.03f;
    internal const float MaxWait = 0.5f;
    /// <summary>Skin stopping the tip more than this (meters, float noise) short of where the hand took it holds the
    /// hand back. Any amount counts: skin that gives only part of each move (TissueSim.Restrain()) would otherwise
    /// give it all to a hand moving slowly enough, step by step.</summary>
    private const float HeldBack = 0.00001f;

    public override void Apply(ToolStep step) {
        var tool = step.Tool;
        var tools = step.Tools;
        var patient = step.Patient;
        var pad = tools.CarriedBy(tool);
        var loosePad = step.Pressed && step.Lowered && ToolActions.PadHolders.Contains(step.Def.Id)
            ? tools.NearestOf("cotton_pad", step.Tip, ToolActions.PadReach)
            : null;
        if (pad is { Def.Action: "graft" }) {
            // A graft taken from the skin goes down where it's pressed onto a cleaned burn; pressed in the air it's let go.
            if (step.Pressed && step.Lowered && step.Zone == SiteZone.Site && patient.GraftAt(step.Uv, pad.Def)) {
                ToolActions.UseCharge(pad);
                if (pad.Charges == 0) {
                    tools.Consume(pad);
                }
            } else if (step.Pressed && !step.Touching) {
                tools.DropCarried(tool);
            }
        } else if (pad is not null) {
            // Use lowers the pad to wipe or dip it; pressed in the air, away from the dish, it lets the pad go.
            if (step.Pressed && !step.Touching && tools.NearestDish(step.Tip) is null) {
                tools.DropCarried(tool);
            } else if (step.Lowered) {
                ToolActions.Wipe(pad, step, step.Dt, gloved: false);
            }
        } else if (loosePad is not null && tool.Hold is null) {
            tools.Carry(loosePad, tool);
        } else if (step.Pressed && tool.Hold is not null) {
            // Letting go works anywhere.
            patient.ReleaseGrip(tool.Uid, tool.Hold, false);
            tool.Hold = null;
            step.Surgery.SetAttached(step.Hand.Peer, tool.Slot, false);
        } else if (tool.Hold is null && ((step.Pressed && step.Lowered) || tool.Use.PinchWait is not null)) {
            Pinch(step);
        } else if (tool.Hold is { } hold) {
            var power = step.Def.Power * step.Hand.Mods.Mult("grip_strength_mult");
            // Tools act before the surgeon poses this frame. Mouse input has already moved the attached hand's
            // target, while step.Tip still belongs to the previous pose. Restrain the requested skin contact and
            // correct that same request: correcting the old tip lets each new mouse step overshoot the skin.
            var tip = step.Tip;
            if (hold is SkinHold && step.Surgery.Surgeons.TryGetValue(step.Hand.Peer, out var surgeon)) {
                var hand = surgeon.Hands[tool.Slot];
                // Dragged out of reach by the surgeon walking off, the hand doesn't draw the skin after it: the skin
                // stays where it was held until the host settles it (Surgery.Overstretched(): a self-retaining tool is
                // left in place, forceps tear it). Dragged along, it would be pulled back over the cut first.
                if (hand.Target.DistanceTo(surgeon.Shoulder(tool.Slot)) > Surgeon.Overreach) {
                    return;
                }
                tip = hand.EffectivePosition() + hand.TipOffset(tool.Def.Length);
            }
            tool.Hold = patient.UpdateGrip(tool.Uid, hold, tip, power, step.Dt, step.Hand.Speed, out var heldAt);
            if (tool.Hold is null) {
                step.Surgery.SetAttached(step.Hand.Peer, tool.Slot, false);
            } else if (tool.Hold is CarryHold && hold is TargetHold) {
                // Pulled free, it no longer holds the hand to the patient: it's carried wherever the surgeon goes.
                step.Surgery.SetAttached(step.Hand.Peer, tool.Slot, false, keepHeight: true);
            } else if (heldAt.DistanceSquaredTo(tip) > HeldBack * HeldBack) {
                step.Surgery.HoldHandBack(step.Hand.Peer, tool.Slot, heldAt - tip);
            }
        }
    }

    /// <summary>Pressed onto the patient, the jaws close on what's under the tip once it has come to rest there (see
    /// RestTime); let go of before then, they close on nothing.</summary>
    private static void Pinch(ToolStep step) {
        var tool = step.Tool;
        var use = tool.Use;
        if (!step.Trigger || !step.Lowered) {
            use.PinchWait = null;
            return;
        }
        var fresh = step.Hand.States != use.RestState;
        use.RestState = step.Hand.States;
        if (use.PinchWait is null || (fresh && Mathf.Abs(step.Tip.Y - use.RestHeight) > RestReach)) {
            use.RestHeight = step.Tip.Y;
            use.RestFor = 0f;
            use.RestConfirmed = false;
        } else {
            use.RestFor += step.Dt;
            use.RestConfirmed |= fresh;
        }
        use.PinchWait = (use.PinchWait ?? 0f) + (step.Touching ? step.Dt : 0f);
        var rested = use.RestConfirmed && use.RestFor >= RestTime;
        if (!step.Touching || (!rested && use.PinchWait < MaxWait)) {
            return;
        }
        use.PinchWait = null;
        tool.Hold = step.Patient.Grip(tool.Uid, step.Zone, step.Uv, step.Probe.Depth, ToolActions.SkinHooks.Contains(step.Def.Id));
        step.Surgery.SetAttached(step.Hand.Peer, tool.Slot, tool.Hold is not null);
    }
}
