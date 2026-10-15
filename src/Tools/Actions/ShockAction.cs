namespace Scalpel.Tools;

/// <summary>The defibrillator: held on the chest it charges, let go fully charged it shocks the heart (and anyone else
/// touching the patient). On the chest of a surgeon whose heart has stopped, it may restart theirs.</summary>
public sealed class ShockAction : ToolAction {
    public override void Apply(ToolStep step) {
        var use = step.Tool.Use;
        var patient = step.Patient;
        var surgeon = step.Surgery.ArrestedAt(step.Tip, step.Hand.Peer);
        var onChest = surgeon is not null || (step.Zone == SiteZone.Site && patient.Scenario.Site is "chest" or "abdomen")
            || step.Probe.Part == "torso";
        if (step.Trigger && step.Lowered && onChest) {
            if (use.ChargeTime == 0f) {
                step.Surgery.Sound("defib_charge", step.Tip);
            }
            use.ChargeTime += step.Dt;
        } else if (step.Released && use.ChargeTime >= ToolActions.DefibChargeTime * step.Hand.Mods.Mult("defib_charge_mult")) {
            step.Surgery.Sound("defib_shock", step.Tip);
            step.Surgery.Effect(ToolEffect.Spark, step.Tip, 0);
            use.ChargeTime = 0f;
            if (surgeon is not null) {
                step.Surgery.Defibrillate(surgeon);
                return;
            }
            patient.Shock(step.Def.Power * 0.5f);
            if (step.Zone == SiteZone.Site) {
                // Paddle contact leaves a faint red mark on the skin.
                patient.Paint(WoundMap.Layer.Wounds, WoundMap.Burn, step.Uv, step.Uv, 0.06f, 0.1f, WoundMap.Mode.Max);
            }
            step.Surgery.ShockBystanders(step.Hand.Peer);
        } else if (!step.Trigger) {
            use.ChargeTime = 0f;
        }
    }
}
