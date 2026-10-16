using System.Threading.Tasks;

namespace Scalpel.World;

/// <summary>
/// Mobile X-ray unit. Push it by the handle at its back (+Z) with its front, the arm and tube head, toward the table.
/// The collimator lights the two fields on whatever is under them: straight down from the tube head (AP), and level and
/// forward from the cart a little below the head's height (lateral). An exposure takes both (<see cref="XrayExposure"/>), two films that develop in
/// the cassette holder over a few seconds. A film is black where the beam got through and clear where bone or metal
/// stopped it: held up against a light it reads best. The host owns the cart's position, arm and exposures; every
/// peer develops the films from its own copy of the scene.
/// </summary>
public partial class XrayCart : Node3D {
    public const float ExposeTime = 2.5f;
    public const float Cooldown = 30f;
    /// <summary>Seconds a film takes to develop.</summary>
    public const float DevelopTime = 6f;
    private const float Reach = 1.6f;
    private const float SyncInterval = 0.1f;
    /// <summary>The cart's outline on the floor, local, as points along it: the base with its wheels and handle, and the
    /// tube head out in front. Every one stays on the room's floor (or in its niche) as it's pushed.</summary>
    private static readonly Vector3[] Footprint = Outline(0.28f, -0.36f, 0.42f).Concat(Outline(0.16f, -0.86f, -0.36f)).ToArray();
    /// <summary>How close (meters) the cart's outline comes to a wall.</summary>
    private const float WallGap = 0.02f;
    /// <summary>The tube head's height above the floor, meters, as the arm slides up and down the column: its range and
    /// one wheel step.</summary>
    internal const float ArmLow = 1.1f;
    internal const float ArmHigh = 1.75f;
    internal const float ArmStep = 0.05f;
    /// <summary>How far (meters) the collimator's lens at least starts over the surgical site: slightly above the
    /// patient, ready for an AP film without touching them.</summary>
    internal const float SiteClearance = 0.08f;
    /// <summary>How far the floor lock's pedal goes down, in radians.</summary>
    private const float PedalDown = 0.35f;
    /// <summary>The developed films stand in the cassette holder on top of the generator, the AP one in front.</summary>
    private static readonly Vector3 FilmSpot = new(0f, 1.0f, 0.23f);
    /// <summary>The collimator's lens, local to the tube head.</summary>
    private static readonly Vector3 Lens = new(0f, -0.185f, 0f);
    /// <summary>How far below the tube head (meters) the lateral beam runs, level and forward from the cart: with the
    /// head lowered to just over the patient, it crosses the patient's body.</summary>
    internal const float LateralDrop = 0.3f;
    /// <summary>A film's size, meters: 35 x 43 cm, its long side along the patient.</summary>
    private static readonly Vector2 FilmSize = new(0.35f, 0.43f);
    /// <summary>Held up, the films are this far in front of the eyes, side by side.</summary>
    private static readonly Vector3 HeldAp = new(-0.2f, 0f, -0.55f);
    private static readonly Vector3 HeldLateral = new(0.24f, 0f, -0.55f);
    /// <summary>Points sampled along each side of a field's outline, and how often (seconds) it's redrawn at most:
    /// each redraw casts a ray per point.</summary>
    private const int OutlineSteps = 8;
    private const float OutlineInterval = 0.1f;
    /// <summary>How far (meters) the outline is lifted off what its rays hit, toward the source: they hit the body as
    /// it rests, and breathing and the gown's folds lift the drawn skin above that.</summary>
    private const float OutlineLift = 0.012f;
    /// <summary>Half the outline band's width, meters.</summary>
    private const float OutlineWidth = 0.006f;

    /// <summary>The last exposure's films, AP then lateral; empty until the first.</summary>
    public IReadOnlyList<XrayFilm> Films { get; private set; } = [];
    /// <summary>The peer pushing the cart, 0 for nobody: then it's locked against the floor.</summary>
    public int Pusher { get; private set; }
    /// <summary>The tube head's height above the floor, meters.</summary>
    public float Arm { get; private set; } = ArmHigh;
    /// <summary>Where the cart is, and which way it faces, from its pusher as they took hold of it: like a held tool, it
    /// keeps that as they walk and turn. Host only.</summary>
    private Vector3 _gripOffset;
    private float _gripYaw;
    private float _cooldown;
    private float _exposing;
    private float _syncAccumulated;
    private ulong _takenAtMsec;
    private readonly MeshInstance3D[] _films = new MeshInstance3D[2];
    private MeshInstance3D _outline = null!;
    /// <summary>Where the films are drawn, AP then lateral: each keeps its last picture.</summary>
    private SubViewport[] _viewports = [];
    private AnimatableBody3D _body = null!;
    private Transform3D _outlined;
    private float _outlineWait;
    /// <summary>Each field's outline triangles, redrawn one field at a time: how many are still to redraw since the
    /// tube head last moved, and which one is next.</summary>
    private readonly List<Vector3>[] _bands = [[], []];
    private int _staleFields;
    private int _nextField;
    private Dictionary<string, Node3D> _parts = null!;
    private Surgery _surgery = null!;

    public void Build(Surgery surgery) {
        _surgery = surgery;
        var model = ModelSlot.Instantiate("props", "xray", this);
        _parts = ModelSlot.Parts(model, "Arm", "Head", "Pedal");
        PoseArm(1f);
        _parts["Pedal"].Rotation = new Vector3(PedalDown, 0f, 0f);
        _viewports =
        [
            XrayExposure.Viewport(this, "ApPicture", FilmSize),
            XrayExposure.Viewport(this, "LateralPicture", new Vector2(FilmSize.Y, FilmSize.X)),
        ];
        for (var i = 0; i < _films.Length; i++) {
            _films[i] = new MeshInstance3D { Name = i == 0 ? "ApFilm" : "LateralFilm", Visible = false };
            AddChild(_films[i]);
        }
        // The light the collimator throws on what's under its fields, drawn over the surfaces it lands on.
        _outline = new MeshInstance3D {
            Name = "Fields",
            Mesh = new ImmediateMesh(),
            TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(1f, 0.93f, 0.6f),
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
        AddChild(_outline);
        // Only the generator and column block: the base and the arm reach under and over the table.
        _body = new AnimatableBody3D();
        _body.AddChild(new CollisionShape3D {
            Shape = new BoxShape3D { Size = new Vector3(0.44f, 1.2f, 0.4f) },
            Position = new Vector3(0f, 0.6f, 0.12f),
        });
        AddChild(_body);
        // Grab takes hold of it anywhere, like a tool: the base and generator, the handle, the column, and the arm with
        // the tube head, which moves with it. The films' spot over the holder stays clear.
        Interactable Hold(Node3D parent, Vector3 size, Vector3 position) {
            var spot = Interactable.Create(parent, "Push / let go of the X-ray cart", size, position,
                _ => RpcId(Net.HostId, MethodName.RequestPush));
            spot.Button = InputActions.Grab;
            spot.Highlight = [model];
            // A hand that holds a tool lets go of it instead, and one by a tool picks that up.
            spot.Offered = surgeon => Pusher == surgeon.PeerId
                || (surgeon.HeldTool(surgeon.Active) is null && !IsInstanceValid(surgeon.Hovered));
            return spot;
        }
        Hold(this, new Vector3(0.5f, 0.75f, 0.7f), new Vector3(0f, 0.375f, -0.02f))
            .AddBox(new Vector3(0.6f, 0.12f, 0.14f), new Vector3(0f, 0.9f, 0.4f))
            .AddBox(new Vector3(0.14f, 2.0f, 0.13f), new Vector3(-0.17f, 1.1f, -0.03f));
        // Local to the tube head: the head, and the arm from it back to the column.
        Hold(_parts["Arm"], new Vector3(0.34f, 0.4f, 0.3f), new Vector3(0f, -0.04f, 0f))
            .AddBox(new Vector3(0.3f, 0.12f, 0.75f), new Vector3(-0.08f, 0.24f, 0.35f));
        var films = Interactable.Create(this, "Hold up the X-ray films", new Vector3(0.44f, 0.44f, 0.1f), FilmSpot,
            surgeon => surgeon.HoldingFilms = true);
        films.Offered = surgeon => Films.Count > 0 && !surgeon.HoldingFilms;
        films.Highlight = _films;
    }

    /// <summary>Raises the arm one wheel step (-1 lowers it).</summary>
    public void Raise(int steps) => RpcId(Net.HostId, MethodName.RequestRaise, steps);

    /// <summary>Lets go of the cart and locks it against the floor, if this peer pushes it.</summary>
    public void Lock() => RpcId(Net.HostId, MethodName.RequestLock);

    /// <summary>Starts the arm at the lowest wheel step that keeps the lens <see cref="SiteClearance"/> over
    /// <paramref name="site"/> (world), the surgical site's skin.</summary>
    public void StartOver(Vector3 site) {
        var steps = Mathf.Ceil((site.Y + SiteClearance - Lens.Y - ArmLow) / ArmStep);
        Arm = Mathf.Clamp(ArmLow + (steps * ArmStep), ArmLow, ArmHigh);
        PoseArm(1f);
    }

    /// <summary>Takes an exposure, if the cart is at the table and ready.</summary>
    public void TakeXray() => RpcId(Net.HostId, MethodName.RequestExpose);

    public override void _Process(double delta) {
        PoseArm(Mathf.Min((float)delta * 10f, 1f));
        UpdateOutline((float)delta);
        if (Films.Count > 0) {
            PlaceFilms();
        }
    }

    /// <summary>The two beams as the tube head stands now. The AP film's up points to the patient's head, so the
    /// patient's right is on the film's left, as AP films are read.</summary>
    public XrayBeam[] Beams() {
        var head = _parts["Head"].GlobalTransform;
        var toHead = (_surgery.Patient.GlobalBasis.X with { Y = 0f }).Normalized();
        var ap = new Basis(toHead.Cross(Vector3.Up), toHead, Vector3.Up);
        var forward = (-GlobalBasis.Z with { Y = 0f }).Normalized();
        var lateral = new Basis(Vector3.Up.Cross(-forward), Vector3.Up, -forward);
        var lateralSource = GlobalPosition with { Y = head.Origin.Y - LateralDrop };
        return
        [
            new XrayBeam(XrayView.Ap, new Transform3D(ap, head * Lens), FilmSize),
            new XrayBeam(XrayView.Lateral, new Transform3D(lateral, lateralSource), new Vector2(FilmSize.Y, FilmSize.X)),
        ];
    }

    /// <summary>Draws both fields' outlines where they land, while the cart is at the table. Redrawn only when the
    /// tube head has moved.</summary>
    private void UpdateOutline(float delta) {
        var near = DistanceToTable() <= Reach;
        _outline.Visible = near;
        _outlineWait -= delta;
        var head = _parts["Head"].GlobalTransform;
        if (!head.IsEqualApprox(_outlined)) {
            _outlined = head;
            _staleFields = _bands.Length;
        }
        if (!near || _outlineWait > 0f || _staleFields == 0) {
            return;
        }
        // One field per redraw, so no frame casts both fields' rays.
        _outlineWait = OutlineInterval;
        _staleFields--;
        _nextField = (_nextField + 1) % _bands.Length;
        _bands[_nextField].Clear();
        Outline(_bands[_nextField], Beams()[_nextField]);
        var mesh = (ImmediateMesh)_outline.Mesh;
        mesh.ClearSurfaces();
        if (_bands.All(band => band.Count == 0)) {
            return;
        }
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        foreach (var corner in _bands.SelectMany(band => band)) {
            mesh.SurfaceAddVertex(corner);
        }
        mesh.SurfaceEnd();
    }

    /// <summary>Triangles of a band along the edge of the field, on whatever its rays meet first, lifted toward the
    /// source so it isn't hidden in the surface.</summary>
    private void Outline(List<Vector3> bands, XrayBeam beam) {
        var half = beam.Size * 0.5f;
        Vector2[] corners = [new(-half.X, -half.Y), new(half.X, -half.Y), new(half.X, half.Y), new(-half.X, half.Y)];
        var along = -beam.Source.Basis.Z;
        Vector3? last = null;
        for (var side = 0; side < 4; side++) {
            for (var step = 0; step <= OutlineSteps; step++) {
                var at = corners[side].Lerp(corners[(side + 1) % 4], step / (float)OutlineSteps);
                var from = beam.Source * new Vector3(at.X, at.Y, 0f);
                // The lateral beam starts inside the cart itself.
                var hit = Rays.Cast(this, from, from + (along * XrayExposure.Depth), PatientBody.SurfaceLayer | 1u, _body.GetRid());
                var point = hit is { } h ? h.Position - (along * OutlineLift) : (Vector3?)null;
                if (point is { } b && last is { } a && a.DistanceTo(b) < 0.15f) {
                    var across = along.Cross(b - a).Normalized() * OutlineWidth;
                    bands.AddRange([a - across, b - across, b + across, a - across, b + across, a + across]);
                }
                last = point;
            }
        }
    }

    private float DistanceToTable() {
        var patient = _surgery.Patient.GlobalPosition;
        return new Vector2(GlobalPosition.X, GlobalPosition.Z).DistanceTo(new Vector2(patient.X, patient.Z));
    }

    /// <summary>The films in the cassette holder, or in front of the local surgeon's eyes while they hold them up.
    /// </summary>
    private void PlaceFilms() {
        var developed = Developed();
        var holder = _surgery.LocalSurgeon is { HoldingFilms: true } me ? me : null;
        for (var i = 0; i < _films.Length; i++) {
            ((ShaderMaterial)_films[i].MaterialOverride).SetShaderParameter("developed", developed);
            _films[i].TopLevel = holder is not null;
            _films[i].Transform = holder is null
                ? new Transform3D(Basis.Identity, FilmSpot + new Vector3(0f, 0f, i * -0.02f))
                : holder.Camera.GlobalTransform.TranslatedLocal(i == 0 ? HeldAp : HeldLateral);
        }
    }

    /// <summary>Slides the arm toward <see cref="Arm"/> by `follow` of the way.</summary>
    private void PoseArm(float follow) {
        var arm = _parts["Arm"];
        arm.Position = arm.Position with { Y = Mathf.Lerp(arm.Position.Y, Arm, follow) };
    }

    public override void _PhysicsProcess(double delta) {
        if (!Multiplayer.IsServer()) {
            return;
        }
        var dt = (float)delta;
        _cooldown = Mathf.Max(_cooldown - dt, 0f);
        if (_exposing > 0f) {
            _exposing -= dt;
            if (_exposing <= 0f) {
                Expose();
            }
        }
        if (!_surgery.Surgeons.TryGetValue(Pusher, out var surgeon)) {
            return;
        }
        var follow = Mathf.Min(dt * 8f, 1f);
        var position = GlobalPosition.Lerp(surgeon.ToGlobal(_gripOffset) with { Y = 0f }, follow);
        var yaw = Mathf.LerpAngle(Rotation.Y, surgeon.Rotation.Y + _gripYaw, follow);
        // Against a wall it slides along it, or only turns, or moves without turning (out of its niche, say), or stays.
        Vector3[] places = [position, position with { X = GlobalPosition.X }, position with { Z = GlobalPosition.Z }];
        var tries = places.Select(at => (at, yaw)).Append((GlobalPosition, yaw)).Concat(places.Select(at => (at, Rotation.Y)));
        foreach (var (at, turn) in tries) {
            if (Fits(at, turn)) {
                GlobalPosition = at;
                Rotation = Rotation with { Y = turn };
                break;
            }
        }
        _syncAccumulated += dt;
        // Not after the report, like the surgeons' states: the others may have left for the lobby.
        if (_syncAccumulated >= SyncInterval && !_surgery.Finished) {
            _syncAccumulated = 0f;
            Rpc(MethodName.SyncPosition, GlobalPosition, Rotation.Y);
        }
    }

    /// <summary>True when the cart's outline stands on the floor, clear of the walls, at <paramref name="position"/>
    /// turned to <paramref name="yaw"/>.</summary>
    internal bool Fits(Vector3 position, float yaw) {
        var basis = new Basis(Vector3.Up, yaw);
        return Footprint.All(point => _surgery.Room.OnFloor(position + (basis * point), WallGap));
    }

    /// <summary>Points along a rectangle's outline on the floor, local: <paramref name="halfWidth"/> to each side, from
    /// <paramref name="front"/> to <paramref name="back"/> along z, every 10 cm or so.</summary>
    private static IEnumerable<Vector3> Outline(float halfWidth, float front, float back) {
        Vector3[] corners = [new(-halfWidth, 0f, front), new(halfWidth, 0f, front), new(halfWidth, 0f, back), new(-halfWidth, 0f, back)];
        for (var i = 0; i < 4; i++) {
            var (a, b) = (corners[i], corners[(i + 1) % 4]);
            var steps = Mathf.CeilToInt(a.DistanceTo(b) / 0.1f);
            for (var step = 0; step < steps; step++) {
                yield return a.Lerp(b, step / (float)steps);
            }
        }
    }

    /// <summary>0..1, how far the films have developed.</summary>
    public float Developed() => Films.Count == 0
        ? 0f
        : Mathf.Clamp((GameClock.Msec - _takenAtMsec) / (DevelopTime * 1000f), 0f, 1f);

    private void Expose() {
        _surgery.Scoring.Add("xray_used", true);
        Rpc(MethodName.FilmsReady);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true)]
    private void RequestPush() {
        var peer = Net.Instance.Sender();
        if (Pusher != peer && _surgery.Surgeons.TryGetValue(peer, out var surgeon)) {
            _gripOffset = surgeon.ToLocal(GlobalPosition) with { Y = 0f };
            _gripYaw = Rotation.Y - surgeon.Rotation.Y;
        }
        Rpc(MethodName.SyncState, Pusher == peer ? 0 : peer, Arm);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true)]
    private void RequestLock() {
        if (Pusher == Net.Instance.Sender()) {
            Rpc(MethodName.SyncState, 0, Arm);
        }
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true)]
    private void RequestRaise(int steps) =>
        Rpc(MethodName.SyncState, Pusher, Mathf.Clamp(Arm + (steps * ArmStep), ArmLow, ArmHigh));

    /// <summary>Everyone learns who pushes the cart, so the pusher's wheel and click work it, and where the arm is.
    /// </summary>
    [Rpc(CallLocal = true)]
    private void SyncState(int pusher, float arm) {
        Pusher = pusher;
        Arm = arm;
        _parts["Pedal"].Rotation = new Vector3(pusher == 0 ? PedalDown : 0f, 0f, 0f);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true)]
    private void RequestExpose() {
        var peer = Net.Instance.Sender();
        if (_exposing > 0f || _cooldown > 0f) {
            _surgery.Tell(peer, $"The X-ray is warming up ({Mathf.CeilToInt(_cooldown + _exposing)} s).");
            return;
        }
        if (DistanceToTable() > Reach) {
            _surgery.Tell(peer, "Push the cart up to the table first.");
            return;
        }
        _exposing = ExposeTime;
        _cooldown = Cooldown;
        _surgery.Sound("xray_expose", GlobalPosition);
        _surgery.Announce("X-ray! Everyone step back.");
    }

    /// <summary>Everyone takes the films from their own copy of the scene, as it is now.</summary>
    [Rpc(CallLocal = true)]
    private void FilmsReady() => _ = Develop(Beams());

    /// <summary>Draws the new films over the last ones' pictures and puts them in the holder.</summary>
    private async Task Develop(XrayBeam[] beams) {
        Films = await XrayExposure.Take(_viewports, _surgery, beams);
        _takenAtMsec = GameClock.Msec;
        for (var i = 0; i < _films.Length; i++) {
            var size = Films[i].Beam.Size;
            _films[i].Mesh = new QuadMesh { Size = size };
            _films[i].MaterialOverride = Materials.XrayFilm(Films[i].Image, _takenAtMsec % 1000);
            _films[i].Visible = true;
        }
        Sfx.Play("print_whir", GlobalPosition);
        _surgery.Hud.Toast("Two films slide into the holder. Give them a few seconds.");
    }

    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void SyncPosition(Vector3 position, float yaw) {
        GlobalPosition = position;
        Rotation = Rotation with { Y = yaw };
    }
}
