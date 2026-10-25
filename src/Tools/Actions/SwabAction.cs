namespace Scalpel.Tools;

/// <summary>Lowered, gauze and swabs wipe blood away and soak it up until full, and gauze presses on the wounds under
/// its pad, full or not: there's no pressure to set.
/// A cotton pad held in the glove dips and wipes like one held in forceps, spoiling the site.
/// Either wipes blood beads off wherever it touches, on the arm too.</summary>
public sealed class SwabAction : ToolAction {
    public override void Apply(ToolStep step) {
        if (!step.Lowered) {
            return;
        }
        var tool = step.Tool;
        if (step.Def.Id == "cotton_pad") {
            ToolActions.Wipe(tool, step, step.Dt, gloved: true);
            return;
        }
        ToolActions.WipeBeads(step, tool);
        if (step.InSite) {
            var wiped = ToolActions.Gather(tool, step.Uv, step.Dt);
            if (wiped > 0f) {
                ToolActions.Swab(step, tool, wiped);
            }
            if (step.Def.Id == "gauze") {
                step.Patient.PressWithGauze(tool, step.Zone, step.Dt);
            }
        }
    }
}
