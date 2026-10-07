namespace Scalpel.Tools;

/// <summary>A skin marker: draws where it's moved on the skin.</summary>
public sealed class MarkAction : ToolAction
{
    public override void Apply(ToolStep step)
    {
        var use = step.Tool.Use;
        if (!(step.Lowered && step.Zone == SiteZone.Site))
        {
            use.LastUv = new Vector2(-1, -1);
            return;
        }
        if (use.LastUv.X >= 0f)
        {
            step.Patient.Mark(use.LastUv, step.Uv);
        }
        use.LastUv = step.Uv;
    }
}
