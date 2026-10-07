using GodotDictionary = Godot.Collections.Dictionary;

namespace Scalpel.Patients;

/// <summary>An organ of a site's anatomy: where it lies (uv), how far under the muscle its top is, its model and look.
/// Motion is "beat", "breath" or none; layer 0 lies on top of layer 1.</summary>
public sealed record OrganSpec(
    Vector2 Uv, float Top, float Size, string Model, float Yaw, bool Mirror, Color Color, int Layer, string Motion)
{
    public static OrganSpec FromVariant(GodotDictionary data)
    {
        var color = data.TryGetValue("color", out var raw) ? raw.AsGodotArray() : [0.6, 0.3, 0.3];
        return new OrganSpec(
            data.Vector("uv", new Vector2(0.5f, 0.5f)), data.Float("top"), data.Float("size", 0.04f), data.String("model"),
            data.Float("yaw"), data.Bool("mirror"), new Color(color[0].AsSingle(), color[1].AsSingle(), color[2].AsSingle()),
            data.Int("layer"), data.String("motion"));
    }
}

/// <summary>A bone along a straight line in uv, with its radius in meters.</summary>
public sealed record BoneSpec(Vector2 From, Vector2 To, float Radius)
{
    public static BoneSpec FromVariant(GodotDictionary data) =>
        new(data.Vector("from", Vector2.Zero), data.Vector("to", Vector2.Zero), data.Float("radius", 0.01f));
}

/// <summary>Rows of ribs mirrored across the midline, running out and down from the breastbone.</summary>
public sealed record RibSpec(float[] Rows, float Inner, float Drop, float Radius);

/// <summary>
/// One surgical site of data/patient_sites.json, in patient body space (head at +X, face up +Y, meters): where the site
/// lies, its size, how deep its cavity is, how much fat is under it, whether it faces down, and its anatomy.
/// </summary>
public sealed record SiteDef(
    Vector3 Position,
    Vector2 Size,
    float Depth,
    float Fat,
    bool Back,
    IReadOnlyList<OrganSpec> Organs,
    IReadOnlyList<BoneSpec> Bones,
    BoneSpec? Sternum,
    RibSpec? Ribs)
{
    /// <summary>Subcutaneous fat where a site doesn't say.</summary>
    public const float DefaultFat = 0.012f;

    public bool HasAnatomy => Organs.Count > 0 || Bones.Count > 0 || Sternum is not null || Ribs is not null;

    /// <summary>The site's definition, the abdomen's for an unknown site.</summary>
    public static SiteDef Of(string site) =>
        FromVariant(Db.PatientSites.ContainsKey(site) ? Db.Site(site) : Db.Site("abdomen"));

    public static SiteDef FromVariant(GodotDictionary data)
    {
        var position = data["pos"].AsGodotArray();
        var anatomy = data.TryGetValue("anatomy", out var value) ? value.AsGodotDictionary() : [];
        RibSpec? ribs = null;
        if (anatomy.TryGetValue("ribs", out var ribData))
        {
            var spec = ribData.AsGodotDictionary();
            ribs = new RibSpec([.. spec["rows"].AsGodotArray().Select(row => row.AsSingle())], spec.Float("inner"),
                spec.Float("drop"), spec.Float("radius"));
        }
        return new SiteDef(
            new Vector3(position[0].AsSingle(), position[1].AsSingle(), position[2].AsSingle()),
            data.Vector("size", Vector2.One * 0.3f),
            data.Float("depth"),
            data.Float("fat", DefaultFat),
            data.Bool("back"),
            List(anatomy, "organs", OrganSpec.FromVariant),
            List(anatomy, "bones", BoneSpec.FromVariant),
            anatomy.TryGetValue("sternum", out var sternum) ? BoneSpec.FromVariant(sternum.AsGodotDictionary()) : null,
            ribs);
    }

    private static List<T> List<T>(GodotDictionary data, string key, Func<GodotDictionary, T> read) =>
        data.TryGetValue(key, out var list) ? [.. list.AsGodotArray().Select(entry => read(entry.AsGodotDictionary()))] : [];
}
