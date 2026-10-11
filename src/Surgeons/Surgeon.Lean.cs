namespace Scalpel.Surgeons;

/// <summary>
/// Leaning in over a deep opening. Zoomed all the way in with the active hand's aim over the open cavity, the surgeon
/// bends forward from the hips: the eyes come over the opening and look down into it, past the edges a standing view
/// can't see over. The body only bends, it doesn't turn, so the mouse still moves the hand left and right on screen.
/// Reach, collisions and how the hands are steered stay as they are standing.
/// </summary>
public partial class Surgeon
{
    /// <summary>Farthest the torso bends forward over an opening (radians).</summary>
    internal const float MaxLean = 0.6f;
    /// <summary>Leaning in, the eyes stop this far (m, across the floor) short of right over the spot they look at: the
    /// view goes down into the opening steeply, not straight down onto the back of the hand.</summary>
    private const float LeanSetback = 0.15f;
    /// <summary>Leaning in keeps the eyes at least this far (m) above the spot they look at, clear of the drapes, the
    /// organs and the tools in the opening.</summary>
    internal const float LeanClearance = 0.4f;
    /// <summary>Moved further than this (m, across the floor) off the spot it last aimed at in the opening, the aim no
    /// longer keeps the surgeon leaning in.</summary>
    private const float LeanHold = 0.12f;
    /// <summary>Walking away from the opening faster than this (m/s) leaves the close zoom.</summary>
    private const float LeaveSpeed = 0.3f;
    /// <summary>How fast the bend and the view follow (a share of the way each second).</summary>
    private const float LeanRate = 5f;
    /// <summary>How far above the hips the eyes are: what the bend swings about.</summary>
    private const float EyeAboveHips = EyeHeight - HipHeight;

    /// <summary>How far the torso bends forward over an opening now (radians), synced so everyone sees it.</summary>
    public float Lean { get; internal set; }

    /// <summary>The spot in the opening the bent view looks at, null while not leaning in.</summary>
    internal Vector3? LeanSpot { get; private set; }

    /// <summary>Where the eyes looked up and down before leaning in, to look back there straightening up (null: not
    /// leaning in or straightening up).</summary>
    private float? _uprightPitch;

    /// <summary>How far a forward bend of <paramref name="lean"/> radians moves the eyes (surgeon space).</summary>
    internal static Vector3 LeanShift(float lean) =>
        new(0f, -EyeAboveHips * (1f - Mathf.Cos(lean)), -EyeAboveHips * Mathf.Sin(lean));

    /// <summary>
    /// Finds what the view leans in on, bends toward it and turns the eyes down into it. Zooming out, moving the aim
    /// out of the opening, crouching, inspecting or a needle view straightens back up, the eyes looking where they did
    /// before.
    /// Walking away leaves the close zoom too.
    /// </summary>
    private void UpdateLean(bool canAct, float delta)
    {
        var close = canAct && Zoom == ZoomFov.Length - 1 && Crouch <= 0f && _needleFraming <= 0f
            && !Hands[Active].Inspecting;
        var aim = AimPoint();
        var under = SurfaceBelow(aim);
        if (close && under.Open)
        {
            LeanSpot = aim with { Y = under.Y };
            _uprightPitch ??= Pitch;
        }
        else if (LeanSpot is { } spot && (!close || Flat(aim - spot).Length() > LeanHold))
        {
            LeanSpot = null;
        }
        if (LeanSpot is { } from && Flat(Velocity).Dot(Flat(GlobalPosition - from).Normalized()) > LeaveSpeed)
        {
            Zoom = 0;
            _zoomBefore = null;
            LeanSpot = null;
        }
        var follow = Mathf.Min(delta * LeanRate, 1f);
        if (LeanSpot is not { } at)
        {
            Lean = Mathf.Lerp(Lean, 0f, follow);
            if (_uprightPitch is { } upright)
            {
                Pitch = Mathf.Lerp(Pitch, upright, follow);
                // Straight again, the mouse has the look back.
                _uprightPitch = Lean < 0.01f ? null : upright;
            }
            return;
        }
        var local = ToLocal(at);
        var ahead = Mathf.Clamp(-local.Z - LeanSetback, 0f, EyeAboveHips * Mathf.Sin(MaxLean));
        // No further than keeps the eyes clear above it: over a high spot the bend stops short, or doesn't start.
        var clear = Mathf.Clamp((local.Y + LeanClearance - HipHeight) / EyeAboveHips, -1f, 1f);
        Lean = Mathf.Lerp(Lean, Mathf.Min(Mathf.Asin(ahead / EyeAboveHips), Mathf.Acos(clear)), follow);
        var toSpot = local - (new Vector3(0f, EyeHeight, 0f) + LeanShift(Lean));
        var look = Mathf.Clamp(Mathf.Atan2(toSpot.Y, -toSpot.Z), LookPitch.X, LookPitch.Y);
        Pitch = Mathf.Lerp(Pitch, look, follow);
    }

    private static Vector3 Flat(Vector3 v) => v with { Y = 0f };
}
