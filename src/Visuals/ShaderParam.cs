namespace Scalpel.Visuals;

/// <summary>Names of shader parameters set while the game runs, made once. A string passed to SetShaderParameter()
/// becomes a new StringName on every call: a tracked wrapper the runtime has to collect, and frequent collections stall
/// the game. Parameters only set when a material is made can stay strings.</summary>
public static class ShaderParam
{
    public static readonly StringName Albedo = "albedo";
    public static readonly StringName Blackout = "blackout";
    public static readonly StringName Blur = "blur";
    public static readonly StringName CarveBox = "carve_box";
    public static readonly StringName CarveGrid = "carve_grid";
    public static readonly StringName CarveInverse = "carve_inverse";
    public static readonly StringName CarveLift = "carve_lift";
    public static readonly StringName CarveMap = "carve_map";
    public static readonly StringName Coat = "coat";
    public static readonly StringName CoatInverse = "coat_inverse";
    public static readonly StringName CoatLength = "coat_length";
    public static readonly StringName CoatReach = "coat_reach";
    public static readonly StringName Contamination = "contamination";
    public static readonly StringName Damage = "damage";
    public static readonly StringName Daze = "daze";
    public static readonly StringName EmissionColor = "emission_color";
    public static readonly StringName Grime = "grime";
    public static readonly StringName LensAge = "lens_age";
    public static readonly StringName LensBlood = "lens_blood";
    public static readonly StringName LensSeed = "lens_seed";
    public static readonly StringName Pallor = "pallor";
    public static readonly StringName Pressure = "pressure";
    public static readonly StringName RegionLift = "region_lift";
    public static readonly StringName SiteLift = "site_lift";
    public static readonly StringName SiteToModel = "site_to_model";
    public static readonly StringName Stains = "stains";
    public static readonly StringName Wobble = "wobble";
}
