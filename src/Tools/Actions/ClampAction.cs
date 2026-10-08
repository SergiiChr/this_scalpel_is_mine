namespace Scalpel.Tools;

/// <summary>
/// Forceps, hemostats and retractors: pressed, the jaws pinch what they were lowered onto and let go anywhere. Forceps
/// also pick up a cotton pad (wiped and dipped while lowered) or carry a skin graft to a cleaned burn.
/// </summary>
public sealed class ClampAction : ToolAction
{
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
        // Pinching takes hold only on something the jaws were lowered onto; letting go works anywhere.
        else if (step.Pressed && (step.Lowered || tool.Hold is not null))
        {
            if (tool.Hold is null)
            {
                tool.Hold = patient.Grip(tool.Uid, step.Zone, step.Uv, step.Probe.Depth, ToolActions.SkinHooks.Contains(step.Def.Id));
            }
            else
            {
                patient.ReleaseGrip(tool.Uid, tool.Hold, false);
                tool.Hold = null;
            }
            step.Surgery.SetAttached(step.Hand.Peer, tool.Slot, tool.Hold is not null);
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
}
