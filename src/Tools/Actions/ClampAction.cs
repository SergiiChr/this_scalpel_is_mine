namespace Scalpel.Tools;

/// <summary>
/// Forceps, hemostats and retractors: pressed, the jaws pinch what they come to rest on and let go anywhere. Forceps
/// also pick up a cotton pad (wiped and dipped while lowered) or carry a skin graft to a cleaned burn.
/// </summary>
public sealed class ClampAction : ToolAction
{
    /// <summary>A pinch closes once the tip has stayed within RestReach (meters, up or down) for RestTime (seconds) on the
    /// patient, not when the press counts: lowered out of an aim or a lift, or by a remote hand whose moves come late,
    /// the tip is still coming down then.
    /// RestReach is more than a shaking hand moves the tip in a frame (SurgeonStatus.MaxTremor) and less than a tool
    /// settling at SurgeonHand.SettleSpeed comes down.
    /// Only height counts, so a hand moving across still closes.
    /// A tip that never comes to rest closes after MaxWait on whatever is under it.</summary>
    internal const float RestReach = 0.005f;
    internal const float RestTime = 0.03f;
    internal const float MaxWait = 0.5f;

    public override void Apply(ToolStep step)
    {
        var tool = step.Tool;
        var tools = step.Tools;
        var patient = step.Patient;
        var pad = tools.CarriedBy(tool);
        var loosePad = step.Pressed && step.Lowered && ToolActions.PadHolders.Contains(step.Def.Id)
            ? tools.NearestOf("cotton_pad", step.Tip, ToolActions.PadReach)
            : null;
        if (pad is { Def.Action: "graft" })
        {
            // A graft taken from the skin goes down where it's pressed onto a cleaned burn; pressed in the air it's let go.
            if (step.Pressed && step.Lowered && step.Zone == SiteZone.Site && patient.GraftAt(step.Uv, pad.Def))
            {
                ToolActions.UseCharge(pad);
                if (pad.Charges == 0)
                {
                    tools.Consume(pad);
                }
            }
            else if (step.Pressed && !step.Touching)
            {
                tools.DropCarried(tool);
            }
        }
        else if (pad is not null)
        {
            // Use lowers the pad to wipe or dip it; pressed in the air, away from the dish, it lets the pad go.
            if (step.Pressed && !step.Touching && tools.NearestDish(step.Tip) is null)
            {
                tools.DropCarried(tool);
            }
            else if (step.Lowered)
            {
                ToolActions.Wipe(pad, step, step.Dt, gloved: false);
            }
        }
        else if (loosePad is not null && tool.Hold is null)
        {
            tools.Carry(loosePad, tool);
        }
        else if (step.Pressed && tool.Hold is not null)
        {
            // Letting go works anywhere.
            patient.ReleaseGrip(tool.Uid, tool.Hold, false);
            tool.Hold = null;
            step.Surgery.SetAttached(step.Hand.Peer, tool.Slot, false);
        }
        else if (tool.Hold is null && ((step.Pressed && step.Lowered) || tool.Use.PinchWait is not null))
        {
            Pinch(step);
        }
        else if (tool.Hold is { } hold)
        {
            var power = step.Def.Power * step.Hand.Mods.Mult("grip_strength_mult");
            tool.Hold = patient.UpdateGrip(tool.Uid, hold, step.Tip, power, step.Dt, step.Hand.Speed);
            if (tool.Hold is null)
            {
                step.Surgery.SetAttached(step.Hand.Peer, tool.Slot, false);
            }
        }
    }

    /// <summary>Pressed onto the patient, the jaws close on what's under the tip once it has come to rest there (see
    /// RestTime); let go of before then, they close on nothing.</summary>
    private static void Pinch(ToolStep step)
    {
        var tool = step.Tool;
        var use = tool.Use;
        if (!step.Trigger || !step.Lowered)
        {
            use.PinchWait = null;
            return;
        }
        if (use.PinchWait is not { } waited)
        {
            waited = 0f;
            use.RestHeight = step.Tip.Y;
            use.RestFor = 0f;
        }
        else if (Mathf.Abs(step.Tip.Y - use.RestHeight) <= RestReach)
        {
            use.RestFor += step.Dt;
        }
        else
        {
            use.RestHeight = step.Tip.Y;
            use.RestFor = 0f;
        }
        use.PinchWait = waited + step.Dt;
        if (!(step.Touching && use.RestFor >= RestTime) && use.PinchWait < MaxWait)
        {
            return;
        }
        use.PinchWait = null;
        tool.Hold = step.Patient.Grip(tool.Uid, step.Zone, step.Uv, step.Probe.Depth, ToolActions.SkinHooks.Contains(step.Def.Id));
        step.Surgery.SetAttached(step.Hand.Peer, tool.Slot, tool.Hold is not null);
    }
}
