namespace Scalpel.Tools;

/// <summary>Staplers: pressed down, one staple goes in where its legs (the aim's two points) are. Its middle is over the
/// cut, in the opening when the cut gapes.</summary>
public sealed class StapleAction : ToolAction
{
    public override void Apply(ToolStep step)
    {
        var tool = step.Tool;
        if (!(step.Pressed && step.Lowered && step.InSite && tool.Charges != 0))
        {
            return;
        }
        var body = step.Patient.Body;
        var legs = tool.Use.StapleAim.Length > 0 ? tool.Use.StapleAim : ToolActions.StapleLegs(tool, body);
        var a = body.WorldToUv(legs[0]);
        var b = body.WorldToUv(legs[1]);
        if (step.Patient.Staple(a, b, step.Def, step.Hand.Mods.Mult("improvised_mult")))
        {
            ToolActions.UseCharge(tool);
            step.Surgery.Sound(step.Def.Improvised ? "office_staple" : "staple", step.Tip);
        }
        else
        {
            step.Surgery.Tell(step.Hand.Peer, ToolActions.StapleMiss(body.Tissue, a, b));
        }
    }
}
