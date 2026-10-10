namespace Scalpel.Visuals;

/// <summary>A short-lived effect where a tool works on the body.</summary>
public enum ToolEffect
{
    /// <summary>Cautery and lighter: a thin plume of tissue smoke rising from the tip.</summary>
    Smoke,
    /// <summary>Saw on bone: a spray of pale bone dust with a little blood.</summary>
    Dust,
    /// <summary>Blunt impact (mallet): blood droplets thrown up from the skin.</summary>
    Spatter,
    /// <summary>Defibrillator discharge: a blue-white flash and sparks at the paddles, the body jerks.</summary>
    Spark,
    /// <summary>Injection or catheter: a bead of blood welling up where the needle went in.</summary>
    Bead,
}

/// <summary>
/// Short-lived visual effects where a tool works on the body, on every peer (the host sends them, see
/// <see cref="Surgery.Effect"/>). Lasting marks (cuts, burns, stitches, ink) are painted into the wound map instead.
/// </summary>
public partial class ToolEffects : Node3D
{
    /// <summary>Blood beads stay this long (seconds), then dry away.</summary>
    private const float BeadLife = 90f;

    /// <summary>How a burst of particles looks and moves.</summary>
    private sealed record Burst(
        int Amount, float Lifetime, float Explosiveness, float Spread, float Speed, float Size, Vector3 Gravity,
        Curve Grow, Gradient Colors, float Damping = 0f, bool Glow = false);

    private static readonly Lazy<GradientTexture2D> SoftDot = new(MakeSoftDot);

    /// <summary>Blood beads still on the body: blood like any other, gauze or a pad wipes them away.</summary>
    private readonly List<MeshInstance3D> _beads = [];

    public Patient Patient { get; set; } = null!;

    public void Play(ToolEffect kind, Vector3 at)
    {
        switch (kind)
        {
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
    private void Emit(Vector3 at, Burst burst)
    {
        var particles = new CpuParticles3D
        {
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

    private static StandardMaterial3D ParticleMaterial(bool glow) => new()
    {
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
    private static GradientTexture2D MakeSoftDot()
    {
        var gradient = new Gradient();
        gradient.SetColor(0, Colors.White);
        gradient.SetColor(1, new Color(1, 1, 1, 0));
        return new GradientTexture2D
        {
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(1f, 0.5f),
            Width = 32,
            Height = 32,
            Gradient = gradient,
        };
    }

    private static Gradient Ramp(params Color[] colors) => new()
    {
        Offsets = [.. colors.Select((_, i) => (float)i / (colors.Length - 1))],
        Colors = colors,
    };

    private static Curve GrowCurve(float from, float to)
    {
        var curve = new Curve();
        curve.AddPoint(new Vector2(0, from));
        curve.AddPoint(new Vector2(1, to));
        return curve;
    }

    private void Flash(Vector3 at)
    {
        var light = new OmniLight3D { LightColor = new Color(0.75f, 0.85f, 1f), LightEnergy = 4f, OmniRange = 1.2f };
        AddChild(light);
        light.GlobalPosition = at + new Vector3(0, 0.05f, 0);
        var fade = CreateTween();
        fade.TweenProperty(light, "light_energy", 0f, 0.18f);
        fade.TweenCallback(Callable.From(light.QueueFree));
    }

    /// <summary>A small dome of blood on the skin, stuck to the body so it follows turns and breathing.</summary>
    private void Bead(Vector3 at)
    {
        var bead = new MeshInstance3D
        {
            Name = "BloodBead",
            Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 12, Rings = 4, IsHemisphere = true },
            MaterialOverride = Materials.BloodPool(),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        var body = Patient.Body;
        Node3D root = body.Root;
        var normal = root.GlobalBasis.Y.Normalized();
        var probe = body.Probe(at);
        if (probe.Zone == SiteZone.Site)
        {
            // The needle tip is under the skin. Its puncture is directly above it on the drawn skin, riding the site.
            root = body.Site;
            var local = root.ToLocal(at);
            local.Y = body.SkinHeight(probe.Uv);
            at = root.ToGlobal(local);
            normal = root.GlobalBasis.Y.Normalized();
        }
        else if (probe.Zone != SiteZone.Cavity)
        {
            if (Rays.Cast(this, at + Vector3.Up * 0.03f, at + Vector3.Down * 0.03f, PatientBody.SurfaceLayer) is { } hit)
            {
                at = hit.Position;
                normal = hit.Normal;
            }
            if (probe.Part.StartsWith("arm", StringComparison.Ordinal))
            {
                root = body.IvSite(at).Node;
            }
        }
        root.AddChild(bead, true);
        bead.GlobalPosition = at;
        var across = root.GlobalBasis.X.Slide(normal).Normalized();
        if (across.IsZeroApprox())
        {
            across = root.GlobalBasis.Z.Slide(normal).Normalized();
        }
        bead.GlobalBasis = new Basis(across, normal, across.Cross(normal));
        bead.Scale = new Vector3(0.002f, 0.0015f, 0.002f);
        var grow = CreateTween();
        grow.TweenProperty(bead, "scale", new Vector3(0.0035f, 0.0025f, 0.0035f), 2f);
        grow.TweenInterval(BeadLife);
        grow.TweenProperty(bead, "scale", new Vector3(0.002f, 0.0004f, 0.002f), 10f);
        grow.TweenCallback(Callable.From(() => Remove(bead)));
        _beads.Add(bead);
    }

    /// <summary>Where the blood beads still on the body are (world).</summary>
    internal List<Vector3> Beads() => [.. BeadsLeft().Select(bead => bead.GlobalPosition)];

    /// <summary>Whether a blood bead lies within <paramref name="reach"/> (meters) of <paramref name="at"/>.</summary>
    public bool BeadNear(Vector3 at, float reach) => BeadsLeft().Any(bead => bead.GlobalPosition.DistanceTo(at) < reach);

    /// <summary>Wipes away the blood beads within <paramref name="reach"/> of <paramref name="at"/>.</summary>
    public void WipeBeads(Vector3 at, float reach)
    {
        foreach (var bead in BeadsLeft().Where(bead => bead.GlobalPosition.DistanceTo(at) < reach).ToList())
        {
            Remove(bead);
        }
    }

    /// <summary>The beads not yet gone with what they were stuck to (an IV site taken away).</summary>
    private List<MeshInstance3D> BeadsLeft()
    {
        _beads.RemoveAll(bead => !IsInstanceValid(bead));
        return _beads;
    }

    private void Remove(MeshInstance3D bead)
    {
        _beads.Remove(bead);
        if (IsInstanceValid(bead))
        {
            bead.QueueFree();
        }
    }
}
