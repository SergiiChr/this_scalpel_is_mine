namespace Scalpel.Tools;

/// <summary>The defibrillator: held on the chest it charges, let go fully charged it shocks the heart (and anyone else
/// touching the patient).</summary>
public sealed class ShockAction : ToolAction
{
    public override void Apply(ToolStep step)
    {
        var use = step.Tool.Use;
        var patient = step.Patient;
        var onChest = (step.Zone == SiteZone.Site && patient.Scenario.Site is "chest" or "abdomen") || step.Probe.Part == "torso";
        if (step.Trigger && step.Lowered && onChest)
        {
            if (use.ChargeTime == 0f)
            {
                step.Surgery.Sound("defib_charge", step.Tip);
            }
            use.ChargeTime += step.Dt;
        }
        else if (step.Released && use.ChargeTime >= ToolActions.DefibChargeTime * step.Hand.Mods.Mult("defib_charge_mult"))
        {
            patient.Shock(step.Def.Power * 0.5f);
            step.Surgery.Sound("defib_shock", step.Tip);
            step.Surgery.Effect(ToolEffect.Spark, step.Tip, 0);
            if (step.Zone == SiteZone.Site)
            {
                // Paddle contact leaves a faint red mark on the skin.
                patient.Paint(WoundMap.Layer.Wounds, WoundMap.Burn, step.Uv, step.Uv, 0.06f, 0.1f, WoundMap.Mode.Max);
            }
            step.Surgery.ShockBystanders(step.Hand.Peer);
            use.ChargeTime = 0f;
        }
        else if (!step.Trigger)
        {
            use.ChargeTime = 0f;
        }
    }
}
