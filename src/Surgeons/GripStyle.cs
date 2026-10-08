namespace Scalpel.Surgeons;

/// <summary>
/// How a hand holds a kind of tool (<see cref="ToolDef.Grip"/>), in the tool's frame (grip at the origin, tip toward -Z).
/// </summary>
/// <param name="Frame">The glove's axes (fingers, back of the hand, pinky side) in tool space, for the right hand.</param>
/// <param name="At">The glove point (glove model space) that sits on <paramref name="On"/>, a point of the tool.</param>
/// <param name="Curl">How far each finger closes while holding (index, middle, ring, pinky, thumb).</param>
/// <param name="Oppose">Radians the thumb swings across the palm toward the index, for pinching.</param>
/// <param name="Tilt">The tool's neutral tilt in this grip.</param>
/// <param name="WorkTilt">The tilt the tool takes at most while working, null to keep the held tilt.</param>
/// <param name="Turn">How far in toward the middle the tool turns at rest (radians).</param>
public sealed record GripStyle(
    Basis Frame, Vector3 At, Vector3 On, float[] Curl, float Oppose = 0f, float Tilt = SurgeonHand.RestTilt,
    float? WorkTilt = null, float Turn = 0f)
{
    private static readonly Basis Along = new(new(0f, 0.2f, -0.98f), new(0f, 0.98f, 0.2f), new(1f, 0f, 0f));

    public static readonly IReadOnlyDictionary<string, GripStyle> All = new Dictionary<string, GripStyle>
    {
        // Pinched between the thumb pad on top and the curled index tip under it, the thumb swung across to meet the
        // index. The back of the tool runs up between the thumb and index into the web, the other fingers curled under
        // it.
        ["pencil"] = new(
            new(new(0.243f, 0.527f, -0.815f), new(0f, 0.84f, 0.543f), new(0.97f, -0.132f, 0.204f)),
            new(0.1f, -0.056f, -0.031f), Vector3.Zero, [0.75f, 0.95f, 1f, 1f, 0.3f], Oppose: 0.35f),
        // Thumb and ring finger through the rings at the back, index laid along the shaft.
        ["rings"] = new(Along, new(0.1f, -0.024f, 0f), new(0f, 0f, 0.055f), [0.2f, 0.7f, 0.8f, 0.85f, 0.6f]),
        // A needle holder crosses the player's view while its curved needle hangs toward the skin. The inward turn keeps
        // the hand and rings behind the working end instead of stacking them over the aim point.
        ["needle"] = new(Along, new(0.1f, -0.024f, 0f), new(0f, 0f, 0.055f), [0.2f, 0.7f, 0.8f, 0.85f, 0.6f],
            Tilt: -0.28f, WorkTilt: SurgeonHand.RestTilt, Turn: 0.55f),
        // Wrapped around a handle that runs across the palm, thumb toward the tip, knuckles on top.
        ["fist"] = new(new(new(0f, -1f, 0f), new(1f, 0f, 0f), new(0f, 0f, 1f)),
            new(0.06f, -0.026f, 0f), Vector3.Zero, [1f, 1f, 1f, 1f, 0.8f]),
        // A syringe, ready to inject: the index and middle fingers hooked over its finger grip, the thumb on the plunger
        // (see SurgeonHand.ReachPlunger()). The barrel runs from the thumb across under the fingers, so the hand holds it
        // from the outer side and behind, its printed scale up toward the eyes.
        ["syringe"] = new(
            new(new(0.063f, -0.596f, -0.801f), new(-0.966f, -0.237f, 0.1f), new(-0.25f, 0.768f, -0.59f)),
            new(0.13f, -0.02f, -0.019f), new(0f, 0f, -0.006f), [0.6f, 0.6f, 0.9f, 0.95f, 0.3f]),
        // Pinched at the back edge between thumb and fingertips, palm down over it.
        ["flat"] = new(new(new(0f, 0.25f, -0.97f), new(0f, 0.97f, 0.25f), new(1f, 0f, 0f)),
            new(0.1f, -0.03f, 0f), new(0f, 0.004f, 0f), [0.55f, 0.55f, 0.6f, 0.65f, 0.5f]),
    };

    /// <summary>The style for a grip name; unknown names hold like a pencil.</summary>
    public static GripStyle For(string grip) => All.GetValueOrDefault(grip) ?? All["pencil"];
}

/// <summary>
/// How one hand's grip is fitted to one tool model (data/grips.json, made by tests/Support/FitGrips.tscn), so the tool
/// doesn't pass through the glove.
/// </summary>
/// <param name="Lift">Meters the glove moves off the tool toward the back of the hand.</param>
/// <param name="Shift">Meters toward the pinky side, so the tool sits more in the web of the thumb.</param>
/// <param name="Curl">Replaces the grip's finger curl when set.</param>
public sealed record GripFit(float Lift = 0f, float Shift = 0f, float[]? Curl = null)
{
    /// <summary>The grip as it is.</summary>
    public static readonly GripFit None = new();

    public static GripFit FromDictionary(Godot.Collections.Dictionary fit) => fit.Count == 0
        ? None
        : new(fit.Float("lift"), fit.Float("shift"),
            fit.TryGetValue("curl", out var curl) ? [.. curl.AsGodotArray().Select(value => value.AsSingle())] : null);

    public Godot.Collections.Dictionary ToDictionary()
    {
        var fit = new Godot.Collections.Dictionary { ["lift"] = Lift, ["shift"] = Shift };
        if (Curl is not null)
        {
            fit["curl"] = Curl;
        }
        return fit;
    }
}
