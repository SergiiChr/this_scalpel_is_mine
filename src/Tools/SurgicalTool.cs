namespace Scalpel.Tools;

/// <summary>Where a tool is.</summary>
public enum ToolState
{
    /// <summary>Lying loose, physics running (host).</summary>
    Free,
    /// <summary>In a surgeon's hand.</summary>
    Held,
    /// <summary>On a surgeon's belt.</summary>
    Belt,
    /// <summary>Let go of but keeping its hold (a self-retaining clamp, a hung bag, a tourniquet).</summary>
    Standing,
    /// <summary>Dropped into the patient and left there.</summary>
    Inside,
    /// <summary>Used up, gone.</summary>
    Consumed,
    /// <summary>Pinched at another tool's tip (a cotton pad in forceps); its holder is that tool's uid.</summary>
    Carried,
}

/// <summary>Host-side state of a tool while it's worked, from frame to frame (see <see cref="ToolActions"/>).
/// </summary>
public sealed class ToolUse
{
    public bool LoweredBefore { get; set; }
    public bool TriggerBefore { get; set; }
    public bool PressedBefore { get; set; }
    public int LevelBefore { get; set; }
    /// <summary>Counts the strokes of a blade: a new one each time it's lowered or dragged sideways.</summary>
    public int Stroke { get; set; }
    /// <summary>Deepest level this stroke's blade point has been pressed in at.</summary>
    public int StabbedLevel { get; set; }
    /// <summary>Where a stapler's legs were (world) when Use tool was pressed, as its aim showed them: lowering it moves
    /// it a little.</summary>
    public Vector3[] StapleAim { get; set; } = [];
    public Vector2 LastUv { get; set; } = new(-1, -1);
    public Vector3 LastTip { get; set; } = Vector3.Inf;
    public float ChargeTime { get; set; }
    /// <summary>How long a clamp's pinch has waited to close (seconds), null when none is waiting: it closes once the tip
    /// has come to rest on what it was lowered onto (see ClampAction).</summary>
    public float? PinchWait { get; set; }
    /// <summary>How high the tip came to rest (world y) and how long it has stayed there (seconds).</summary>
    public float RestHeight { get; set; }
    public float RestFor { get; set; }
    /// <summary>Wiping time not painted yet.</summary>
    public float PaintDt { get; set; }
    /// <summary>Where wiping was last painted.</summary>
    public Vector2 PaintUv { get; set; } = new(-1, -1);
    /// <summary>Things already scored for this tool ("dirty", "improvised").</summary>
    public HashSet<string> Reported { get; } = [];

    /// <summary>Forgets what the hand did last, for a tool that just left it.</summary>
    public void Reset()
    {
        LoweredBefore = false;
        TriggerBefore = false;
        PressedBefore = false;
        LevelBefore = 0;
        LastTip = Vector3.Inf;
        PinchWait = null;
    }
}

/// <summary>
/// A needle's running suture: the live thread's id (0 for none), its tension (the wheel) and the layer it's in. Each
/// Use tool press makes one hole, at where the needle last rested on the patient ((-1, -1) for nowhere): Hold is how
/// long it's been held, PressUsed that it already did something (tied off, sewed an internal injury).
/// </summary>
public sealed class SutureState
{
    public int Thread { get; set; }
    public float Tension { get; set; } = 1.15f;
    public TissueDepth Layer { get; set; } = TissueDepth.None;
    public float Hold { get; set; }
    public Vector2 At { get; set; } = new(-1, -1);
    public bool PressUsed { get; set; }
}

/// <summary>
/// A grabbable item. Physics runs on the host only, clients get transforms from <see cref="ToolManager"/>.
/// The grip is at the origin and the tip at -Z * length.
/// </summary>
public partial class SurgicalTool : RigidBody3D
{
    public const uint ToolLayer = 8;
    public static readonly Color IodineColor = new(0.55f, 0.3f, 0.12f);
    /// <summary>Liquid that is all blood. A mix tints toward it by its share of blood.</summary>
    public static readonly Color BloodColor = new(0.5f, 0.03f, 0.04f);
    /// <summary>A hung IV bag comes with this much fluid (ml), with room left in it for drugs pushed in.</summary>
    public const float DripFluid = 500f;
    /// <summary>Smallest size (meters) a tool counts as across for how hard it is to turn, see Setup().</summary>
    private const float MinTurningSize = 0.04f;
    /// <summary>How thick a tourniquet's band is where it wraps a limb.</summary>
    private const float BandThickness = 0.012f;
    /// <summary>Seconds a spreader takes to go down into a cut once set (<see cref="DigTo"/>).</summary>
    public const float DigTime = 0.2f;

    private readonly ToolAnimator _animator = new();
    /// <summary>This tool's own copies of its toon materials, made the first time it needs to look different from the
    /// rest, with their own grime and color to go back to.</summary>
    private List<(ShaderMaterial Material, float Grime)> _ownMaterials = [];
    /// <summary>The liquid parts' own colors before blood or iodine tinted them.</summary>
    private readonly Dictionary<MeshInstance3D, Color> _liquidColors = [];
    /// <summary>The model parts' colors before iodine soaked into them.</summary>
    private readonly Dictionary<ShaderMaterial, Color> _dryColors = [];
    private Node3D _model = null!;
    private float _spread = ToolActions.SpreadRange.X;
    private Transform3D _digFrom;
    private Transform3D _digTo;
    private float _dig = 1f;
    private Vector3? _levelRest;

    public int Uid { get; private set; }
    public ToolDef Def { get; internal set; } = null!;
    public ToolState State { get; private set; } = ToolState.Free;
    /// <summary>Peer id of whoever holds it (Held, Belt) or last held it; the carrying tool's uid while Carried.
    /// </summary>
    public int Holder { get; private set; }
    /// <summary>Hand index while Held, belt slot while on the Belt.</summary>
    public int Slot { get; private set; } = -1;
    public bool Sterile { get; set; } = true;
    /// <summary>Fell on the floor: visibly dirty. Needs the sink before the sanitizer can make it sterile again.
    /// </summary>
    public bool Soiled { get; private set; }
    public int Charges { get; set; } = -1;
    /// <summary>How bloody the working end is (0..1), for everyone.</summary>
    public float Blood { get; private set; }
    /// <summary>Host-side blood exposure, building up to what everyone sees in Blood.</summary>
    public float BloodExposure { get; set; }
    /// <summary>How full of liquid it is (0..1): iodine in the dish, soaked into a cotton pad, a syringe, vial or kidney
    /// dish. Exact on the host, in steps elsewhere (a syringe, vial or kidney dish is exact everywhere, see Ml).
    /// </summary>
    public float Fill { get; set; }
    // ml of liquid in a syringe, vial or kidney dish, ml of air drawn into a syringe and the share of the liquid that
    // is blood and iodine (0..1). Exact on every peer: the host sends changes (ToolManager.AddLiquid() and Transfer()).
    public float Ml { get; set; }
    public float Air { get; set; }
    public float Red { get; set; }
    public float Iodine { get; set; }
    /// <summary>Host only: how much of each drug is in the liquid (drug id -> amount in its unit, "blood" in ml). A
    /// syringe drawn from two vials holds a mix.</summary>
    public Dictionary<string, float> Contents { get; } = [];
    /// <summary>Host only, the IV drip: ml pushed into the bag that haven't run down the line yet (they went in by the
    /// port at its bottom, where the line leaves it, so they run before the bag's own fluid).</summary>
    public float Bolus { get; set; }
    /// <summary>Host only, the IV drip: ml run since debug mode last told.</summary>
    public float DrippedMl { get; set; }
    /// <summary>Host only, the IV drip: ml run since the bolus started running.</summary>
    public float DrippedTotal { get; set; }
    /// <summary>Host only, for debug mode: ml a syringe pushed out since its needle went where it is now.</summary>
    public float PushedMl { get; set; }
    /// <summary>The drugs in what a syringe pushed (see PushedMl).</summary>
    public List<string> PushedDrugs { get; } = [];
    /// <summary>Where a syringe pushed (see PushedMl): "the vein", "the IV bag"...</summary>
    public string PushedInto { get; set; } = "";
    /// <summary>Host only: what the tool has hold of in the patient, null for nothing.</summary>
    public ToolHold? Hold { get; set; }
    /// <summary>Host only: how the hand worked it from frame to frame.</summary>
    public ToolUse Use { get; } = new();
    /// <summary>A needle's running suture.</summary>
    public SutureState Suture { get; } = new();
    /// <summary>Host only: let go of and not landed yet (see ToolManager.CheckDrop()).</summary>
    public bool Falling { get; set; }
    /// <summary>A spreader set in a wound: it stays where it went in, held or not (see ToolManager.SyncSpread()).
    /// </summary>
    public bool InWound { get; set; }
    /// <summary>Lying on the patient (a retractor let go of), it rides this node (the site): breathing lifts and lowers
    /// it with the belly. Null for anything else.</summary>
    public Node3D? Riding { get; private set; }
    /// <summary>Where it lies, in the space of the node it rides.</summary>
    public Transform3D RidingPose { get; private set; }
    /// <summary>The model's box in the tool's own space (the grip at the origin).</summary>
    public Aabb Bounds { get; private set; }
    /// <summary>The band around a limb while this tool is wrapped around one (a tourniquet), see
    /// <see cref="WrapAround"/>.</summary>
    public MeshInstance3D? Band { get; private set; }
    public ToolAnimator Animator => _animator;

    /// <summary>A spreader's opening: meters between its tips (the wheel), on every peer.</summary>
    public float Spread
    {
        get => _spread;
        set
        {
            _spread = value;
            _animator.OpenTo(value, -Def.Length);
        }
    }

    public void Setup(int uid, ToolDef def)
    {
        Uid = uid;
        Def = def;
        Name = $"Tool{uid}_{def.Id}";
        Sterile = def.Sterile;
        Charges = def.Charges;
        Mass = def.IsHeavy ? 1.2f : 0.2f;
        CollisionLayer = ToolLayer;
        CollisionMask = 1 | 2 | 4 | ToolLayer;
        ContinuousCd = true;
        ContactMonitor = true;
        MaxContactsReported = 2;
        _model = ModelSlot.InstantiateTool(def, this);
        // The collision box wraps the model itself, so a bag or a flask rests on the tray instead of sinking into it.
        Bounds = ModelBounds();
        AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = Bounds.Size.Max(Vector3.One * 0.006f) },
            Position = Bounds.GetCenter(),
        });
        // A thin blade has next to no inertia about its length, so contacts would set it spinning and it would never
        // settle, rocking half into the tray. Treat every tool as at least a few centimeters thick for turning.
        var turning = Bounds.Size.Max(Vector3.One * MinTurningSize);
        Inertia = Mass / 12f * new Vector3(
            turning.Y * turning.Y + turning.Z * turning.Z,
            turning.X * turning.X + turning.Z * turning.Z,
            turning.X * turning.X + turning.Y * turning.Y);
        AngularDamp = 1f;
        _animator.Setup(_model, def.Action);
        if (def.Action == "spread")
        {
            _animator.OpenTo(_spread, -def.Length);
        }
        // Vials come full of their drug, the IV drip with a bag of plain fluid.
        if (def.Action == "vial")
        {
            Ml = def.Volume;
            Contents[def.Drug] = def.Volume * def.Concentration;
        }
        else if (def.Action == "drip")
        {
            Ml = DripFluid;
        }
        if (def.Volume > 0f)
        {
            Fill = Ml / def.Volume;
            ShowLiquid();
        }
        FreezeMode = FreezeModeEnum.Kinematic;
        Freeze = !Multiplayer.IsServer();
        if (def.Grip == "needle")
        {
            _animator.Animate(false, true, 0f);
        }
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        var active = false;
        // A suture needle is supplied already locked in its holder; it must not float between open jaws while idle.
        var closed = State == ToolState.Standing || Def.Grip == "needle";
        if (State == ToolState.Held && Surgery.Current?.Surgeons.GetValueOrDefault(Holder) is { } surgeon)
        {
            var hand = surgeon.Hands[Slot];
            active = ToolActions.InUse(Def.Action, hand.Lowered, hand.Trigger, hand.Level);
            closed = hand.Attached || Def.Grip == "needle";
        }
        _animator.Animate(active, closed, dt);
        if (Riding is not null && State == ToolState.Standing)
        {
            GlobalTransform = Riding.GlobalTransform * RidingPose;
        }
        if (_dig < 1f)
        {
            _dig = Mathf.Min(_dig + dt / DigTime, 1f);
            GlobalTransform = _digFrom.InterpolateWith(_digTo, Mathf.Ease(_dig, 0.4f));
        }
        if (Blood > 0f)
        {
            foreach (var (material, _) in _ownMaterials)
            {
                material.SetShaderParameter(ShaderParam.CoatInverse, new Projection(GlobalTransform.AffineInverse()));
            }
        }
    }

    /// <summary>The model's bounding box in the tool's own space. Falls back to a thin box along the tool if there's no
    /// model.</summary>
    private Aabb ModelBounds()
    {
        Aabb? bounds = null;
        foreach (var mesh in _model.FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>())
        {
            var box = GlobalTransform.AffineInverse() * mesh.GlobalTransform * mesh.GetAabb();
            bounds = bounds is { } merged ? merged.Merge(box) : box;
        }
        return bounds ?? new Aabb(new Vector3(-Def.Width * 0.5f, -Def.Width * 0.3f, -Def.Length), new Vector3(Def.Width, Def.Width * 0.6f, Def.Length));
    }

    /// <summary>Set in a cut, a spreader goes down into it to <paramref name="pose"/>, its points as deep as the cut
    /// goes, so the arms show how deep that is.</summary>
    public void DigTo(Transform3D pose)
    {
        _digFrom = GlobalTransform;
        _digTo = pose;
        _dig = 0f;
    }

    /// <summary>Lies on <paramref name="node"/> (the patient's site) from now on, where it is now, moving with it.
    /// </summary>
    public void Ride(Node3D node)
    {
        Riding = node;
        RidingPose = node.GlobalTransform.AffineInverse() * GlobalTransform;
    }

    public Vector3 TipPosition() => GlobalTransform * new Vector3(0, 0, -Def.Length);

    /// <summary>The middle of the tool, halfway to its tip.</summary>
    public Vector3 Middle() => GlobalTransform * new Vector3(0, 0, -Def.Length * 0.5f);

    /// <summary>The name the HUD shows. Syringes carry no drug name, only whether there's anything in them.</summary>
    public string Label() => Def.Action == "syringe" ? $"{Def.Name} ({(Ml > 0f ? "full" : "empty")})" : Def.Name;

    /// <summary>Germaphobe quirk: unsterile tools glow for this player only.</summary>
    public void ShowContamination(bool visibleToMe)
    {
        var glow = Sterile || !visibleToMe ? 0f : 1f;
        if (glow > 0f || _ownMaterials.Count > 0)
        {
            foreach (var (material, _) in OwnMaterials())
            {
                material.SetShaderParameter(ShaderParam.Contamination, glow);
            }
        }
    }

    /// <summary>Floor dirt shows as heavy grime on the tool for everyone.</summary>
    public void SetSoiled(bool value)
    {
        Soiled = value;
        if (value || _ownMaterials.Count > 0)
        {
            foreach (var (material, grime) in OwnMaterials())
            {
                material.SetShaderParameter(ShaderParam.Grime, value ? 0.95f : grime);
            }
        }
    }

    /// <summary>Blood on the working end. Gauze and swabs soak through along their whole length; instruments only near
    /// the tip.</summary>
    public void SetBlood(float amount)
    {
        Blood = amount;
        if (amount <= 0f && _ownMaterials.Count == 0)
        {
            return;
        }
        var soaks = Def.Action == "swab";
        foreach (var (material, _) in OwnMaterials())
        {
            material.SetShaderParameter(ShaderParam.Coat, amount);
            material.SetShaderParameter(ShaderParam.CoatLength, Def.Length);
            material.SetShaderParameter(ShaderParam.CoatReach, Def.Length * (soaks ? 1.2f : 0.12f + 0.3f * amount));
            material.SetShaderParameter(ShaderParam.CoatInverse, new Projection(GlobalTransform.AffineInverse()));
        }
    }

    /// <summary>Iodine soaked into it tints the whole tool (a cotton pad).</summary>
    public void ShowFill(float amount)
    {
        foreach (var (material, _) in OwnMaterials())
        {
            if (!_dryColors.TryGetValue(material, out var dry))
            {
                dry = material.GetShaderParameter("albedo").AsColor();
                _dryColors[material] = dry;
            }
            material.SetShaderParameter(ShaderParam.Albedo, dry.Lerp(IodineColor, Mathf.Min(amount * 2f, 1f)));
        }
    }

    /// <summary>
    /// A syringe, vial or dish shows exactly what's in it (ml, air, red), tinted toward blood by its share of blood.
    /// In a syringe the air sits at the needle end (it rises there, so a push lets it out first), the liquid behind it
    /// and the plunger right behind the liquid. A vial's "Level" stretches from its end, the kidney dish's "Pool"
    /// rises, the iodine dish's "Liquid" spreads.
    /// </summary>
    public void ShowLiquid()
    {
        var amount = Ml / Def.Volume;
        var level = LiquidPart("Level");
        if (level is not null)
        {
            level.Visible = amount > 0f;
            level.Scale = new Vector3(1f, 1f, Mathf.Max(amount, 0.001f));
            _levelRest ??= level.Position;
            level.Position = _levelRest.Value + new Vector3(0, 0, level.GetAabb().Size.Z * Air / Def.Volume);
            _animator.Fill = (Ml + Air) / Def.Volume;
        }
        var pool = LiquidPart("Pool");
        if (pool is not null)
        {
            // The dish widens upward: the top of the pool keeps to its wall at every level.
            var wide = Mathf.Lerp(0.88f, 1f, amount);
            pool.Visible = amount > 0f;
            pool.Scale = new Vector3(wide, Mathf.Max(amount, 0.001f), wide);
        }
        var liquid = LiquidPart("Liquid");
        if (liquid is not null)
        {
            liquid.Visible = amount > 0f;
            liquid.Scale = Vector3.One * Mathf.Lerp(0.6f, 1f, amount);
        }
        foreach (var part in new[] { level, pool, liquid })
        {
            if (part is not null)
            {
                TintLiquid(part);
            }
        }
    }

    /// <summary>The part of the model that shows the liquid, by name ("Level", "Pool", "Liquid").</summary>
    public MeshInstance3D? LiquidPart(string name) => _model.FindChild(name, true, false) as MeshInstance3D;

    /// <summary>Where a syringe's "Level" part rests when it holds no air.</summary>
    public Vector3? LevelRest => _levelRest;

    /// <summary>The liquid part gets its own material the first time it holds blood or iodine, then follows their share
    /// in it.</summary>
    private void TintLiquid(MeshInstance3D part)
    {
        if (part.GetSurfaceOverrideMaterial(0) is not ShaderMaterial material)
        {
            return;
        }
        if (!_liquidColors.TryGetValue(part, out var clear))
        {
            if (Red <= 0f && Iodine <= 0f)
            {
                return;
            }
            clear = material.GetShaderParameter("albedo").AsColor();
            _liquidColors[part] = clear;
            material = (ShaderMaterial)material.Duplicate();
            part.SetSurfaceOverrideMaterial(0, material);
        }
        // Blood and iodine are opaque: a little already colors the whole liquid, so the tint rises fast at first.
        var color = clear.Lerp(IodineColor, 1f - Mathf.Pow(1f - Iodine, 3f));
        material.SetShaderParameter(ShaderParam.Albedo, color.Lerp(BloodColor, 1f - Mathf.Pow(1f - Red, 3f)));
    }

    /// <summary>The tool your hand would pick up glows faintly (local player only).</summary>
    public void SetHighlight(bool on)
    {
        foreach (var (material, _) in OwnMaterials())
        {
            material.SetShaderParameter(ShaderParam.EmissionColor, on ? new Color(0.25f, 0.3f, 0.22f) : Colors.Black);
        }
    }

    /// <summary>Toon materials are shared between tools, so the first per-tool change swaps in copies of its own.
    /// </summary>
    private List<(ShaderMaterial Material, float Grime)> OwnMaterials()
    {
        if (_ownMaterials.Count == 0)
        {
            _ownMaterials = [.. ModelSlot.OwnMaterials(_model).Select(material => (material, material.GetShaderParameter("grime").AsSingle()))];
        }
        return _ownMaterials;
    }

    /// <summary>Wrapped around a limb (a tourniquet): the tool's own model gives way to a band around it, snug on the
    /// skin. The tool itself sits on top of the band, where a hand reaches for it to take it off again.</summary>
    public void WrapAround(Vector3 center, Vector3 axis, float radius)
    {
        Unwrap();
        var band = new MeshInstance3D
        {
            Name = "Band",
            Mesh = new TorusMesh { InnerRadius = radius, OuterRadius = radius + BandThickness, Rings = 32 },
            MaterialOverride = Materials.ToonShaded(Def.Color, 0.2f),
            TopLevel = true,
        };
        AddChild(band);
        Band = band;
        // A torus turns about its Y axis.
        var side = axis.Cross(Vector3.Up).Normalized();
        band.GlobalTransform = new Transform3D(new Basis(side, axis, side.Cross(axis)), center);
        var up = (Vector3.Up - axis * axis.Dot(Vector3.Up)).Normalized();
        var basis = side.LengthSquared() > 0.5f ? new Basis(side, up, side.Cross(up)) : GlobalBasis;
        GlobalTransform = new Transform3D(basis, center + up * (radius + BandThickness));
        _model.Visible = false;
    }

    public void Unwrap()
    {
        Band?.QueueFree();
        Band = null;
        _model.Visible = true;
    }

    public void SetState(ToolState state, int holder, int slot)
    {
        State = state;
        Holder = holder;
        Slot = slot;
        if (state != ToolState.Standing)
        {
            Unwrap();
            Riding = null;
        }
        Visible = state != ToolState.Consumed;
        // Left holding onto the patient (set, clamped, hooked), a tool doesn't keep hands or other tools off what's under
        // it.
        var onPatient = state == ToolState.Standing && !Def.Fixed;
        CollisionLayer = state is ToolState.Free or ToolState.Standing or ToolState.Inside && !onPatient ? ToolLayer : 0;
        // Only a tool moved by a hand is kinematic: frozen as one, a tool let go of would have the physics engine report
        // its last moving transform back over the one it was put down at (a retractor lying down would stand back up).
        FreezeMode = state is ToolState.Held or ToolState.Belt or ToolState.Carried ? FreezeModeEnum.Kinematic : FreezeModeEnum.Static;
        Freeze = !(state == ToolState.Free && Multiplayer.IsServer());
        if (state != ToolState.Held)
        {
            Use.Reset();
        }
    }
}
