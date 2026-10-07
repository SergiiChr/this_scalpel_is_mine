namespace Scalpel.Tools;

/// <summary>Gauze and swabs wipe blood away (and press on a wound); a cotton pad held in the glove dips and wipes like
/// one held in forceps, spoiling the site.</summary>
public sealed class SwabAction : ToolAction
{
    public override void Apply(ToolStep step)
    {
        if (!(step.Lowered && step.Level > 0))
        {
            return;
        }
        var tool = step.Tool;
        if (step.Def.Id == "cotton_pad")
        {
            ToolActions.Wipe(tool, step, step.Dt * step.Effort, gloved: true);
        }
        else if (step.InSite)
        {
            var wiped = ToolActions.Gather(tool, step.Uv, step.Dt * step.Effort);
            if (wiped > 0f)
            {
                step.Patient.SwabAt(step.Zone, step.Uv, step.Def, wiped);
            }
        }
    }
}
