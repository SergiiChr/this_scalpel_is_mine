namespace Scalpel.Tools;

/// <summary>
/// The Gelpi retractor: pressed onto a cut, the jaws go in on both sides of the aim and stay there, the points down in
/// the cut as deep as it goes; pressed again they come out. Pressed where there's no cut to go into, it bounces off.
/// </summary>
public sealed class SpreadAction : ToolAction
{
    public override void Apply(ToolStep step)
    {
        var tool = step.Tool;
        var patient = step.Patient;
        if (step.Pressed && tool.Hold is null && step.Lowered)
        {
            if (patient.SetSpreader(tool.Uid, ToolActions.SidePoints(tool, tool.Spread), tool.Spread) is { } hold)
            {
                tool.Hold = hold;
                step.Surgery.SetAttached(step.Hand.Peer, tool.Slot, true);
                // It goes down lying along the skin, the hand holding it as flat as a hand tilts a tool.
                var dug = patient.Body.UvToWorld(hold.Middle, Mathf.Min(hold.Depth, ToolActions.SpreadReach));
                var lying = new Basis(Vector3.Up, tool.GlobalBasis.GetEuler(EulerOrder.Yxz).Y) * new Basis(Vector3.Right, SurgeonHand.TiltRange.Y);
                step.Tools.SyncSpread(tool, new Transform3D(lying, dug + lying.Z * tool.Def.Length));
            }
            else if (step.Touching)
            {
                step.Surgery.BounceHand(step.Hand.Peer, tool.Slot);
            }
        }
        else if (step.Pressed && tool.Hold is not null)
        {
            patient.ReleaseGrip(tool.Uid, tool.Hold, false);
            tool.Hold = null;
            step.Surgery.SetAttached(step.Hand.Peer, tool.Slot, false);
            step.Tools.SyncSpread(tool);
        }
    }

    /// <summary>The spreader's wheel: <paramref name="direction"/> positive for up (open), negative for down (close).
    /// Set in a cut, the jaws take its edges along.</summary>
    public static void Adjust(SurgicalTool tool, int direction, Patient patient)
    {
        tool.Spread = Mathf.Clamp(tool.Spread + direction * ToolActions.SpreadStep, ToolActions.SpreadRange.X, ToolActions.SpreadRange.Y);
        if (tool.Hold is SpreadHold hold)
        {
            patient.OpenSpreader(hold, tool.Spread);
        }
    }
}
