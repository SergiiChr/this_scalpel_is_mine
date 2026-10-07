using GodotDictionary = Godot.Collections.Dictionary;

namespace Scalpel.Patients;

/// <summary>The heart's rhythm: beating, fibrillating or flat.</summary>
public enum Rhythm { Sinus, Vfib, Asystole }

/// <summary>Live patient numbers. Loosely realistic, tuned for play. Host simulates, clients receive a copy.</summary>
public sealed class Vitals
{
    public const float NormalBloodMl = 5000f;

    public float HeartRate = 75f;
    public float Systolic = 120f;
    public float Spo2 = 98f;
    public float Temperature = 36.8f;
    public float Glucose = 5.5f;
    public float BloodMl = NormalBloodMl;
    public float MaxBloodMl = NormalBloodMl;
    public Rhythm Rhythm = Rhythm.Sinus;
    public float Consciousness = 1f;
    public float Anesthesia;
    public float LocalBlock;
    public float Pain;
    public float Panic;
    /// <summary>Total ml/s across every wound, for the HUD and the Hemophobia quirk.</summary>
    public float BleedRate;
    public float Swelling;
    public bool Seizing;

    public bool IsAwake => Consciousness > 0.45f;

    public float BloodRatio => BloodMl / MaxBloodMl;

    public bool IsArrested => Rhythm != Rhythm.Sinus;

    public string RhythmName => Rhythm switch
    {
        Rhythm.Vfib => "V-FIB",
        Rhythm.Asystole => "ASYSTOLE",
        _ => "Sinus",
    };

    /// <summary>Everything clients need, for the host's sync.</summary>
    public GodotDictionary ToVariant() => new()
    {
        ["heart_rate"] = HeartRate, ["systolic"] = Systolic, ["spo2"] = Spo2, ["temperature"] = Temperature,
        ["glucose"] = Glucose, ["blood_ml"] = BloodMl, ["max_blood_ml"] = MaxBloodMl, ["rhythm"] = (int)Rhythm,
        ["consciousness"] = Consciousness, ["anesthesia"] = Anesthesia, ["local_block"] = LocalBlock, ["pain"] = Pain,
        ["panic"] = Panic, ["bleed_rate"] = BleedRate, ["swelling"] = Swelling, ["seizing"] = Seizing,
    };

    /// <summary>Takes the given numbers (a host sync, or a scenario's start_vitals), leaving the rest.</summary>
    public void Apply(GodotDictionary data)
    {
        HeartRate = data.Float("heart_rate", HeartRate);
        Systolic = data.Float("systolic", Systolic);
        Spo2 = data.Float("spo2", Spo2);
        Temperature = data.Float("temperature", Temperature);
        Glucose = data.Float("glucose", Glucose);
        BloodMl = data.Float("blood_ml", BloodMl);
        MaxBloodMl = data.Float("max_blood_ml", MaxBloodMl);
        Rhythm = (Rhythm)data.Int("rhythm", (int)Rhythm);
        Consciousness = data.Float("consciousness", Consciousness);
        Anesthesia = data.Float("anesthesia", Anesthesia);
        LocalBlock = data.Float("local_block", LocalBlock);
        Pain = data.Float("pain", Pain);
        Panic = data.Float("panic", Panic);
        BleedRate = data.Float("bleed_rate", BleedRate);
        Swelling = data.Float("swelling", Swelling);
        Seizing = data.Bool("seizing", Seizing);
    }
}
