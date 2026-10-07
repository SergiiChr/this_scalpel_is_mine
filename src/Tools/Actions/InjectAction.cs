namespace Scalpel.Tools;

/// <summary>A prefilled injection: the needle goes in while lowered; pushing the plunger all the way gives the dose.
/// </summary>
public sealed class InjectAction : ToolAction
{
    public override void Apply(ToolStep step)
    {
        var tool = step.Tool;
        if (!(step.Lowered && step.Touching && step.LevelUp && step.Level == 3 && tool.Charges != 0))
        {
            return;
        }
        if (step.Def.IvOnly)
        {
            step.Surgery.Announce($"{step.Def.Name} goes on the IV stand, not in the patient.", true);
            return;
        }
        step.Patient.Administer(step.Def.Drug, DrugRoute.Direct);
        step.Surgery.Sound("syringe_inject", step.Tip);
        step.Surgery.Effect(ToolEffect.Bead, step.Tip, 0);
        ToolActions.UseCharge(tool);
    }
}
