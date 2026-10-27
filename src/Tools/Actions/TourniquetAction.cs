namespace Scalpel.Tools;

/// <summary>Pressed onto an arm or a leg, the band goes around the limb there and stays when the hand lets go. Not
/// over the surgical site: wrapped across it, the band would lie in the way of the work it's meant to make easier.
/// </summary>
public sealed class TourniquetAction : ToolAction {
    public override void Apply(ToolStep step) {
        if (!step.Pressed || !step.Lowered) {
            return;
        }
        if (step.Patient.Body.LimbRingAt(step.Tip) is not { } ring) {
            return;
        }
        if (step.InSite || step.Patient.Body.RingCrossesSite(ring)) {
            step.Surgery.Announce("The tourniquet goes on above the wound, not over it.", true);
        } else {
            step.Patient.ApplyTourniquet();
            step.Tools.Wrap(step.Tool, ring);
        }
    }
}
