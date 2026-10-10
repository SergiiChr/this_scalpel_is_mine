namespace Scalpel.Operation;

/// <summary>
/// One check per objective step type. Add a new type by adding a case. Step parameters come from the scenario file,
/// the step's state persists between ticks.
/// </summary>
public static class ObjectiveChecks
{
    /// <summary>The step's own check, or else its alternative's (which shares the step's state).</summary>
    public static bool Check(ObjectiveStep step, StepState state, Surgery surgery, float delta) =>
        Met(step, state, surgery, delta)
        || (step.Alternative is { } alternative && Check(alternative, state, surgery, delta));

    private static bool Met(ObjectiveStep step, StepState state, Surgery surgery, float delta)
    {
        var patient = surgery.Patient;
        var vitals = patient.Vitals;
        var parameters = step.Parameters;
        switch (step.Type)
        {
            case "sanitize":
                return patient.SanitizedFraction() >= parameters.Float("amount", 0.5f);
            case "iv":
                return patient.IvWorking;
            case "anesthesia":
                return vitals.Anesthesia >= parameters.Float("level", 0.7f);
            case "local_block":
                return vitals.LocalBlock >= parameters.Float("level", 0.5f);
            case "mark":
                return patient.Body.UvToMeters(patient.MarkedUv) >= parameters.Float("length", 0.1f);
            case "incise":
                return patient.SurgeonCutLengthM(0.7f) >= parameters.Float("length", 0.1f);
            case "extract":
                var kind = parameters.String("target");
                return patient.Targets.Where(t => t.Kind == kind).All(t => t.Extracted);
            case "close":
                return patient.SkinClosure() >= Patient.ClosedEnough;
            case "close_internal":
                return patient.InternalClosed();
            case "stop_bleeding":
                // Gauze only holds it for a while: it counts once the bleeding stays stopped without it.
                return Held(state, patient.LastingBleedRate <= parameters.Float("max_ml_s", 0.3f), 3f, delta);
            case "stabilize":
                var stable = !vitals.IsArrested && vitals.Spo2 >= 94f && vitals.Systolic >= 90f;
                return Held(state, stable, parameters.Float("seconds", 30f), delta);
            case "calm":
                return Held(state, vitals.Panic < parameters.Float("max_panic", 0.6f), 60f, delta);
            case "inject":
                return patient.Flags.ContainsKey(parameters.ContainsKey("drug") ? "drug_" + parameters.String("drug") : parameters.String("flag"));
            case "defib":
                return patient.Flags.ContainsKey("revived") && !vitals.IsArrested;
            case "tourniquet":
                return patient.TourniquetOn;
            case "clamp":
                return patient.AnyClamped();
            case "transfuse":
                return patient.TransfusedMl > 0f
                    && vitals.BloodMl >= parameters.Float("min_ml", 4000f) * vitals.MaxBloodMl / Vitals.NormalBloodMl;
            case "flip":
                return (int)patient.Body.Pose == parameters.Int("orientation", (int)PatientPose.FaceDown);
            case "align":
                return Held(state, Aligned(surgery), parameters.Float("seconds", 6f), delta);
            case "debride":
                return patient.GridFraction(Patient.BurnGrid.Debrided) >= parameters.Float("amount", 0.5f);
            case "graft":
                return patient.GridFraction(Patient.BurnGrid.Grafted) >= parameters.Float("amount", 0.7f);
            case "listen":
                return Held(state, patient.Flags.ContainsKey("euthanized"), parameters.Float("seconds", 90f), delta);
            case "comfort":
                return patient.Flags.GetValueOrDefault("comfort_time") >= parameters.Float("seconds", 20f);
            case "wait":
                return Held(state, true, parameters.Float("seconds", 30f), delta);
            default:
                GD.PushWarning($"Unknown objective type '{step.Type}'");
                return false;
        }
    }

    /// <summary>Every broken bone end is back in line, held by as many surgeons as there are (up to two).</summary>
    private static bool Aligned(Surgery surgery)
    {
        var holders = new HashSet<int>();
        var aligned = true;
        foreach (var target in surgery.Patient.Targets.Where(t => t.IsFragment))
        {
            aligned = aligned && target.IsAligned(0.012f);
            if (surgery.Tools.ByUid(target.GrippedBy) is { } tool)
            {
                holders.Add(tool.Holder);
            }
        }
        return aligned && holders.Count >= Math.Min(2, surgery.Surgeons.Count);
    }

    /// <summary>True once <paramref name="condition"/> has held continuously for <paramref name="seconds"/>.</summary>
    private static bool Held(StepState state, bool condition, float seconds, float delta)
    {
        state.Timer = condition ? state.Timer + delta : 0f;
        return state.Timer >= seconds;
    }
}
