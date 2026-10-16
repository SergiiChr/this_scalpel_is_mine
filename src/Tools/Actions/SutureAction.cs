namespace Scalpel.Tools;

/// <summary>Closures pressed on with Use tool (paper clips, quick stitches): on the skin they close the cut bin
/// by bin, in an opening the muscle, or an internal injury first. A tensioned one (Patient.TensionedClosures) is pulled
/// as tight as its wheel says (<see cref="SutureState.Tension"/>).</summary>
public sealed class SutureAction : ToolAction {
    public override void Apply(ToolStep step) {
        var tool = step.Tool;
        var patient = step.Patient;
        var def = step.Def;
        if (!(step.Lowered && tool.Charges != 0)) {
            return;
        }
        if (step.Zone == SiteZone.Site) {
            // Where the skin and fat still gape and the muscle shows, the needle reaches it: sewing the muscle on both
            // sides closes the opening before the muscle right here is done.
            if (patient.Body.LayerAt(step.Uv) == "muscle" && patient.CloseMuscleAt(step.Uv, def, step.Dt)) {
                ToolActions.SpendCharge(tool);
                step.Surgery.Sound("suture_pull", step.Tip);
            } else if (patient.CloseAt(step.Uv, def, step.Dt, step.Hand.Mods.Mult("improvised_mult"), tool.Suture.Tension)) {
                ToolActions.SpendCharge(tool);
                step.Surgery.Sound("suture_pull", step.Tip);
            }
        } else if (step.Zone == SiteZone.Cavity) {
            // An internal injury under the needle comes first: sewing the muscle of the opening shut would close the way
            // in to it. Then, inside a wound through the muscle, the muscle.
            if (!patient.CloseInternalAt(step.Uv, def, step.Dt) && patient.CloseMuscleAt(step.Uv, def, step.Dt)) {
                ToolActions.SpendCharge(tool);
                step.Surgery.Sound("suture_pull", step.Tip);
            }
        }
    }
}
