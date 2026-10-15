namespace Scalpel.World;

/// <summary>Where the IV tubing's far end is (the near one always hangs from the stand's drip chamber).</summary>
public enum IvLineEnd {
    /// <summary>Hung on the stand, bare, waiting for a catheter.</summary>
    Clipped,
    /// <summary>On a catheter that isn't in the arm yet, usually in a surgeon's hand.</summary>
    Catheter,
    /// <summary>On the catheter taped into the patient's arm.</summary>
    Arm,
    /// <summary>Hanging free, with the catheter it was ripped out with or bare.</summary>
    Loose,
}

/// <summary>
/// The IV tubing: loose, flexible tubing from the drip chamber on the stand to its far end (<see cref="IvLineEnd"/>).
/// A rope of points that gravity pulls down, kept a fixed length apart and pushed out of whatever they meet: furniture
/// and the floor (their boxes), the patient's skin and drape (rays from where each point was) and surgeons (a capsule
/// around the body, a ball around each hand). Every peer simulates its own; the host decides when it pulls out
/// (<see cref="YankedBy"/>, Patient.PullIv()).
/// <para>
/// A surgeon who catches the tubing and walks on stretches it past its length, as it's trapped against them: held
/// stretched for a moment, the catheter rips out of the arm. Brushing past only pushes the slack around.
/// </para>
/// It sleeps once it hangs still, until an end moves or a surgeon near it moves.
/// </summary>
public partial class IvLine : Node3D {
    internal const int Samples = 48;
    public const float Radius = 0.004f;
    /// <summary>The tubing's length (meters): slack enough to reach either arm from the stand.</summary>
    internal const float Length = 2.4f;
    private const float Segment = Length / (Samples - 1);
    private const int Iterations = 8;
    /// <summary>Two points apart, the tubing's points stay at least this far apart (a bend of at most about 60 degrees
    /// at each point), pushed apart this much each time round.</summary>
    private const float MinSpan = Segment * 1.7f;
    private const float Stiffness = 0.3f;
    private const int Sides = 6;
    /// <summary>The share of its speed a point keeps from one frame to the next: air and the tubing's own stiffness.
    /// </summary>
    private const float Damping = 0.97f;
    /// <summary>The share of its speed a point lying on something loses each frame.</summary>
    private const float Friction = 0.5f;
    /// <summary>A surgeon as the tubing meets them: an upright capsule this wide around their feet, this tall.</summary>
    internal const float BodyRadius = 0.17f;
    internal const float BodyHeight = 1.6f;
    private const float HandRadius = 0.04f;
    /// <summary>Stretched this many times its length by a surgeon for <see cref="YankTime"/>, the far end lets go.
    /// </summary>
    internal const float YankStretch = 1.15f;
    private const float YankTime = 0.1f;
    /// <summary>A surgeon who pushed the tubing this many physics frames ago still has it caught.</summary>
    private const ulong HookFrames = 15;
    /// <summary>It sleeps once no point moved more than this (meters) in a frame for <see cref="SleepTime"/>.
    /// </summary>
    private const float Still = 0.0001f;
    private const float SleepTime = 0.5f;
    /// <summary>Frames simulated at once when the far end jumps somewhere new, so it starts out hanging.</summary>
    private const int SettleFrames = 180;
    /// <summary>Where the bare end hangs on the stand, across from the drip chamber, local to the stand.</summary>
    internal static readonly Vector3 ClipPoint = new(-0.08f, 1.45f, 0f);
    private const uint SkinMask = PatientBody.SurfaceLayer | Drape.DrapeLayer;
    private static readonly Vector3 Gravity = new(0f, -9.8f, 0f);

    private Node3D _stand = null!;
    private Vector3 _dripPoint;
    private MeshInstance3D _mesh = null!;
    private readonly ArrayMesh _tube = new();
    private readonly Vector3[] _points = new Vector3[Samples];
    private readonly Vector3[] _previous = new Vector3[Samples];
    /// <summary>The points lying on something this frame, which keeps them from sliding.</summary>
    private readonly bool[] _resting = new bool[Samples];
    private readonly Vector3[] _vertices = new Vector3[Samples * Sides];
    private readonly Vector3[] _normals = new Vector3[Samples * Sides];
    private readonly Godot.Collections.Array _arrays = [];
    /// <summary>Solid boxes in the room the tubing could reach, found once (furniture, walls, the floor).</summary>
    private readonly List<(CollisionShape3D Shape, Vector3 Half)> _boxes = [];
    /// <summary>This frame's boxes near the tubing.</summary>
    private readonly List<NearBox> _near = [];
    /// <summary>The patient's body part boxes, each in its body's own space.</summary>
    private readonly List<(CollisionObject3D Body, Aabb Local)> _skins = [];
    private readonly List<(Vector3 From, Vector3 To, Surgeon Surgeon)> _bodies = [];
    private readonly List<(Vector3 At, Surgeon Surgeon)> _hands = [];
    private Node3D? _endCatheter;
    private IvDressing? _dressing;
    private IvTear? _tear;
    private Vector3 _lastStart;
    private Vector3? _lastEnd;
    private float _stillFor;
    private bool _asleep;
    private Aabb _bounds;
    /// <summary>Where the surgeons and hands near the tubing were (summed) when it fell asleep.</summary>
    private Vector3 _nearWhenAsleep;
    private Surgeon? _caughtBy;
    private ulong _caughtFrame;
    private float _stretchedFor;

    /// <summary>Where the tubing is (world), from the stand to its far end.</summary>
    internal IReadOnlyList<Vector3> Points => _points;
    public IvLineEnd End { get; private set; } = IvLineEnd.Clipped;
    /// <summary>The catheter the far end is on while <see cref="IvLineEnd.Catheter"/>.</summary>
    public SurgicalTool? Catheter { get; private set; }
    /// <summary>A loose end still has the catheter it was ripped out with.</summary>
    public bool HasCatheter => _endCatheter?.Visible == true;
    /// <summary>The catheter, film and tape on the arm, null while no catheter is in.</summary>
    internal IvDressing? Dressing => _dressing;
    /// <summary>The torn skin where a catheter was ripped out, null until one was.</summary>
    internal IvTear? Tear => _tear;
    /// <summary>How many times its length the tubing is stretched (1 when it's just taut or slack).</summary>
    public float Stretch { get; private set; } = 1f;
    /// <summary>How far the point that moved most moved in the last simulated frame (meters).</summary>
    internal float Moved { get; private set; }
    /// <summary>Hanging still, not simulated until something moves it.</summary>
    internal bool Asleep => _asleep;

    /// <summary>Where the bare end clips onto the stand (world).</summary>
    internal Vector3 ClipAt => _stand.ToGlobal(ClipPoint);

    /// <summary>The line runs into the patient's arm.</summary>
    public bool IsAttached => End == IvLineEnd.Arm;

    public override void _Ready() {
        // After surgeons have moved this frame, so it's pushed out of where they are, not where they were.
        ProcessPhysicsPriority = 100;
        _mesh = new MeshInstance3D { Mesh = _tube, MaterialOverride = IvDressing.TubingMaterial(), TopLevel = true };
        AddChild(_mesh);
        _arrays.Resize((int)Mesh.ArrayType.Max);
        _arrays[(int)Mesh.ArrayType.Index] = TubeIndices();
        if (Db.Tool("iv_catheter") is { } catheter) {
            _endCatheter = ModelSlot.InstantiateTool(catheter, this);
            _endCatheter.TopLevel = true;
            _endCatheter.Visible = false;
        }
    }

    /// <summary>Hangs the tubing from <paramref name="dripPoint"/> (local to <paramref name="stand"/>), its bare end
    /// hung on the stand across from it.</summary>
    public void Hang(Node3D stand, Vector3 dripPoint) {
        _stand = stand;
        _dripPoint = dripPoint;
        FindBoxes();
        // Laid out hanging already: down from the drip chamber, round a half circle and up to the clip.
        var start = stand.ToGlobal(dripPoint);
        var clip = ClipAt;
        var across = (clip - start) with { Y = 0f };
        var radius = across.Length() * 0.5f;
        var down = (Length + start.Y - clip.Y - (Mathf.Pi * radius)) * 0.5f;
        var bottom = start with { Y = start.Y - down } + (across * 0.5f);
        for (var i = 0; i < Samples; i++) {
            var along = i * Segment;
            var round = (along - down) / radius;
            _points[i] = along < down ? start + (Vector3.Down * along)
                : round < Mathf.Pi ? bottom - (across.Normalized() * radius * Mathf.Cos(round)) + (Vector3.Down * radius * Mathf.Sin(round))
                : clip + (Vector3.Down * (Length - along));
            _previous[i] = _points[i];
        }
        End = IvLineEnd.Clipped;
        Settle(false);
    }

    /// <summary>A catheter went into the arm at <paramref name="site"/> (local to <paramref name="to"/>, see
    /// PatientBody.IvSite()), taped on with a dressing. <paramref name="connected"/>: the tubing was on it, and now hangs
    /// from the dressing; otherwise it stays where it was.</summary>
    public void Attach(Node3D to, Transform3D site, float armRadius, bool connected) {
        _dressing?.QueueFree();
        _dressing = new IvDressing { Name = "IvDressing" };
        to.AddChild(_dressing);
        _dressing.Transform = site;
        _dressing.Build(armRadius, connected);
        if (!connected) {
            return;
        }
        // From a catheter in hand the tubing is already there; from anywhere else (a pre-op line) it settles first.
        var jumps = End != IvLineEnd.Catheter;
        End = IvLineEnd.Arm;
        Catheter = null;
        ShowEndCatheter(false);
        if (jumps) {
            Settle(true);
        }
        Wake();
    }

    /// <summary>The far end goes onto <paramref name="catheter"/> (a used one on a loose end comes off).</summary>
    public void HoldOn(SurgicalTool catheter) {
        End = IvLineEnd.Catheter;
        Catheter = catheter;
        ShowEndCatheter(false);
        Wake();
    }

    /// <summary>The catheter comes out of the arm. <paramref name="torn"/>: ripped out by the tubing, which keeps it on
    /// its end, tearing the skin where it went in. A catheter that wasn't on the tubing leaves it where it is.</summary>
    public void PullOut(bool torn) {
        if (_dressing is not null) {
            if (torn) {
                _tear?.QueueFree();
                _tear = new IvTear { Name = "IvTear" };
                _dressing.GetParent().AddChild(_tear);
                _tear.Transform = _dressing.Transform;
                _tear.Build(_dressing.ArmRadius);
            }
            _dressing.QueueFree();
            _dressing = null;
        }
        if (End != IvLineEnd.Arm) {
            return;
        }
        End = IvLineEnd.Loose;
        Catheter = null;
        ShowEndCatheter(torn);
        Wake();
    }

    /// <summary>The far end slips off the catheter it was on and hangs bare.</summary>
    public void SlipOff() {
        End = IvLineEnd.Loose;
        Catheter = null;
        ShowEndCatheter(false);
        Wake();
    }

    /// <summary>Host: the surgeon who caught the tubing and stretched it until it pulls out of the arm, or null.
    /// </summary>
    public Surgeon? YankedBy() =>
        End == IvLineEnd.Arm && _stretchedFor >= YankTime && Caught() ? _caughtBy : null;

    /// <summary>Host: the tubing on a catheter that's carried further from the stand than it reaches.</summary>
    public bool SlipsOff => End == IvLineEnd.Catheter && _stretchedFor >= YankTime;

    private bool Caught() => _caughtBy is not null && Engine.GetPhysicsFrames() - _caughtFrame < HookFrames;

    public override void _PhysicsProcess(double delta) {
        if (!IsInstanceValid(_stand)) {
            return;
        }
        if (End == IvLineEnd.Catheter && (Catheter is null || !IsInstanceValid(Catheter)
            || Catheter.State == ToolState.Consumed)) {
            SlipOff();
        }
        var start = _stand.ToGlobal(_dripPoint);
        var end = EndAnchor();
        FindSurgeons();
        if (_asleep && start.IsEqualApprox(_lastStart) && Same(end, _lastEnd) && !SurgeonMovedNear()) {
            return;
        }
        _asleep = false;
        Step(start, end, (float)delta, true);
        PlaceEndCatheter();
        BuildMesh();
    }

    private static bool Same(Vector3? a, Vector3? b) =>
        a is null ? b is null : b is { } other && a.Value.IsEqualApprox(other);

    /// <summary>Where the far end is held (world), null while it hangs free.</summary>
    private Vector3? EndAnchor() => End switch {
        IvLineEnd.Clipped => ClipAt,
        IvLineEnd.Catheter when Catheter is not null => Catheter.GlobalTransform * new Vector3(0f, 0f, Catheter.Bounds.End.Z),
        IvLineEnd.Arm when _dressing is not null => _dressing.ToGlobal(IvDressing.Exit),
        _ => null,
    };

    /// <summary>One frame of the tubing: gravity, its length, then out of whatever it went into.</summary>
    private void Step(Vector3 start, Vector3? end, float dt, bool rays) {
        _lastStart = start;
        _lastEnd = end;
        var last = end is null ? Samples : Samples - 1;
        for (var i = 1; i < last; i++) {
            var velocity = (_points[i] - _previous[i]) * Damping;
            _previous[i] = _points[i];
            _points[i] += velocity + (Gravity * dt * dt);
        }
        _previous[0] = _points[0];
        _points[0] = start;
        if (end is { } held) {
            _previous[^1] = _points[^1];
            _points[^1] = held;
        }
        NearBoxes();
        var caught = false;
        for (var iteration = 0; iteration < Iterations; iteration++) {
            // Back and forth in turn, so neither end's pull wins.
            for (var k = 0; k < Samples - 1; k++) {
                var i = iteration % 2 == 0 ? k : Samples - 2 - k;
                var a = _points[i];
                var b = _points[i + 1];
                var gap = b - a;
                var length = gap.Length();
                if (length < 1e-6f) {
                    continue;
                }
                var fix = gap * ((length - Segment) / length);
                var aFree = i > 0;
                var bFree = i + 1 < last;
                if (aFree && bFree) {
                    _points[i] = a + (fix * 0.5f);
                    _points[i + 1] = b - (fix * 0.5f);
                } else if (aFree) {
                    _points[i] = a + fix;
                } else if (bFree) {
                    _points[i + 1] = b - fix;
                }
            }
            // The tube's own stiffness: two points apart stay apart, so it bends in curves, not sharp folds.
            for (var i = 0; i < Samples - 2; i++) {
                var gap = _points[i + 2] - _points[i];
                var length = gap.Length();
                if (length >= MinSpan || length < 1e-6f) {
                    continue;
                }
                var fix = gap * ((length - MinSpan) / length * Stiffness * 0.5f);
                if (i > 0) {
                    _points[i] += fix;
                }
                if (i + 2 < last) {
                    _points[i + 2] -= fix;
                }
            }
            // Every other time round is enough to keep it out, as long as the last one does.
            if (iteration % 2 == 1) {
                for (var i = 1; i < last; i++) {
                    caught |= Collide(ref _points[i], ref _resting[i]);
                }
            }
        }
        if (rays) {
            var skin = SkinReach();
            for (var i = 1; i < last; i++) {
                var moved = _points[i] - _previous[i];
                if (moved.LengthSquared() < 1e-10f || !skin.HasPoint(_points[i])) {
                    continue;
                }
                var reach = _points[i] + (moved.Normalized() * Radius);
                if (Rays.Cast(this, _previous[i], reach, SkinMask) is { } hit) {
                    _points[i] = hit.Position + (hit.Normal * Radius);
                    _resting[i] = true;
                }
            }
        }
        // What lies on something doesn't slide off on its own.
        for (var i = 1; i < last; i++) {
            if (_resting[i]) {
                _previous[i] = _previous[i].Lerp(_points[i], Friction);
                _resting[i] = false;
            }
        }
        if (caught) {
            _caughtFrame = Engine.GetPhysicsFrames();
        }
        var stretched = 0f;
        var most = 0f;
        for (var i = 0; i < Samples - 1; i++) {
            stretched += _points[i].DistanceTo(_points[i + 1]);
            most = Mathf.Max(most, _points[i + 1].DistanceSquaredTo(_previous[i + 1]));
        }
        Stretch = stretched / Length;
        // On a catheter it lets go once carried further from the stand than it reaches; in the arm, once stretched.
        var tooFar = End == IvLineEnd.Catheter ? _points[0].DistanceTo(_points[^1]) > Length : Stretch > YankStretch;
        _stretchedFor = tooFar ? _stretchedFor + dt : 0f;
        Moved = Mathf.Sqrt(most);
        _stillFor = most < Still * Still ? _stillFor + dt : 0f;
        if (_stillFor >= SleepTime) {
            _asleep = true;
            _bounds = new Aabb(_points[0], Vector3.Zero);
            foreach (var point in _points) {
                _bounds = _bounds.Expand(point);
            }
            _nearWhenAsleep = SurgeonsNear();
        }
    }

    /// <summary>Pushes a point out of the boxes, surgeons and hands it went into. True when a surgeon pushed it;
    /// <paramref name="resting"/> set when it lies on the floor or furniture.</summary>
    private bool Collide(ref Vector3 point, ref bool resting) {
        if (point.Y < Radius) {
            point.Y = Radius;
            resting = true;
        }
        foreach (var box in _near) {
            if (!box.Reach.HasPoint(point)) {
                continue;
            }
            var half = box.Half;
            var local = box.ToBox * point;
            var outside = local.Clamp(-half, half);
            var away = local - outside;
            var distance = away.Length();
            if (distance >= Radius) {
                continue;
            }
            if (distance > 1e-6f) {
                local = outside + (away * (Radius / distance));
            } else {
                // Inside: out through the nearest face.
                var depth = half - local.Abs();
                var axis = depth.X < depth.Y ? (depth.X < depth.Z ? 0 : 2) : (depth.Y < depth.Z ? 1 : 2);
                local[axis] = (local[axis] < 0f ? -1f : 1f) * (half[axis] + Radius);
            }
            point = box.ToWorld * local;
            resting = true;
        }
        var caught = false;
        foreach (var (bottom, top, surgeon) in _bodies) {
            // Upright: the nearest point on its axis is level with the point.
            caught |= PushOut(ref point, bottom with { Y = Mathf.Clamp(point.Y, bottom.Y, top.Y) }, BodyRadius + Radius,
                surgeon);
        }
        foreach (var (at, surgeon) in _hands) {
            caught |= PushOut(ref point, at, HandRadius + Radius, surgeon);
        }
        return caught;
    }

    private bool PushOut(ref Vector3 point, Vector3 center, float radius, Surgeon surgeon) {
        var away = point - center;
        var distance = away.Length();
        if (distance >= radius) {
            return false;
        }
        point = center + (distance > 1e-6f ? away * (radius / distance) : Vector3.Up * radius);
        _caughtBy = surgeon;
        return true;
    }

    /// <summary>Surgeons' bodies and the hands that aren't holding the tubing's own catheter, this frame.</summary>
    private void FindSurgeons() {
        _bodies.Clear();
        _hands.Clear();
        if (Surgery.Current is not { } surgery) {
            return;
        }
        foreach (var surgeon in surgery.Surgeons.Values) {
            var feet = surgeon.GlobalPosition;
            _bodies.Add((feet + (Vector3.Up * BodyRadius), feet + (Vector3.Up * (BodyHeight - BodyRadius)), surgeon));
            for (var i = 0; i < surgeon.Hands.Length; i++) {
                var holding = Catheter is { State: ToolState.Held } held && held.Holder == surgeon.PeerId && held.Slot == i;
                if (!holding) {
                    _hands.Add((surgeon.Hands[i].GlobalPosition, surgeon));
                }
            }
        }
    }

    /// <summary>A surgeon's body or hand close enough to the hanging tubing to touch it moved since it fell asleep.
    /// </summary>
    private bool SurgeonMovedNear() => !SurgeonsNear().IsEqualApprox(_nearWhenAsleep);

    /// <summary>Where the surgeons' bodies and hands close enough to the hanging tubing to touch it are, summed up: it
    /// changes when any of them moves.</summary>
    private Vector3 SurgeonsNear() {
        var near = _bounds.Grow(BodyRadius + 0.05f);
        var where = Vector3.Zero;
        foreach (var (bottom, top, _) in _bodies) {
            if (near.Intersects(new Aabb(bottom, top - bottom).Abs())) {
                where += bottom;
            }
        }
        foreach (var (at, _) in _hands) {
            if (near.HasPoint(at)) {
                where += at;
            }
        }
        return where;
    }

    private void Wake() {
        _asleep = false;
        _stillFor = 0f;
    }

    /// <summary>Runs <see cref="SettleFrames"/> frames at once, so a far end that jumped starts out hanging. Rays only
    /// for the last of them: the skin is only met once it's near. <paramref name="layOut"/>: first lays the tubing out
    /// straight to the far end.</summary>
    private void Settle(bool layOut) {
        var start = _stand.ToGlobal(_dripPoint);
        var end = EndAnchor();
        if (layOut && end is { } held) {
            // Laid out straight first, so no point is left on the wrong side of the arm or the table.
            for (var i = 0; i < Samples; i++) {
                _points[i] = start.Lerp(held, (float)i / (Samples - 1)) + (Vector3.Up * 0.3f * Mathf.Sin(Mathf.Pi * i / (Samples - 1)));
                _previous[i] = _points[i];
            }
        }
        FindSurgeons();
        const float Dt = 1f / 60f;
        for (var frame = 0; frame < SettleFrames; frame++) {
            Step(start, end, Dt, frame >= SettleFrames - 30);
        }
        Wake();
        PlaceEndCatheter();
        BuildMesh();
    }

    /// <summary>The room's solid boxes (layer 1) within the tubing's reach of the stand.</summary>
    private void FindBoxes() {
        _boxes.Clear();
        var drip = _stand.ToGlobal(_dripPoint);
        foreach (var shape in GetParent().FindChildren("*", nameof(CollisionShape3D), true, false).Cast<CollisionShape3D>()) {
            if (shape.Shape is not BoxShape3D box || shape.GetParent() is not CollisionObject3D { CollisionLayer: var layer }
                || (layer & 1) == 0) {
                continue;
            }
            var half = box.Size * 0.5f;
            if ((shape.GlobalTransform * new Aabb(-half, box.Size)).Grow(Length).HasPoint(drip)) {
                _boxes.Add((shape, half));
            }
        }
    }

    /// <summary>Where the patient's skin and drape are (world, a little grown), to cast rays only near them: around the
    /// boxes the patient's body parts are (PatientBody.BuildColliders()). Empty without a patient.</summary>
    private Aabb SkinReach() {
        if (_skins.Count == 0 || !IsInstanceValid(_skins[0].Body)) {
            _skins.Clear();
            if (Surgery.Current?.Patient is { } patient) {
                foreach (var shape in patient.FindChildren("*", nameof(CollisionShape3D), true, false).Cast<CollisionShape3D>()) {
                    if (shape.Shape is BoxShape3D box && shape.GetParent() is CollisionObject3D body
                        && (body.CollisionLayer & PatientBody.PatientLayer) != 0) {
                        _skins.Add((body, shape.Transform * new Aabb(box.Size * -0.5f, box.Size)));
                    }
                }
            }
        }
        if (_skins.Count == 0) {
            return default;
        }
        var reach = _skins[0].Body.GlobalTransform * _skins[0].Local;
        foreach (var (body, local) in _skins) {
            reach = reach.Merge(body.GlobalTransform * local);
        }
        return reach.Grow(0.1f);
    }

    /// <summary>The boxes near the tubing this frame, with their transforms worked out once.</summary>
    private void NearBoxes() {
        _near.Clear();
        var reach = new Aabb(_points[0], Vector3.Zero);
        foreach (var point in _points) {
            reach = reach.Expand(point);
        }
        reach = reach.Grow(0.3f);
        foreach (var (shape, half) in _boxes) {
            var toWorld = shape.GlobalTransform;
            var bounds = toWorld * new Aabb(-half, half * 2f);
            if (bounds.Intersects(reach)) {
                _near.Add(new NearBox(toWorld.AffineInverse(), toWorld, half, bounds.Grow(Radius)));
            }
        }
    }

    private void ShowEndCatheter(bool shown) {
        if (_endCatheter is not null) {
            _endCatheter.Visible = shown;
        }
    }

    /// <summary>A catheter on a loose end hangs off it, its back on the tubing, its needle pointing on.</summary>
    private void PlaceEndCatheter() {
        if (_endCatheter is not { Visible: true }) {
            return;
        }
        var back = (_points[^2] - _points[^1]).Normalized();
        var side = back.Cross(Vector3.Up);
        if (side.LengthSquared() < 1e-4f) {
            side = Vector3.Right;
        }
        var basis = new Basis(side.Normalized(), back.Cross(side).Normalized(), back);
        _endCatheter.GlobalTransform = new Transform3D(basis.Orthonormalized(), _points[^1]);
    }

    /// <summary>A solid box near the tubing this frame: to its own space and back, its half size and what it reaches in
    /// the world, which most points are outside of.</summary>
    private readonly record struct NearBox(Transform3D ToBox, Transform3D ToWorld, Vector3 Half, Aabb Reach);

    /// <summary>Refills the tube's mesh around the points: rings of <see cref="Sides"/> vertices, each normal pointing
    /// straight out of the tube. No garbage: it's rebuilt every frame the tubing moves.</summary>
    private void BuildMesh() {
        for (var i = 0; i < Samples; i++) {
            var along = (_points[Math.Min(i + 1, Samples - 1)] - _points[Math.Max(i - 1, 0)]).Normalized();
            var side = along.Cross(Vector3.Up);
            side = side.LengthSquared() < 1e-4f ? Vector3.Right : side.Normalized();
            var up = side.Cross(along).Normalized();
            for (var k = 0; k < Sides; k++) {
                var angle = Mathf.Tau * k / Sides;
                var normal = (side * Mathf.Cos(angle)) + (up * Mathf.Sin(angle));
                _vertices[(i * Sides) + k] = _points[i] + (normal * Radius);
                _normals[(i * Sides) + k] = normal;
            }
        }
        _arrays[(int)Mesh.ArrayType.Vertex] = _vertices;
        _arrays[(int)Mesh.ArrayType.Normal] = _normals;
        _tube.ClearSurfaces();
        _tube.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, _arrays);
    }

    /// <summary>The tube's triangles between neighboring rings, clockwise seen from outside: Godot's front faces.
    /// </summary>
    private static int[] TubeIndices() {
        var indices = new int[(Samples - 1) * Sides * 6];
        var n = 0;
        for (var i = 1; i < Samples; i++) {
            var previous = (i - 1) * Sides;
            var ring = i * Sides;
            for (var k = 0; k < Sides; k++) {
                var next = (k + 1) % Sides;
                foreach (var index in (int[])[previous + k, ring + next, ring + k, previous + k, previous + next, ring + next]) {
                    indices[n++] = index;
                }
            }
        }
        return indices;
    }
}
