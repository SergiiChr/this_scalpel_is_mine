namespace Scalpel.Tools;

/// <summary>
/// Moves a tool model's named parts from the holding hand's state, which every peer has. Jaws close while squeezed or
/// holding tissue, a syringe plunger sits behind its liquid and air, triggers squeeze, saw blades oscillate, flames and
/// glows light up in use. Part names come from tools/assetgen/instruments.py.
/// </summary>
public sealed class ToolAnimator
{
    private static readonly string[] GlowParts = ["Flame", "Glow"];
    private static readonly string[] PartNames = ["JawA", "JawB", "Plunger", "Trigger", "Blade", "Flame", "Glow", "Light"];
    private const float JawOpen = 0.12f;

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

    /// <summary>How far a syringe's plunger is pulled out (0..1 of its volume): its liquid and any air drawn in. The
    /// plunger moves the moment it changes, with the liquid, not a frame later.</summary>
    public float Fill
    {
        get => _fill;
        set
        {
            _fill = value;
            Pose("Plunger", Basis.Identity, new Vector3(0, 0, _plungerTravel * _fill));
        }
    }

    /// <summary>A spreader's jaws stand at this angle (radians), set by its wheel rather than squeezed, NaN for other
    /// jaws.</summary>
    public float Opening { get; private set; } = float.NaN;

    public void Setup(Node3D model, string action)
    {
        _saw = action == "saw";
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
}
