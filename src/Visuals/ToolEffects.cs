namespace Scalpel.Visuals;

/// <summary>A short-lived effect where a tool works on the body.</summary>
public enum ToolEffect {
    /// <summary>Cautery and lighter: a thin plume of tissue smoke rising from the tip.</summary>
    Smoke,
    /// <summary>Saw on bone: a spray of pale bone dust with a little blood.</summary>
    Dust,
    /// <summary>Blunt impact (mallet): blood droplets thrown up from the skin.</summary>
    Spatter,
    /// <summary>Defibrillator discharge: a blue-white flash and sparks at the paddles, the body jerks.</summary>
    Spark,
    /// <summary>Injection or catheter: a bead of blood where the needle went in, a spot on the site's skin.</summary>
    Bead,
}

/// <summary>
/// Short-lived visual effects where a tool works on the body, on every peer (the host sends them, see
/// <see cref="Surgery.Effect"/>). Lasting marks (cuts, burns, stitches, ink) are painted into the wound map instead.
/// </summary>
public partial class ToolEffects : Node3D {
    /// <summary>Blood beads stay this long (seconds), then dry away.</summary>
    private const float BeadLife = 90f;
    /// <summary>How long (seconds) a bead takes to dry away.</summary>
    private const float BeadDrying = 10f;
    /// <summary>How long (seconds) a stream shows after a push.</summary>
    private const float StreamLife = 0.5f;
    /// <summary>How fast (m/s) liquid leaves the needle, and the time step (seconds) and number of steps its arc is
    /// traced in: it falls at most that long.</summary>
    private const float StreamSpeed = 0.4f;
    private const float StreamStep = 0.02f;
    private const int StreamSteps = 40;
    private const float StreamRadius = 0.0025f;
    private const int StreamSides = 6;
    private const uint StreamStops = PatientBody.SurfaceLayer | PatientBody.CavityLayer | 1u;
    private const float StreamAlpha = 0.7f;

    /// <summary>How a burst of particles looks and moves.</summary>
    private sealed record Burst(
        int Amount, float Lifetime, float Explosiveness, float Spread, float Speed, float Size, Vector3 Gravity,
        Curve Grow, Gradient Colors, float Damping = 0f, bool Glow = false);

    private static readonly Lazy<GradientTexture2D> SoftDot = new(MakeSoftDot);

    /// <summary>Blood beads still on the body: blood like any other, gauze or a pad wipes them away.</summary>
    private readonly List<BloodBead> _beads = [];
    private readonly List<LiquidStream> _streams = [];

    public Patient Patient { get; set; } = null!;

    public void Play(ToolEffect kind, Vector3 at) {
        switch (kind) {
            case ToolEffect.Smoke:
                Emit(at, new Burst(6, 1.8f, 0.3f, 25f, 0.05f, 0.05f, new Vector3(0.01f, 0.06f, 0f), GrowCurve(0.3f, 1f),
                    Ramp(new Color(0.5f, 0.5f, 0.48f, 0.4f), new Color(0.52f, 0.52f, 0.5f, 0.6f), new Color(0.6f, 0.6f, 0.6f, 0f)),
                    Damping: 0.02f));
                break;
            case ToolEffect.Dust:
                Emit(at, new Burst(14, 0.7f, 0.8f, 70f, 0.5f, 0.012f, new Vector3(0, -6f, 0), GrowCurve(1f, 0.6f),
                    Ramp(new Color(0.95f, 0.92f, 0.82f), new Color(0.9f, 0.86f, 0.76f, 0f))));
                Patient.Body.Blood.Spray(at, 2, 0.6f);
                break;
            case ToolEffect.Spatter:
                Patient.Body.Blood.Spray(at, 6, 1f);
                break;
            case ToolEffect.Spark:
                Emit(at, new Burst(24, 0.3f, 1f, 80f, 0.9f, 0.012f, new Vector3(0, -3f, 0), GrowCurve(1f, 0.2f),
                    Ramp(new Color(0.85f, 0.92f, 1f), new Color(0.4f, 0.6f, 1f, 0f)), Glow: true));
                Flash(at);
                Patient.Body.Animator.Jolt();
                break;
            case ToolEffect.Bead:
                Bead(at);
                break;
        }
    }

    /// <summary>One-shot particles that free themselves when done.</summary>
    private void Emit(Vector3 at, Burst burst) {
        var particles = new CpuParticles3D {
            OneShot = true,
            Explosiveness = burst.Explosiveness,
            Amount = burst.Amount,
            Lifetime = burst.Lifetime,
            Direction = Vector3.Up,
            Spread = burst.Spread,
            InitialVelocityMin = burst.Speed * 0.5f,
            InitialVelocityMax = burst.Speed,
            Gravity = burst.Gravity,
            DampingMin = burst.Damping,
            DampingMax = burst.Damping,
            ScaleAmountMin = burst.Size * 0.6f,
            ScaleAmountMax = burst.Size,
            ScaleAmountCurve = burst.Grow,
            ColorRamp = burst.Colors,
            Mesh = new QuadMesh { Size = Vector2.One },
            MaterialOverride = ParticleMaterial(burst.Glow),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(particles);
        particles.GlobalPosition = at;
        particles.Emitting = true;
        particles.Finished += particles.QueueFree;
    }

    private static StandardMaterial3D ParticleMaterial(bool glow) => new() {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
        // Particle scale (the size and growth curves) only survives billboarding with this on.
        BillboardKeepScale = true,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        VertexColorUseAsAlbedo = true,
        AlbedoTexture = SoftDot.Value,
        BlendMode = glow ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
    };

    /// <summary>A round, soft-edged sprite, made once.</summary>
    private static GradientTexture2D MakeSoftDot() {
        var gradient = new Gradient();
        gradient.SetColor(0, Colors.White);
        gradient.SetColor(1, new Color(1, 1, 1, 0));
        return new GradientTexture2D {
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(1f, 0.5f),
            Width = 32,
            Height = 32,
            Gradient = gradient,
        };
    }

    private static Gradient Ramp(params Color[] colors) => new() {
        Offsets = [.. colors.Select((_, i) => (float)i / (colors.Length - 1))],
        Colors = colors,
    };

    private static Curve GrowCurve(float from, float to) {
        var curve = new Curve();
        curve.AddPoint(new Vector2(0, from));
        curve.AddPoint(new Vector2(1, to));
        return curve;
    }

    private void Flash(Vector3 at) {
        var light = new OmniLight3D { LightColor = new Color(0.75f, 0.85f, 1f), LightEnergy = 4f, OmniRange = 1.2f };
        AddChild(light);
        light.GlobalPosition = at + new Vector3(0, 0.05f, 0);
        var fade = CreateTween();
        fade.TweenProperty(light, "light_energy", 0f, 0.18f);
        fade.TweenCallback(Callable.From(light.QueueFree));
    }

    /// <summary>
    /// The arc liquid squirted from <paramref name="from"/> along <paramref name="along"/> falls in, up to the first thing
    /// it lands on: the stream's points, the last where it lands.
    /// </summary>
    internal static List<Vector3> StreamPath(Node3D world, Vector3 from, Vector3 along) {
        var path = new List<Vector3> { from };
        var velocity = along.Normalized() * StreamSpeed;
        var gravity = Vector3.Down * (float)ProjectSettings.GetSetting("physics/3d/default_gravity");
        for (var i = 0; i < StreamSteps; i++) {
            var next = path[^1] + (velocity * StreamStep);
            if (Rays.Cast(world, path[^1], next, StreamStops) is { } hit) {
                path.Add(hit.Position);
                break;
            }
            path.Add(next);
            velocity += gravity * StreamStep;
        }
        return path;
    }

    /// <summary>
    /// A thin stream of clear liquid squirted from <paramref name="tool"/>'s tip, arcing down to where it lands, fading
    /// out. It follows the tip while it shows, and pushes in a row keep it going.
    /// </summary>
    public void Stream(SurgicalTool tool, float ml, float bloodShare) {
        _streams.RemoveAll(stream => !IsInstanceValid(stream.Mesh));
        var stream = _streams.FirstOrDefault(stream => stream.Tool == tool);
        if (stream is null) {
            var mesh = new MeshInstance3D {
                Name = "Stream",
                Mesh = new ImmediateMesh(),
                MaterialOverride = Materials.Stream(),
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            };
            AddChild(mesh);
            stream = new LiquidStream(mesh, tool);
            _streams.Add(stream);
        }
        Follow(stream);
        Patient.Body.Blood.Irrigate(stream.Path[^1] + Vector3.Up * 0.002f, ml, bloodShare);
        var material = (StandardMaterial3D)stream.Mesh.MaterialOverride;
        material.AlbedoColor = material.AlbedoColor with { A = StreamAlpha };
        stream.Fade?.Kill();
        stream.Fade = stream.Mesh.CreateTween();
        stream.Fade.TweenInterval(StreamLife * 0.6f);
        stream.Fade.TweenProperty(material, "albedo_color:a", 0f, StreamLife * 0.4f);
        stream.Fade.TweenCallback(Callable.From(stream.Mesh.QueueFree));
    }

    public override void _Process(double delta) {
        _streams.RemoveAll(stream => !IsInstanceValid(stream.Mesh) || !IsInstanceValid(stream.Tool));
        foreach (var stream in _streams) {
            Follow(stream);
        }
    }

    /// <summary>Lays <paramref name="stream"/> along the arc from where its tool's tip is now.</summary>
    private void Follow(LiquidStream stream) {
        stream.Path = StreamPath(this, stream.Tool.TipPosition(), stream.Tool.TipDirection());
        Tube((ImmediateMesh)stream.Mesh.Mesh, stream.Path);
    }

    /// <summary>Rebuilds <paramref name="mesh"/> (world space: the node sits at the origin) as a thin tube along
    /// <paramref name="path"/>, narrowing a little as it falls.</summary>
    private static void Tube(ImmediateMesh mesh, List<Vector3> path) {
        mesh.ClearSurfaces();
        if (path.Count < 2) {
            return;
        }
        var rings = new Vector3[path.Count][];
        var normals = new Vector3[path.Count][];
        for (var i = 0; i < path.Count; i++) {
            var forward = (path[Math.Min(i + 1, path.Count - 1)] - path[Math.Max(i - 1, 0)]).Normalized();
            var side = forward.Cross(Mathf.Abs(forward.Y) > 0.9f ? Vector3.Right : Vector3.Up).Normalized();
            var up = side.Cross(forward);
            var radius = StreamRadius * Mathf.Lerp(1f, 0.6f, (float)i / (path.Count - 1));
            rings[i] = new Vector3[StreamSides];
            normals[i] = new Vector3[StreamSides];
            for (var k = 0; k < StreamSides; k++) {
                var angle = Mathf.Tau * k / StreamSides;
                normals[i][k] = (side * Mathf.Cos(angle)) + (up * Mathf.Sin(angle));
                rings[i][k] = path[i] + (normals[i][k] * radius);
            }
        }
        mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        for (var i = 0; i + 1 < path.Count; i++) {
            for (var k = 0; k < StreamSides; k++) {
                var n = (k + 1) % StreamSides;
                foreach (var (ring, side) in new[] { (i, k), (i + 1, k), (i + 1, n), (i, k), (i + 1, n), (i, n) }) {
                    mesh.SurfaceSetNormal(normals[ring][side]);
                    mesh.SurfaceAddVertex(rings[ring][side]);
                }
            }
        }
        mesh.SurfaceEnd();
    }

    /// <summary>Where each stream still showing starts and lands.</summary>
    internal List<(Vector3 Top, Vector3 Bottom)> Streams() => [.. _streams
        .Where(stream => IsInstanceValid(stream.Mesh))
        .Select(stream => (stream.Path[0], stream.Path[^1]))];

    /// <summary>A stream still showing, the tool it comes from and the points it runs through.</summary>
    private sealed class LiquidStream(MeshInstance3D mesh, SurgicalTool tool) {
        public MeshInstance3D Mesh => mesh;
        public SurgicalTool Tool => tool;
        public List<Vector3> Path { get; set; } = [];
        public Tween? Fade { get; set; }
    }

    /// <summary>
    /// A bead of blood where a needle went in. On the site it's a spot painted on the skin, so it rides the skin when a
    /// cut there is pulled open, where a dome would hang in the air. Elsewhere a small dome stuck to the body, so it
    /// follows turns and breathing.
    /// </summary>
    private void Bead(Vector3 at) {
        var body = Patient.Body;
        var probe = body.Probe(at);
        if (probe.Zone == SiteZone.Site) {
            var spot = new BloodBead(null, probe.Uv, body);
            body.Blood.AddPuncture(probe.Uv);
            _beads.Add(spot);
            var dry = CreateTween();
            dry.TweenInterval(BeadLife + BeadDrying);
            dry.TweenCallback(Callable.From(() => Remove(spot)));
            return;
        }
        var dome = new MeshInstance3D {
            Name = "BloodBead",
            Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 12, Rings = 4, IsHemisphere = true },
            MaterialOverride = Materials.BloodPool(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        Node3D root = body.Root;
        var normal = root.GlobalBasis.Y.Normalized();
        if (probe.Zone != SiteZone.Cavity) {
            if (Rays.Cast(this, at + Vector3.Up * 0.03f, at + Vector3.Down * 0.03f, PatientBody.SurfaceLayer) is { } hit) {
                at = hit.Position;
                normal = hit.Normal;
            }
            if (probe.Part.StartsWith("arm", StringComparison.Ordinal)) {
                root = body.IvSite(at).Node;
            }
        }
        root.AddChild(dome, true);
        dome.GlobalPosition = at;
        var across = root.GlobalBasis.X.Slide(normal).Normalized();
        if (across.IsZeroApprox()) {
            across = root.GlobalBasis.Z.Slide(normal).Normalized();
        }
        dome.GlobalBasis = new Basis(across, normal, across.Cross(normal));
        dome.Scale = new Vector3(0.002f, 0.0015f, 0.002f);
        var bead = new BloodBead(dome, default, body);
        var grow = CreateTween();
        grow.TweenProperty(dome, "scale", new Vector3(0.0035f, 0.0025f, 0.0035f), 2f);
        grow.TweenInterval(BeadLife);
        grow.TweenProperty(dome, "scale", new Vector3(0.002f, 0.0004f, 0.002f), BeadDrying);
        grow.TweenCallback(Callable.From(() => Remove(bead)));
        _beads.Add(bead);
    }

    /// <summary>The blood beads still on the body.</summary>
    internal List<BloodBead> Beads() => [.. BeadsLeft()];

    /// <summary>Whether a blood bead lies within <paramref name="reach"/> (meters) of <paramref name="at"/>.</summary>
    public bool BeadNear(Vector3 at, float reach) => BeadsLeft().Any(bead => bead.GlobalPosition.DistanceTo(at) < reach);

    /// <summary>Wipes away the blood beads within <paramref name="reach"/> of <paramref name="at"/>.</summary>
    public void WipeBeads(Vector3 at, float reach) {
        foreach (var bead in BeadsLeft().Where(bead => bead.GlobalPosition.DistanceTo(at) < reach).ToList()) {
            Remove(bead);
        }
    }

    /// <summary>The beads not yet gone with what they were stuck to (an IV site taken away).</summary>
    private List<BloodBead> BeadsLeft() {
        _beads.RemoveAll(bead => bead.Dome is { } dome && !IsInstanceValid(dome));
        return _beads;
    }

    private void Remove(BloodBead bead) {
        if (!_beads.Remove(bead)) {
            return;
        }
        if (bead.Dome is null) {
            Patient.Body.Blood.RemovePuncture(bead.Uv);
        } else if (IsInstanceValid(bead.Dome)) {
            bead.Dome.QueueFree();
        }
    }

    /// <summary>A bead of blood: a <paramref name="dome"/> stuck to the body, or without one a spot painted on the site's
    /// skin at <paramref name="uv"/>.</summary>
    internal sealed class BloodBead(MeshInstance3D? dome, Vector2 uv, PatientBody body) {
        public MeshInstance3D? Dome => dome;
        public Vector2 Uv => uv;
        public Vector3 GlobalPosition => dome?.GlobalPosition ?? body.SkinPoint(uv);
    }
}
