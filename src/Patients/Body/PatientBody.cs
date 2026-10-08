namespace Scalpel.Patients;

/// <summary>How the patient lies on the table.</summary>
public enum PatientPose { FaceUp, Side, FaceDown }

/// <summary>What a point near the patient is in.</summary>
public enum SiteZone
{
    /// <summary>Above the surgical site.</summary>
    Air,
    /// <summary>On or in the site's skin.</summary>
    Site,
    /// <summary>Inside an opening.</summary>
    Cavity,
    /// <summary>On another part of the body.</summary>
    Body,
    /// <summary>Nowhere on the patient.</summary>
    None,
}

/// <summary>What a tool tip is touching: the zone, where on the site (uv), how deep under the skin and which body part.
/// </summary>
public readonly record struct SiteProbe(SiteZone Zone, Vector2 Uv, float Depth, string Part = "");

/// <summary>Where something wrapped around a limb goes: the middle of the limb there, its axis and radius.</summary>
public readonly record struct LimbRing(Vector3 Center, Vector3 Axis, float Radius);

/// <summary>
/// Where an IV catheter sits on the arm: the node it rides (a forearm), its frame local to it (origin on the skin, X
/// along the arm toward the elbow, Y out of the skin, Z across) and the arm's radius there.
/// </summary>
public readonly record struct IvPlacement(Node3D Node, Transform3D Frame, float Radius);

/// <summary>
/// The patient you see and touch: body model, the layered tissue at the surgical site, the cavity, organs and
/// colliders. Exists on every peer. Game state lives in <see cref="Patient"/>; this node knows geometry, the wound map
/// and the tissue sim.
///
/// The surgical site is real layered tissue: skin (<see cref="TissueSim"/>, soft and under tension) over subcutaneous
/// fat over muscle over the cavity. Each layer only opens where a cut went deep enough and the sim pulled the edges
/// apart. Where nothing is cut or held, the body model itself is the skin and shows the painted damage (wound maps);
/// around cuts and pinched skin the model is cut away and the simulated layers take over (the region).
///
/// Body space: patient lies along X with the head at +X, origin at the body's center line.
/// Site space: a plane whose local XZ maps to wound map uv, +Y points out of the skin.
///
/// The class is split by concern: this file builds the body and answers where things are, PatientBody.Layers draws
/// the site's tissue layers, PatientBody.Closures the threads and staples, PatientBody.Anatomy the cavity, organs and
/// bones.
/// </summary>
public partial class PatientBody : Node3D
{
    public const float HalfHeight = 0.11f;
    public const uint SiteLayer = 4;
    public const uint PatientLayer = 2;
    public const uint CavityLayer = 32;
    /// <summary>Hands push organs aside on their own layer, so rays looking for what's in the cavity don't hit the
    /// hands.</summary>
    public const uint PusherLayer = 128;
    /// <summary>The patient's real skin (body and gown meshes at rest), for resting hands and tools on. The boxes on
    /// PatientLayer stay for what a tool touches, they're too rough to rest a hand on without sinking into a leg.
    /// </summary>
    public const uint SurfaceLayer = 256;
    public const float SkinThickness = 0.004f;
    public const float MuscleThickness = 0.006f;
    /// <summary>Subcutaneous fat where a site doesn't say ("fat" in patient_sites.json).</summary>
    public const float DefaultFat = SiteDef.DefaultFat;
    private static readonly string[] LimbSites = ["forearm", "shoulder", "thigh", "lower_leg"];
    /// <summary>A vein drawn along the inside of each forearm runs over this part of the forearm (0 the elbow, 1 the
    /// wrist).</summary>
    public static readonly Vector2 VeinSpan = new(0.15f, 0.8f);
    /// <summary>How far a vein is raised out of the skin.</summary>
    public const float VeinRadius = 0.0016f;
    public static readonly Color VeinColor = new(0.28f, 0.33f, 0.55f);
    /// <summary>How close a needle tip has to come to a vein's line to be in it (the tip rests about 1 cm over the
    /// skin).</summary>
    public const float VeinReach = 0.014f;

    private readonly List<ShaderMaterial> _bodyMaterials = [];
    /// <summary>Each forearm vein (its mesh rides the forearm bone), for <see cref="VeinAt"/>.</summary>
    private readonly List<Vein> _veins = [];
    private readonly List<Forearm> _forearms = [];
    /// <summary>Reused by <see cref="PartAt"/>, which runs every physics frame for every held tool.</summary>
    private readonly PhysicsShapeQueryParameters3D _partQuery = new();
    private readonly SphereShape3D _partSphere = new();
    private Node3D _bodyRoot = null!;
    /// <summary>The body model's skin mesh, whose space the site skin lays out its pores and grime in.</summary>
    private Node3D _skinModel = null!;
    private bool _onBack;
    private float _siteBaseY;

    public WoundMap WoundMap { get; private set; } = new();
    public string SiteId { get; private set; } = "";
    public SiteDef SiteDef { get; private set; } = null!;
    public Vector2 SiteSize { get; private set; }
    public Node3D Site { get; private set; } = null!;
    /// <summary>The thin box on SiteLayer over the site, that rays and hands meet over it.</summary>
    public StaticBody3D SiteCollider { get; private set; } = null!;
    public ShaderMaterial? SkinMaterial { get; private set; }
    public TissueSim Tissue { get; } = new();
    /// <summary>Subcutaneous fat under this site, thicker on obese patients (set before <see cref="Build"/>). None on a
    /// forearm.</summary>
    public float FatThickness { get; set; } = DefaultFat;
    /// <summary>Heart contraction 0..1, set by the animator from the vitals every frame.</summary>
    public float Heartbeat { get; set; }
    /// <summary>Lung fill 0..1, set by the animator from the vitals every frame.</summary>
    public float Breath { get; set; }
    public PatientAnimator Animator { get; } = new() { Name = "Animator" };
    public BloodFlow Blood { get; } = new() { Name = "BloodFlow" };
    public PatientPose Pose { get; private set; } = PatientPose.FaceUp;
    /// <summary>The surgical drape (operating room only), null without one.</summary>
    public Drape? Drape { get; private set; }

    /// <summary>The node that carries the body model, colliders and site. It turns with the patient.</summary>
    public Node3D Root => _bodyRoot;

    public void Build(string siteName, Color tone, float ageScale)
    {
        SiteId = siteName;
        SiteDef = SiteDef.Of(siteName);
        WoundMap = new WoundMap(WoundMap.SizeFor(SiteDef.Size));
        _bodyRoot = new Node3D { Name = "BodyRoot", Position = new Vector3(0, HalfHeight * ageScale, 0), Scale = Vector3.One * ageScale };
        AddChild(_bodyRoot);
        var skin = Materials.BodySkin(tone);
        // The gown gets the same carve-capable material, or it would show through the surgical site on the hips.
        var gown = Materials.BodySkin(Materials.PatientGown);
        _bodyMaterials.AddRange([skin, gown]);
        foreach (var material in _bodyMaterials)
        {
            Materials.SetSiteMaps(material, WoundMap.Texture(WoundMap.Layer.Wounds), WoundMap.Texture(WoundMap.Layer.Fluids));
        }
        var model = ModelSlot.Instantiate("patient", "body", _bodyRoot,
            new Dictionary<string, Material> { ["skin"] = skin, ["gown"] = gown });
        _skinModel = (Node3D)model.FindChild("Body", true, false);
        AddChild(Animator);
        Animator.Setup(this, model);
        BuildColliders();
        BuildSurface(model);
        BuildSite(tone, model);
        BuildClosureDrawing();
        BuildVeins(model);
        AddChild(Blood);
        Blood.Setup(this);
    }

    public override void _Process(double delta)
    {
        WoundMap.Flush();
        // The meshes catch up with the sim on the frame after it stepped, so a frame that steps the sim (30 times a
        // second) isn't also the frame that rebuilds them: the two costs land on alternate frames.
        var stale = Tissue.TopologyVersion != _layerVersion || Tissue.StepsDone != _layerSteps;
        var rebuild = stale && !_rebuiltLast;
        if (rebuild)
        {
            RebuildLayers();
        }
        _rebuiltLast = rebuild;
        Tissue.Advance((float)delta, !rebuild);
        UpdateSutures();
        UpdateStaples();
        JiggleOrgans((float)delta);
    }

    public bool IsLimbSite => LimbSites.Contains(SiteId);

    /// <summary>
    /// How deep the cavity is under the skin in the middle of the site: at least deep enough for the site's bones to
    /// fit under the muscle from their middle on, where the cavity can already be shallower (a thick fat layer, a bone
    /// off to one side like the shoulder's).
    /// </summary>
    public float CavityDepth()
    {
        var depth = SiteDef.Depth;
        foreach (var bone in SiteDef.Bones)
        {
            var bowl = Bowl((bone.From + bone.To) * 0.5f);
            var needed = MuscleBottom + 0.003f + bone.Radius * 2f + 0.004f;
            depth = Mathf.Max(depth, (needed - (1f - bowl) * (SkinThickness + 0.003f)) / bowl);
        }
        return depth;
    }

    /// <summary>How far under the skin the muscle layer ends: bones lie right under it, organs further down.</summary>
    public float MuscleBottom => SkinThickness + FatThickness + MuscleThickness;

    /// <summary>Meters under the skin each layer starts: skin, fat, muscle.</summary>
    private float LayerTop(int layer) => layer switch
    {
        0 => 0f,
        1 => SkinThickness,
        _ => SkinThickness + FatThickness,
    };

    /// <summary>The site faces up the way the patient lies, so it can be worked on.</summary>
    public bool SiteActive => Pose == (_onBack ? PatientPose.FaceDown : PatientPose.FaceUp);

    public void SetPose(PatientPose value)
    {
        Pose = value;
        _bodyRoot.Rotation = _bodyRoot.Rotation with { X = value switch { PatientPose.Side => Mathf.Pi / 2, PatientPose.FaceDown => Mathf.Pi, _ => 0f } };
        UpdateCarve();
        // Turned away from the site, the drape would lie between the patient and the table.
        if (Drape is not null)
        {
            Drape.Visible = SiteActive;
        }
    }

    /// <summary>The body model's parts the drape lies over.</summary>
    private static readonly string[] DrapedParts = ["Body", "Gown"];

    /// <summary>Lays the surgical drape over the patient, open over the site (operating room only; call after
    /// <see cref="Build"/>).</summary>
    public void AddDrape()
    {
        var meshes = DrapedParts.Select(name => _bodyRoot.FindChild(name, true, false)).OfType<MeshInstance3D>();
        Drape = new Drape();
        _bodyRoot.AddChild(Drape);
        Drape.Build(_bodyRoot, meshes, Site, SiteSize, _onBack ? -1f : 1f);
        Drape.Visible = SiteActive;
        LaySkinOnDrape(Drape);
    }

    /// <summary>
    /// Skin flaps folded out of the drape's opening lie on the drape instead of passing through it: the drape's height
    /// over and around the site (site space, a grid out to a site's size past each edge). Only the skin is held up, the
    /// layers drawn under it stay under the drape: lifting the flap by their thickness too would tear it off its edge.
    /// Only skin that starts inside the opening is held up; the site's edge stays under the drape's frame.
    /// </summary>
    private void LaySkinOnDrape(Drape drape)
    {
        const float Cell = 0.01f;
        var drapeMesh = new TriangleMesh();
        var toSite = Site.Transform.AffineInverse() * drape.Transform;
        drapeMesh.CreateFromFaces([.. drape.Mesh.GetFaces().Select(v => toSite * v)]);
        var columns = Mathf.CeilToInt(SiteSize.X * 3f / Cell) + 1;
        var rows = Mathf.CeilToInt(SiteSize.Y * 3f / Cell) + 1;
        var heights = new float[columns * rows];
        for (var j = 0; j < rows; j++)
        {
            for (var i = 0; i < columns; i++)
            {
                var hit = drapeMesh.IntersectRay(new Vector3(-SiteSize.X * 1.5f + i * Cell, 0.5f, -SiteSize.Y * 1.5f + j * Cell), Vector3.Down);
                heights[j * columns + i] = hit.Count > 0 ? hit["position"].AsVector3().Y + 0.004f : float.NaN;
            }
        }
        var origin = new Vector2(-SiteSize.X * 1.5f, -SiteSize.Y * 1.5f);
        Tissue.FloorAt = (x, z) =>
        {
            var c = (Vector2I)((new Vector2(x, z) - origin) / Cell).Round();
            return c.X < 0 || c.Y < 0 || c.X >= columns || c.Y >= rows ? float.NaN : heights[c.Y * columns + c.X];
        };
        // The opening: the site short of the drape's frame, a cell further in to be safe.
        var frame = SiteSize * Drape.Frame + Vector2.One * Cell;
        Tissue.FloorOpen = new Rect2(-SiteSize * 0.5f + frame, SiteSize - frame * 2f);
        var exposed = new bool[Tissue.Rest.Length];
        for (var k = 0; k < exposed.Length; k++)
        {
            var uv = Tissue.UvOf(k);
            // Inside the opening, and not under the drape's edge where it slopes down to the skin.
            var floorY = Tissue.FloorAt(Tissue.Settled[k].X, Tissue.Settled[k].Z);
            var open = float.IsNaN(floorY) || floorY <= Tissue.Settled[k].Y + 0.002f;
            exposed[k] = open && uv.X > Drape.Frame && uv.X < 1f - Drape.Frame && uv.Y > Drape.Frame && uv.Y < 1f - Drape.Frame;
        }
        Tissue.Exposed = exposed;
    }

    // --- Space conversion ---------------------------------------------------------------------------------

    public Vector2 WorldToUv(Vector3 p)
    {
        var local = Site.ToLocal(p);
        return new Vector2(local.X / SiteSize.X + 0.5f, local.Z / SiteSize.Y + 0.5f);
    }

    /// <summary>Site uv of a site-local point.</summary>
    public Vector2 LocalToUv(Vector3 local) => new(local.X / SiteSize.X + 0.5f, local.Z / SiteSize.Y + 0.5f);

    public Vector3 UvToWorld(Vector2 uv, float depth = 0f) => Site.ToGlobal(SitePoint(uv, SurfaceHeight(uv) - depth));

    /// <summary>The site-local point over uv at <paramref name="height"/> above the site plane.</summary>
    public Vector3 SitePoint(Vector2 uv, float height) => new((uv.X - 0.5f) * SiteSize.X, height, (uv.Y - 0.5f) * SiteSize.Y);

    /// <summary>Meters between the skin and p along the site normal. Negative = under the skin.</summary>
    public float HeightAboveSite(Vector3 p)
    {
        var local = Site.ToLocal(p);
        return local.Y - SkinHeight(LocalToUv(local));
    }

    /// <summary>Skin height at uv as it's drawn now: the simulated skin where it replaces the body (pulled, pressed or
    /// cut), the body's own rest surface everywhere else and over an opening.</summary>
    public float SkinHeight(Vector2 uv)
    {
        var k = Tissue.Nearest(uv);
        if (k < _region.Length && _region[k] == 1)
        {
            var height = Tissue.SkinHeight(uv);
            if (!float.IsNaN(height))
            {
                // Drawn off the sim's skin (see _onModel): by as much as its grid points around uv are.
                var p = uv.Clamp(Vector2.Zero, Vector2.One) * new Vector2(Tissue.ResX, Tissue.ResY);
                var at = new Vector2I(Math.Min((int)p.X, Tissue.ResX - 1), Math.Min((int)p.Y, Tissue.ResY - 1));
                var f = p - (Vector2)at;
                float Drawn(int i, int j)
                {
                    var n = Tissue.Index(at.X + i, at.Y + j);
                    return LayerPoint(0, n).Y - Tissue.Pos[n].Y;
                }
                return height + Mathf.Lerp(Mathf.Lerp(Drawn(0, 0), Drawn(1, 0), f.X), Mathf.Lerp(Drawn(0, 1), Drawn(1, 1), f.X), f.Y);
            }
        }
        return SurfaceHeight(uv);
    }

    /// <summary>Skin height relative to the flat site plane at uv, from the heights measured on the body model (0 when
    /// flat).</summary>
    public float SurfaceHeight(Vector2 uv)
    {
        if (_heights.Length != Heights * Heights)
        {
            return 0f;
        }
        var p = uv.Clamp(Vector2.Zero, Vector2.One) * (Heights - 1);
        var x0 = Math.Min((int)p.X, Heights - 2);
        var y0 = Math.Min((int)p.Y, Heights - 2);
        var f = p - new Vector2(x0, y0);
        var top = Mathf.Lerp(_heights[y0 * Heights + x0], _heights[y0 * Heights + x0 + 1], f.X);
        var bottom = Mathf.Lerp(_heights[(y0 + 1) * Heights + x0], _heights[(y0 + 1) * Heights + x0 + 1], f.X);
        return Mathf.Lerp(top, bottom, f.Y);
    }

    /// <summary>False where the site hangs off the body, or the body under it is too thin to hold the site's layers.
    /// </summary>
    public bool OnBody(Vector2 uv)
    {
        if (_onBody.Length != Heights * Heights)
        {
            return true;
        }
        var cell = (Vector2I)(uv.Clamp(Vector2.Zero, Vector2.One) * (Heights - 1)).Round();
        return _onBody[cell.Y * Heights + cell.X];
    }

    /// <summary>Blood on the skin at uv, 0..1, from the fluid map.</summary>
    public float BloodAt(Vector2 uv)
    {
        if (uv.X < 0f || uv.Y < 0f || uv.X > 1f || uv.Y > 1f)
        {
            return 0f;
        }
        return WoundMap.Value(WoundMap.Layer.Fluids, WoundMap.Blood, uv);
    }

    public float UvToMeters(float uvLength) => uvLength * (SiteSize.X + SiteSize.Y) * 0.5f;

    public float MetersToUv(float meters) => meters / ((SiteSize.X + SiteSize.Y) * 0.5f);

    /// <summary>What a tool tip at p is touching.</summary>
    public SiteProbe Probe(Vector3 p)
    {
        var uv = WorldToUv(p);
        var height = HeightAboveSite(p);
        var onSite = SiteActive && uv.X >= 0f && uv.X <= 1f && uv.Y >= 0f && uv.Y <= 1f && OnBody(uv);
        if (onSite && height > -CavityDepth())
        {
            if (height > 0.012f)
            {
                return new SiteProbe(SiteZone.Air, uv, 0f);
            }
            return Tissue.IsOpen(uv)
                ? new SiteProbe(SiteZone.Cavity, uv, -height)
                : new SiteProbe(SiteZone.Site, uv, Mathf.Max(0f, -height));
        }
        var part = PartAt(p);
        return new SiteProbe(part.Length > 0 ? SiteZone.Body : SiteZone.None, uv, 0f, part);
    }

    /// <summary>The body part within <paramref name="radius"/> of p (world space), "" for none.</summary>
    public string PartAt(Vector3 p, float radius = 0.025f) =>
        Overlapping(p, radius, PatientLayer, 4).OfType<BodyPart>().FirstOrDefault()?.Part ?? "";

    /// <summary>The colliders on <paramref name="mask"/> within <paramref name="radius"/> of p (world space).</summary>
    private IEnumerable<GodotObject> Overlapping(Vector3 p, float radius, uint mask, int most)
    {
        _partSphere.Radius = radius;
        _partQuery.Shape = _partSphere;
        _partQuery.Transform = new Transform3D(Basis.Identity, p);
        _partQuery.CollisionMask = mask;
        return GetWorld3D().DirectSpaceState.IntersectShape(_partQuery, most).Select(hit => hit["collider"].AsGodotObject());
    }

    /// <summary>
    /// The ring of a limb's skin around p (world space), for something wrapped around it, null when p isn't on an arm
    /// or a leg. Arms and legs lie along the body; rays cast out from inside the limb find its skin.
    /// </summary>
    public LimbRing? LimbRingAt(Vector3 p)
    {
        // The limb boxes are rough and thinner than the limbs, so look well around p; the skin found decides.
        var part = PartAt(p, 0.08f);
        if (!(part.StartsWith("arm", StringComparison.Ordinal) || part.StartsWith("leg", StringComparison.Ordinal)))
        {
            return null;
        }
        var box = (Node3D)_bodyRoot.FindChild(part.ToPascalCase(), false, false);
        var axis = _bodyRoot.GlobalBasis.X.Normalized();
        var inside = box.GlobalPosition + axis * axis.Dot(p - box.GlobalPosition);
        var space = GetWorld3D().DirectSpaceState;
        var hits = new List<Vector3>();
        for (var i = 0; i < 16; i++)
        {
            var outward = new Basis(axis, Mathf.Tau * i / 16f) * _bodyRoot.GlobalBasis.Y.Normalized();
            var query = PhysicsRayQueryParameters3D.Create(inside, inside + outward * 0.2f, SurfaceLayer);
            query.HitBackFaces = true;
            query.HitFromInside = true;
            var hit = space.IntersectRay(query);
            if (hit.Count > 0)
            {
                hits.Add(hit["position"].AsVector3());
            }
        }
        if (hits.Count < 8)
        {
            return null;
        }
        var center = hits.Aggregate(Vector3.Zero, (sum, hit) => sum + hit) / hits.Count;
        var radius = hits.Max(hit => (hit - center).Slide(axis).Length());
        // Only when p is right at this limb's skin, not somewhere above it.
        return (p - center).Slide(axis).Length() > radius + 0.03f ? null : new LimbRing(center, axis, radius);
    }

    /// <summary>Cut through every layer and pulled open, so tools reach into the cavity.</summary>
    public bool IsOpen(Vector2 uv) => Tissue.IsOpen(uv);

    /// <summary>How deep (meters under the skin) a cut within <paramref name="radius"/> (uv) of uv goes: to the bottom
    /// of the deepest layer it cut through, 0 where there's no cut.</summary>
    public float OpeningDepth(Vector2 uv, float radius) => Tissue.DeepestCut(uv, radius) switch
    {
        TissueDepth.Skin => SkinThickness,
        TissueDepth.Fat => SkinThickness + FatThickness,
        TissueDepth.Muscle => MuscleBottom,
        _ => 0f,
    };

    /// <summary>The deepest layer showing at uv: "skin" where it's whole, "fat" or "muscle" where a cut opened down to
    /// it, "cavity" where it's open through the muscle.</summary>
    public string LayerAt(Vector2 uv)
    {
        if (Tissue.IsOpen(uv))
        {
            return "cavity";
        }
        if (Tissue.IsOpen(uv, TissueDepth.Fat))
        {
            return "muscle";
        }
        if (Tissue.IsOpen(uv, TissueDepth.Skin))
        {
            // No fat on this part of the body: the muscle lies right under the skin.
            return FatThickness > 0.0005f ? "fat" : "muscle";
        }
        return "skin";
    }

    /// <summary>
    /// Where an IV catheter going in at p (world space, just under the skin) sits on the arm. The arm counts as round
    /// about the forearm bone, clamped to its ends (the back of the hand counts as the wrist). Falls back to the body
    /// when there are no forearms.
    /// </summary>
    public IvPlacement IvSite(Vector3 p)
    {
        Forearm? best = null;
        var center = Vector3.Zero;
        foreach (var forearm in _forearms)
        {
            var onBone = Geometry3D.GetClosestPointToSegment(p, forearm.GlobalPosition, forearm.ToGlobal(forearm.Wrist));
            if (best is null || onBone.DistanceTo(p) < center.DistanceTo(p))
            {
                best = forearm;
                center = onBone;
            }
        }
        if (best is null)
        {
            var flat = new Transform3D(_bodyRoot.GlobalBasis.Orthonormalized(), p);
            return new IvPlacement(_bodyRoot, _bodyRoot.GlobalTransform.AffineInverse() * flat, 0.035f);
        }
        var elbow = (best.GlobalPosition - best.ToGlobal(best.Wrist)).Normalized();
        var outward = (p - center).Slide(elbow);
        var radius = outward.Length() + 0.002f;
        var normal = outward.Normalized();
        var world = new Transform3D(new Basis(elbow, normal, elbow.Cross(normal)), center + normal * radius);
        return new IvPlacement(best, best.GlobalTransform.AffineInverse() * world, radius);
    }

    /// <summary>The forearm veins, for finding where to put a needle.</summary>
    public IReadOnlyList<Vein> Veins => _veins;

    /// <summary>True when p (world space) is in or just over a forearm vein.</summary>
    public bool VeinAt(Vector3 p)
    {
        foreach (var vein in _veins)
        {
            var local = vein.ToLocal(p);
            for (var i = 1; i < vein.Line.Length; i++)
            {
                if (Geometry3D.GetClosestPointToSegment(local, vein.Line[i - 1], vein.Line[i]).DistanceTo(local) < VeinReach)
                {
                    return true;
                }
            }
        }
        return false;
    }

    // --- Construction -------------------------------------------------------------------------------------

    private void BuildColliders()
    {
        var parts = new (string Part, Vector3 Size, Vector3 Position)[]
        {
            ("torso", new(0.62f, 0.22f, 0.38f), new(0.12f, 0, 0)),
            ("pelvis", new(0.22f, 0.2f, 0.36f), new(-0.3f, 0, 0)),
            ("neck", new(0.14f, 0.1f, 0.1f), new(0.5f, 0, 0)),
            ("head", new(0.2f, 0.2f, 0.2f), new(0.67f, 0.03f, 0)),
            ("arm_right", new(0.7f, 0.09f, 0.09f), new(0.12f, -0.02f, 0.25f)),
            ("arm_left", new(0.7f, 0.09f, 0.09f), new(0.12f, -0.02f, -0.25f)),
            ("leg_right", new(0.95f, 0.14f, 0.14f), new(-0.8f, -0.02f, 0.1f)),
            ("leg_left", new(0.95f, 0.14f, 0.14f), new(-0.8f, -0.02f, -0.1f)),
        };
        foreach (var (part, size, position) in parts)
        {
            var body = new BodyPart
            {
                Name = part.ToPascalCase(),
                Part = part,
                CollisionLayer = PatientLayer,
                CollisionMask = 0,
                Position = position,
            };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            _bodyRoot.AddChild(body);
        }
    }

    /// <summary>A vein on the upper side of each forearm, found on the body mesh at rest and carried by the forearm
    /// bone, so it moves with the arm. A forearm under the surgical site gets none: the site's own skin lies there.
    /// </summary>
    private void BuildVeins(Node3D model)
    {
        if (model.FindChild("Body", true, false) is not MeshInstance3D skin
            || model.FindChildren("*", "Skeleton3D", true, false).FirstOrDefault() is not Skeleton3D skeleton)
        {
            return;
        }
        var faces = skin.Mesh.GenerateTriangleMesh();
        var toSkin = skin.GlobalTransform.AffineInverse();
        var up = _bodyRoot.GlobalBasis.Y.Normalized();
        foreach (var side in new[] { "L", "R" })
        {
            var bone = skeleton.FindBone("Forearm" + side);
            var hand = skeleton.FindBone("Hand" + side);
            if (bone < 0 || hand < 0)
            {
                continue;
            }
            var bonePose = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone);
            var wrist = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(hand).Origin;
            var forearm = new Forearm { Name = "Forearm" + side, BoneName = "Forearm" + side };
            skeleton.AddChild(forearm);
            forearm.Transform = skeleton.GetBoneGlobalPose(bone);
            forearm.Wrist = bonePose.AffineInverse() * wrist;
            _forearms.Add(forearm);
            var across = (wrist - bonePose.Origin).Cross(up).Normalized();
            var line = new List<Vector3>();
            for (var i = 0; i < 12; i++)
            {
                var t = Mathf.Lerp(VeinSpan.X, VeinSpan.Y, i / 11f);
                // A gentle wander across the arm, like a real vein.
                var over = bonePose.Origin.Lerp(wrist, t) + across * Mathf.Sin(t * 9f) * 0.004f;
                var hit = faces.IntersectRay(toSkin * (over + up * 0.15f), (toSkin.Basis * -up).Normalized());
                if (hit.Count == 0)
                {
                    continue;
                }
                var onSkin = skin.GlobalTransform * hit["position"].AsVector3();
                if (SiteActive && new Rect2(0, 0, 1, 1).HasPoint(WorldToUv(onSkin)))
                {
                    line.Clear();
                    break;
                }
                // Mostly under the skin: only a low ridge of it shows.
                line.Add(onSkin - up * VeinRadius * 0.4f);
            }
            if (line.Count < 2)
            {
                continue;
            }
            var into = bonePose.AffineInverse();
            var local = line.Select(point => into * point).ToArray();
            var vein = new Vein
            {
                Name = "Vein",
                Mesh = Shapes.Tube(local, VeinRadius * 1.3f, VeinRadius),
                MaterialOverride = Materials.ToonShaded(VeinColor, 0.1f, false, 0.4f),
                Line = local,
            };
            forearm.AddChild(vein);
            _veins.Add(vein);
        }
    }

    private void BuildSurface(Node3D model)
    {
        foreach (var partName in new[] { "Body", "Gown" })
        {
            if (model.FindChild(partName, true, false) is not MeshInstance3D mesh)
            {
                continue;
            }
            var skin = mesh.Mesh.CreateTrimeshShape();
            // So rays from inside a limb find its skin too (LimbRingAt()).
            skin.BackfaceCollision = true;
            var surface = new StaticBody3D { Name = partName + "Surface", CollisionLayer = SurfaceLayer, CollisionMask = 0 };
            surface.AddChild(new CollisionShape3D { Shape = skin });
            _bodyRoot.AddChild(surface);
            surface.Transform = _bodyRoot.GlobalTransform.AffineInverse() * mesh.GlobalTransform;
        }
    }

    /// <summary>Breathing lifts sites that sit on top of the torso together with the chest.</summary>
    public void SetBreathOffset(float offset)
    {
        if (SiteId is "abdomen" or "chest" or "shoulder")
        {
            Site.Position = Site.Position with { Y = _siteBaseY + offset };
            UpdateCarve();
        }
        // The drape lies on the trunk, so it rises with it.
        if (Drape is not null)
        {
            Drape.Position = Drape.Position with { Y = offset };
        }
    }

    /// <summary>Where the body model is cut away (the region), the simulated skin layers take over.</summary>
    private void UpdateCarve()
    {
        foreach (var material in _bodyMaterials)
        {
            Materials.SetCarve(material, Site.GlobalTransform, SiteSize * 0.5f, CavityDepth() + 0.02f, RegionTexture);
        }
        SkinMaterial?.SetShaderParameter("site_to_model", new Projection(_skinModel.GlobalTransform.AffineInverse() * Site.GlobalTransform));
        Materials.SetReveal(_cavityMaterial, Site.GlobalTransform, SiteSize * 0.5f, RegionTexture);
    }

    /// <summary>Blood loss drains the color from the skin, body and site alike.</summary>
    public void SetPallor(float value)
    {
        SkinMaterial?.SetShaderParameter("pallor", value);
        _bodyMaterials[0].SetShaderParameter("pallor", value);
    }

    /// <summary>
    /// The site's shape as measured on the body model (heights, points off the body, where the skin rests on it): the
    /// same on every peer that built the same patient, so every player sees the same site. FNV-1a, so it doesn't change
    /// between processes.
    /// </summary>
    public ulong ShapeHash()
    {
        var hash = 14695981039346656037UL;
        void Add(float value) => hash = (hash ^ BitConverter.SingleToUInt32Bits(value)) * 1099511628211UL;
        foreach (var height in _heights)
        {
            Add(height);
        }
        foreach (var on in _onBody)
        {
            Add(on ? 1f : 0f);
        }
        foreach (var point in _onModel)
        {
            Add(point.X);
            Add(point.Y);
            Add(point.Z);
        }
        return hash;
    }
}
