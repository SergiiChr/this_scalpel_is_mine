namespace Scalpel.Tools;

/// <summary>A blade: pressed in at a depth level it stabs as wide as itself, moved along its edge it cuts what it
/// passes through, dragged sideways it only drags.</summary>
public sealed class CutAction : ToolAction {
    public override void Apply(ToolStep step) {
        var tool = step.Tool;
        var use = tool.Use;
        var patient = step.Patient;
        var level = step.Level;
        var body = patient.Body;
        var direction = ToolActions.BladeDirection(tool);
        var span = step.Lowered && level > 0 && step.InSite ? tool.Blade?.SkinSpan(body, tool.GlobalTransform, direction) : null;
        // Inside the cut the blade meets the lips it's parting, and their skin lies to one side or the other of the line
        // it cuts: there the incision's end keeps across to where the blade has gone over the body since it went in.
        var cutting = use.LastUv.X >= 0f && use.LastCutEnd.IsFinite();
        var offset = cutting ? use.LastUv - body.WorldToUv(use.LastCutEnd) : Vector2.Zero;
        var side = (body.WorldToUv(step.Tip + direction.Cross(Vector3.Up) * 0.01f) - body.WorldToUv(step.Tip)).Normalized();
        Vector2 Placed(Vector3 end, Vector2 uv) => cutting && body.Tissue.IsOpen(uv, TissueDepth.Skin)
            ? uv + side * (body.WorldToUv(body.Site.ToGlobal(end)) + offset - uv).Dot(side)
            : uv;
        var from = span is { } contact ? Placed(contact.From, contact.FromUv) : step.Uv;
        var to = span is { } contactEnd ? Placed(contactEnd.To, contactEnd.ToUv) : step.Uv;
        var surfaceContact = span is { } footprint ? body.Site.ToGlobal((footprint.From + footprint.To) * 0.5f) : step.Tip;
        var stabbed = false;
        if (step.Lowered && level > 0 && step.Zone == SiteZone.Site && level > use.StabbedLevel
            && (tool.Blade is null || span is not null)) {
            // Pressed in at a new depth: the point goes in as wide as the blade, before it's moved at all.
            use.StabbedLevel = level;
            stabbed = true;
            if (span is null) {
                var half = direction * ToolActions.StabLength * 0.5f;
                from = body.WorldToUv(step.Tip - half);
                to = body.WorldToUv(step.Tip + half);
            }
            patient.Cut(StrokeKey(tool), from, to, ToolActions.DepthByLevel[level], step.Def.Sharpness, !tool.Sterile, 0f);
            use.CutWall = true;
            step.Surgery.Sound(level >= 3 ? "cut_deep" : "cut_skin", step.Tip);
        }
        // Pressed in at full effort where the muscle is thin (no fat over it), the point reaches the bone under it.
        if (step.Lowered && level == 3 && step.Zone == SiteZone.Site && step.Probe.Depth >= patient.Body.MuscleBottom) {
            patient.ScrapeBone(step.Uv, step.Tip, step.Dt);
        }
        // Moved along its edge, the blade cuts what it passes through, also where it runs on past the end of an opening
        // it's already in.
        if (!(step.Lowered && level > 0 && step.InSite)) {
            use.LastUv = new Vector2(-1, -1);
            return;
        }
        if (use.PutDownAt.X < 0f) {
            use.PutDownAt = step.Uv;
        }
        if (step.Zone == SiteZone.Cavity) {
            patient.CutCavity(step.Uv, step.Probe.Depth, step.Def.Sharpness, !tool.Sterile, step.Dt * step.Effort,
                use.CutWall ? null : use.PutDownAt);
        }
        if (tool.Blade is not null && span is null
            && !(body.Tissue.IsOpen(step.Uv, TissueDepth.Skin) && step.Probe.Depth > PatientBody.SkinThickness)) {
            // A real blade must enter skin before making a surface wound. Inside an existing opening,
            // a buried blade can cut the deeper wall without intersecting the skin above it.
            // Retain the last real material contact while the blade lies wholly inside its slit.
            // Mark a fresh footprint on re-entry, rather than treating a missing intersection as a cut.
            use.LastCutContact = Vector3.Inf;
            return;
        }
        var fresh = use.LastUv.X < 0f || !use.LastCutContact.IsFinite();
        if (fresh && !stabbed && span is not null) {
            // Re-entering intact skin after running inside an opening still cuts the whole visible section.
            // Starting at its midpoint leaves the half behind it as an intact bridge.
            patient.Cut(StrokeKey(tool), from, to, ToolActions.DepthByLevel[level], step.Def.Sharpness,
                !tool.Sterile, step.Hand.Speed);
        }
        var motion = fresh ? Vector3.Zero : surfaceContact - use.LastCutContact;
        var moved = !fresh && body.WorldToUv(use.LastCutContact).DistanceTo(body.WorldToUv(surfaceContact)) > 0.003f;
        if (moved) {
            var along = motion * new Vector3(1, 0, 1);
            var aligned = Mathf.Abs(along.Normalized().Dot(direction)) >= ToolActions.AlongBlade;
            var end = along.Dot(direction) >= 0f ? to : from;
            if (aligned && patient.Cut(StrokeKey(tool), use.LastUv, end, ToolActions.DepthByLevel[level],
                    step.Def.Sharpness, !tool.Sterile, step.Hand.Speed)) {
                // A blade that has sunk into the cut it's making still cuts the wall ahead of it, but only cutting on
                // whole tissue makes it pass over a target.
                use.CutWall = use.CutWall || step.Zone == SiteZone.Site;
                patient.DebrideAt(step.Uv);
                step.Surgery.Sound(level >= 3 ? "cut_deep" : "cut_skin", step.Tip);
            } else if (!aligned) {
                // Dragged sideways: the next stroke starts here.
                // Lowering or tilting can move the footprint sideways without being a sideways drag.
                var grip = tool.GlobalPosition - use.LastCutGrip;
                var across = grip.Slide(Vector3.Up);
                if (body.WorldToUv(tool.GlobalPosition).DistanceTo(body.WorldToUv(use.LastCutGrip)) > 0.003f
                    && across.LengthSquared() > grip.Y * grip.Y
                    && Mathf.Abs(across.Normalized().Dot(direction)) < ToolActions.AlongBlade) { use.Stroke++; }
            }
        }
        if (fresh || moved) {
            var forward = motion.Dot(direction) >= 0f;
            use.LastUv = fresh ? (from + to) * 0.5f : forward ? to : from;
            use.LastCutEnd = fresh || span is not { } reached
                ? surfaceContact
                : body.Site.ToGlobal(forward ? reached.To : reached.From);
            use.LastCutContact = surfaceContact;
            use.LastCutGrip = tool.GlobalPosition;
        }
    }

    /// <summary>The key of the tool's current stroke: one continuous stroke grows one wound.</summary>
    public static long StrokeKey(SurgicalTool tool) => tool.Uid * 1000L + tool.Use.Stroke;
}
