namespace Scalpel.Visuals;

/// <summary>
/// Loads the model for assets/models/&lt;category&gt;/&lt;name&gt;.glb (or .gltf/.tscn). Generated models come from
/// tools/assetgen; replace a file to replace the art. Imported materials are swapped for the game's shaded ones by
/// material name.
/// </summary>
public static class ModelSlot
{
    private const string Root = "res://assets/models";
    private static readonly string[] Extensions = ["glb", "gltf", "tscn"];
    /// <summary>Every model loaded so far, kept for the whole game. Unloaded with a surgery and loaded again for the
    /// next, a model's materials can come back out of Godot's resource cache while their old C# wrappers wait to be
    /// finalized, which fails ("Handle is not initialized").</summary>
    private static readonly Dictionary<string, PackedScene> Loaded = [];

    /// <summary>Adds the model under <paramref name="parent"/>. <paramref name="overrides"/>: material name -> material,
    /// for names that need per-instance treatment ("skin", "tint").</summary>
    public static Node3D Instantiate(string category, string modelName, Node3D parent,
        IReadOnlyDictionary<string, Material>? overrides = null)
    {
        foreach (var extension in Extensions)
        {
            var path = $"{Root}/{category}/{modelName}.{extension}";
            if (Loaded.TryGetValue(path, out var scene) || ResourceLoader.Exists(path))
            {
                scene ??= Loaded[path] = GD.Load<PackedScene>(path);
                var node = scene.Instantiate<Node3D>();
                parent.AddChild(node);
                Toonify(node, overrides);
                return node;
            }
        }
        GD.PushError($"Missing model {category}/{modelName}. Run: python -m tools.assetgen");
        var empty = new Node3D();
        parent.AddChild(empty);
        return empty;
    }

    /// <summary>A tool's model: "tint" parts take the tool's color.</summary>
    public static Node3D InstantiateTool(ToolDef def, Node3D parent) =>
        Instantiate("tools", def.ModelName, parent,
            new Dictionary<string, Material> { ["tint"] = Materials.ToonShaded(def.Color, 0.15f) });

    /// <summary>
    /// Replaces imported glTF materials with the game's shading by material family (<see cref="Materials.Imported"/>),
    /// keeping each material's color, roughness, metallic and texture maps.
    /// </summary>
    public static void Toonify(Node root, IReadOnlyDictionary<string, Material>? overrides = null)
    {
        foreach (var meshInstance in root.FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>())
        {
            for (var i = 0; i < meshInstance.Mesh.GetSurfaceCount(); i++)
            {
                var source = meshInstance.Mesh.SurfaceGetMaterial(i);
                var key = source?.ResourceName ?? "";
                Material? material = key switch
                {
                    _ when overrides is not null && overrides.TryGetValue(key, out var own) => own,
                    // Fine print: an outline would blot it out.
                    "marks" => Materials.ToonShaded(new Color(0.05f, 0.05f, 0.06f), 0.1f, false),
                    "glass" => Materials.Glass(),
                    "flame" => Materials.Glow(new Color(1f, 0.62f, 0.2f)),
                    _ when source is BaseMaterial3D imported =>
                        Materials.Imported(imported, Materials.OutlineSize(meshInstance.Mesh.GetAabb())),
                    _ => null,
                };
                if (material is not null)
                {
                    meshInstance.SetSurfaceOverrideMaterial(i, material);
                }
            }
        }
    }

    /// <summary>Swaps the model's shared toon materials for copies of its own, for per-object tweaks (blood, grime),
    /// and returns them.</summary>
    public static List<ShaderMaterial> OwnMaterials(Node root)
    {
        var own = new List<ShaderMaterial>();
        foreach (var mesh in root.FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>())
        {
            for (var i = 0; i < mesh.GetSurfaceOverrideMaterialCount(); i++)
            {
                if (mesh.GetSurfaceOverrideMaterial(i) is ShaderMaterial shared && shared.Shader == Materials.Toon)
                {
                    var copy = (ShaderMaterial)shared.Duplicate();
                    mesh.SetSurfaceOverrideMaterial(i, copy);
                    own.Add(copy);
                }
            }
        }
        return own;
    }

    /// <summary>Named part lookup for procedural animation. Missing parts are simply absent from the result.</summary>
    public static Dictionary<string, Node3D> Parts(Node root, params string[] names)
    {
        var found = new Dictionary<string, Node3D>();
        foreach (var name in names)
        {
            if (root.FindChild(name, true, false) is Node3D node)
            {
                found[name] = node;
            }
        }
        return found;
    }
}
