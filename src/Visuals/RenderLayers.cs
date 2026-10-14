namespace Scalpel.Visuals;

/// <summary>Visual layers (<c>VisualInstance3D.Layers</c>, <c>Camera3D.CullMask</c>). Everything else is on the first.
/// </summary>
public static class RenderLayers {
    /// <summary>What only X-ray films show: the patient's skeleton.</summary>
    public const uint Xray = 1u << 19;
    /// <summary>A camera's mask for everything a person sees: all 20 layers but <see cref="Xray"/>.</summary>
    public const uint Normal = 0xFFFFFu & ~Xray;
}
