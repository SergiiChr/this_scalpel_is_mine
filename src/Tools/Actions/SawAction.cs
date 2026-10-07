namespace Scalpel.Tools;

/// <summary>Saws: run on a bone target they cut through it, dragged over the skin they tear it.</summary>
public sealed class SawAction : ToolAction
{
    public override void Apply(ToolStep step)
    {
        if (!(step.Lowered && step.Level > 0 && step.InSite))
        {
            return;
        }
        var tool = step.Tool;
        var use = tool.Use;
        if (step.Patient.SawAt(step.Uv, step.Def, step.Dt * step.Effort))
        {
            step.Surgery.Effect(ToolEffect.Dust, step.Tip, 150);
        }
        else if (step.Zone == SiteZone.Site && use.LastUv.X >= 0f && use.LastUv.DistanceTo(step.Uv) > 0.004f)
        {
            step.Patient.Cut(CutAction.StrokeKey(tool), use.LastUv, step.Uv, 1f, 0.3f, !tool.Sterile, 0.5f);
        }
        if (GD.Randf() < step.Dt * 2f)
        {
            step.Surgery.Sound("saw_bone", step.Tip);
        }
        use.LastUv = step.Uv;
    }
}
