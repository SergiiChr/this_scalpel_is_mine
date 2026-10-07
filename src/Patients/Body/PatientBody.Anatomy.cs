namespace Scalpel.Patients;

/// <summary>The cavity under the site: its floor, the blood pooling in it, organs and bones.</summary>
public partial class PatientBody
{
    /// <summary>How fast a pushed organ drifts back to where it belongs (1/s): most of the way in about half a second.
    /// </summary>
    private const float OrganReturnRate = 5f;
    /// <summary>Points per side of an organ's footprint where the skin over it is measured.</summary>
    private const int OrganSamples = 5;
    /// <summary>Cavity grid points per side.</summary>
    private const int CavitySteps = 24;
    /// <summary>How much the heart shrinks at full contraction.</summary>
    public const float HeartSqueeze = 0.12f;
    /// <summary>How much the lungs swell full of air.</summary>
    public const float LungSwell = 0.08f;
    public static readonly Color BoneColor = new(0.86f, 0.81f, 0.68f);

    /// <summary>Organs that belong under each site, for organs placed without a model of their own (one over a hidden
    /// target, filler in a deep site without anatomy data), in the order they're used.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> SiteOrgans = new Dictionary<string, string[]>
    {
        ["abdomen"] = ["bowel", "lobe", "sac"],
        ["chest"] = ["lung", "heart", "lung"],
        ["back"] = ["kidney", "bowel", "kidney"],
    };

    private readonly List<Vector3> _organLast = [];
    private readonly List<Vector2> _jiggle = [];
    /// <summary>Organ index -> site-local point a tool is holding it at (host only).</summary>
    private readonly Dictionary<int, Vector3> _heldOrgans = [];
    private ShaderMaterial _cavityMaterial = null!;
    private float _poolHeight = float.NegativeInfinity;

    public MeshInstance3D CavityBlood { get; private set; } = null!;
    public List<Organ> Organs { get; } = [];
    /// <summary>Bones under the site (ribs, breastbone, limb bones).</summary>
    public List<Bone> Bones { get; } = [];

    /// <summary>Height of the cavity floor at uv: a bowl that is deepest (CavityDepth() under the skin) in the middle
    /// and rises to just under the skin at the site's edges. It follows the skin, so on a round limb it never pokes out
    /// of the sides and is as deep on a sloping shoulder as on a flat belly.</summary>
    public float CavityFloor(Vector2 uv)
    {
        var underSkin = SurfaceHeight(uv) - SkinThickness - 0.003f;
        return Mathf.Min(Mathf.Lerp(underSkin, SurfaceHeight(uv) - CavityDepth(), Bowl(uv)), underSkin);
    }

    /// <summary>How much of the cavity's depth it has at uv: all of it in the middle, rising to nothing at the site's
    /// edges.</summary>
    private static float Bowl(Vector2 uv)
    {
        var edge = new Vector2(Mathf.Abs(uv.X * 2f - 1f), Mathf.Abs(uv.Y * 2f - 1f));
        return (1f - Mathf.Pow(edge.X, 4f)) * (1f - Mathf.Pow(edge.Y, 4f));
    }

    private static Vector2 CavityUv(int i, int j) => new Vector2(i, j) / CavitySteps;

    /// <summary>Grid triangles over the site at <paramref name="height"/>(uv), for quads whose four corners pass
    /// <paramref name="keep"/>(i, j).</summary>
    private ArrayMesh CavityGrid(Func<Vector2, float> height, Func<int, int, bool> keep)
    {
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        for (var j = 0; j < CavitySteps; j++)
        {
            for (var i = 0; i < CavitySteps; i++)
            {
                if (!(keep(i, j) && keep(i + 1, j) && keep(i + 1, j + 1) && keep(i, j + 1)))
                {
                    continue;
                }
                foreach (var (ci, cj) in new[] { (i, j), (i + 1, j), (i + 1, j + 1), (i, j), (i + 1, j + 1), (i, j + 1) })
                {
                    var uv = CavityUv(ci, cj);
                    surface.AddVertex(SitePoint(uv, height(uv)));
                }
            }
        }
        surface.GenerateNormals();
        return surface.Commit();
    }

    private void BuildCavity()
    {
        // Only under the body: past its edge the bowl would hang in the air.
        var cavity = new MeshInstance3D { Name = "Cavity", Mesh = CavityGrid(CavityFloor, (i, j) => OnBody(CavityUv(i, j))) };
        _cavityMaterial = Materials.FleshMaterial();
        cavity.MaterialOverride = _cavityMaterial;
        Site.AddChild(cavity);
        // What a tool reaching into an opening comes down on when nothing else is in the way: the bowl itself.
        var floor = new StaticBody3D { Name = "CavityFloor", CollisionLayer = CavityLayer, CollisionMask = 0 };
        floor.AddChild(new CollisionShape3D { Shape = cavity.Mesh.CreateTrimeshShape() });
        Site.AddChild(floor);
        CavityBlood = new MeshInstance3D { Name = "CavityBlood", MaterialOverride = Materials.BloodPool(), Visible = false };
        Site.AddChild(CavityBlood);
    }

    /// <summary>
    /// Adds a pushable organ. Only the host simulates them, clients get transforms from Patient. Without a spec (or a
    /// spec without a model) it's a round blob with a sphere collider; an anatomical organ collides as the box around
    /// its model.
    /// </summary>
    public Organ AddOrgan(Vector2 uv, float depth, float radius, Color color, OrganSpec? spec = null)
    {
        var anatomical = spec is { Model.Length: > 0 };
        var choices = SiteOrgans.GetValueOrDefault(SiteId, SiteOrgans["abdomen"]);
        var modelName = anatomical ? spec!.Model : choices[Organs.Count % choices.Length];
        var organ = new Organ
        {
            Name = $"Organ{Organs.Count}",
            CollisionLayer = CavityLayer,
            // Organs lie on top of each other without pushing each other around; hands push them aside.
            CollisionMask = PusherLayer,
            GravityScale = 0f,
            LinearDamp = 6f,
            AngularDamp = 6f,
            LockRotation = true,
            Mass = 0.3f,
            Freeze = !Multiplayer.IsServer(),
            Rotation = new Vector3(0, Mathf.DegToRad(spec?.Yaw ?? 0f), 0),
            Kind = modelName,
            Layer = spec?.Layer ?? 0,
            Motion = spec?.Motion ?? "",
        };
        var model = ModelSlot.Instantiate("organs", modelName, organ,
            new Dictionary<string, Material> { ["organ"] = Materials.FleshMaterial(color) });
        model.Name = "Model";
        organ.BaseScale = new Vector3(1, 1, spec?.Mirror == true ? -1 : 1) * radius;
        model.Scale = organ.BaseScale;
        var shape = new CollisionShape3D();
        var height = SurfaceHeight(uv) - depth;
        if (anatomical)
        {
            var bounds = LocalBounds(model, organ);
            shape.Shape = new BoxShape3D { Size = bounds.Size };
            shape.Position = bounds.GetCenter();
            // Top: how far under the muscle the organ's top lies, so it stays under the muscle and ribs on any patient.
            // Measured from the lowest skin over it: the body curves, the box's top is flat. Only over the site's skin on
            // the body: past it the skin drops away down the flank, under the drape, where nobody looks into the body.
            var lowest = SurfaceHeight(uv);
            for (var i = 0; i < OrganSamples; i++)
            {
                for (var j = 0; j < OrganSamples; j++)
                {
                    var corner = bounds.Position + bounds.Size * new Vector3(i, 0, j) / (OrganSamples - 1);
                    var at = new Basis(Vector3.Up, organ.Rotation.Y) * corner;
                    var over = uv + new Vector2(at.X / SiteSize.X, at.Z / SiteSize.Y);
                    if (over.X >= 0f && over.Y >= 0f && over.X <= 1f && over.Y <= 1f && OnBody(over))
                    {
                        lowest = Mathf.Min(lowest, SurfaceHeight(over));
                    }
                }
            }
            height = lowest - MuscleBottom - spec!.Top - bounds.End.Y;
        }
        else
        {
            shape.Shape = new SphereShape3D { Radius = radius };
        }
        organ.AddChild(shape);
        Site.AddChild(organ);
        organ.Position = SitePoint(uv, height);
        organ.RestPosition = organ.Position;
        Organs.Add(organ);
        _organLast.Add(organ.Position);
        _jiggle.Add(Vector2.Zero);
        return organ;
    }

    /// <summary>The box around every mesh under <paramref name="node"/>, in the space of <paramref name="space"/> (an
    /// ancestor). Works before they're in the tree.</summary>
    private static Aabb LocalBounds(Node3D node, Node3D space)
    {
        Aabb? bounds = null;
        foreach (var mesh in node.FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>())
        {
            var transform = Transform3D.Identity;
            Node at = mesh;
            while (at != space && at is Node3D spatial)
            {
                transform = spatial.Transform * transform;
                at = at.GetParent();
            }
            var box = transform * mesh.GetAabb();
            bounds = bounds is { } merged ? merged.Merge(box) : box;
        }
        return bounds ?? new Aabb();
    }

    /// <summary>
    /// Builds the site's anatomy (patient_sites.json "anatomy"): organs in layers, the rib cage, limb bones.
    /// <paramref name="avoid"/>: uv of targets that must stay in view, top layer organs over one move aside.
    /// <paramref name="skipBones"/>: a scenario target takes the bones' place (a femur to saw, a sternum to open), so
    /// the anatomical ones are left out.
    /// </summary>
    public void BuildAnatomy(IReadOnlyList<Vector2> avoid, IReadOnlyCollection<string> skipBones)
    {
        foreach (var spec in SiteDef.Organs)
        {
            AddOrgan(spec.Uv, 0f, spec.Size, spec.Color, spec);
            if (spec.Layer == 0)
            {
                ClearView(Organs.Count - 1, avoid);
            }
        }
        if (!skipBones.Contains("bone"))
        {
            foreach (var spec in SiteDef.Bones)
            {
                AddBone("Bone", [spec.From, spec.To], spec.Radius, 1f);
            }
        }
        if (SiteDef.Sternum is { } sternum && !skipBones.Contains("sternum"))
        {
            AddBone("Sternum", [sternum.From, sternum.To], sternum.Radius, 0.4f);
        }
        if (SiteDef.Ribs is not { } ribs)
        {
            return;
        }
        foreach (var row in ribs.Rows)
        {
            foreach (var side in new[] { -1f, 1f })
            {
                // From beside the breastbone out to the side of the site, dropping toward the feet as it goes.
                var points = Enumerable.Range(0, 7).Select(i =>
                {
                    var t = i / 6f;
                    return new Vector2(row - ribs.Drop * t * t, 0.5f + side * Mathf.Lerp(ribs.Inner, 0.5f, t));
                }).ToList();
                if (skipBones.Contains("rib") && points.Any(p => avoid.Any(a => a.DistanceTo(p) < 0.08f)))
                {
                    continue;
                }
                AddBone("Rib", points, ribs.Radius, 0.55f);
            }
        }
    }

    /// <summary>Moves a top layer organ off a target that has to stay in view, just far enough that its box clears it.
    /// </summary>
    private void ClearView(int index, IReadOnlyList<Vector2> avoid)
    {
        var organ = Organs[index];
        var box = ((BoxShape3D)organ.GetChild<CollisionShape3D>(organ.GetChildCount() - 1).Shape).Size;
        var reach = Mathf.Max(box.X / SiteSize.X, box.Z / SiteSize.Y) * 0.5f;
        foreach (var target in avoid)
        {
            var uv = LocalToUv(organ.Position);
            var away = uv - target;
            if (away.Length() >= reach)
            {
                continue;
            }
            uv = (target + (away.Length() > 0.001f ? away.Normalized() : Vector2.Right) * reach).Clamp(Vector2.One * 0.1f, Vector2.One * 0.9f);
            organ.Position = organ.Position with { X = (uv.X - 0.5f) * SiteSize.X, Z = (uv.Y - 0.5f) * SiteSize.Y };
            organ.RestPosition = organ.Position;
            _organLast[index] = organ.Position;
        }
    }

    /// <summary>A bone along a polyline in uv, lying right under the muscle: a tube, flattened to
    /// <paramref name="flat"/> of its width for ribs and the breastbone, with capsules along it for tools to rest on.
    /// </summary>
    private void AddBone(string kind, List<Vector2> points, float radius, float flat)
    {
        var top = MuscleBottom + 0.003f;
        // Short steps, so a straight bone still follows the curve of the skin over it.
        var dense = new List<Vector2> { points[0] };
        for (var i = 1; i < points.Count; i++)
        {
            var steps = Math.Max(1, Mathf.CeilToInt(points[i - 1].DistanceTo(points[i]) / 0.08f));
            for (var s = 0; s < steps; s++)
            {
                dense.Add(points[i - 1].Lerp(points[i], (float)(s + 1) / steps));
            }
        }
        var path = new List<Vector3>();
        foreach (var uv in dense)
        {
            // Toward the site's edges the cavity rises to the skin; a bone that no longer fits under the muscle there
            // ends.
            var height = SurfaceHeight(uv) - top - radius * flat;
            if (height - radius * flat >= CavityFloor(uv) + 0.001f)
            {
                path.Add(SitePoint(uv, height));
            }
        }
        if (path.Count < 2)
        {
            return;
        }
        var bone = new Bone
        {
            Name = $"{kind}{Bones.Count}", Kind = kind.ToLowerInvariant(), CollisionLayer = CavityLayer, CollisionMask = 0,
        };
        bone.AddChild(new MeshInstance3D
        {
            Name = "Mesh", Mesh = Shapes.Tube(path, radius, radius * flat), MaterialOverride = Materials.ToonShaded(BoneColor, 0.2f),
        });
        for (var i = 1; i < path.Count; i++)
        {
            var (a, b) = (path[i - 1], path[i]);
            var capsule = new CapsuleShape3D { Radius = radius * flat };
            capsule.Height = a.DistanceTo(b) + capsule.Radius * 2f;
            // A capsule runs along its Y axis.
            var along = (b - a).Normalized();
            var side = along.Cross(Mathf.Abs(along.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
            bone.AddChild(new CollisionShape3D
            {
                Shape = capsule, Transform = new Transform3D(new Basis(side, along, side.Cross(along)), (a + b) * 0.5f),
            });
        }
        Site.AddChild(bone);
        Bones.Add(bone);
    }

    /// <summary>The bone within <paramref name="radius"/> of p (world space), "" when there's none: "rib", "sternum"
    /// or "bone".</summary>
    public string BoneAt(Vector3 p, float radius = 0.015f) =>
        Overlapping(p, radius, CavityLayer, 32).OfType<Bone>().FirstOrDefault()?.Kind ?? "";

    /// <summary>The organ at p (world space), or -1. Of two organs lying on top of each other, the one on top is what
    /// the tool meets.</summary>
    public int OrganAt(Vector3 p, float radius = 0.012f)
    {
        var best = -1;
        foreach (var organ in Overlapping(p, radius, CavityLayer, 32).OfType<Organ>())
        {
            var index = Organs.IndexOf(organ);
            if (index >= 0 && (best < 0 || Organs[index].Position.Y > Organs[best].Position.Y))
            {
                best = index;
            }
        }
        return best;
    }

    /// <summary>Host: a tool holding an organ drags it to a site-local point; released, it drifts back where it
    /// belongs.</summary>
    public void HoldOrgan(int index, Vector3 at) => _heldOrgans[index] = at;

    public void ReleaseOrgan(int index) => _heldOrgans.Remove(index);

    /// <summary>
    /// Host: organs drift back to where they belong once you stop pushing them. Held ones go where the tool takes them.
    /// They're drawn back directly, not by a force: they ride on the site, which breathing moves every frame, and
    /// moving a body's parent puts it back where it was, so physics alone would never carry them home.
    /// </summary>
    public void SettleOrgans(float delta)
    {
        for (var i = 0; i < Organs.Count; i++)
        {
            var organ = Organs[i];
            if (_heldOrgans.TryGetValue(i, out var held))
            {
                organ.Position = held;
                organ.LinearVelocity = Vector3.Zero;
                continue;
            }
            organ.Position = organ.Position.Lerp(organ.RestPosition, 1f - Mathf.Exp(-OrganReturnRate * delta));
        }
    }

    /// <summary>Soft organs: a damped spring squashes and stretches each organ when it's pushed, on every peer. The
    /// heart beats and the lungs fill with the vitals.</summary>
    private void JiggleOrgans(float delta)
    {
        if (delta <= 0f)
        {
            return;
        }
        // The wobble spring is stiff: stepped over a long frame (a hitch, a slow renderer) it would blow up to NaN.
        var step = Mathf.Min(delta, 1f / 30f);
        for (var i = 0; i < Organs.Count; i++)
        {
            var organ = Organs[i];
            var velocity = (organ.Position - _organLast[i]) / delta;
            _organLast[i] = organ.Position;
            var state = _jiggle[i];
            state.Y += (-state.X * 180f - state.Y * 9f + Mathf.Clamp(velocity.Length() * 6f, 0f, 3f)) * step;
            state.X += state.Y * step;
            _jiggle[i] = state;
            var squash = Mathf.Clamp(state.X, -0.25f, 0.25f);
            if (organ.Model is { } model)
            {
                model.Scale = new Vector3(1f + squash, 1f - squash, 1f + squash) * organ.BaseScale * OrganMotion(i);
            }
        }
    }

    /// <summary>How much bigger than at rest the organ is drawn right now: the heart shrinks as it contracts, lungs
    /// swell.</summary>
    public float OrganMotion(int index) => Organs[index].Motion switch
    {
        "beat" => 1f - HeartSqueeze * Heartbeat,
        "breath" => 1f + LungSwell * Breath,
        _ => 1f,
    };

    /// <summary>How far (meters) an organ is from where it belongs.</summary>
    public float OrganOffset(int index) => Organs[index].Position.DistanceTo(Organs[index].RestPosition);

    public void SetOrganDamage(int index, float amount)
    {
        foreach (var mesh in Organs[index].FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>())
        {
            if (mesh.GetSurfaceOverrideMaterial(0) is ShaderMaterial material)
            {
                material.SetShaderParameter("damage", amount);
            }
        }
    }

    public Vector3[] OrganStates() => [.. Organs.Select(organ => organ.Position)];

    public void ApplyOrganStates(Vector3[] positions)
    {
        for (var i = 0; i < Math.Min(positions.Length, Organs.Count); i++)
        {
            Organs[i].Position = positions[i];
        }
    }

    /// <summary>Blood filling the cavity bowl, level 0..1. The surface only covers the part of the bowl that is under it
    /// and still under the skin, so it never shows outside the body. Rebuilt only when the level moves a millimeter or
    /// so.</summary>
    public void SetCavityBlood(float level)
    {
        var height = CavityFloor(new Vector2(0.5f, 0.5f)) + 0.002f + Mathf.Clamp(level, 0f, 1f) * CavityDepth() * 0.85f;
        CavityBlood.Visible = level > 0.01f;
        if (!CavityBlood.Visible || Mathf.Abs(height - _poolHeight) < 0.0015f)
        {
            return;
        }
        _poolHeight = height;
        CavityBlood.Mesh = CavityGrid(_ => height, (i, j) =>
        {
            var uv = CavityUv(i, j);
            return OnBody(uv) && CavityFloor(uv) < height && height < SurfaceHeight(uv) - SkinThickness;
        });
    }
}
