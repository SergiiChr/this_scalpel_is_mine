namespace Scalpel.Tools;

/// <summary>
/// Moves a tool model's named parts from the holding hand's state, which every peer has. Jaws close while squeezed or
/// holding tissue, a syringe plunger sits behind its liquid and air, triggers squeeze, saw blades oscillate, flames and
/// glows light up in use. Part names come from tools/assetgen/instruments.py.
/// A film bag (the IV bag, tools/assetgen/iv_bag.py) falls flat as it empties and spreads out lying down, through its
/// EmptyBag and RestingFlat blend shapes, and sways on its rig behind the hand as it's carried.
/// </summary>
public sealed class ToolAnimator
{
    private static readonly string[] GlowParts = ["Flame", "Glow"];
    private static readonly string[] PartNames = ["JawA", "JawB", "Plunger", "Trigger", "Blade", "Flame", "Glow", "Light"];
    private const float JawOpen = 0.12f;
    // StringNames made once: a string passed to Godot each frame would allocate one every time.
    private static readonly StringName EmptyBag = "EmptyBag";
    private static readonly StringName RestingFlat = "RestingFlat";
    /// <summary>How long a film bag takes to settle flat once it lies down, or fill out again once picked up (s).
    /// </summary>
    private const float FlattenTime = 0.3f;
    /// <summary>The film bag's bones that bend as it sways, from below the hanger down; each takes an equal share.
    /// </summary>
    private static readonly string[] SwayBones = ["Neck", "Upper", "Middle"];
    /// <summary>A hanging bag swings behind its hanger's acceleration like a pendulum: tilted by the acceleration over
    /// gravity, pulled back by a spring (1/s², 1/s) that settles it in about half a second, never past
    /// <see cref="SwayMost"/> (radians).</summary>
    private const float SwayStiffness = 150f;
    private const float SwayDamping = 12f;
    private const float SwayMost = 0.15f;
    /// <summary>Moved further than this in a frame, a film bag was put somewhere rather than carried there: it doesn't
    /// swing for it (meters).</summary>
    private const float SwayJump = 0.3f;

    private Dictionary<string, Node3D> _parts = [];
    private readonly Dictionary<string, Transform3D> _rest = [];
    private float _squeeze;
    private float _time;
    /// <summary>Static cutting tools also have a part named Blade; only saws move it independently of the handle.
    /// </summary>
    private bool _saw;
    /// <summary>How far the plunger moves from empty to full: the length of the full "Level" part.</summary>
    private float _plungerTravel;
    private float _fill;
    private Node3D _model = null!;
    /// <summary>The parts of a film bag with its blend shapes, none for other tools.</summary>
    private List<MeshInstance3D> _film = [];
    /// <summary>How far a film bag lying down is spread flat (0..1).</summary>
    private float _flat;
    /// <summary>How far the faces of a film bag come in when it's flat (meters): it's moved down by this so its lower
    /// face stays on what it lies on.</summary>
    private float _flatDrop;
    /// <summary>Which face of a lying film bag is up: 1 for +Y, -1 for -Y.</summary>
    private float _upSide = 1f;
    private Skeleton3D? _skeleton;
    private int[] _swayBones = [];
    /// <summary>Where the bag was last frame and how fast it was moving (world), NaN until it's first seen.</summary>
    private Vector3 _swayAt = new(float.NaN, 0f, 0f);
    private Vector3 _swayVelocity;
    /// <summary>How far the bag hangs tilted about its X and Y axes (radians), and how fast that changes.</summary>
    private Vector2 _tilt;
    private Vector2 _tiltSpeed;
    private Vector2 _shownTilt;

    /// <summary>How far a syringe's plunger is pulled out (0..1 of its volume): its liquid and any air drawn in. The
    /// plunger moves the moment it changes, with the liquid, not a frame later.</summary>
    public float Fill
    {
        get => _fill;
        set
        {
            _fill = value;
            Pose("Plunger", Basis.Identity, new Vector3(0, 0, _plungerTravel * _fill));
            Blend(EmptyBag, 1f - _fill);
        }
    }

    /// <summary>A spreader's jaws stand at this angle (radians), set by its wheel rather than squeezed, NaN for other
    /// jaws.</summary>
    public float Opening { get; private set; } = float.NaN;

    public void Setup(Node3D model, string action)
    {
        _saw = action == "saw";
        _model = model;
        _film = [.. model.FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>()
            .Where(mesh => mesh.FindBlendShapeByName(EmptyBag) >= 0)];
        if (_film.FirstOrDefault(mesh => mesh.Name == "Bag") is { } bag)
        {
            _flatDrop = FlatDrop(bag);
        }
        if (_film.Count > 0 && model.FindChild("Skeleton3D", true, false) is Skeleton3D skeleton)
        {
            _swayBones = [.. SwayBones.Select(bone => skeleton.FindBone(bone)).Where(bone => bone >= 0)];
            _skeleton = _swayBones.Length > 0 ? skeleton : null;
        }
        _parts = ModelSlot.Parts(model, PartNames);
        foreach (var (name, part) in _parts)
        {
            _rest[name] = part.Transform;
        }
        if (model.FindChild("Level", true, false) is MeshInstance3D level)
        {
            _plungerTravel = level.GetAabb().Size.Z;
        }
        AnimateParts(false, false);
    }

    /// <summary>Stands a spreader's jaws open so its tips, at <paramref name="tipZ"/> along the tool, are
    /// <paramref name="spread"/> meters apart. Closed (ToolActions.SpreadRange.X) they rest as modelled; each jaw swings
    /// about its hinge, the model's part origin.</summary>
    public void OpenTo(float spread, float tipZ)
    {
        var hinge = _rest.GetValueOrDefault("JawA", Transform3D.Identity);
        var reach = hinge.Origin.Z - tipZ;
        var restHalf = ToolActions.SpreadRange.X * 0.5f;
        Opening = Mathf.Asin(Mathf.Clamp(spread * 0.5f / new Vector2(restHalf, reach).Length(), -1f, 1f)) - Mathf.Atan2(restHalf, reach);
        AnimateParts(false, false);
    }

    /// <summary>A film bag lying on something (<paramref name="upSide"/>: 1 with its +Y face up, -1 with -Y up, 0 when
    /// it isn't lying) spreads flat over <see cref="FlattenTime"/>, and fills out again once it's picked up. Not lying,
    /// it sways.</summary>
    public void Rest(float upSide, float delta)
    {
        if (_film.Count == 0)
        {
            return;
        }
        if (upSide != 0f)
        {
            _upSide = upSide;
        }
        var flat = Mathf.MoveToward(_flat, upSide != 0f ? 1f : 0f, delta / FlattenTime);
        if (flat != _flat)
        {
            _flat = flat;
            Blend(RestingFlat, _flat);
        }
        var lowered = new Vector3(0f, -_upSide * _flatDrop * _flat, 0f);
        if (_model.Position != lowered)
        {
            _model.Position = lowered;
        }
        Sway(upSide != 0f, delta);
    }

    /// <summary>Takes a film bag's sway out at once, for a tool put somewhere rather than moved there.</summary>
    public void Settle()
    {
        _swayAt = new Vector3(float.NaN, 0f, 0f);
        _swayVelocity = Vector3.Zero;
        _tilt = _tiltSpeed = Vector2.Zero;
    }

    /// <summary>A film bag swings behind its acceleration, bending its bones; lying down it lies still.</summary>
    private void Sway(bool lying, float delta)
    {
        if (_skeleton is null || delta <= 0f)
        {
            return;
        }
        var at = _model.GlobalPosition;
        if (lying || float.IsNaN(_swayAt.X) || at.DistanceTo(_swayAt) > SwayJump)
        {
            Settle();
            _swayAt = at;
        }
        else
        {
            var velocity = (at - _swayAt) / delta;
            // In the bag's own space, hanging down -Z: pushed along X its bottom swings back about Y, along Y about X.
            var push = _model.GlobalBasis.Inverse() * ((velocity - _swayVelocity) / delta);
            var target = (new Vector2(-push.Y, push.X) / 9.8f).LimitLength(SwayMost);
            _tiltSpeed += ((target - _tilt) * SwayStiffness - (_tiltSpeed * SwayDamping)) * delta;
            _tilt = (_tilt + (_tiltSpeed * delta)).LimitLength(SwayMost);
            _swayAt = at;
            _swayVelocity = velocity;
        }
        // Posing the bones redraws the skin: only when the tilt changed, not every frame a bag hangs still.
        if (_tilt == _shownTilt)
        {
            return;
        }
        _shownTilt = _tilt;
        var share = Quaternion.FromEuler(new Vector3(_tilt.X, _tilt.Y, 0f) / _swayBones.Length);
        foreach (var bone in _swayBones)
        {
            _skeleton.SetBonePoseRotation(bone, _skeleton.GetBoneRest(bone).Basis.GetRotationQuaternion() * share);
        }
    }

    /// <summary><paramref name="active"/>: the tool is being used right now. <paramref name="closed"/>: jaws clamped on
    /// something.</summary>
    public void Animate(bool active, bool closed, float delta)
    {
        if (_parts.Count == 0)
        {
            return;
        }
        _time += delta;
        _squeeze = Mathf.MoveToward(_squeeze, active ? 1f : 0f, delta * 6f);
        AnimateParts(active, closed);
    }

    private void AnimateParts(bool active, bool closed)
    {
        var jaw = !float.IsNaN(Opening) ? Opening : closed || active ? 0f : JawOpen;
        Pose("JawA", new Basis(Vector3.Up, jaw), Vector3.Zero);
        Pose("JawB", new Basis(Vector3.Up, -jaw), Vector3.Zero);
        Pose("Plunger", Basis.Identity, new Vector3(0, 0, _plungerTravel * _fill));
        Pose("Trigger", new Basis(Vector3.Right, -0.35f * _squeeze), Vector3.Zero);
        Pose("Blade", Basis.Identity, new Vector3(active && _saw ? Mathf.Sin(_time * 70f) * 0.004f : 0f, 0, 0));
        var flicker = 1f + Mathf.Sin(_time * 31f) * 0.15f + Mathf.Sin(_time * 53f) * 0.1f;
        foreach (var glow in GlowParts)
        {
            if (_parts.TryGetValue(glow, out var part))
            {
                part.Visible = active;
                Pose(glow, Basis.FromScale(new Vector3(1f, 1f, flicker)), Vector3.Zero);
            }
        }
        if (_parts.TryGetValue("Light", out var light))
        {
            light.Visible = active && Mathf.PosMod(_time * 4f, 1f) < 0.6f;
        }
    }

    private void Pose(string partName, Basis rotation, Vector3 offset)
    {
        if (_parts.TryGetValue(partName, out var node))
        {
            var rest = _rest[partName];
            node.Transform = new Transform3D(rest.Basis * rotation, rest.Origin + offset);
        }
    }

    private void Blend(StringName shape, float weight)
    {
        foreach (var mesh in _film)
        {
            mesh.SetBlendShapeValue(mesh.FindBlendShapeByName(shape), weight);
        }
    }

    /// <summary>How far the film's lower face rises when the bag lies flat: the blend shape holds where each vertex
    /// goes.</summary>
    private static float FlatDrop(MeshInstance3D bag)
    {
        var mesh = (ArrayMesh)bag.Mesh;
        var rest = mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var flat = mesh.SurfaceGetBlendShapeArrays(0)[bag.FindBlendShapeByName(RestingFlat)][(int)Mesh.ArrayType.Vertex]
            .AsVector3Array();
        return rest.Max(vertex => vertex.Y) - flat.Max(vertex => vertex.Y);
    }
}
