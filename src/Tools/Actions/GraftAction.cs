namespace Scalpel.Tools;

/// <summary>A skin graft pressed onto a cleaned burn goes on.</summary>
public sealed class GraftAction : ToolAction
{
    public override void Apply(ToolStep step)
    {
        if (step.Pressed && step.Lowered && step.Zone == SiteZone.Site && step.Tool.Charges != 0
            && step.Patient.GraftAt(step.Uv, step.Def))
        {
            ToolActions.UseCharge(step.Tool);
        }
    }
}
