namespace Scalpel.Tools;

/// <summary>Bottles pour as long as Use tool holds them tipped over a dish, up to the dish's rim.</summary>
public sealed class PourAction : ToolAction
{
    public override void Apply(ToolStep step)
    {
        if (!step.Lowered)
        {
            return;
        }
        if (step.Tools.NearestDish(step.Tip) is { } dish)
        {
            var ml = Mathf.Min(step.Def.Power * step.Dt, dish.Def.Volume - dish.Ml);
            if (ml > 0f)
            {
                step.Tools.AddLiquid(dish, ml, new Dictionary<string, float> { [step.Def.Drug] = ml });
            }
        }
        else if (step.Touching)
        {
            step.Surgery.Announce($"Pour the {step.Def.Name.ToLowerInvariant()} into a dish.", true);
        }
    }
}
