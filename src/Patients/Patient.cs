namespace Scalpel.Patients;

/// <summary>How a drug goes in: through the IV line (smooth, needs a working line), a syringe straight into a vein
/// (like the line, no line needed) or into tissue (soaks in faster).</summary>
public enum DrugRoute { Iv, Vein, Direct }

/// <summary>
/// Host-authoritative patient simulation.
/// Every peer builds the same patient from the session seed. Only the host changes state, then broadcasts paint ops
/// (reliable) and vitals, targets and organs (unreliable, 5 Hz). Tool code calls the public methods, and only on the
/// host.
///
/// Split by concern: this file sets the patient up and simulates vitals and drugs, Patient.Treatment handles what tools
/// do to the body (cuts, closures, cautery...), Patient.Grips what tools take hold of, Patient.Network painting and
/// the RPCs every peer runs.
/// </summary>
public partial class Patient : Node3D
{
    [Signal] public delegate void DiedEventHandler(string reason);

    public const float Tick = 0.1f;
    public const float SyncInterval = 0.2f;
    /// <summary>Cells per side of the coarse grids over the site (sanitized skin, burns, debrided and grafted).</summary>
    public const int Grid = 16;
    private static readonly string[] BloodTypes = ["O+", "O-", "A+", "A-", "B+", "AB+"];
    /// <summary>Seconds of V-fib before it decays to asystole.</summary>
    public const float VfibToAsystole = 40f;
    /// <summary>Total arrest time before death.</summary>
    public const float ArrestDeath = 80f;
    public const float TourniquetSafe = 300f;
    /// <summary>Systolic pressure (mmHg) of a patient with a full blood volume, no drugs and no panic.</summary>
    public const float NormalPressure = 120f;
    /// <summary>Systolic pressure (mmHg) above which closures leak and fragile vessels may burst.</summary>
    public const float HighPressure = 140f;
    /// <summary>Systolic pressure (mmHg) below which the heart may arrest.</summary>
    public const float ArrestPressure = 60f;
    /// <summary>Blood glucose (mmol/l) above which consciousness fades.</summary>
    public const float GlucoseHigh = 20f;
    /// <summary>Blood glucose (mmol/l) below which any patient may seize.</summary>
    public const float GlucoseLow = 3f;
    /// <summary>Core temperature rise (°C per second) during malignant hyperthermia.</summary>
    public const float HyperthermiaRate = 0.03f;
    /// <summary>The temperature that kills.</summary>
    public const float LethalTemperature = 42.5f;
    /// <summary>Chance that a dangerous drug combination (<see cref="DrugDef.DangerWith"/>) stops the heart.</summary>
    public const float DangerArrestChance = 0.5f;
    /// <summary>Total bleeding (ml/s) that frightens an awake patient: a surgical emergency.</summary>
    public const float HeavyBleeding = 1f;
    /// <summary>Seconds after adrenaline in which a shock can restart a flat line.</summary>
    public const float RestartWindow = 60f;
    /// <summary>Systolic rise (mmHg) of a fully panicking patient.</summary>
    public const float PanicPressure = 30f;
    /// <summary>Fragile vessel bursts per second for each mmHg above HighPressure.</summary>
    public const float BurstChance = 0.005f;
    /// <summary>Seconds after a burst before the next one can happen.</summary>
    public const float BurstCooldown = 30f;
    /// <summary>Blood (ml) that fills the cavity to the top.</summary>
    public const float CavityFullMl = 350f;
    /// <summary>Blood (ml) in the cavity from which it spills over open wounds onto the skin.</summary>
    public const float CavitySpillMl = 280f;
    /// <summary>Where a line set before the surgery goes in: the back of the right hand, in body space.</summary>
    public static readonly Vector3 PreopIvPoint = new(-0.2f, 0.03f, 0.26f);

    /// <summary>The coarse grids over the site that count burned skin treated.</summary>
    public enum BurnGrid { Debrided, Grafted }

    private float[] _sanitized = new float[Grid * Grid];
    private readonly bool[] _burnCells = new bool[Grid * Grid];
    private readonly bool[] _debrided = new bool[Grid * Grid];
    private readonly bool[] _grafted = new bool[Grid * Grid];
    /// <summary>Seconds until a lethal drug that worked ends it (infinity: none has).</summary>
    private float _lethalLeft = float.PositiveInfinity;
    private float _arrestTime;
    private float _restartWindow;
    private float _seizureLeft;
    private float _reassure;
    private bool _mhActive;
    private int _nextWoundId = 1;
    private float _tickAcc;
    private float _syncAcc;
    private float _voiceCooldown;
    private float _breathCooldown;
    private float _burstCooldown;
    private float _initialSuction = 1f;
    private readonly Dictionary<int, float> _organStrain = [];
    private readonly Dictionary<int, float> _organDamage = [];

    public PatientBody Body { get; private set; } = null!;
    public Vitals Vitals { get; } = new();
    public Modifiers Mods { get; private set; } = new();
    public IReadOnlyList<QuirkRoll> Rolls { get; private set; } = [];
    public ScenarioDef Scenario { get; private set; } = null!;
    public string Age { get; private set; } = "adult";
    /// <summary>Body weight for drug doses, on the chart. The body model is scaled to match.</summary>
    public float WeightKg { get; private set; } = 75f;
    public string BloodType { get; private set; } = "O+";
    public List<Wound> Wounds { get; } = [];
    public List<CavityTarget> Targets { get; } = [];
    /// <summary>What's in the patient's blood, every injection of a drug adding up.</summary>
    public DrugLevels Drugs { get; } = new();
    /// <summary>Counters for scoring and the post-op report, see data/consequences.cfg.</summary>
    public Dictionary<string, float> Flags { get; } = [];
    public bool IvSet { get; private set; }
    /// <summary>The catheter went into a vein (set with IvSet). Missed, the line still sticks but nothing runs through
    /// it.</summary>
    public bool IvInVein { get; private set; }
    public bool TourniquetOn { get; private set; }
    public float TourniquetTime { get; private set; }
    public float CavityBloodMl { get; set; }
    public float TransfusedMl { get; private set; }
    public float MarkedUv { get; private set; }
    public bool Alive { get; private set; } = true;
    public RandomNumberGenerator Rng { get; } = new();

    /// <summary>The surgery this patient is in. Host-side code only runs during one.</summary>
    private static Surgery Session => Surgery.Current!;

    public override void _Ready()
    {
        Body = new PatientBody { Name = "Body" };
        AddChild(Body);
    }

    /// <summary>Runs on every peer with identical inputs, so everyone ends up with the same patient.</summary>
    public void Setup(ScenarioDef scenario, IReadOnlyList<QuirkRoll> rolls, ulong seed)
    {
        Scenario = scenario;
        Rolls = rolls;
        Rng.Seed = seed;
        Age = scenario.PatientAge;
        Mods = Modifiers.FromRolls(rolls, Db.PatientQuirks);
        var tone = Materials.SkinTones[Rng.RandiRange(0, Materials.SkinTones.Count - 1)];
        var site = SiteDef.Of(scenario.Site);
        Body.FatThickness = site.Fat * (1f + Mods.Num("fat_depth") * 1.5f);
        Body.Tissue.BreakMult = Mods.Mult("tear_threshold_mult");
        Body.Tissue.Tearing = Multiplayer.IsServer();
        Body.Build(scenario.Site, tone, RollWeight(seed));
        if (scenario.Environment == "or")
        {
            Body.AddDrape();
        }
        Body.SetOrientation((Orientation)scenario.StartOrientation);
        BloodType = Mods.Flag("rare_blood") ? "Bombay" : BloodTypes[Rng.RandiRange(0, BloodTypes.Length - 1)];

        var volume = Vitals.NormalBloodMl * Mods.Mult("blood_ml_mult") * (Age == "child" ? 0.5f : 1f);
        Vitals.MaxBloodMl = volume;
        Vitals.BloodMl = volume;
        Vitals.Spo2 += Mods.Num("spo2_offset");
        Vitals.Glucose += Mods.Num("glucose_drift") * 300f;
        Vitals.Apply(scenario.StartVitals);
        IvSet = scenario.PreopIv;
        IvInVein = IvSet;
        if (IvSet)
        {
            ConnectIv(PreopIvPoint);
        }

        foreach (var spec in scenario.Wounds)
        {
            var wound = NewWound(spec.Kind, MirroredUv(spec.Points[0]), spec.Depth);
            foreach (var point in spec.Points.Skip(1))
            {
                wound.Extend(MirroredUv(point));
            }
            wound.Held = spec.Held;
            PaintWoundLocal(wound);
        }
        foreach (var burn in scenario.Burns)
        {
            AddBurnLocal(MirroredUv(burn.Uv), burn.Radius);
        }
        foreach (var spec in scenario.Internal)
        {
            NewWound(WoundKind.Internal, MirroredUv(spec.Uv), 0.8f).DepthM = spec.Depth;
        }
        for (var i = 0; i < scenario.Targets.Count; i++)
        {
            var target = new CavityTarget();
            target.Setup(i, scenario.Targets[i], Mods.Flag("mirrored"));
            Body.Site.AddChild(target);
            target.Position = SiteLocal(target.Uv, target.Depth);
            Targets.Add(target);
            if (target.Covered)
            {
                Body.AddOrgan(target.Uv + new Vector2(0.03f, -0.02f), target.Depth - 0.03f, 0.04f, new Color(0.6f, 0.35f, 0.3f));
            }
        }
        _initialSuction = Mathf.Max(Targets.Where(t => t.IsSuctionTarget).Sum(t => t.Amount), 1f);
        if (site.HasAnatomy)
        {
            Body.BuildAnatomy([.. Targets.Where(t => !t.Covered).Select(t => t.RestUv)], [.. Targets.Select(t => t.Kind)]);
        }
        else if (Body.CavityDepth() > 0.1f)
        {
            for (var i = 0; i < 3; i++)
            {
                var uv = new Vector2(Rng.RandfRange(0.2f, 0.8f), Rng.RandfRange(0.2f, 0.8f));
                var radius = Rng.RandfRange(0.03f, 0.045f);
                Body.AddOrgan(uv, 0.06f, radius, new Color(0.55f, 0.2f, 0.2f).Lerp(new Color(0.7f, 0.5f, 0.4f), Rng.Randf()));
            }
        }
        if (scenario.DirtyStart)
        {
            Body.WoundMap.Disk(WoundMap.Layer.Fluids, WoundMap.Grime, new Vector2(0.5f, 0.5f), 0.6f, 0.4f, WoundMap.Mode.Max);
        }
        if (scenario.PreopAnesthesia > 0f && Db.Drug("propofol") is { } propofol)
        {
            // Already under when the surgery starts: in the blood and working, nobody gave it here.
            Drugs.Give(propofol, 0f, 0.1f);
            var entry = Drugs.Find(propofol.Id)!;
            entry.Level = 1f;
            entry.Working = 0f;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!Multiplayer.IsServer() || !Alive || Surgery.Current is not { Running: true } surgery)
        {
            return;
        }
        var dt = (float)delta;
        Body.SettleOrgans(dt);
        HandleOrgans(dt);
        if (surgery.Room.IvLine?.TrippedBy(surgery.Surgeons.Values) is { } tripped)
        {
            PullIv(tripped);
        }
        _tickAcc += dt;
        while (_tickAcc >= Tick)
        {
            _tickAcc -= Tick;
            Simulate(Tick);
        }
        _syncAcc += dt;
        if (_syncAcc >= SyncInterval)
        {
            _syncAcc = 0f;
            BroadcastState();
        }
    }

    public override void _Process(double delta)
    {
        Body.Animator.Animate(Vitals, Alive, (float)delta);
        if (Body.SkinMaterial is not null)
        {
            Body.SetPallor(Mathf.Clamp(1f - Vitals.BloodRatio * 1.4f + 0.4f, 0f, 1f));
        }
        if (Multiplayer.IsServer())
        {
            foreach (var snapped in Body.Tissue.Snapped)
            {
                OnSnap(snapped);
            }
            Body.Tissue.Snapped.Clear();
        }
    }

    // --- Simulation ---------------------------------------------------------------------------------------

    private void Simulate(float dt)
    {
        var v = Vitals;
        var fx = DrugEffectsOver(dt);
        var siteM = Body.UvToMeters(1f);
        var bleedMult = Mods.Mult("bleed_mult") * Mathf.Clamp(1f - fx.Clot, 0.2f, 2f);
        if (TourniquetOn && Body.IsLimbSite)
        {
            bleedMult *= 0.1f;
        }
        SettleClosures();
        var total = 0f;
        var heal = Mods.Num("heal_rate");
        var sources = new List<BleedSource>();
        var leak = ClosureLeak(fx);
        foreach (var wound in Wounds)
        {
            if (!wound.IsInternal)
            {
                wound.Opened = Mathf.Clamp(Body.Tissue.GapAlong(wound.Points, 0.03f, TissueDepth.Skin) / FullGap, 0f, 1f);
            }
            var rate = wound.BleedRate(siteM, bleedMult, leak);
            wound.Bleeding = rate;
            total += rate;
            // An open wound fills the cavity first; once that is nearly full it spills over the edges onto the skin.
            var intoCavity = wound.IsInternal || wound.Opened > 0.3f;
            var spills = !wound.IsInternal && CavityBloodMl > CavitySpillMl;
            if (intoCavity)
            {
                CavityBloodMl += rate * dt * 0.6f;
            }
            if (rate > 0.05f && (spills || !intoCavity))
            {
                sources.Add(new BleedSource(wound.Midpoint, rate));
                if (Rng.Randf() < dt * 0.5f)
                {
                    Session.Sound("blood_drip", Body.UvToWorld(wound.Midpoint));
                }
            }
            if (!wound.MadeBySurgeon || wound.Kind != WoundKind.Cut)
            {
                wound.Held = Mathf.Max(wound.Held - dt * 0.004f, 0f);
            }
            if (heal > 0f)
            {
                for (var i = 0; i < wound.Bins.Length; i++)
                {
                    var before = wound.Bins[i];
                    wound.Bins[i] = Mathf.Min(before + heal * dt, 1f);
                    if (before < 1f && wound.Bins[i] >= 1f && !wound.IsInternal)
                    {
                        Rpc(MethodName.TissueStitch, wound.BinPosition(i), StitchTension[0], TissueSim.TissueBreak);
                    }
                }
            }
        }
        v.BleedRate = total;
        // The worst few external bleeds run as fluid on every peer (BloodFlow); the rest is too little to see.
        Body.Blood.Sources = [.. sources.OrderByDescending(source => source.Rate).Take(6)];
        v.BloodMl = Mathf.Clamp(v.BloodMl - total * dt + fx.VolumeMl * dt, 0f, v.MaxBloodMl * 1.1f);
        CavityBloodMl = Mathf.Max(CavityBloodMl, 0f);
        Body.SetCavityBlood(CavityBloodMl / CavityFullMl);

        v.Anesthesia = Mathf.Clamp(fx.Anesthesia * Mods.Mult("sedation_mult"), 0f, 1f);
        v.LocalBlock = Mathf.Clamp(fx.LocalBlock, 0f, 1f);
        v.Glucose += (Mods.Num("glucose_drift") * (1f + v.Panic) + fx.Glucose) * dt;
        v.Pain = Mathf.Clamp(v.Pain - dt * 0.05f - fx.PainRelief * dt * 0.2f, 0f, 1f);
        v.Swelling = Mathf.Max(v.Swelling - dt * (0.004f + fx.Antihistamine * 0.02f + fx.Adrenaline * 0.05f), 0f);
        var shock = Mathf.Clamp((0.6f - v.BloodRatio) / 0.3f, 0f, 1f);
        var glucoseComa = Mathf.Clamp((v.Glucose - GlucoseHigh) / 10f, 0f, 1f) + Mathf.Clamp((2.5f - v.Glucose) / 1.5f, 0f, 1f);
        v.Consciousness = v.IsArrested ? 0f : Mathf.Clamp(1f - Mathf.Max(v.Anesthesia, fx.Sedation * 0.8f) - shock - glucoseComa, 0f, 1f);

        if (_reassure > 0f)
        {
            AddFlag("comfort_time", dt);
        }
        _reassure = Mathf.Max(_reassure - dt, 0f);
        if (v.IsAwake)
        {
            var calming = 0.06f + fx.Sedation * 0.3f + (_reassure > 0f ? 0.25f : 0f);
            var fear = v.Pain * 0.12f + (total > HeavyBleeding ? 0.03f : 0f);
            v.Panic = Mathf.Clamp(v.Panic + (fear - calming) * Mods.Mult("panic_mult") * dt * 2f, 0f, 1f);
        }
        else
        {
            v.Panic = Mathf.Max(v.Panic - dt * 0.2f, 0f);
        }

        var ratio = v.BloodRatio;
        var awakeFactor = v.IsAwake ? 1f : 0.2f;
        var targetHr = 72f + (1f - ratio) * 140f + v.Pain * 35f * awakeFactor + v.Panic * 40f + fx.Hr + (v.Temperature - 37f) * 10f;
        var suctionLeft = Targets.Where(t => t.IsSuctionTarget && !t.Extracted).Sum(t => t.Amount);
        var targetSpo2 = 98f + Mods.Num("spo2_offset") + fx.Spo2 - (1f - ratio) * 30f - v.Swelling * 15f - suctionLeft / _initialSuction * 16f;
        switch (v.Rhythm)
        {
            case Rhythm.Sinus:
                v.HeartRate = Mathf.Lerp(v.HeartRate, targetHr, dt * 0.6f) + Rng.RandfRange(-0.6f, 0.6f);
                v.Systolic = Mathf.Lerp(v.Systolic,
                    NormalPressure * Mathf.Pow(ratio, 1.6f) + fx.Bp + v.Panic * PanicPressure - v.Swelling * 50f, dt * 0.6f);
                v.Spo2 = Mathf.Clamp(Mathf.Lerp(v.Spo2, targetSpo2, dt * 0.3f), 50f, 100f);
                _arrestTime = 0f;
                RollArrest(dt, fx);
                break;
            case Rhythm.Vfib:
                v.HeartRate = Rng.RandfRange(180f, 300f);
                v.Systolic = Mathf.Lerp(v.Systolic, 15f, dt);
                Arrested(dt);
                break;
            case Rhythm.Asystole:
                v.HeartRate = 0f;
                v.Systolic = Mathf.Lerp(v.Systolic, 0f, dt);
                Arrested(dt);
                break;
        }
        if (v.Rhythm != Rhythm.Sinus)
        {
            v.Spo2 = Mathf.Max(v.Spo2 - dt * 1.5f, 40f);
        }

        if (Mods.Flag("mh_trigger") && HasActive("mh_trigger") && !HasActive("mh_cure"))
        {
            _mhActive = true;
        }
        if (HasActive("mh_cure"))
        {
            _mhActive = false;
        }
        var baseline = Session.RunMods.Flag("cold") ? 35.4f : 36.8f;
        v.Temperature += ((_mhActive ? HyperthermiaRate : 0f) + (baseline - v.Temperature) * 0.01f + fx.Temp * 0.01f) * dt;

        _restartWindow = Mathf.Max(_restartWindow - dt, 0f);
        UpdateFragileVessels(dt);
        UpdateSeizure(dt);
        UpdateMisc(dt);
        CheckDeath(fx);
    }

    /// <summary>Organs held out of place too long, or shoved hard, bruise and start bleeding.</summary>
    private void HandleOrgans(float delta)
    {
        for (var i = 0; i < Body.Organs.Count; i++)
        {
            var organ = Body.Organs[i];
            var strain = _organStrain.GetValueOrDefault(i);
            if (Body.OrganOffset(i) > 0.035f)
            {
                strain += delta;
            }
            if (organ.LinearVelocity.Length() > 0.5f)
            {
                strain += delta * 4f;
            }
            strain = Mathf.Max(strain - delta * 0.3f, 0f);
            if (strain > 5f)
            {
                strain = 0f;
                _organDamage[i] = Mathf.Min(_organDamage.GetValueOrDefault(i) + 0.35f, 1f);
                Rpc(MethodName.SetOrganDamage, i, _organDamage[i]);
                var wound = NewWound(WoundKind.Internal, Body.LocalToUv(organ.Position), 0.4f);
                wound.DepthM = -organ.Position.Y;
                Session.Scoring.Add("organ_bruise");
                Session.Announce("You've been manhandling an organ. It's bruising and oozing.", true);
            }
            _organStrain[i] = strain;
        }
    }

    private void RollArrest(float dt, DrugEffects fx)
    {
        var v = Vitals;
        var risk = Mathf.Max(0.55f - v.BloodRatio, 0f) * 0.05f;
        risk += v.Systolic < ArrestPressure ? 0.01f : 0f;
        risk += v.HeartRate > 170f ? 0.004f : 0f;
        risk += v.Temperature > 40.5f ? 0.01f : 0f;
        risk += v.Glucose < 2f ? 0.004f : 0f;
        risk += v.Swelling > 0.7f ? 0.02f : 0f;
        risk += Mods.Num("clot_risk") * (fx.Clot < -0.2f ? 0f : 1f);
        if (Rng.Randf() < risk * Mods.Mult("arrest_mult") * dt)
        {
            Arrest();
        }
    }

    private void Arrested(float dt)
    {
        _arrestTime += dt;
        if (Vitals.Rhythm == Rhythm.Vfib && _arrestTime > VfibToAsystole)
        {
            Vitals.Rhythm = Rhythm.Asystole;
        }
    }

    /// <summary>How much blood gets through closures and packing (see <see cref="Wound.BleedRate"/>): heparin, or
    /// pressure above HighPressure.</summary>
    private float ClosureLeak(DrugEffects fx)
    {
        var thinned = Mathf.Clamp(-fx.Clot, 0f, 1f) * 0.5f;
        var pressure = Mathf.Clamp((Vitals.Systolic - HighPressure) / 40f, 0f, 0.5f);
        return Mathf.Min(thinned + pressure, 0.6f);
    }

    /// <summary>Fragile vessels (an aneurysm) burst under pressure above HighPressure: a deep vessel under the site
    /// gives way.</summary>
    private void UpdateFragileVessels(float dt)
    {
        if (!Mods.Flag("fragile_vessels"))
        {
            return;
        }
        _burstCooldown = Mathf.Max(_burstCooldown - dt, 0f);
        var excess = Vitals.Systolic - HighPressure;
        if (excess <= 0f || _burstCooldown > 0f || Rng.Randf() >= excess * BurstChance * dt)
        {
            return;
        }
        _burstCooldown = BurstCooldown;
        var uv = new Vector2(Rng.RandfRange(0.3f, 0.7f), Rng.RandfRange(0.3f, 0.7f));
        NewWound(WoundKind.Internal, uv, 0.7f).DepthM = Mathf.Min(0.04f, Body.CavityDepth() * 0.6f);
        Session.Sound("blood_spurt", Body.UvToWorld(uv));
        Session.Announce("A vessel gives way!");
        foreach (var roll in Rolls.Where(roll => Db.PatientQuirks[roll.Id].Effects(roll.Variant).ContainsKey("fragile_vessels")))
        {
            Reveal(roll.Id);
        }
    }

    private void UpdateSeizure(float dt)
    {
        var chance = Mods.Num("seizure_chance") + (Vitals.Glucose < GlucoseLow ? 0.02f : 0f);
        if (_seizureLeft > 0f)
        {
            _seizureLeft -= dt;
            Vitals.Seizing = _seizureLeft > 0f && !HasActive("anticonvulsant");
            if (Vitals.Seizing && Rng.Randf() < dt * 1.5f)
            {
                Session.JoltAll(0.6f, "The patient convulses!");
                StrainClosures(0.3f);
            }
        }
        else if (!HasActive("anticonvulsant") && Rng.Randf() < chance * dt)
        {
            StartSeizure();
        }
    }

    private void UpdateMisc(float dt)
    {
        if (TourniquetOn)
        {
            TourniquetTime += dt;
            if (TourniquetTime > TourniquetSafe && !Flags.ContainsKey("tourniquet_overtime"))
            {
                AddFlag("tourniquet_overtime");
                Session.Announce("The tourniquet has been on too long.");
            }
        }
        foreach (var target in Targets.Where(t => t.IsSuctionTarget && !t.Extracted && t.Refill > 0f))
        {
            target.Amount += target.Refill * dt;
        }
        if (Vitals.IsAwake && Mods.Num("cough_chance") > 0f && Rng.Randf() < Mods.Num("cough_chance") * dt)
        {
            Session.JoltAll(0.3f, "The patient coughs violently.");
            StrainClosures(0.15f);
        }
        _breathCooldown -= dt;
        if (Vitals.IsAwake && Vitals.Panic > 0.6f && _breathCooldown <= 0f)
        {
            _breathCooldown = 2f;
            Rpc(MethodName.Vocal, "patient_breath");
        }
        _voiceCooldown -= dt;
        if (Vitals.IsAwake && _voiceCooldown <= 0f)
        {
            AmbientVoice();
        }
    }

    private void CheckDeath(DrugEffects fx)
    {
        var v = Vitals;
        string? reason = null;
        if (v.BloodMl < v.MaxBloodMl * 0.3f)
        {
            reason = "Bled out.";
        }
        else if (_arrestTime > ArrestDeath)
        {
            reason = "Cardiac arrest.";
        }
        else if (v.Temperature > LethalTemperature)
        {
            reason = "Malignant hyperthermia.";
        }
        else if (fx.Lethal > 0.95f)
        {
            reason = "Passed away peacefully.";
        }
        if (reason is not null)
        {
            Alive = false;
            v.Rhythm = Rhythm.Asystole;
            v.HeartRate = 0f;
            EmitSignal(SignalName.Died, reason);
        }
    }

    // --- Drugs --------------------------------------------------------------------------------------------

    private DrugEffects DrugEffectsOver(float dt)
    {
        var working = new List<DrugDef>();
        foreach (var (def, crossing) in Drugs.Update(dt, Wear))
        {
            if (crossing == DrugLevels.Crossing.Works)
            {
                DrugWorks(def, working);
                working.Add(def);
            }
            else
            {
                AddFlag("overdose");
                Session.Scoring.Add("overdose");
            }
        }
        var fx = new DrugEffects();
        var potency = Surgery.Current?.RunMods.Mult("drug_strength_mult") ?? 1f;
        foreach (var entry in Drugs.Entries)
        {
            var def = entry.Def;
            var strength = DrugDef.DoseStrength(entry.Level) * potency;
            fx.AddStrengths(def.Peak, strength);
            if (entry.Level > 0f)
            {
                // Totals, spread over the time it takes to wear off, as fast as it does: two doses give twice as much.
                var share = Wear(def, entry.Level) / Mathf.Max(def.Duration, 1f);
                fx.Glucose += def.Peak.Glucose * share;
                fx.VolumeMl += def.Peak.VolumeMl * share;
            }
            if (def.HasFlag("antihistamine"))
            {
                fx.Antihistamine += strength;
            }
            if (def.Id == "adrenaline")
            {
                fx.Adrenaline += strength;
            }
        }
        if (!float.IsPositiveInfinity(_lethalLeft))
        {
            _lethalLeft -= dt;
            fx.Lethal = _lethalLeft <= 0f ? 1f : 0f;
        }
        return fx;
    }

    /// <summary>
    /// How fast a drug at <paramref name="level"/> wears off (1: one right dose over its duration). General
    /// anesthesia lasts the whole surgery, kept topped up to the right dose like an anesthetist would: more than that
    /// wears off as usual, so another dose deepens it only for a while. A patient who burns through it
    /// (anesthesia_decay_mult) loses it all.
    /// </summary>
    private float Wear(DrugDef def, float level)
    {
        if (def.Peak.Anesthesia <= 0f)
        {
            return 1f;
        }
        var decayMult = Mods.Mult("anesthesia_decay_mult");
        if (decayMult > 1f)
        {
            return decayMult;
        }
        return level > 1f ? 1f : 0f;
    }

    private bool HasActive(string flag) => Drugs.Working(flag);

    /// <summary>Says so when there's no line to give anything through, or it isn't in a vein.</summary>
    public bool IvReady()
    {
        if (!IvSet)
        {
            Session.Announce("Nothing happens. There's no IV line in.");
        }
        else if (!IvInVein)
        {
            Session.Announce("Nothing goes in. The IV line missed the vein.");
        }
        return IvWorking;
    }

    /// <summary>A line is in and in a vein: drugs and fluids run through it.</summary>
    public bool IvWorking => IvSet && IvInVein;

    /// <summary>
    /// Gives a drug. <paramref name="amount"/>: how much was given in the drug's unit (see <see cref="DrugDef.Dose"/>);
    /// negative means just the right dose (bags, masks). Every injection adds to what's already in
    /// (<see cref="DrugLevels"/>): ten small ones work like one big one.
    /// </summary>
    public void Administer(string drugId, DrugRoute route, float amount = -1f)
    {
        if (Db.Drug(drugId) is not { } def || (route == DrugRoute.Iv && !IvReady()))
        {
            return;
        }
        var share = amount >= 0f && def.Dose > 0f ? amount / (def.Dose * WeightKg) : 1f;
        if (def.IsBlood)
        {
            TransfusedMl += def.Peak.VolumeMl;
            if (!BloodCompatible(def.BloodType))
            {
                AddFlag("wrong_blood");
                Vitals.Swelling = Mathf.Min(Vitals.Swelling + 0.3f, 1f);
                Vitals.Temperature += 1f;
                Session.Scoring.Add("wrong_blood");
                Session.Announce("Fever and shaking. Transfusion reaction!");
            }
        }
        if (drugId == "whiskey")
        {
            AddFlag("whiskey_given");
            if (Mods.Flag("whiskey_friendly") && Db.Drug("diazepam") is { } diazepam)
            {
                def = diazepam;
                share *= 0.5f;
            }
        }
        var fresh = Drugs.Give(def, share, def.Onset * (route == DrugRoute.Direct ? DrugDef.DirectOnset : 1.5f));
        if (fresh && Mods.List("allergen").Contains(drugId))
        {
            Vitals.Swelling = Mathf.Min(Vitals.Swelling + 0.6f, 1f);
            Vitals.Systolic -= 30f;
            Session.Scoring.Add("allergic_reaction");
            Session.Announce("Hives spread across the skin. Allergic reaction!");
            Reveal("allergy");
        }
    }

    /// <summary>A drug just reached an effective level: it does its job. <paramref name="along"/> started working in
    /// the same step, before it: a dangerous pair of them reacts once, not once for each.</summary>
    private void DrugWorks(DrugDef def, List<DrugDef> along)
    {
        foreach (var entry in Drugs.Entries.ToList())
        {
            var other = entry.Def;
            if (other != def && !along.Contains(other) && entry.IsWorking
                && (other.DangerWith.Contains(def.Id) || def.DangerWith.Contains(other.Id)))
            {
                Session.Announce("Blood pressure spikes through the roof!");
                if (Rng.Randf() < DangerArrestChance)
                {
                    Arrest();
                }
            }
        }
        AddFlag("drug_" + def.Id);
        if (def.HasFlag("restart"))
        {
            _restartWindow = RestartWindow;
            Vitals.Swelling = Mathf.Max(Vitals.Swelling - 0.4f, 0f);
        }
        if (def.HasFlag("reverse_opioid"))
        {
            Drugs.Remove(drug => drug.HasFlag("opioid"));
        }
        if (def.HasFlag("reverse_benzo"))
        {
            Drugs.Remove(drug => drug.HasFlag("benzo"));
        }
        if (def.HasFlag("anticonvulsant"))
        {
            _seizureLeft = 0f;
            Vitals.Seizing = false;
        }
        if (def.HasFlag("antibiotic"))
        {
            AddFlag("antibiotic");
        }
        if (def.HasFlag("lethal"))
        {
            AddFlag("euthanized");
            // There's no coming back from it, however fast it wears off.
            _lethalLeft = Mathf.Min(_lethalLeft, def.Duration * 0.8f);
        }
    }

    /// <summary>
    /// Rolls the weight on its own generator (so the rest of the patient stays the same) and returns the body scale for
    /// it. Size follows the cube root of weight: twice as heavy is about a quarter bigger. Heavy build quirks add
    /// weight.
    /// </summary>
    private float RollWeight(ulong seed)
    {
        var weightRng = new RandomNumberGenerator { Seed = seed + 3 };
        var (low, high, typical, scale) = Age switch
        {
            "child" => (18f, 34f, 26f, 0.72f),
            "elderly" => (45f, 85f, 68f, 0.96f),
            _ => (55f, 105f, 75f, 1f),
        };
        WeightKg = Mathf.Round(weightRng.RandfRange(low, high) * (1f + Mods.Num("fat_depth") * 0.3f));
        return scale * Mathf.Pow(WeightKg / typical, 1f / 3f);
    }

    private bool BloodCompatible(string pack)
    {
        if (BloodType == "Bombay")
        {
            return pack == "Bombay";
        }
        return pack == "O-" || pack == BloodType || (pack is "A+" or "B+" && BloodType == "AB+");
    }

    // --- Events -------------------------------------------------------------------------------------------

    public void Arrest()
    {
        if (Vitals.Rhythm != Rhythm.Sinus || !Alive)
        {
            return;
        }
        Vitals.Rhythm = Rhythm.Vfib;
        Session.Scoring.Add("arrest");
        Session.Announce("V-fib! The heart has stopped pumping.");
        Reveal("heart");
    }

    public void StartSeizure()
    {
        if (HasActive("anticonvulsant"))
        {
            return;
        }
        _seizureLeft = Rng.RandfRange(6f, 10f);
        Vitals.Seizing = true;
        Session.Announce("Seizure!");
        Reveal("epilepsy");
    }

    public void WakeUp()
    {
        Drugs.Remove(drug => drug.Peak.Anesthesia > 0f);
        Speak("wake_up");
    }

    public void Shock(float power)
    {
        if (Mods.Flag("pacemaker") && Scenario.Site is "chest" or "abdomen")
        {
            Vitals.HeartRate += Rng.RandfRange(-30f, 40f);
            Reveal("pacemaker");
        }
        Hurt(0.8f);
        switch (Vitals.Rhythm)
        {
            case Rhythm.Vfib:
                var chance = 0.45f * power + (HasActive("antiarrhythmic") ? 0.3f : 0f);
                if (Rng.Randf() < chance)
                {
                    Revive();
                }
                break;
            case Rhythm.Asystole:
                if (_restartWindow > 0f && Rng.Randf() < 0.4f * power)
                {
                    Revive();
                }
                break;
            case Rhythm.Sinus:
                if (Rng.Randf() < 0.25f)
                {
                    Session.Announce("You shocked a beating heart.");
                    Arrest();
                }
                break;
        }
    }

    private void Revive()
    {
        Vitals.Rhythm = Rhythm.Sinus;
        Vitals.HeartRate = 110f;
        Vitals.Systolic = Mathf.Max(Vitals.Systolic, 70f);
        AddFlag("revived");
        Session.Scoring.Add("revived");
        Session.Announce("Sinus rhythm. They're back.");
    }

    public void Reassure()
    {
        if (!Vitals.IsAwake)
        {
            return;
        }
        if (_reassure <= 0f)
        {
            Session.Scoring.Add("reassured", true);
            Speak("reassured");
        }
        _reassure = 8f;
    }

    public void HeavyDrop(Vector3 at)
    {
        if (!Mods.Flag("bone_fragile") || Body.PartAt(at, 0.1f).Length == 0)
        {
            return;
        }
        Bruise(Body.WorldToUv(at).Clamp(Vector2.Zero, Vector2.One), 0.08f, 0.8f);
        Hurt(0.7f);
        AddFlag("fracture");
        Session.Sound("bone_crack", at);
        Session.Announce("Something cracked under that. A fragile bone broke!");
        Reveal("bones");
    }

    public void TurnOver(Orientation to, bool fell)
    {
        Rpc(MethodName.SetOrientation, (int)to);
        if (fell)
        {
            AddFlag("fell");
            Hurt(1f);
            Vitals.BloodMl -= 150f;
            StrainClosures(1f);
        }
    }

    /// <summary>Seizures, coughs and falls pull on closures. Weak ones (office staples, tape) can burst.</summary>
    private void StrainClosures(float strength)
    {
        foreach (var wound in Wounds)
        {
            if (wound.Closure > 0f && Rng.Randf() < strength * (1f - wound.ClosureQuality))
            {
                for (var i = 0; i < wound.Bins.Length; i++)
                {
                    wound.Bins[i] *= 0.4f;
                }
                Rpc(MethodName.TissueBurst, wound.Midpoint, wound.LengthUv * 0.5f + 0.03f);
                Session.Announce("A closure bursts open!");
                PaintWound(wound);
            }
        }
    }

    public void AddFlag(string key, float amount = 1f) => Flags[key] = Flags.GetValueOrDefault(key) + amount;

    /// <summary>Pain at a spot on the site (uv) is dulled by a local block; deep pain (the blade on a bone) mostly gets
    /// through it.</summary>
    public void Hurt(float amount, Vector2? uv = null, bool deep = false)
    {
        var numb = Vitals.Anesthesia;
        if (uv is not null)
        {
            numb = Mathf.Max(numb, Vitals.LocalBlock * (deep ? DeepBlock : 1f));
        }
        Vitals.Pain = Mathf.Clamp(Vitals.Pain + amount * (1f - numb) * Mods.Mult("pain_mult"), 0f, 1f);
        if (Vitals.IsAwake && amount * (1f - numb) > 0.15f)
        {
            Speak("pain");
        }
    }

    /// <summary>Touch outside the treated area. Ticklish patients flinch.</summary>
    public void Touch()
    {
        if (Mods.Flag("ticklish") && Vitals.IsAwake && Rng.Randf() < 0.05f)
        {
            Session.JoltAll(0.25f, "");
            Speak("tickle", force: true);
            Reveal("ticklish");
        }
    }

    // --- Queries for objectives ---------------------------------------------------------------------------

    public float SanitizedFraction() => _sanitized.Average();

    public float GridFraction(BurnGrid which)
    {
        var burned = _burnCells.Count(cell => cell);
        if (burned == 0)
        {
            return 1f;
        }
        var grid = which == BurnGrid.Debrided ? _debrided : _grafted;
        return (float)Enumerable.Range(0, grid.Length).Count(i => grid[i] && _burnCells[i]) / burned;
    }

    public float SurgeonCutLengthM(float minDepth) =>
        Wounds.Where(w => w.MadeBySurgeon && w.Kind == WoundKind.Cut && w.Depth >= minDepth)
            .Sum(w => Body.UvToMeters(w.LengthUv));

    /// <summary>How much of the skin's wounds is closed, by length: a small hole left open counts for as little of it
    /// as it is.</summary>
    public float SkinClosure()
    {
        var closed = 0f;
        var total = 0f;
        foreach (var wound in Wounds.Where(w => w.IsSkinCut))
        {
            // A hole is a bin long at least.
            var length = Mathf.Max(wound.LengthUv, Wound.BinLengthUv);
            closed += wound.Closure * length;
            total += length;
        }
        return total > 0f ? closed / total : 1f;
    }

    public bool InternalClosed() => Wounds.All(w => !w.IsInternal || w.Closure >= 0.85f || w.Cauterized >= 0.9f);

    public bool AnyClamped() => Wounds.Any(w => w.Clamped >= 0.8f);

    public CavityTarget? TargetByKind(string kind, bool skipExtracted = false) =>
        Targets.FirstOrDefault(t => t.Kind == kind && !(skipExtracted && t.Extracted));

    public List<string> RevealedCardLines() =>
        [.. Rolls.Select(roll => Db.PatientQuirks[roll.Id].Text("card", roll.Variant)).Where(line => line.Length > 0)];

    // --- Internals ----------------------------------------------------------------------------------------

    private Wound NewWound(WoundKind kind, Vector2 at, float depth)
    {
        var wound = new Wound(_nextWoundId++, kind, at, depth);
        Wounds.Add(wound);
        return wound;
    }

    public Wound? WoundWithId(int id) => Wounds.Find(w => w.Id == id);

    private Wound? NearestWound(Vector2 uv, float maxDistance, bool internalWound)
    {
        Wound? best = null;
        var bestDistance = maxDistance;
        foreach (var wound in Wounds)
        {
            if (wound.IsInternal != internalWound || wound.Kind == WoundKind.Burn)
            {
                continue;
            }
            var distance = wound.DistanceTo(uv);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = wound;
            }
        }
        return best;
    }

    /// <summary>A scenario's uv as on this patient: mirrored anatomy flips left and right (uv.y runs across the body).
    /// </summary>
    private Vector2 MirroredUv(Vector2 raw) => new(raw.X, Mods.Flag("mirrored") ? 1f - raw.Y : raw.Y);

    private Vector3 SiteLocal(Vector2 uv, float depth) => Body.SitePoint(uv, Body.SurfaceHeight(uv) - depth);

    private static int Cell(Vector2 uv)
    {
        var c = (Vector2I)(uv * Grid).Floor();
        return c.X >= 0 && c.Y >= 0 && c.X < Grid && c.Y < Grid ? c.Y * Grid + c.X : -1;
    }

    /// <summary>The grid cells within <paramref name="radius"/> (uv) of uv, as a square.</summary>
    private static IEnumerable<Vector2I> CellsAround(Vector2 uv, float radius)
    {
        var r = Mathf.CeilToInt(radius * Grid);
        var center = (Vector2I)(uv * Grid).Floor();
        for (var y = center.Y - r; y <= center.Y + r; y++)
        {
            for (var x = center.X - r; x <= center.X + r; x++)
            {
                if (x >= 0 && y >= 0 && x < Grid && y < Grid)
                {
                    yield return new Vector2I(x, y);
                }
            }
        }
    }

    private static void MarkGrid(float[] grid, Vector2 uv, float radius, float strength)
    {
        foreach (var cell in CellsAround(uv, radius))
        {
            grid[cell.Y * Grid + cell.X] = Mathf.Max(grid[cell.Y * Grid + cell.X], strength);
        }
    }

    private void AddBurnLocal(Vector2 uv, float radius)
    {
        Body.WoundMap.Disk(WoundMap.Layer.Wounds, WoundMap.Burn, uv, radius, 1.4f, WoundMap.Mode.Max);
        foreach (var cell in CellsAround(uv, radius))
        {
            if (new Vector2(cell.X + 0.5f, cell.Y + 0.5f).DistanceTo(uv * Grid) <= radius * Grid)
            {
                _burnCells[cell.Y * Grid + cell.X] = true;
            }
        }
        Array.Fill(NewWound(WoundKind.Burn, uv, 0.3f).Bins, 1f);
    }

    private void AmbientVoice()
    {
        var talk = Mods.Num("talk_rate", 1f);
        _voiceCooldown = Rng.RandfRange(12f, 25f) / talk;
        if (Vitals.Panic > 0.6f)
        {
            Speak("panic");
        }
        else if (talk > 1f && Rng.Randf() < 0.3f)
        {
            Speak(Rng.Randf() < 0.6f ? "hint" : "lie");
        }
        else
        {
            Speak("calm");
        }
    }

    private void Speak(string trigger, bool force = false)
    {
        if (!force && _voiceCooldown > 0f && trigger is "pain" or "calm")
        {
            return;
        }
        _voiceCooldown = Mathf.Max(_voiceCooldown, 4f);
        var sound = trigger switch
        {
            "pain" or "tickle" => "patient_groan",
            "panic" => "patient_scream",
            _ => "",
        };
        if (sound.Length > 0)
        {
            Rpc(MethodName.Vocal, sound);
        }
        var lines = Db.Dialogue.GetValue(trigger, Age, Db.Dialogue.GetValue(trigger, "any", Array.Empty<string>())).AsStringArray();
        if (lines.Length == 0)
        {
            return;
        }
        var index = Rng.RandiRange(0, lines.Length - 1);
        Session.Say(lines[index], $"{trigger}_{index}");
    }

    /// <summary>Next ramble line for the Last Request scenario. Plays in order.</summary>
    public bool Ramble(int index)
    {
        var lines = Db.Dialogue.GetValue("ramble", "any", Array.Empty<string>()).AsStringArray();
        if (index >= lines.Length || !Alive)
        {
            return false;
        }
        Session.Say(lines[index], $"ramble_{index}");
        return true;
    }

    /// <summary>A hidden quirk showed itself. Players unlock it in the codex from the report either way.</summary>
    private void Reveal(string quirkId)
    {
        if (Rolls.Any(roll => roll.Id == quirkId))
        {
            AddFlag("revealed_" + quirkId);
        }
    }
}
