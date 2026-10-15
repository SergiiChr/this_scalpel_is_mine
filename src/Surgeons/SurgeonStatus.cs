namespace Scalpel.Surgeons;

/// <summary>Something that happened to a surgeon's body this frame, for <see cref="Surgeon"/> to act on.</summary>
public enum StatusEvent {
    PassOut, Vomit, Cough, Slip, Drip, Gasp, KnockedOut, CameRound, Moan, Heartbeat, Racing, HeartAttack, Jitters,
    NauseaGone,
}

/// <summary>What around a surgeon affects them: how fast the patient bleeds, and a partner's bad breath nearby.
/// </summary>
public readonly record struct StatusContext(float BleedRate, float PartnerBreath);

/// <summary>
/// Personal gauges of the local surgeon: stress, sickness, breath, sweat, drink and smoke buffs, drugs given to them.
/// Runs only on the surgeon's own peer. <see cref="Update"/> returns what happened for Surgeon to act on.
/// </summary>
public sealed class SurgeonStatus(Modifiers mods) {
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
    /// <summary>A sedative (<see cref="DrugDef.IsSedative"/>) at the right dose steadies the shaking of stress, blurs
    /// the view and delays hand moves by SedatedDelay.
    /// Every sedative's share of its own right dose adds up.
    /// From DoseHigh times the dose on the view darkens and the delay grows, by up to OverdoseDelay at KnockoutShare.
    /// There the surgeon is knocked out for KnockoutTime.</summary>
    public const float SedatedDelay = 0.1f;
    public const float OverdoseDelay = 0.2f;
    public const float KnockoutShare = 2f;
    public const float KnockoutTime = 300f;
    /// <summary>Knocked out, a moan every so often (s, random in between).</summary>
    public static readonly Vector2 MoanGap = new(8f, 20f);
    /// <summary>An opioid (morphine) doesn't slow the hands: from DoseEffective times the dose the surgeon nods off
    /// instead.
    /// Every NodGapMin to NodGapMax seconds the eyelids close over LidClose seconds, stay shut NodDarkMin to NodDarkMax
    /// seconds and open over LidOpen.</summary>
    public const float NodGapMin = 10f;
    public const float NodGapMax = 30f;
    public const float NodDarkMin = 1f;
    public const float NodDarkMax = 2f;
    public const float LidClose = 0.3f;
    public const float LidOpen = 0.4f;
    /// <summary>A stimulant (adrenaline, cocaine) at work: hands faster by StimulantSpeed, stress builds faster by
    /// StimulantStress, and the heart thumps at StimulatedBpm.</summary>
    public const float StimulantSpeed = 1.3f;
    public const float StimulantStress = 1.5f;
    public const float StimulatedBpm = 110f;
    /// <summary>Two drugs dangerous together (adrenaline and cocaine) at work at once: hands and feet faster by
    /// RaceSpeed for RaceTime seconds, the heart speeding up from RaceBpm.X to RaceBpm.Y, then a heart attack kills the
    /// surgeon.
    /// Nothing stops it.</summary>
    public const float RaceTime = 180f;
    public const float RaceSpeed = 1.6f;
    public static readonly Vector2 RaceBpm = new(140f, 200f);
    /// <summary>A local anaesthetic (local_block) injected into a hand numbs it for NumbTime seconds: it can't hold
    /// anything.</summary>
    public const float NumbTime = 60f;
    /// <summary>An antidote with nothing of its kind to reverse jolts the surgeon by this much stress.</summary>
    public const float AntidoteStress = 0.4f;

    private float _dripTimer = 20f;
    private float _coughCooldown;
    /// <summary>Set once a sedative knocks the surgeon out, so the same dose doesn't do it again.</summary>
    private bool _knocked;
    private float _shiverWait = 3f;
    private float _shiverLeft;
    private float _moanWait = 5f;
    private float _beatWait;
    private bool _nodding;
    private float _nodWait = NodGapMin;
    private float _nodLeft;
    private float _nodLength;
    private readonly float[] _numbLeft = new float[2];

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
    public float SmokeLeft { get; internal set; }
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
    public float KnockedOut { get; internal set; }
    /// <summary>Seconds left of being kept up by a stimulant while knocked out.</summary>
    public float KeptUp { get; private set; }
    /// <summary>The share of Calm that slows the hands: an opioid's part makes the surgeon nod off instead.</summary>
    public float Lag { get; private set; } = 1f;
    /// <summary>How far the eyelids are shut while nodding off (0 open, 1 shut).</summary>
    public float Eyelids { get; private set; }
    /// <summary>A stimulant is at work.</summary>
    public bool Stimulated { get; private set; }
    /// <summary>Seconds left before the racing heart gives out, 0 when it isn't racing.</summary>
    public float RaceLeft { get; private set; }
    /// <summary>Died of a heart attack: down for good.</summary>
    public bool Dead { get; private set; }

    /// <summary>A surgeon's weight (kg) with these quirks.</summary>
    public static float WeightOf(Modifiers modifiers) => BaseWeight + modifiers.Num("weight_kg");

    public bool IsOut => PassedOut > 0f || IsKnockedOut;

    public bool IsKnockedOut => Dead || (KnockedOut > 0f && KeptUp <= 0f);

    public bool IsRacing => RaceLeft > 0f;

    /// <summary>The hand (0 left, 1 right) is numb and can't hold anything.</summary>
    public bool IsNumb(int hand) => _numbLeft[hand] > 0f;

    public void AddStress(float amount) {
        if (SmokeLeft <= 0f) {
            var stimulant = Stimulated ? StimulantStress : 1f;
            Stress = Mathf.Clamp(Stress + amount * Mods.Mult("stress_mult") * stimulant, 0f, 1f);
        }
    }

    public void AddSickness(float amount) {
        // An antihistamine keeps nausea away while it works.
        if (!Mods.Flag("sickness_immune") && !Drugs.Working("antihistamine")) {
            Sickness = Mathf.Clamp(Sickness + amount, 0f, 1f);
        }
    }

    /// <summary>How low stress drains: quirks set it, coffee lifts it, a steadying drink takes it away.</summary>
    public float StressFloor() {
        if (WhiskeyLeft > 0f && Mods.Flag("drink_steady")) {
            return 0f;
        }
        return Mathf.Min(Mods.Num("stress_floor") + (CoffeeLeft > 0f ? CoffeeStress : 0f), MaxFloor);
    }

    /// <summary>Shake that moves the tool (m): stress, which a sedative steadies, and cold, which it doesn't.</summary>
    public float TremorAmount() {
        var shake = 0f;
        if (Stress > ShakeLight) {
            shake = Mathf.Lerp(PlainTremor, MaxTremor, (Stress - ShakeLight) / (1f - ShakeLight));
        } else if (Stress > ShakeVisual) {
            shake = LightTremor;
        }
        return Steadied(shake * (1f - Calm) + ColdTremor);
    }

    /// <summary>The glove's twitch while stress is low (m): now and then, and the glove only.</summary>
    public float Shiver() {
        if (_shiverLeft <= 0f || Stress > ShakeVisual) {
            return 0f;
        }
        return Steadied(ShiverReach * Stress / ShakeVisual * (1f - Calm));
    }

    private float Steadied(float amount) {
        if (HoldingBreath && Breath > 0f) {
            amount *= 0.1f;
        }
        return amount * Mods.Mult("tremor_mult");
    }

    /// <summary>How long a sedative holds back what the mouse does to a hand (s).</summary>
    public float InputDelay() => (SedatedDelay * Calm + OverdoseDelay * Overdose) * Lag;

    public float HandSpeed() {
        var stimulant = IsRacing ? RaceSpeed : Stimulated ? StimulantSpeed : 1f;
        var speed = (CoffeeLeft > 0f ? 1.3f : 1f) * SmokeSpeedNow() * stimulant;
        if (Mods.Flag("caffeine") && CoffeeLeft <= 0f) {
            speed *= 1f - Mathf.Min(SinceCoffee / 300f, 0.4f);
        }
        return speed;
    }

    public float MoveSpeed() => Mods.Mult("move_speed_mult") * SmokeSpeedNow() * (IsRacing ? RaceSpeed : 1f);

    /// <summary>The heart's pace while a stimulant works (beats a minute), 0 otherwise.</summary>
    public float HeartRate() => IsRacing ? Mathf.Lerp(RaceBpm.X, RaceBpm.Y, 1f - (RaceLeft / RaceTime))
        : Stimulated ? StimulatedBpm : 0f;

    private float SmokeSpeedNow() => SmokeLeft > 0f ? SmokeSpeed : 1f;

    public void Smoke() => SmokeLeft = SmokeTime;

    /// <summary>A dose (in the drug's unit) injected into this surgeon, into <paramref name="hand"/> (-1 the body).
    /// A local anaesthetic numbs the hand it goes into.</summary>
    public void Administer(string drugId, float amount, int hand = -1) {
        if (Db.Drug(drugId) is not { Dose: > 0f } def) {
            return;
        }
        Drugs.Give(def, amount / (def.Dose * WeightKg), def.Onset * DrugDef.DirectOnset);
        if (hand >= 0 && def.Peak.LocalBlock > 0f) {
            _numbLeft[hand] = NumbTime;
        }
    }

    /// <summary>A drug taken without a needle (snorted cocaine): the right dose, soaking in like an injection.
    /// </summary>
    public void Take(string drugId) {
        if (Db.Drug(drugId) is { } def) {
            Drugs.Give(def, 1f, def.Onset * DrugDef.DirectOnset);
        }
    }

    public void Drink(string toolId) {
        switch (toolId) {
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
    public List<StatusEvent> Update(float delta, StatusContext context) {
        var events = new List<StatusEvent>();
        if (Dead) {
            return events;
        }
        UpdateDrugs(delta, events);
        UpdateHeart(delta, events);
        if (Dead) {
            return events;
        }
        for (var hand = 0; hand < 2; hand++) {
            _numbLeft[hand] = Mathf.Max(_numbLeft[hand] - delta, 0f);
        }
        if (IsKnockedOut) {
            Eyelids = 0f;
            _nodLeft = 0f;
            _moanWait -= delta;
            if (_moanWait <= 0f) {
                _moanWait = (float)GD.RandRange(MoanGap.X, MoanGap.Y);
                events.Add(StatusEvent.Moan);
            }
            return events;
        }
        if (PassedOut > 0f) {
            PassedOut -= delta;
            return events;
        }
        UpdateNods(delta);
        Stress = Mathf.Max(Stress - delta * 0.01f, StressFloor());
        CoffeeLeft = Mathf.Max(CoffeeLeft - delta, 0f);
        WhiskeyLeft = Mathf.Max(WhiskeyLeft - delta, 0f);
        SmokeLeft = Mathf.Max(SmokeLeft - delta, 0f);
        SinceCoffee += delta;
        _shiverLeft -= delta;
        _shiverWait -= delta;
        if (_shiverWait <= 0f) {
            _shiverWait = (float)GD.RandRange(ShiverGap.X, ShiverGap.Y);
            _shiverLeft = ShiverTime;
        }
        if (HoldingBreath) {
            Breath -= delta / BreathHoldTime;
            if (Breath <= 0f) {
                Breath = 0f;
                HoldingBreath = false;
                events.Add(StatusEvent.Gasp);
            }
        } else {
            Breath = Mathf.Min(Breath + delta / BreathRefillTime, 1f);
        }
        AddSickness(context.PartnerBreath * delta);
        AddSickness(Mods.Num("blood_sickness_rate") * Mathf.Clamp(context.BleedRate / 2f, 0f, 1f) * delta);
        Sickness = Mathf.Max(Sickness - delta * 0.005f, 0f);
        Sweat = Mathf.Min(Sweat + Mods.Num("sweat_rate") * delta * (1f + Stress), 1f);
        _coughCooldown -= delta;
        if (_coughCooldown <= 0f && GD.Randf() < Mods.Num("cough_chance") * delta) {
            _coughCooldown = 20f;
            events.Add(StatusEvent.Cough);
        }
        if (Sweat > 0.85f && GD.Randf() < 0.02f * delta * 10f) {
            events.Add(StatusEvent.Slip);
        }
        if (Sweat > 0.5f && !CapOn) {
            _dripTimer -= delta;
            if (_dripTimer <= 0f) {
                _dripTimer = 25f;
                events.Add(StatusEvent.Drip);
            }
        }
        if (Stress >= 1f) {
            PassedOut = PassOutTime;
            Stress = 0.5f;
            events.Add(StatusEvent.PassOut);
        }
        if (Sickness >= 1f) {
            Sickness = 0.3f;
            events.Add(StatusEvent.Vomit);
        }
        return events;
    }

    /// <summary>Sedatives add up into calm and overdose, and knock the surgeon out at KnockoutShare. An antidote
    /// takes away its own kind (flumazenil benzo, naloxone opioid) and brings them round unless the other sedatives
    /// still knock them out; a stimulant only keeps a knocked out surgeon up while it lasts.</summary>
    private void UpdateDrugs(float delta, List<StatusEvent> events) {
        foreach (var (def, crossing) in Drugs.Update(delta, (_, _) => 1f)) {
            if (crossing != DrugLevels.Crossing.Works) {
                continue;
            }
            if (!IsRacing && Drugs.Entries.Any(entry => entry.Def != def && entry.IsWorking
                    && (entry.Def.DangerWith.Contains(def.Id) || def.DangerWith.Contains(entry.Def.Id)))) {
                RaceLeft = RaceTime;
                events.Add(StatusEvent.Racing);
            }
            if (def.HasFlag("antihistamine") && Sickness > 0f) {
                Sickness = 0f;
                events.Add(StatusEvent.NauseaGone);
            }
            var reverses = def.HasFlag("reverse_benzo") ? "benzo" : def.HasFlag("reverse_opioid") ? "opioid" : null;
            if (reverses is not null) {
                if (!Drugs.Entries.Any(entry => entry.Def.HasFlag(reverses))) {
                    // Nothing to reverse: it only sets the heart pounding.
                    AddStress(AntidoteStress);
                    events.Add(StatusEvent.Jitters);
                }
                Drugs.Remove(drug => drug.HasFlag(reverses));
                if (KnockedOut > 0f && SedativeLevel() < KnockoutShare) {
                    // Woken under the knockout level, a top up past it knocks them out again.
                    _knocked = false;
                    KnockedOut = 0f;
                    KeptUp = 0f;
                    events.Add(StatusEvent.CameRound);
                }
            } else if (def.HasFlag("stimulant") && KnockedOut > 0f) {
                KeptUp = def.Duration;
                events.Add(StatusEvent.CameRound);
            }
        }
        var sedative = SedativeLevel();
        var opioid = Drugs.Entries.Where(entry => entry.Def.IsSedative && entry.Def.HasFlag("opioid"))
            .Sum(entry => entry.Level);
        Lag = sedative > 0f ? 1f - (opioid / sedative) : 1f;
        _nodding = opioid >= DrugDef.DoseEffective;
        Stimulated = Drugs.Working("stimulant");
        Calm = Mathf.Min(DrugDef.DoseStrength(sedative), 1f);
        Overdose = Mathf.Clamp((sedative - DrugDef.DoseHigh) / (KnockoutShare - DrugDef.DoseHigh), 0f, 1f);
        if (sedative >= KnockoutShare && !_knocked) {
            _knocked = true;
            KnockedOut = KnockoutTime;
            PassedOut = 0f;
            events.Add(StatusEvent.KnockedOut);
        } else if (sedative < DrugDef.DoseHigh) {
            _knocked = false;
        }
        if (KnockedOut <= 0f) {
            KeptUp = 0f;
            return;
        }
        KnockedOut = Mathf.Max(KnockedOut - delta, 0f);
        if (KeptUp > 0f) {
            KeptUp = Mathf.Max(KeptUp - delta, 0f);
            if (KeptUp == 0f) {
                // The stimulant wore off: still too much sedative in the blood and they go down again.
                if (sedative >= KnockoutShare && KnockedOut > 0f) {
                    events.Add(StatusEvent.KnockedOut);
                } else {
                    KnockedOut = 0f;
                }
            }
        } else if (KnockedOut == 0f) {
            events.Add(StatusEvent.CameRound);
        }
    }

    /// <summary>A stimulant thumps the heart; racing, it speeds up until it gives out.</summary>
    private void UpdateHeart(float delta, List<StatusEvent> events) {
        if (IsRacing) {
            RaceLeft = Mathf.Max(RaceLeft - delta, 0f);
            if (RaceLeft == 0f) {
                Dead = true;
                Eyelids = 0f;
                events.Add(StatusEvent.HeartAttack);
                return;
            }
        }
        var bpm = HeartRate();
        if (bpm <= 0f) {
            _beatWait = 0f;
            return;
        }
        _beatWait -= delta;
        if (_beatWait <= 0f) {
            _beatWait += 60f / bpm;
            events.Add(StatusEvent.Heartbeat);
        }
    }

    /// <summary>On an opioid the eyelids drop now and then: they close, stay shut a moment and open again. One already
    /// closing finishes when the opioid wears off.</summary>
    private void UpdateNods(float delta) {
        if (_nodLeft <= 0f && _nodding) {
            _nodWait -= delta;
            if (_nodWait <= 0f) {
                _nodLength = LidClose + (float)GD.RandRange(NodDarkMin, NodDarkMax) + LidOpen;
                _nodLeft = _nodLength;
                _nodWait = (float)GD.RandRange(NodGapMin, NodGapMax);
            }
        }
        if (_nodLeft <= 0f) {
            Eyelids = 0f;
            return;
        }
        _nodLeft = Mathf.Max(_nodLeft - delta, 0f);
        var shut = _nodLength - _nodLeft;
        Eyelids = Mathf.SmoothStep(0f, 1f, Mathf.Min(Mathf.Min(shut / LidClose, _nodLeft / LidOpen), 1f));
    }

    /// <summary>Every sedative in the blood, as shares of its own right dose.</summary>
    private float SedativeLevel() => Drugs.Entries.Where(entry => entry.Def.IsSedative).Sum(entry => entry.Level);
}
