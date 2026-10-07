namespace Scalpel.Tools;

/// <summary>The IV catheter: held against an arm it starts a line. It sticks wherever it goes into the arm, but only a
/// needle in a vein lets anything through.</summary>
public sealed class IvLineAction : ToolAction
{
    public override void Apply(ToolStep step)
    {
        var patient = step.Patient;
        // Held against an arm, not only on the frame the button went down: the tip may land a moment later.
        var arm = step.Probe.Part.StartsWith("arm", StringComparison.Ordinal)
            || (step.Zone == SiteZone.Site && patient.Scenario.Site == "forearm");
        if (!step.Settled || !arm || patient.IvSet)
        {
            return;
        }
        var inVein = patient.Body.VeinAt(step.Tip);
        patient.SetIv(step.Tip, inVein);
        if (inVein)
        {
            step.Surgery.AnnounceDebug("Hit the vein");
        }
        step.Surgery.Effect(ToolEffect.Bead, step.Tip, 0);
        ToolActions.UseCharge(step.Tool);
    }
}
