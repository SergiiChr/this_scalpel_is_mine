namespace Scalpel.Surgeons;

/// <summary>Something that happened to a surgeon's body this frame, for <see cref="Surgeon"/> to act on.</summary>
public enum StatusEvent { PassOut, Vomit, Cough, Slip, Drip, Gasp, KnockedOut, CameRound, Moan }

/// <summary>What around a surgeon affects them: how fast the patient bleeds, and a partner's bad breath nearby.
/// </summary>
public readonly record struct StatusContext(float BleedRate, float PartnerBreath);

/// <summary>
/// Personal gauges of the local surgeon: stress, sickness, breath, sweat, drink and smoke buffs, drugs given to them.
/// Runs only on the surgeon's own peer. <see cref="Update"/> returns what happened for Surgeon to act on.
/// </summary>
public sealed class SurgeonStatus(Modifiers mods)
{
    public const float BreathHoldTime = 8f;
    public const float BreathRefillTime = 12f;
    public const float PassOutTime = 6f;
    /// <summary>A smoke break: no stress gain for this long, hands and feet faster by SmokeSpeed.</summary>
    public const float SmokeTime = 180f;
    public const float SmokeSpeed = 1.2f;
    /// <summary>Stress shakes the hands in steps. Up to ShakeVisual only the glove twitches now and then (see
    /// <see cref="Shiver"/>), the tool stays put. Up to ShakeLight a light shake reaches the tool, above it a plain one
    /// that grows with stress. Quirks only change how fast stress builds and how low it can drain (stress_floor).
    /// </summary>
    public const float ShakeVisual = 0.3f;
    public const float ShakeLight = 0.6f;
    public const float LightTremor = 0.002f;
    public const float PlainTremor = 0.005f;
    public const float MaxTremor = 0.009f;
    /// <summary>The calm glove's twitch: how far (m) at ShakeVisual.</summary>
    public const float ShiverReach = 0.0015f;
    /// <summary>How long one twitch lasts (s).</summary>
    public const float ShiverTime = 0.8f;
    /// <summary>The wait between twitches (s, random in between).</summary>
    public static readonly Vector2 ShiverGap = new(4f, 10f);
    /// <summary>A coffee keeps stress this much higher while it works.</summary>
    public const float CoffeeStress = 0.15f;
    /// <summary>Stacked stress floors stop here, short of passing out.</summary>
    public const float MaxFloor = 0.9f;
    /// <summary>A surgeon's weight without quirks (kg). Doses given to a surgeon are worked out from it, like a
    /// patient's.</summary>
    public const float BaseWeight = 80f;
    /// <summary>A sedative (flag "benzo") at the right dose steadies the shaking of stress, blurs the view and delays
    /// hand moves by SedatedDelay. From DoseHigh times the dose on the view darkens and the delay grows, by up to
    /// OverdoseDelay at KnockoutShare. There the surgeon is knocked out for KnockoutTime.</summary>
    public const float SedatedDelay = 0.1f;
    public const float OverdoseDelay = 0.2f;
    public const float KnockoutShare = 2f;
    public const float KnockoutTime = 300f;
    /// <summary>Knocked out, a moan every so often (s, random in between).</summary>
    public static readonly Vector2 MoanGap = new(8f, 20f);

    private float _dripTimer = 20f;
    private float _coughCooldown;
    /// <summary>Set once a sedative knocks the surgeon out, so the same dose doesn't do it again.</summary>
    private bool _knocked;
    private float _shiverWait = 3f;
    private float _shiverLeft;
    private float _moanWait = 5f;

    public Modifiers Mods { get; } = mods;
    public float Stress { get; set; }
    public float Sickness { get; set; }
    public float Breath { get; set; } = 1f;
    public float Sweat { get; set; }
    public bool HoldingBreath { get; set; }
    public bool CapOn { get; private set; }
    public float PassedOut { get; private set; }
    public float CoffeeLeft { get; private set; }
    public float WhiskeyLeft { get; private set; }
    public float SmokeLeft { get; private set; }
    public float SinceCoffee { get; private set; }
    /// <summary>Broken heating run modifier: stiff, slightly shaky fingers for everyone.</summary>
    public float ColdTremor { get; set; }
    public float WeightKg { get; } = WeightOf(mods);
    /// <summary>Drugs given to this surgeon, every injection adding up (shares of the right dose for WeightKg).</summary>
    public DrugLevels Drugs { get; } = new();
    /// <summary>How calm a sedative makes the surgeon now (0..1, 1 from the right dose).</summary>
    public float Calm { get; private set; }
    /// <summary>How far past the right dose a sedative is (0 up to DoseHigh, 1 at KnockoutShare).</summary>
    public float Overdose { get; private set; }
    /// <summary>Seconds left knocked out by a sedative.</summary>
    public float KnockedOut { get; private set; }
    /// <summary>Seconds left of being kept up by a stimulant while knocked out.</summary>
    public float KeptUp { get; private set; }

    /// <summary>A surgeon's weight (kg) with these quirks.</summary>
    public static float WeightOf(Modifiers modifiers) => BaseWeight + modifiers.Num("weight_kg");

    public bool IsOut => PassedOut > 0f || IsKnockedOut;

    public bool IsKnockedOut => KnockedOut > 0f && KeptUp <= 0f;

    public void AddStress(float amount)
    {
        if (SmokeLeft <= 0f)
        {
            Stress = Mathf.Clamp(Stress + amount * Mods.Mult("stress_mult"), 0f, 1f);
        }
    }

    public void AddSickness(float amount)
    {
        if (!Mods.Flag("sickness_immune"))
        {
            Sickness = Mathf.Clamp(Sickness + amount, 0f, 1f);
        }
    }

    /// <summary>How low stress drains: quirks set it, coffee lifts it, a steadying drink takes it away.</summary>
    public float StressFloor()
    {
        if (WhiskeyLeft > 0f && Mods.Flag("drink_steady"))
        {
            return 0f;
        }
        return Mathf.Min(Mods.Num("stress_floor") + (CoffeeLeft > 0f ? CoffeeStress : 0f), MaxFloor);
    }

    /// <summary>Shake that moves the tool (m): stress, which a sedative steadies, and cold, which it doesn't.</summary>
    public float TremorAmount()
    {
        var shake = 0f;
        if (Stress > ShakeLight)
        {
            shake = Mathf.Lerp(PlainTremor, MaxTremor, (Stress - ShakeLight) / (1f - ShakeLight));
        }
        else if (Stress > ShakeVisual)
        {
            shake = LightTremor;
        }
        return Steadied(shake * (1f - Calm) + ColdTremor);
    }

    /// <summary>The glove's twitch while stress is low (m): now and then, and the glove only.</summary>
    public float Shiver()
    {
        if (_shiverLeft <= 0f || Stress > ShakeVisual)
        {
            return 0f;
        }
        return Steadied(ShiverReach * Stress / ShakeVisual * (1f - Calm));
    }

    private float Steadied(float amount)
    {
        if (HoldingBreath && Breath > 0f)
        {
            amount *= 0.1f;
        }
        return amount * Mods.Mult("tremor_mult");
    }

    /// <summary>How long a sedative holds back what the mouse does to a hand (s).</summary>
    public float InputDelay() => SedatedDelay * Calm + OverdoseDelay * Overdose;

    public float HandSpeed()
    {
        var speed = (CoffeeLeft > 0f ? 1.3f : 1f) * SmokeSpeedNow();
        if (Mods.Flag("caffeine") && CoffeeLeft <= 0f)
        {
            speed *= 1f - Mathf.Min(SinceCoffee / 300f, 0.4f);
        }
        return speed;
    }

    public float MoveSpeed() => Mods.Mult("move_speed_mult") * SmokeSpeedNow();

    private float SmokeSpeedNow() => SmokeLeft > 0f ? SmokeSpeed : 1f;

    public void Smoke() => SmokeLeft = SmokeTime;

    /// <summary>A dose (in the drug's unit) injected into this surgeon. Only sedatives and what wakes from them do
    /// anything.</summary>
    public void Administer(string drugId, float amount)
    {
        if (Db.Drug(drugId) is { Dose: > 0f } def)
        {
            Drugs.Give(def, amount / (def.Dose * WeightKg), def.Onset * DrugDef.DirectOnset);
        }
    }

    public void Drink(string toolId)
    {
        switch (toolId)
        {
            case "coffee_thermos":
                CoffeeLeft = 60f;
                SinceCoffee = 0f;
                break;
            case "whiskey_flask":
                WhiskeyLeft = 45f;
                Stress = Mathf.Max(Stress - 0.3f, 0f);
                break;
            case "surgical_cap":
                CapOn = true;
                break;
        }
    }

    /// <summary>Moves the gauges on by <paramref name="delta"/> seconds. Returns what happened.</summary>
    public List<StatusEvent> Update(float delta, StatusContext context)
    {
        var events = new List<StatusEvent>();
        UpdateDrugs(delta, events);
        if (IsKnockedOut)
        {
            _moanWait -= delta;
            if (_moanWait <= 0f)
            {
                _moanWait = (float)GD.RandRange(MoanGap.X, MoanGap.Y);
                events.Add(StatusEvent.Moan);
            }
            return events;
        }
        if (PassedOut > 0f)
        {
            PassedOut -= delta;
            return events;
        }
        Stress = Mathf.Max(Stress - delta * 0.01f, StressFloor());
        CoffeeLeft = Mathf.Max(CoffeeLeft - delta, 0f);
        WhiskeyLeft = Mathf.Max(WhiskeyLeft - delta, 0f);
        SmokeLeft = Mathf.Max(SmokeLeft - delta, 0f);
        SinceCoffee += delta;
        _shiverLeft -= delta;
        _shiverWait -= delta;
        if (_shiverWait <= 0f)
        {
            _shiverWait = (float)GD.RandRange(ShiverGap.X, ShiverGap.Y);
            _shiverLeft = ShiverTime;
        }
        if (HoldingBreath)
        {
            Breath -= delta / BreathHoldTime;
            if (Breath <= 0f)
            {
                Breath = 0f;
                HoldingBreath = false;
                events.Add(StatusEvent.Gasp);
            }
        }
        else
        {
            Breath = Mathf.Min(Breath + delta / BreathRefillTime, 1f);
        }
        AddSickness(context.PartnerBreath * delta);
        AddSickness(Mods.Num("blood_sickness_rate") * Mathf.Clamp(context.BleedRate / 2f, 0f, 1f) * delta);
        Sickness = Mathf.Max(Sickness - delta * 0.005f, 0f);
        Sweat = Mathf.Min(Sweat + Mods.Num("sweat_rate") * delta * (1f + Stress), 1f);
        _coughCooldown -= delta;
        if (_coughCooldown <= 0f && GD.Randf() < Mods.Num("cough_chance") * delta)
        {
            _coughCooldown = 20f;
            events.Add(StatusEvent.Cough);
        }
        if (Sweat > 0.85f && GD.Randf() < 0.02f * delta * 10f)
        {
            events.Add(StatusEvent.Slip);
        }
        if (Sweat > 0.5f && !CapOn)
        {
            _dripTimer -= delta;
            if (_dripTimer <= 0f)
            {
                _dripTimer = 25f;
                events.Add(StatusEvent.Drip);
            }
        }
        if (Stress >= 1f)
        {
            PassedOut = PassOutTime;
            Stress = 0.5f;
            events.Add(StatusEvent.PassOut);
        }
        if (Sickness >= 1f)
        {
            Sickness = 0.3f;
            events.Add(StatusEvent.Vomit);
        }
        return events;
    }

    /// <summary>Sedatives add up into calm and overdose, and knock the surgeon out at KnockoutShare. Flumazenil
    /// (reverse_benzo) takes them all away; a stimulant only keeps a knocked out surgeon up while it lasts.</summary>
    private void UpdateDrugs(float delta, List<StatusEvent> events)
    {
        foreach (var (def, crossing) in Drugs.Update(delta, (_, _) => 1f))
        {
            if (crossing != DrugLevels.Crossing.Works)
            {
                continue;
            }
            if (def.HasFlag("reverse_benzo"))
            {
                Drugs.Remove(drug => drug.HasFlag("benzo"));
                if (KnockedOut > 0f)
                {
                    KnockedOut = 0f;
                    KeptUp = 0f;
                    events.Add(StatusEvent.CameRound);
                }
            }
            else if (def.HasFlag("stimulant") && KnockedOut > 0f)
            {
                KeptUp = def.Duration;
                events.Add(StatusEvent.CameRound);
            }
        }
        var sedative = Drugs.Entries.Where(entry => entry.Def.HasFlag("benzo")).Sum(entry => entry.Level);
        Calm = Mathf.Min(DrugDef.DoseStrength(sedative), 1f);
        Overdose = Mathf.Clamp((sedative - DrugDef.DoseHigh) / (KnockoutShare - DrugDef.DoseHigh), 0f, 1f);
        if (sedative >= KnockoutShare && !_knocked)
        {
            _knocked = true;
            KnockedOut = KnockoutTime;
            PassedOut = 0f;
            events.Add(StatusEvent.KnockedOut);
        }
        else if (sedative < DrugDef.DoseHigh)
        {
            _knocked = false;
        }
        if (KnockedOut <= 0f)
        {
            KeptUp = 0f;
            return;
        }
        KnockedOut = Mathf.Max(KnockedOut - delta, 0f);
        if (KeptUp > 0f)
        {
            KeptUp = Mathf.Max(KeptUp - delta, 0f);
            if (KeptUp == 0f)
            {
                // The stimulant wore off: still too much sedative in the blood and they go down again.
                if (sedative >= KnockoutShare && KnockedOut > 0f)
                {
                    events.Add(StatusEvent.KnockedOut);
                }
                else
                {
                    KnockedOut = 0f;
                }
            }
        }
        else if (KnockedOut == 0f)
        {
            events.Add(StatusEvent.CameRound);
        }
    }
}
