namespace Scalpel.World;

/// <summary>
/// The IV tubing: from the drip chamber on the stand, sagging, to the catheter in the patient's arm.
/// Exists once the line is in, on every peer. It follows the patient when they're turned.
/// Anyone who walks into it at full speed yanks it out (host decides, see Patient.PullIv()).
/// Crouch-walking is slow enough to step over it.
/// </summary>
public partial class IvLine : Node3D
{
    private const int Samples = 18;
    public const float Radius = 0.004f;
    private const float Sag = 0.55f;
    /// <summary>Only tubing hanging lower than this (meters above the floor) catches a walking surgeon's legs.</summary>
    internal const float TripHeight = 1.1f;
    private const float TripDistance = 0.22f;
    /// <summary>Faster than a crouch-walk, slower than a normal walk (<see cref="Surgeon.WalkSpeed"/>).</summary>
    private const float TripSpeed = 0.9f;

    private Node3D? _from;
    private Vector3 _fromPoint;
    private Node3D? _to;
    private MeshInstance3D _mesh = null!;
    private readonly Vector3[] _points = new Vector3[Samples];
    /// <summary>Where the tubing hangs (world), from the stand to the arm.</summary>
    internal IReadOnlyList<Vector3> Points => _points;
    private (Vector3 From, Vector3 To)? _lastEnds;
    /// <summary>The catheter, film and tape where the line goes into the arm. The tubing hangs from its end.</summary>
    private IvDressing? _dressing;
    /// <summary>The catheter, film and tape on the arm, null while the line isn't in.</summary>
    internal IvDressing? Dressing => _dressing;

    public bool IsAttached => _to is not null && Visible;

    public override void _Ready()
    {
        _mesh = new MeshInstance3D { MaterialOverride = IvDressing.TubingMaterial(), TopLevel = true };
        AddChild(_mesh);
        Visible = false;
    }

    /// <summary><paramref name="fromPoint"/> is local to <paramref name="from"/>, <paramref name="site"/> to
    /// <paramref name="to"/> (where the catheter goes in, see PatientBody.IvSite()), so the line follows whatever they're
    /// attached to. The dressing is taped on at the site, the tubing hangs from its end.</summary>
    public void Attach(Node3D from, Vector3 fromPoint, Node3D to, Transform3D site, float armRadius)
    {
        _from = from;
        _fromPoint = fromPoint;
        Visible = true;
        _dressing?.QueueFree();
        _dressing = new IvDressing { Name = "IvDressing" };
        to.AddChild(_dressing);
        _dressing.Transform = site;
        _dressing.Build(armRadius);
        _to = _dressing;
        // Built at once so trip checks never see the points of a previous attachment before the next frame.
        _lastEnds = null;
        Rebuild();
    }

    public void Detach()
    {
        _to = null;
        Visible = false;
        _dressing?.QueueFree();
        _dressing = null;
    }

    /// <summary>Host: the first surgeon walking fast through the low part of the tubing, or null.</summary>
    public Surgeon? TrippedBy(IEnumerable<Surgeon> surgeons) =>
        surgeons.FirstOrDefault(surgeon => surgeon.GroundSpeed >= TripSpeed && HangsLowAt(surgeon.GlobalPosition));

    /// <summary>The tubing hangs low enough to catch feet at <paramref name="feet"/>, give or take
    /// <paramref name="margin"/> (meters).</summary>
    public bool HangsLowAt(Vector3 feet, float margin = 0f) =>
        IsAttached && _points.Any(p =>
            p.Y < TripHeight && new Vector2(p.X - feet.X, p.Z - feet.Z).Length() < TripDistance + margin);

    public override void _Process(double delta)
    {
        if (IsAttached)
        {
            Rebuild();
        }
    }

    /// <summary>A quadratic curve pulled down in the middle, like tubing hanging under its own weight. Only rebuilt when
    /// an end moved.</summary>
    private void Rebuild()
    {
        var a = _from!.ToGlobal(_fromPoint);
        var b = _to!.ToGlobal(IvDressing.Exit);
        if (_lastEnds is var (lastA, lastB) && a.IsEqualApprox(lastA) && b.IsEqualApprox(lastB))
        {
            return;
        }
        _lastEnds = (a, b);
        var midpoint = (a + b) * 0.5f;
        // Keeps the normal sag, but a high catheter still leaves a low section a walking surgeon can catch. A quadratic
        // Bezier's midpoint is halfway between its control point and the ends' midpoint.
        var lowest = Mathf.Min(midpoint.Y - (Sag * 0.5f), TripHeight - 0.08f);
        var control = midpoint with { Y = Mathf.Max((2f * lowest) - midpoint.Y, 0.05f) };
        for (var i = 0; i < Samples; i++)
        {
            var t = (float)i / (Samples - 1);
            _points[i] = a.Lerp(control, t).Lerp(control.Lerp(b, t), t);
        }
        _mesh.Mesh = Shapes.Tube(_points, Radius, Radius, 6);
    }
}
