namespace Scalpel.Visuals;

/// <summary>Fine relief drawn on a surface (toon.gdshader detail).</summary>
public enum Detail { None, Pores, Creases, Weave, Brushed }

/// <summary>
/// How a kind of surface responds to light, so skin, gloves, steel and cloth read apart under the same lamp.
/// Specular: highlight strength, Rim: edge light (cloth sheen), Wrap/Scatter: light past the terminator (skin),
/// Grime: procedural dirt, Metallic: 1 for metal, Detail: fine relief. Roughness comes from the model's own material.
/// </summary>
public sealed record SurfaceFamily(
    float Specular, float Rim, float Grime, float Wrap = 0f, Color? Scatter = null, float Metallic = 0f,
    Detail Detail = Detail.None);

/// <summary>Shared materials. Everything goes through here so the look can be tuned in one place.</summary>
public static class Materials
{
    public static readonly Shader Toon = GD.Load<Shader>("res://assets/shaders/toon.gdshader");
    public static readonly Shader Outline = GD.Load<Shader>("res://assets/shaders/outline.gdshader");
    public static readonly Shader Flesh = GD.Load<Shader>("res://assets/shaders/flesh.gdshader");
    public static readonly Shader Skin = GD.Load<Shader>("res://assets/shaders/skin.gdshader");
    public static readonly Shader TissueLayer = GD.Load<Shader>("res://assets/shaders/tissue_layer.gdshader");

    /// <summary>Patient skin. A slight red bias keeps light skin reading as perfused rather than cream under the
    /// surgical lamp.</summary>
    public static readonly IReadOnlyList<Color> SkinTones =
    [
        new(0.82f, 0.61f, 0.53f), new(0.73f, 0.5f, 0.4f), new(0.56f, 0.37f, 0.26f), new(0.4f, 0.24f, 0.18f),
    ];

    // Hospital palette. Surgical green for walls and linens (it's easy on eyes that stare at red all day), ceil blue
    // for the second surgeon's scrubs, one cool fluorescent white for every room light but the surgical lamp.
    public static readonly Color SurgicalGreen = new(0.4f, 0.55f, 0.5f);
    public static readonly Color PatientGown = new(0.52f, 0.64f, 0.6f);
    public static readonly IReadOnlyList<Color> Scrubs = [new(0.22f, 0.4f, 0.36f), new(0.26f, 0.38f, 0.52f)];
    public static readonly Color Fluorescent = new(0.88f, 1f, 0.94f);

    public static readonly IReadOnlyDictionary<string, SurfaceFamily> Families = new Dictionary<string, SurfaceFamily>
    {
        ["skin"] = new(0.15f, 0.12f, 0.05f, Wrap: 0.35f, Scatter: new Color(1f, 0.42f, 0.32f), Detail: Detail.Pores),
        ["rubber"] = new(0.18f, 0.08f, 0f, Detail: Detail.Creases),
        ["cloth"] = new(0f, 0.2f, 0.12f, Wrap: 0.25f, Scatter: Colors.White, Detail: Detail.Weave),
        ["metal"] = new(0.9f, 0.04f, 0.02f, Metallic: 1f, Detail: Detail.Brushed),
        ["plastic"] = new(0.35f, 0.12f, 0.1f),
        ["tissue"] = new(0.55f, 0.1f, 0f, Wrap: 0.4f, Scatter: new Color(1f, 0.3f, 0.25f)),
    };

    /// <summary>Model material name -> family. Names not listed are plastic.</summary>
    private static readonly IReadOnlyDictionary<string, string> FamilyOf = new Dictionary<string, string>
    {
        ["skin"] = "skin",
        ["lips"] = "skin",
        ["glove"] = "rubber",
        ["tint"] = "cloth",
        ["gown"] = "cloth",
        ["mask"] = "cloth",
        ["knit"] = "cloth",
        ["fabric_white"] = "cloth",
        ["fabric_dark"] = "cloth",
        ["mattress"] = "cloth",
        ["cotton"] = "cloth",
        ["paper"] = "cloth",
        ["hair"] = "cloth",
        ["leather"] = "cloth",
        ["steel"] = "metal",
        ["dark_steel"] = "metal",
        ["chrome"] = "metal",
        ["brass"] = "metal",
        ["gold"] = "metal",
        ["flesh"] = "tissue",
        ["organ"] = "tissue",
        ["vessel"] = "tissue",
        ["cartilage"] = "tissue",
        ["bone"] = "tissue",
        ["mouth"] = "tissue",
        ["tongue"] = "tissue",
        ["teeth"] = "tissue",
        ["eye"] = "tissue",
        ["iris"] = "tissue",
        ["blood_bag"] = "tissue",
    };

    /// <summary>Outline width for a part, as a share of its middle dimension (see <see cref="OutlineSize"/>).</summary>
    private const float OutlineShare = 0.06f;

    private static readonly Dictionary<string, Material> Cache = [];

    private static T Cached<T>(string key, Func<T> create) where T : Material
    {
        if (!Cache.TryGetValue(key, out var material))
        {
            material = create();
            Cache[key] = material;
        }
        return (T)material;
    }

    /// <summary>Cached per color/settings, so don't modify the result. Use <see cref="ToonUnique"/> for per-object
    /// tweaks.</summary>
    public static ShaderMaterial ToonShaded(Color color, float grime = 0.25f, bool outline = true, float roughness = 0.7f) =>
        Cached($"{color.ToHtml()}|{grime:0.00}|{outline}|{roughness:0.00}",
            () => ToonUnique(color, grime, outline, roughness));

    /// <summary>See-through glass (syringe barrels, vials), so the liquid level inside shows. No outline: it would
    /// hide the liquid.</summary>
    public static StandardMaterial3D Glass() => Cached("glass", () => new StandardMaterial3D
    {
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        AlbedoColor = new Color(0.85f, 0.93f, 0.97f, 0.22f),
        Roughness = 0.1f,
        MetallicSpecular = 0.8f,
    });

    /// <summary>Walls, floors and big furniture: no highlights, no outline, heavier grime fixed in the world.</summary>
    public static ShaderMaterial RoomSurface(Color color, float grime = 0.35f) => Cached($"env|{color.ToHtml()}|{grime:0.00}", () =>
    {
        var material = ToonUnique(color, grime, false, 0.85f);
        material.SetShaderParameter("rim_strength", 0f);
        material.SetShaderParameter("specular_strength", 0f);
        material.SetShaderParameter("world_grime", true);
        return material;
    });

    public static ShaderMaterial ToonUnique(Color color, float grime = 0.25f, bool outline = true, float roughness = 0.7f)
    {
        var material = new ShaderMaterial { Shader = Toon };
        material.SetShaderParameter("albedo", color);
        material.SetShaderParameter("grime", grime);
        material.SetShaderParameter("roughness", roughness);
        if (outline)
        {
            material.NextPass = OutlineFor(0.003f);
        }
        return material;
    }

    /// <summary>
    /// A model's own material in the game's shading, by its family: keeps its color, roughness, metallic and texture
    /// maps. <paramref name="outline"/>: most the outline may grow (meters), from the part's size, 0 for none.
    /// </summary>
    public static ShaderMaterial Imported(BaseMaterial3D source, float outline)
    {
        var familyName = FamilyOf.GetValueOrDefault(source.ResourceName, "plastic");
        var textured = source.AlbedoTexture is not null || source.NormalTexture is not null
            || source.RoughnessTexture is not null || source.MetallicTexture is not null;
        var metallic = source.Metallic > 0.5f || source.MetallicTexture is not null ? Families[familyName].Metallic : 0f;
        var key = $"imported|{source.ResourceName}|{source.AlbedoColor.ToHtml()}|{source.Roughness:0.00}|{metallic:0.0}|{outline:0.0000}";
        if (!textured && Cache.TryGetValue(key, out var cached))
        {
            return (ShaderMaterial)cached;
        }
        var material = FamilyUnique(familyName, source.AlbedoColor, source.Roughness);
        material.SetShaderParameter("metallic", metallic);
        if (source.AlbedoTexture is not null)
        {
            material.SetShaderParameter("albedo_texture", source.AlbedoTexture);
        }
        if (source.NormalEnabled && source.NormalTexture is not null)
        {
            material.SetShaderParameter("normal_texture", source.NormalTexture);
            material.SetShaderParameter("use_normal_texture", true);
        }
        if (source.RoughnessTexture is not null)
        {
            material.SetShaderParameter("roughness_texture", source.RoughnessTexture);
            material.SetShaderParameter("roughness_channel", Channel(source.RoughnessTextureChannel));
        }
        if (source.MetallicTexture is not null)
        {
            material.SetShaderParameter("metallic_texture", source.MetallicTexture);
            material.SetShaderParameter("metallic_channel", Channel(source.MetallicTextureChannel));
        }
        if (outline > 0f)
        {
            material.NextPass = OutlineFor(outline);
        }
        if (!textured)
        {
            Cache[key] = material;
        }
        return material;
    }

    /// <summary>A new material of a family with no outline, for per-object tweaks.</summary>
    public static ShaderMaterial FamilyUnique(string familyName, Color color, float roughness)
    {
        var family = Families[familyName];
        var material = ToonUnique(color, family.Grime, false, roughness);
        material.SetShaderParameter("specular_strength", family.Specular);
        material.SetShaderParameter("rim_strength", family.Rim);
        material.SetShaderParameter("wrap", family.Wrap);
        material.SetShaderParameter("scatter_tint", family.Scatter ?? Colors.White);
        material.SetShaderParameter("metallic", family.Metallic);
        material.SetShaderParameter("detail", (int)family.Detail);
        return material;
    }

    /// <summary>Most outline a part of this size gets: a hairline on a scalpel blade, a full line round a table.
    /// </summary>
    public static float OutlineSize(Aabb bounds)
    {
        float[] sizes = [bounds.Size.X, bounds.Size.Y, bounds.Size.Z];
        Array.Sort(sizes);
        return Mathf.Clamp(sizes[1] * OutlineShare, 0.0003f, 0.003f);
    }

    /// <summary>The shared outline pass that grows at most <paramref name="maxThickness"/> meters
    /// (outline.gdshader).</summary>
    public static ShaderMaterial OutlineFor(float maxThickness) => Cached($"outline|{maxThickness:0.0000}", () =>
    {
        var material = new ShaderMaterial { Shader = Outline };
        material.SetShaderParameter("max_thickness", maxThickness);
        return material;
    });

    /// <summary>Channel mask for a BaseMaterial3D texture channel (red, green, blue, alpha, grayscale).</summary>
    private static Vector4 Channel(BaseMaterial3D.TextureChannel channel) => channel switch
    {
        BaseMaterial3D.TextureChannel.Red => new Vector4(1, 0, 0, 0),
        BaseMaterial3D.TextureChannel.Green => new Vector4(0, 1, 0, 0),
        BaseMaterial3D.TextureChannel.Blue => new Vector4(0, 0, 1, 0),
        BaseMaterial3D.TextureChannel.Alpha => new Vector4(0, 0, 0, 1),
        _ => new Vector4(0.333f, 0.333f, 0.333f, 0),
    };

    /// <summary>Patient body skin with its own outline, both able to carve out the cavity box (see
    /// <see cref="SetCarve"/>).</summary>
    public static ShaderMaterial BodySkin(Color tone)
    {
        var material = FamilyUnique("skin", tone, 0.6f);
        var outline = new ShaderMaterial { Shader = Outline };
        outline.SetShaderParameter("max_thickness", 0.002f);
        material.NextPass = outline;
        return material;
    }

    /// <summary>
    /// Places the surgical site on the body: <paramref name="region"/> marks where the simulated skin replaces the
    /// body (cut away there), the wound maps draw the damage on the body everywhere else. <paramref name="site"/> is the
    /// surgical site's global transform. The region has one texel per tissue grid point.
    /// </summary>
    public static void SetCarve(ShaderMaterial material, Transform3D site, Vector2 halfSize, float depth, Texture2D region)
    {
        foreach (var pass in new[] { material, (ShaderMaterial)material.NextPass })
        {
            pass.SetShaderParameter(ShaderParam.CarveInverse, new Projection(site.AffineInverse()));
            pass.SetShaderParameter(ShaderParam.CarveBox, new Vector3(halfSize.X, depth, halfSize.Y));
            pass.SetShaderParameter(ShaderParam.CarveMap, region);
            pass.SetShaderParameter(ShaderParam.CarveGrid, (Vector2)region.GetSize());
            pass.SetShaderParameter(ShaderParam.CarveLift, Vector3.Zero);
        }
    }

    /// <summary>Moves the carve placed by <see cref="SetCarve"/> by <paramref name="lift"/> (world space). Sent every
    /// frame while the patient breathes: a vector costs nothing, where a matrix or texture passed to the engine leaves a
    /// wrapper for the garbage collector.</summary>
    public static void LiftCarve(ShaderMaterial material, Vector3 lift)
    {
        material.SetShaderParameter(ShaderParam.CarveLift, lift);
        ((ShaderMaterial)material.NextPass).SetShaderParameter(ShaderParam.CarveLift, lift);
    }

    /// <summary>Cavity walls: only drawn inside the region (see flesh.gdshader).</summary>
    public static void SetReveal(ShaderMaterial material, Transform3D site, Vector2 halfSize, Texture2D region)
    {
        material.SetShaderParameter("region_inverse", new Projection(site.AffineInverse()));
        material.SetShaderParameter("region_box", new Vector3(halfSize.X, 1f, halfSize.Y));
        material.SetShaderParameter("region_map", region);
        material.SetShaderParameter("region_grid", (Vector2)region.GetSize());
        material.SetShaderParameter(ShaderParam.RegionLift, Vector3.Zero);
    }

    public static void SetSiteMaps(ShaderMaterial material, Texture2D wounds, Texture2D fluids)
    {
        material.SetShaderParameter("site_wounds", wounds);
        material.SetShaderParameter("site_fluids", fluids);
    }

    public static ShaderMaterial FleshMaterial(Color? color = null)
    {
        var material = new ShaderMaterial { Shader = Flesh };
        material.SetShaderParameter("albedo", color ?? new Color(0.55f, 0.12f, 0.12f));
        return material;
    }

    public static ShaderMaterial SkinSite(Color tone, Texture2D woundTexture, Texture2D fluidTexture)
    {
        var material = new ShaderMaterial { Shader = Skin };
        material.SetShaderParameter("skin_color", tone);
        // Lit like the body's skin around it (BodySkin()).
        var family = Families["skin"];
        material.SetShaderParameter("specular_strength", family.Specular);
        material.SetShaderParameter("rim_strength", family.Rim);
        material.SetShaderParameter("grime", family.Grime);
        material.SetShaderParameter("wrap", family.Wrap);
        material.SetShaderParameter("scatter_tint", family.Scatter ?? Colors.White);
        material.SetShaderParameter("wound_map", woundTexture);
        material.SetShaderParameter("fluid_map", fluidTexture);
        return material;
    }

    /// <summary>Fat (layer 0) or muscle (layer 1) under the surgical site skin, or the cut face of the skin (layer 2,
    /// in its tone) in the walls of a cut.</summary>
    public static ShaderMaterial TissueLayerMaterial(int layer, Texture2D fluidTexture, Color? tone = null, bool cutFace = false)
    {
        var material = new ShaderMaterial { Shader = TissueLayer };
        material.SetShaderParameter("layer", layer);
        material.SetShaderParameter("skin_color", tone ?? new Color(0.84f, 0.66f, 0.56f));
        material.SetShaderParameter("cut_face", cutFace);
        material.SetShaderParameter("fluid_map", fluidTexture);
        return material;
    }

    public static StandardMaterial3D Glow(Color color) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = color,
    };

    public static StandardMaterial3D BloodPool() => new()
    {
        AlbedoColor = new Color(0.22f, 0f, 0.02f),
        Roughness = 0.05f,
        MetallicSpecular = 0.9f,
    };
}
