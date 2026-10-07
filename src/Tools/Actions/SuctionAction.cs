namespace Scalpel.Tools;

/// <summary>Suction: draws blood off the skin and out of the cavity, and drains fluid targets.</summary>
public sealed class SuctionAction : ToolAction
{
    public override void Apply(ToolStep step)
    {
        if (!(step.Lowered && step.Level > 0 && step.InSite))
        {
            return;
        }
        step.Patient.SuctionAt(step.Zone, step.Uv, step.Def, step.Dt * step.Effort);
        if (GD.Randf() < step.Dt * 1.2f)
        {
            step.Surgery.Sound("suction_slurp", step.Tip);
        }
        if (step.Def.Id == "metal_straw")
        {
            step.Surgery.AddSickness(step.Hand.Peer, step.Dt * 0.08f);
        }
    }
}
