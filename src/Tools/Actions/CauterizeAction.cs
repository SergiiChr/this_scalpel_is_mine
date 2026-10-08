namespace Scalpel.Tools;

/// <summary>Cautery and lighters: heat seals bleeding wounds, smokes and burns what it touches.</summary>
public sealed class CauterizeAction : ToolAction
{
    public override void Apply(ToolStep step)
    {
        var tool = step.Tool;
        if (step.LevelUp && step.Level == 1 && step.Def.Id == "lighter")
        {
            step.Surgery.Sound("lighter_flick", step.Tip);
        }
        if (!(step.Lowered && step.Level > 0 && step.InSite && tool.Charges != 0))
        {
            return;
        }
        step.Patient.CauterizeAt(step.Zone, step.Uv, step.Def, step.Dt * step.Effort);
        step.Surgery.Effect(ToolEffect.Smoke, step.Tip, 180);
        if (GD.Randf() < step.Dt * 1.2f)
        {
            step.Surgery.Sound("cautery_sizzle", step.Tip);
        }
        if (step.Def.Id == "lighter" && GD.Randf() < step.Dt)
        {
            tool.Charges--;
        }
    }
}
