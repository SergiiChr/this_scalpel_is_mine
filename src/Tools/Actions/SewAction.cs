namespace Scalpel.Tools;

/// <summary>
/// The needle: a click (Use tool let go before SutureTieHold) where it rests on a wound passes its thread through a
/// new hole there, a longer hold adds the last hole (where there's room for one) and ties the thread off. Held over an
/// internal injury in the opening, it sews that instead.
/// </summary>
public sealed class SewAction : ToolAction
{
    public override void Apply(ToolStep step)
    {
        var tool = step.Tool;
        var patient = step.Patient;
        var zone = step.Zone;
        var uv = step.Uv;
        if (step.Released && step.Surgery.Surgeons.TryGetValue(tool.Holder, out var surgeon))
        {
            // The release frame already uses the carry angle. Test the working tip, not that rotation's lateral jump.
            var contact = patient.Body.Probe(surgeon.Hands[tool.Slot].WorkingTipPosition(tool.Def.Length));
            zone = contact.Zone;
            uv = contact.Uv;
        }
        var suture = tool.Suture;
        // Releasing lifts the tool immediately, so allow air directly over the puncture (up to 3 mm of lateral drift).
        // Leaving the site/body, or sliding away above it, cancels the cached contact rather than sewing the old point.
        var inSite = zone is SiteZone.Site or SiteZone.Cavity;
        var overPuncture = suture.At.X >= 0f && new Rect2(Vector2.Zero, Vector2.One).HasPoint(uv)
            && patient.Body.OnBody(uv) && ((uv - suture.At) * patient.Body.SiteSize).Length() <= 0.003f;
        if (!inSite && !overPuncture)
        {
            suture.At = new Vector2(-1, -1);
        }
        if (step.Trigger)
        {
            // Start each press unattached, then keep the latest valid puncture point until release. A curved needle
            // resting on deforming skin can cross the contact threshold for a frame; that must not discard a click that
            // already landed.
            if (suture.Hold == 0f)
            {
                suture.At = new Vector2(-1, -1);
            }
            suture.Hold += step.Dt;
            if (step.Lowered && inSite)
            {
                suture.At = uv;
            }
            if (step.Lowered && zone == SiteZone.Cavity && patient.CloseInternalAt(uv, tool.Def, step.Dt))
            {
                suture.PressUsed = true;
            }
            else if (suture.Hold >= ToolActions.SutureTieHold && !suture.PressUsed && suture.Thread != 0)
            {
                // The last hole, if there's room for one here, then the knot either way.
                suture.PressUsed = true;
                AddHole(tool, patient);
                patient.FinishSuture(suture.Thread, tool.Def.Quality);
                step.Surgery.Sound("suture_pull", step.Tip);
            }
        }
        else if (step.Released)
        {
            if (!suture.PressUsed && AddHole(tool, patient))
            {
                step.Surgery.Sound("suture_pull", step.Tip);
            }
            suture.Hold = 0f;
            suture.PressUsed = false;
        }
        EndFinished(tool, patient);
    }

    /// <summary>Passes the needle's thread through a new hole where it was last on the patient, starting a thread if it
    /// has none.</summary>
    private static bool AddHole(SurgicalTool tool, Patient patient)
    {
        var suture = tool.Suture;
        if (suture.At.X < 0f)
        {
            return false;
        }
        if (suture.Thread == 0)
        {
            suture.Thread = patient.NewSuture();
        }
        var placed = patient.PlaceSutureAnchor(suture.Thread, suture.At, suture.Tension);
        var info = patient.Body.Tissue.Thread(suture.Thread);
        if (info is null)
        {
            suture.Thread = 0;
        }
        else if (suture.Layer != info.Layer)
        {
            suture.Layer = info.Layer;
            Surgery.Current!.Tools.SyncSuture(tool);
        }
        return placed;
    }

    /// <summary>A thread tied off or torn through is done with: the needle's next hole starts a new one.</summary>
    private static void EndFinished(SurgicalTool tool, Patient patient)
    {
        if (tool.Suture.Thread != 0 && patient.SutureDone(tool.Suture.Thread))
        {
            tool.Suture.Thread = 0;
            tool.Suture.Layer = TissueDepth.None;
            Surgery.Current!.Tools.SyncSuture(tool);
        }
    }

    /// <summary>The needle's wheel: <paramref name="direction"/> positive for up (loosen), negative for down
    /// (tighten).</summary>
    public static void AdjustTension(SurgicalTool tool, int direction, Patient patient)
    {
        var suture = tool.Suture;
        suture.Tension = Mathf.Clamp(suture.Tension + direction * ToolActions.SutureTensionStep,
            ToolActions.SutureTensionRange.X, ToolActions.SutureTensionRange.Y);
        if (suture.Thread != 0)
        {
            patient.SetSutureTension(suture.Thread, suture.Tension);
            EndFinished(tool, patient);
        }
    }

    /// <summary>How a needle's thread reads at its tension in the layer it's in (skin before a thread is started):
    /// "loose" (the edges don't meet), "closed", or "too tight" (past halfway from closed to tearing through).</summary>
    public static string ThreadState(SurgicalTool tool)
    {
        var layer = (int)(tool.Suture.Layer != TissueDepth.None ? tool.Suture.Layer : TissueDepth.Skin);
        var closed = TissueSim.ThreadClosed[layer];
        if (tool.Suture.Tension > closed)
        {
            return "loose";
        }
        return tool.Suture.Tension < (closed + TissueSim.ThreadTear[layer]) * 0.5f ? "too tight" : "closed";
    }
}
