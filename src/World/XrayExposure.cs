using System.Threading.Tasks;

namespace Scalpel.World;

/// <summary>The cart's two views. Ap: the beam straight down from the tube head. Lateral: level and forward, from below
/// it.</summary>
public enum XrayView { Ap, Lateral }

/// <summary>Something a film shows: what it is and where on the film (0..1, x to the film's right, y down).</summary>
public readonly record struct XrayFinding(string Kind, Vector2 At);

/// <summary>A beam: <see cref="Source"/> looks along its -Z (the film's up its Y, the film's right its X), and
/// <see cref="Size"/> is the field across, in meters. It reaches <see cref="XrayExposure.Depth"/> past the source.
/// </summary>
public readonly record struct XrayBeam(XrayView View, Transform3D Source, Vector2 Size) {
    /// <summary>Where <paramref name="point"/> lands on the film, null outside the field.</summary>
    public Vector2? FilmPoint(Vector3 point) {
        var local = Source.AffineInverse() * point;
        var at = new Vector2((local.X / Size.X) + 0.5f, 0.5f - (local.Y / Size.Y));
        var inside = at.X is >= 0f and <= 1f && at.Y is >= 0f and <= 1f && local.Z < 0f && -local.Z < XrayExposure.Depth;
        return inside ? at : null;
    }
}

/// <summary>A developed film: the beam it was taken with, what it shows, and its picture (null headless, where nothing
/// is drawn).</summary>
public sealed record XrayFilm(XrayBeam Beam, IReadOnlyList<XrayFinding> Findings, Texture2D? Image);

/// <summary>
/// Takes X-ray films of the scene as it is: twins of the patient's body, organs, what's lodged in it and the tools near
/// it, drawn with xray.gdshader on the <see cref="RenderLayers.Xray"/> layer next to the patient's skeleton, seen by an
/// orthographic camera per beam. Everyone takes their own, from the same synced scene. The twins live for one frame.
/// </summary>
public static class XrayExposure {
    /// <summary>How far a beam reaches past its source, meters: through the patient, the table and to the floor.
    /// </summary>
    public const float Depth = 1.8f;
    /// <summary>Film pixels per meter of field.</summary>
    private const float PixelsPerMeter = 1200f;
    private static readonly string[] Metal = ["bullet", "knife", "figurine"];
    /// <summary>Bone, and glass, which stops the beam about as well.</summary>
    private static readonly string[] Bone = ["bone", "splinter", "shard", "nasal_hump", "sternum", "skull_flap"];
    /// <summary>Hollow broken pieces of shaft: the beam crosses their walls four times.</summary>
    private static readonly string[] Pieces = ["fragment", "rib"];
    private static readonly string[] Air = ["air"];
    /// <summary>Skeleton parts a break can show in: a gap where the broken pieces lie.</summary>
    private static readonly string[] LongBones = ["SkeletonUpperArm", "SkeletonForearm", "SkeletonThigh", "SkeletonShin"];
    /// <summary>How far around a break's middle the bone is left out, meters: a fragment's half length and some.
    /// </summary>
    private const float BreakRadius = 0.06f;
    /// <summary>How far from the patient's middle (meters) a tool can be and still lie in a field: the length of a
    /// body.</summary>
    private const float ToolReach = 1.2f;

    // Densities for xray.gdshader. Soft tissue shows by its outline, organs fainter; bone and metal go white.
    internal static Material TissueMaterial => Materials.Xray(0.07f, 0.55f);
    private static Material OrganWall => Materials.Xray(0.015f, 0.18f);
    private static Material Mass => Materials.Xray(0.06f, 0.3f);
    internal static Material BoneMaterial => Materials.Xray(0.3f, 0.12f, 0.7f);
    private static Material PieceMaterial => Materials.Xray(0.15f, 0.12f, 0.7f);
    private static Material MetalMaterial => Materials.Xray(2f, 0f);
    /// <summary>A wound track: torn tissue and the debris a bullet leaves along it.</summary>
    private static Material TrackMaterial => Materials.Xray(0.2f, 0.4f);

    /// <summary>Takes a film per beam, each drawn into its viewport (from <see cref="Viewport"/>), which keeps the
    /// picture until the next film it's given.</summary>
    public static async Task<XrayFilm[]> Take(IReadOnlyList<SubViewport> viewports, Surgery surgery,
        IReadOnlyList<XrayBeam> beams) {
        var findings = beams.Select(beam => Findings(surgery, beam)).ToList();
        // Headless nothing is drawn: the films keep what they show but no picture.
        if (DisplayServer.GetName() == "headless") {
            return [.. beams.Select((beam, i) => new XrayFilm(beam, findings[i], null))];
        }
        // Spread over frames so no frame does it all: the findings, then the twins, then the draw. The twins are
        // placed, and the skinned one bound to its skeleton, by the frame after they're made. The films are drawn in
        // that frame's own draw: no second draw of the scene, no reading the pictures back from the GPU.
        var tree = surgery.GetTree();
        await surgery.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        var twins = Twins(surgery);
        var broken = Breaks(surgery.Patient);
        for (var i = 0; i < beams.Count; i++) {
            var camera = viewports[i].GetCamera3D();
            camera.Transform = beams[i].Source;
            camera.Size = beams[i].Size.Y;
        }
        await surgery.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        foreach (var viewport in viewports) {
            viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        }
        if (RenderingServer.RenderLoopEnabled) {
            await surgery.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        } else {
            // Tests that time the game's own work turn drawing off; then the films are drawn on their own.
            await surgery.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            RenderingServer.ForceDraw(false);
        }
        foreach (var twin in twins) {
            twin.QueueFree();
        }
        foreach (var bone in broken) {
            bone.MaterialOverride = BoneMaterial;
        }
        return [.. beams.Select((beam, i) => new XrayFilm(beam, findings[i], viewports[i].GetTexture()))];
    }

    /// <summary>What's in the beam's field: everything lodged in the patient, and tools left inside.</summary>
    private static List<XrayFinding> Findings(Surgery surgery, XrayBeam beam) {
        var found = new List<XrayFinding>();
        foreach (var target in surgery.Patient.Targets.Where(target => target.Visible && !target.Extracted)) {
            if (beam.FilmPoint(target.GlobalPosition) is { } at) {
                found.Add(new XrayFinding(target.Kind, at));
            }
        }
        foreach (var tool in surgery.Tools.Tools.Values.Where(tool => tool.State == ToolState.Inside)) {
            if (beam.FilmPoint(tool.GlobalPosition) is { } at) {
                found.Add(new XrayFinding("tool", at));
            }
        }
        return found;
    }

    /// <summary>A viewport under <paramref name="holder"/> to take films of <paramref name="size"/> (meters) in. Made
    /// once and kept: it draws only when it's given a film, and keeps the picture.</summary>
    public static SubViewport Viewport(Node holder, string name, Vector2 size) {
        var viewport = new SubViewport {
            Name = name,
            Size = new Vector2I(Mathf.RoundToInt(size.X * PixelsPerMeter), Mathf.RoundToInt(size.Y * PixelsPerMeter)),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            Msaa3D = Godot.Viewport.Msaa.Msaa4X,
        };
        var camera = new Camera3D {
            Projection = Camera3D.ProjectionType.Orthogonal,
            KeepAspect = Camera3D.KeepAspectEnum.Height,
            Size = size.Y,
            Near = 0.01f,
            Far = Depth,
            CullMask = RenderLayers.Xray,
            Environment = new Godot.Environment {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = Colors.Black,
                AmbientLightSource = Godot.Environment.AmbientSource.Disabled,
                TonemapMode = Godot.Environment.ToneMapper.Linear,
            },
        };
        viewport.AddChild(camera);
        holder.AddChild(viewport);
        camera.Current = true;
        return viewport;
    }

    /// <summary>The scene as the beam sees it, on the X-ray layer. The patient's skeleton is there already
    /// (PatientBody).</summary>
    private static List<Node3D> Twins(Surgery surgery) {
        var patient = surgery.Patient;
        var root = new Node3D { Name = "XrayTwins" };
        surgery.AddChild(root);
        var twins = new List<Node3D> { root, SkinnedTwin(patient.Body.SkinMesh, TissueMaterial) };
        foreach (var organ in patient.Body.Organs) {
            twins.AddRange(Meshes(organ).Where(mesh => mesh.IsVisibleInTree()).Select(mesh => Twin(root, mesh, OrganWall)));
        }
        foreach (var target in patient.Targets.Where(target => target.Visible && !target.Extracted)) {
            var material = Metal.Contains(target.Kind) ? MetalMaterial
                : Bone.Contains(target.Kind) ? BoneMaterial
                : Pieces.Contains(target.Kind) ? PieceMaterial
                : Air.Contains(target.Kind) ? Materials.Xray(0f, 0.25f)
                : Mass;
            // Covered ones aren't drawn until the tissue over them is opened, but the beam goes through it.
            twins.AddRange(Meshes(target).Select(mesh => Twin(root, mesh, material)));
            if (target.Kind == "bullet" && Track(root, patient, target) is { } track) {
                twins.Add(track);
            }
        }
        // Only tools at the table can be in a field: the trays and shelves are left out.
        var table = patient.GlobalPosition;
        foreach (var tool in surgery.Tools.Tools.Values.Where(tool => tool.Visible && tool.GlobalPosition.DistanceTo(table) < ToolReach)) {
            twins.AddRange(Meshes(tool).Where(mesh => mesh.IsVisibleInTree()).Select(mesh => Twin(root, mesh, MetalMaterial)));
        }
        return twins;
    }

    /// <summary>Long bones with broken pieces in them get a gap where the pieces lie, so the film shows the pieces as
    /// they lie: apart, or lined back up. Returns the bones to give their own material back.</summary>
    private static List<MeshInstance3D> Breaks(Patient patient) {
        var pieces = patient.Targets.Where(target => target.IsFragment || target.IsBone)
            .Where(target => target.Visible && !target.Extracted)
            .Select(target => target.GlobalPosition)
            .ToList();
        var broken = new List<MeshInstance3D>();
        if (pieces.Count == 0) {
            return broken;
        }
        var middle = pieces.Aggregate(Vector3.Zero, (sum, piece) => sum + piece) / pieces.Count;
        foreach (var bone in patient.Body.Skeleton) {
            if (LongBones.Any(name => bone.Name.ToString().StartsWith(name))
                && (bone.GlobalTransform * bone.GetAabb()).Grow(0.01f).HasPoint(middle)) {
                var gapped = (ShaderMaterial)BoneMaterial.Duplicate();
                gapped.SetShaderParameter("gap_center", middle);
                gapped.SetShaderParameter("gap_radius", BreakRadius);
                bone.MaterialOverride = gapped;
                broken.Add(bone);
            }
        }
        return broken;
    }

    private static IEnumerable<MeshInstance3D> Meshes(Node node) =>
        node.FindChildren("*", nameof(MeshInstance3D), true, false).Cast<MeshInstance3D>();

    /// <summary>A copy where the mesh is, drawn only on films. Under its own root, so it's drawn even where the mesh is
    /// hidden (a bullet under closed skin).</summary>
    private static MeshInstance3D Twin(Node3D root, MeshInstance3D mesh, Material material) {
        // The root stays at the origin, so the twin's own transform is where it is in the world.
        var twin = new MeshInstance3D {
            Mesh = mesh.Mesh,
            Transform = mesh.GlobalTransform,
            Layers = RenderLayers.Xray,
            MaterialOverride = material,
        };
        root.AddChild(twin);
        return twin;
    }

    /// <summary>A copy of a skinned mesh: beside it, so it finds the same skeleton by the same path.</summary>
    private static MeshInstance3D SkinnedTwin(MeshInstance3D mesh, Material material) {
        var twin = new MeshInstance3D {
            Mesh = mesh.Mesh,
            Skin = mesh.Skin,
            Skeleton = mesh.Skeleton,
            Transform = mesh.Transform,
            Layers = RenderLayers.Xray,
            MaterialOverride = material,
        };
        mesh.GetParent().AddChild(twin);
        return twin;
    }

    /// <summary>The bullet's way in: from the gunshot wound nearest it on the skin down to where it lies.</summary>
    private static MeshInstance3D? Track(Node3D root, Patient patient, CavityTarget bullet) {
        var entry = patient.Wounds.Where(wound => wound.Kind == WoundKind.Gunshot)
            .Select(wound => patient.Body.UvToWorld(wound.Points[0]))
            .OrderBy(point => point.DistanceTo(bullet.GlobalPosition))
            .Cast<Vector3?>()
            .FirstOrDefault();
        if (entry is not { } from || from.DistanceTo(bullet.GlobalPosition) < 0.01f) {
            return null;
        }
        var to = bullet.GlobalPosition;
        var length = from.DistanceTo(to);
        var along = (to - from) / length;
        var side = Mathf.Abs(along.Dot(Vector3.Up)) > 0.9f ? Vector3.Right : Vector3.Up;
        var x = along.Cross(side).Normalized();
        var track = new MeshInstance3D {
            Mesh = new CylinderMesh { TopRadius = 0.003f, BottomRadius = 0.004f, Height = length, RadialSegments = 8 },
            Transform = new Transform3D(new Basis(x, along, x.Cross(along)), (from + to) * 0.5f),
            Layers = RenderLayers.Xray,
            MaterialOverride = TrackMaterial,
        };
        root.AddChild(track);
        return track;
    }
}
