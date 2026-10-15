namespace Scalpel.Tools;

/// <summary>The IV catheter: held against an arm it goes in and is taped down. It sticks wherever it goes into the arm,
/// but only one in a vein with the IV tubing connected to it (<see cref="IvLine.HoldOn"/>) lets anything through.
/// One on the tubing replaces one in the arm that isn't.</summary>
public sealed class IvLineAction : ToolAction {
    public override void Apply(ToolStep step) {
        var patient = step.Patient;
        // Held against an arm, not only on the frame the button went down: the tip may land a moment later.
        var arm = step.Probe.Part.StartsWith("arm", StringComparison.Ordinal)
            || (step.Zone == SiteZone.Site && patient.Scenario.Site == "forearm");
        var connected = step.Surgery.Room.IvLine.Catheter == step.Tool;
        if (!step.Settled || !arm || !patient.CanSetIv(connected)) {
            return;
        }
        var inVein = patient.Body.VeinAt(step.Tip);
        patient.SetIv(step.Tip, inVein, connected);
        if (inVein) {
            step.Surgery.AnnounceDebug("Hit the vein");
        }
        if (!connected) {
            step.Surgery.AnnounceDebug("The catheter isn't on the IV line");
        }
        step.Surgery.Effect(ToolEffect.Bead, step.Tip, 0);
        ToolActions.UseCharge(step.Tool);
    }
}
