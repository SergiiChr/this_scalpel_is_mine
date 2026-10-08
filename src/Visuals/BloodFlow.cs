namespace Scalpel.Visuals;

/// <summary>A wound bleeding onto the skin: where (site uv) and how fast (ml/s).</summary>
public readonly record struct BleedSource(Vector2 Uv, float Rate)
{
    public static Godot.Collections.Array ToVariant(IEnumerable<BleedSource> sources) =>
        [.. sources.Select(source => (Variant)new Godot.Collections.Array { source.Uv, source.Rate })];

    public static List<BleedSource> FromVariant(Godot.Collections.Array sources) =>
        [.. sources.Select(entry => entry.AsGodotArray()).Select(pair => new BleedSource(pair[0].AsVector2(), pair[1].AsSingle()))];
}

/// <summary>
/// Blood as a fluid, on every peer. Bleeding wounds (sources, synced by the host) well up into a puddle that grows
/// with the blood lost, and release rivulets from its edge that run downhill over the skin, staining it as they go
/// (the fluid map). Where a rivulet runs off the body it drips: droplets fall to the table or the floor and collect
/// into pools that grow. Strong bleeds also spurt droplets into the air.
/// Purely visual and local: each peer runs its own, so the stains differ a little between players, which is fine.
/// </summary>
public partial class BloodFlow : Node3D
{
    /// <summary>Blood thrown up right in front of the local surgeon's eyes hits their view (see Hud), amount 0..1.
    /// </summary>
    [Signal] public delegate void SplashedEventHandler(float amount);

    private const int MaxRivulets = 48;
    private const int MaxDrops = 96;
    /// <summary>Rivulets per ml of blood lost.</summary>
    private const float RivuletsPerMl = 0.35f;
    private const float RivuletSpeed = 0.05f;
    private const float StainRadius = 0.009f;
    private const float DropRadius = 0.0035f;
    /// <summary>Bleeding faster than this (ml/s) spurts.</summary>
    private const float SpurtRate = 3f;
    private const float PoolMerge = 0.03f;
    private const float PoolMaxRadius = 0.18f;
    /// <summary>Pool area (m²) added per drop.</summary>
    private const float PoolAreaPerDrop = 0.00008f;
    /// <summary>How high a pool stands.</summary>
    private const float PoolThickness = 0.003f;
    private const float FloorY = 0.004f;
    /// <summary>Puddle radius around a bleeding wound (uv) with no blood yet.</summary>
    private const float PuddleStart = 0.015f;
    /// <summary>How much a puddle grows per sqrt(ml).</summary>
    private const float PuddleGrowth = 0.012f;
    private const float PuddleMax = 0.14f;
    private const float PuddlePaintInterval = 0.2f;
    /// <summary>Blood thrown up closer than this (m) to the camera, while looking at it, can land on the view.</summary>
    private const float SplashReach = 0.75f;

    private sealed class Rivulet(Vector2 uv, float volume, float wander)
    {
        public Vector2 Uv = uv;
        public float Volume = volume;
        public float Wander { get; } = wander;
    }

    private sealed class Drop(Vector3 position, Vector3 velocity)
    {
        public Vector3 Position = position;
        public Vector3 Velocity = velocity;
    }

    private readonly List<Rivulet> _rivulets = [];
    private readonly List<Drop> _drops = [];
    private readonly List<MeshInstance3D> _pools = [];
    private readonly Dictionary<int, float> _spawnAcc = [];
    /// <summary>Blood (ml) welled up around each source, keyed by its rounded uv. Sources come and go and change
    /// order.</summary>
    private readonly Dictionary<Vector2I, float> _pooled = [];
    private readonly RandomNumberGenerator _rng = new() { Seed = 7 };
    private float _puddleTimer;
    private MultiMeshInstance3D _dropMesh = null!;
    private PatientBody? _body;

    /// <summary>The wounds bleeding onto the skin.</summary>
    public List<BleedSource> Sources { get; set; } = [];

    public void Setup(PatientBody patientBody)
    {
        _body = patientBody;
        _dropMesh = new MultiMeshInstance3D
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = new SphereMesh
                {
                    Radius = DropRadius,
                    Height = DropRadius * 2.6f,
                    RadialSegments = 6,
                    Rings = 4,
                },
                InstanceCount = MaxDrops,
                VisibleInstanceCount = 0,
            },
            MaterialOverride = Materials.BloodPool(),
            TopLevel = true,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_dropMesh);
    }

    public override void _Process(double delta)
    {
        if (_body is null || delta <= 0)
        {
            return;
        }
        Spawn(_body, (float)delta);
        Flow(_body, (float)delta);
        Fall((float)delta);
    }

    private void Spawn(PatientBody body, float delta)
    {
        _puddleTimer += delta;
        var paintPuddles = _puddleTimer >= PuddlePaintInterval;
        if (paintPuddles)
        {
            _puddleTimer = 0f;
        }
        for (var i = 0; i < Sources.Count; i++)
        {
            var (uv, rate) = Sources[i];
            var radius = Puddle(body, uv, rate * delta, paintPuddles);
            var acc = _spawnAcc.GetValueOrDefault(i) + rate * delta * RivuletsPerMl;
            while (acc >= 1f && _rivulets.Count < MaxRivulets)
            {
                acc -= 1f;
                var edge = Vector2.FromAngle(_rng.Randf() * Mathf.Tau) * radius * _rng.RandfRange(0.6f, 0.95f);
                _rivulets.Add(new Rivulet(uv + edge, _rng.RandfRange(0.6f, 1.2f), _rng.RandfRange(-1f, 1f)));
            }
            _spawnAcc[i] = Mathf.Min(acc, 3f);
            if (rate > SpurtRate && _rng.Randf() < delta * (rate - SpurtRate) * 1.5f)
            {
                var up = body.Site.GlobalBasis.Y;
                var spray = up * _rng.RandfRange(0.6f, 1.3f)
                    + new Vector3(_rng.RandfRange(-0.4f, 0.4f), 0f, _rng.RandfRange(-0.4f, 0.4f));
                AddDrop(body.UvToWorld(uv), spray);
                Splash(body.UvToWorld(uv), 0.04f);
            }
        }
    }

    /// <summary>
    /// Grows the puddle around a wound by <paramref name="ml"/> and returns its radius (uv). Swabbing it away (the
    /// fluid map comes back clean at the wound) starts it over. Painted a few times a second as overlapping blobs, so
    /// its edge is ragged.
    /// </summary>
    private float Puddle(PatientBody body, Vector2 uv, float ml, bool paint)
    {
        var key = (Vector2I)(uv * 100f).Round();
        var map = body.WoundMap;
        if (_pooled.ContainsKey(key) && map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, uv) < 0.3f)
        {
            _pooled[key] = 0f;
        }
        var pooled = _pooled.GetValueOrDefault(key) + ml;
        _pooled[key] = pooled;
        var radius = Mathf.Min(PuddleStart + Mathf.Sqrt(pooled) * PuddleGrowth, PuddleMax);
        if (paint)
        {
            var blob = Vector2.FromAngle(_rng.Randf() * Mathf.Tau) * radius * _rng.RandfRange(0f, 0.35f);
            map.Disk(WoundMap.Layer.Fluids, WoundMap.Blood, uv + blob, radius * _rng.RandfRange(0.7f, 1f), 0.95f,
                WoundMap.Mode.Max);
        }
        return radius;
    }

    /// <summary>Rivulets slide downhill over the skin: gravity along the skin's own slope, wandering a little.</summary>
    private void Flow(PatientBody body, float delta)
    {
        var down = body.Site.GlobalBasis.Inverse() * Vector3.Down;
        foreach (var rivulet in _rivulets.ToList())
        {
            var uv = rivulet.Uv;
            var slope = Slope(body, uv);
            var along = new Vector2(down.X, down.Z) + slope * down.Y;
            if (along.Length() < 0.05f)
            {
                along = new Vector2(rivulet.Wander, 1f) * 0.05f;
            }
            var direction = along.Normalized().Rotated(Mathf.Sin(Time.GetTicksMsec() * 0.0015f + rivulet.Wander * 9f) * 0.2f);
            var step = direction * RivuletSpeed * delta / ((body.SiteSize.X + body.SiteSize.Y) * 0.5f);
            var next = uv + step;
            body.WoundMap.Stroke(WoundMap.Layer.Fluids, WoundMap.Blood, uv, next, StainRadius, 0.55f, WoundMap.Mode.Max);
            rivulet.Uv = next;
            rivulet.Volume -= delta * 0.25f;
            if (next.X < 0f || next.Y < 0f || next.X > 1f || next.Y > 1f)
            {
                // Ran off the site: it drips from here.
                AddDrop(body.UvToWorld(next.Clamp(Vector2.Zero, Vector2.One)), Vector3.Zero);
                _rivulets.Remove(rivulet);
            }
            else if (rivulet.Volume <= 0f)
            {
                _rivulets.Remove(rivulet);
            }
        }
    }

    /// <summary>Skin slope at uv (height change per uv unit), from the baked skin heights.</summary>
    private static Vector2 Slope(PatientBody body, Vector2 uv)
    {
        const float E = 0.02f;
        var dx = body.SurfaceHeight(uv + new Vector2(E, 0)) - body.SurfaceHeight(uv - new Vector2(E, 0));
        var dy = body.SurfaceHeight(uv + new Vector2(0, E)) - body.SurfaceHeight(uv - new Vector2(0, E));
        return new Vector2(dx / body.SiteSize.X, dy / body.SiteSize.Y) / (2f * E) * 8f;
    }

    /// <summary>Throws <paramref name="count"/> droplets up and out from <paramref name="at"/> (an impact, a saw),
    /// <paramref name="speed"/> in m/s.</summary>
    public void Spray(Vector3 at, int count, float speed)
    {
        if (_body is null)
        {
            return;
        }
        var up = _body.Site.GlobalBasis.Y;
        for (var i = 0; i < count; i++)
        {
            var outward = new Vector3(_rng.RandfRange(-1f, 1f), 0f, _rng.RandfRange(-1f, 1f)) * 0.6f;
            AddDrop(at + up * 0.004f, (up + outward).Normalized() * speed * _rng.RandfRange(0.5f, 1f));
        }
        Splash(at, count * speed * 0.05f);
    }

    /// <summary>Closer and more head-on hits more.</summary>
    private void Splash(Vector3 at, float amount)
    {
        if (GetViewport().GetCamera3D() is not { } camera)
        {
            return;
        }
        var to = at - camera.GlobalPosition;
        var facing = to.Normalized().Dot(-camera.GlobalBasis.Z);
        if (to.Length() < SplashReach && facing > 0.7f)
        {
            EmitSignal(SignalName.Splashed, amount * (1f - to.Length() / SplashReach) * 2f);
        }
    }

    private void AddDrop(Vector3 at, Vector3 velocity)
    {
        if (_drops.Count < MaxDrops)
        {
            _drops.Add(new Drop(at, velocity));
        }
    }

    /// <summary>Droplets fall under gravity and land on the table top or the floor, where they pool.</summary>
    private void Fall(float delta)
    {
        const float TableTop = Room.TableHeight + 0.07f;
        foreach (var drop in _drops.ToList())
        {
            drop.Velocity += Vector3.Down * 9.8f * delta;
            var position = drop.Position + drop.Velocity * delta;
            var onTable = position.X > Room.TableFoot && position.X < Room.TableHead && Mathf.Abs(position.Z) < 0.31f;
            var ground = onTable && drop.Position.Y >= TableTop ? TableTop : FloorY;
            if (position.Y <= ground)
            {
                Pool(new Vector3(position.X, ground, position.Z));
                _drops.Remove(drop);
            }
            else
            {
                drop.Position = position;
            }
        }
        var multimesh = _dropMesh.Multimesh;
        multimesh.VisibleInstanceCount = _drops.Count;
        for (var i = 0; i < _drops.Count; i++)
        {
            multimesh.SetInstanceTransform(i, new Transform3D(Basis.Identity, _drops[i].Position));
        }
    }

    /// <summary>
    /// Adds a drop's worth of blood to the nearest pool, or starts a new one. Pools grow with the square root of
    /// volume. Each is a very flat dome, so its rounded rim catches the light like standing liquid.
    /// </summary>
    private void Pool(Vector3 at)
    {
        foreach (var pool in _pools)
        {
            if (pool.Position.DistanceTo(at) < PoolMerge + pool.Scale.X)
            {
                var radius = Mathf.Min(Mathf.Sqrt(pool.Scale.X * pool.Scale.X + PoolAreaPerDrop), PoolMaxRadius);
                pool.Scale = new Vector3(radius, PoolThickness, radius);
                return;
            }
        }
        var mesh = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 28, Rings = 6, IsHemisphere = true },
            MaterialOverride = Materials.BloodPool(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            TopLevel = true,
        };
        AddChild(mesh);
        mesh.Position = at;
        mesh.Scale = new Vector3(0.012f, PoolThickness, 0.012f);
        _pools.Add(mesh);
    }
}
