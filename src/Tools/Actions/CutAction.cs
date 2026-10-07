namespace Scalpel.Tools;

/// <summary>A blade: pressed in at a depth level it stabs as wide as itself, moved along its edge it cuts what it
/// passes through, dragged sideways it only drags.</summary>
public sealed class CutAction : ToolAction
{
    public override void Apply(ToolStep step)
    {
        var tool = step.Tool;
        var use = tool.Use;
        var patient = step.Patient;
        var level = step.Level;
        if (step.Lowered && level > 0 && step.Zone == SiteZone.Site && level > use.StabbedLevel)
        {
            // Pressed in at a new depth: the point goes in as wide as the blade, before it's moved at all.
            use.StabbedLevel = level;
            var half = ToolActions.BladeDirection(tool) * ToolActions.StabLength * 0.5f;
            var from = patient.Body.WorldToUv(step.Tip - half);
            var to = patient.Body.WorldToUv(step.Tip + half);
            patient.Cut(StrokeKey(tool), from, to, ToolActions.DepthByLevel[level], step.Def.Sharpness, !tool.Sterile, 0f);
            step.Surgery.Sound(level >= 3 ? "cut_deep" : "cut_skin", step.Tip);
        }
        // Pressed in at full effort where the muscle is thin (no fat over it), the point reaches the bone under it.
        if (step.Lowered && level == 3 && step.Zone == SiteZone.Site && step.Probe.Depth >= patient.Body.MuscleBottom)
        {
            patient.ScrapeBone(step.Uv, step.Tip, step.Dt);
        }
        // Moved along its edge, the blade cuts what it passes through, also where it runs on past the end of an opening
        // it's already in.
        if (!(step.Lowered && level > 0 && step.InSite))
        {
            use.LastUv = new Vector2(-1, -1);
            return;
        }
        if (step.Zone == SiteZone.Cavity)
        {
            patient.CutCavity(step.Uv, step.Probe.Depth, step.Def.Sharpness, !tool.Sterile, step.Dt * step.Effort);
        }
        var moved = use.LastUv.X >= 0f && use.LastUv.DistanceTo(step.Uv) > 0.003f;
        if (moved)
        {
            var along = (step.Tip - use.LastTip) * new Vector3(1, 0, 1);
            if (Mathf.Abs(along.Normalized().Dot(ToolActions.BladeDirection(tool))) >= ToolActions.AlongBlade)
            {
                patient.Cut(StrokeKey(tool), use.LastUv, step.Uv, ToolActions.DepthByLevel[level], step.Def.Sharpness,
                    !tool.Sterile, step.Hand.Speed);
                patient.DebrideAt(step.Uv);
                step.Surgery.Sound(level >= 3 ? "cut_deep" : "cut_skin", step.Tip);
            }
            else
            {
                // Dragged sideways: the next stroke starts here.
                use.Stroke++;
            }
        }
        if (use.LastUv.X < 0f || moved)
        {
            use.LastUv = step.Uv;
            use.LastTip = step.Tip;
        }
    }

    /// <summary>The key of the tool's current stroke: one continuous stroke grows one wound.</summary>
    public static long StrokeKey(SurgicalTool tool) => tool.Uid * 1000L + tool.Use.Stroke;
}
