namespace Scalpel.Tools;

/// <summary>Pressed onto an arm or a leg, the band goes around the limb there and stays when the hand lets go. Not
/// over a wound: wrapped across it, the band would lie in the way of the work it's meant to make easier. Anywhere
/// else on the limb, the edge of the surgical site included, it goes on.
/// </summary>
public sealed class TourniquetAction : ToolAction {
    /// <summary>How near (meters, along the limb) the band's middle may come to a wound.</summary>
    private const float WoundClearance = 0.02f;

    public override void Apply(ToolStep step) {
        if (!step.Pressed || !step.Lowered) {
            return;
        }
        if (step.Patient.Body.LimbRingAt(step.Tip) is not { } ring) {
            return;
        }
        var body = step.Patient.Body;
        if (step.Patient.Wounds.Any(wound => wound.Points.Any(point => body.RingCovers(ring, point, WoundClearance)))) {
            step.Surgery.Tell(step.Hand.Peer, "The tourniquet goes on above the wound, not over it.");
        } else {
            step.Patient.ApplyTourniquet();
            step.Tools.Wrap(step.Tool, ring);
        }
    }
}
