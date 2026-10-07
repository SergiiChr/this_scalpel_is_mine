namespace Scalpel.Data;

/// <summary>
/// Drug effect strengths. A drug's peak effects (<see cref="DrugDef.Peak"/>, data/drugs.cfg keys in snake case), and
/// the sum of every working drug for one simulation tick. <see cref="Glucose"/> and <see cref="VolumeMl"/> are totals
/// in a definition and per-second rates in a tick's sum; everything else is the current strength.
/// </summary>
public sealed class DrugEffects
{
    public float Hr;
    public float Bp;
    public float Glucose;
    public float VolumeMl;
    public float Anesthesia;
    public float LocalBlock;
    public float Sedation;
    public float PainRelief;
    public float Clot;
    public float Spo2;
    public float Temp;
    public float Antihistamine;
    public float Adrenaline;
    public float Lethal;

    /// <summary>Reads the effect keys of a drugs.cfg section.</summary>
    public static DrugEffects FromConfig(ConfigReader config) => new()
    {
        Hr = config.Float("hr"),
        Bp = config.Float("bp"),
        Glucose = config.Float("glucose"),
        VolumeMl = config.Float("volume_ml"),
        Anesthesia = config.Float("anesthesia"),
        LocalBlock = config.Float("local_block"),
        Sedation = config.Float("sedation"),
        PainRelief = config.Float("pain_relief"),
        Clot = config.Float("clot"),
        Spo2 = config.Float("spo2"),
        Temp = config.Float("temp"),
    };

    /// <summary>Adds the strengths of <paramref name="peak"/> (not its totals, glucose and volume) times
    /// <paramref name="strength"/>.</summary>
    public void AddStrengths(DrugEffects peak, float strength)
    {
        Hr += peak.Hr * strength;
        Bp += peak.Bp * strength;
        Anesthesia += peak.Anesthesia * strength;
        LocalBlock += peak.LocalBlock * strength;
        Sedation += peak.Sedation * strength;
        PainRelief += peak.PainRelief * strength;
        Clot += peak.Clot * strength;
        Spo2 += peak.Spo2 * strength;
        Temp += peak.Temp * strength;
    }
}
