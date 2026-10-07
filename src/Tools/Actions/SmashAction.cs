namespace Scalpel.Tools;

/// <summary>Mallets: a strike bruises, breaks a bone target and throws blood up.</summary>
public sealed class SmashAction : ToolAction
{
    public override void Apply(ToolStep step)
    {
        if (!(step.Pressed && step.Lowered && step.Touching))
        {
            return;
        }
        step.Patient.SmashAt(step.Uv, step.Def);
        if (step.InSite)
        {
            step.Surgery.Effect(ToolEffect.Spatter, step.Tip, 0);
        }
    }
}
