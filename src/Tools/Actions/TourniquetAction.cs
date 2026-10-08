namespace Scalpel.Tools;

/// <summary>Pressed onto an arm or a leg, the band goes around the limb there and stays when the hand lets go.
/// </summary>
public sealed class TourniquetAction : ToolAction
{
    public override void Apply(ToolStep step)
    {
        if (step.Pressed && step.Lowered && step.Patient.Body.LimbRingAt(step.Tip) is { } ring)
        {
            step.Patient.ApplyTourniquet();
            step.Tools.Wrap(step.Tool, ring);
        }
    }
}
