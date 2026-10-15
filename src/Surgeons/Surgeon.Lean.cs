namespace Scalpel.Surgeons;

/// <summary>
/// Leaning in over the patient. Zoomed all the way in with the active hand over the patient (skin, drapes or an
/// opening), with a tool or without, hovering or working, the surgeon bends forward from the hips: the eyes come over
/// the hand and look down at it, into an opening past the edges a standing view can't see over. The body only bends,
/// it doesn't turn, so the mouse still moves the hand left and right on screen. Reach, collisions and how the hands
/// are steered stay as they are standing.
/// </summary>
public partial class Surgeon {
    /// <summary>Farthest the torso bends forward over the patient (radians).</summary>
    internal const float MaxLean = 1f;
    /// <summary>Leaning in, the eyes stop this far (m, across the floor) short of right over the spot they look at: the
    /// view goes down into the opening steeply, not straight down onto the back of the hand, and stays within how far
    /// down the eyes can look (<see cref="LookPitch"/>).</summary>
    private const float LeanSetback = 0.1f;
    /// <summary>Leaning in keeps the eyes at least this far (m) above the skin they look at, clear of the drapes, the
    /// organs and the tools in an opening.</summary>
    internal const float LeanClearance = 0.3f;
    /// <summary>The hand off the patient this long (seconds), the surgeon straightens up: a hand passing over a gap
    /// between drapes or reaching off the side for a moment doesn't bob the view.</summary>
    private const float LeanGrace = 1f;
    /// <summary>Stepping back while leaning in faster than this (m/s) leaves the close zoom. Only back: stepping along
    /// the table keeps it.</summary>
    private const float LeaveSpeed = 0.3f;
    /// <summary>How fast the bend and the view follow (a share of the way each second).</summary>
    private const float LeanRate = 5f;
    /// <summary>Fastest the torso bends or straightens (radians a second): a deep bend starts and ends gently instead of
    /// jumping the view in its first frames.</summary>
    private const float LeanSpeed = 1.5f;
    /// <summary>How far above the hips the eyes are: what the bend swings about.</summary>
    private const float EyeAboveHips = EyeHeight - HipHeight;

    /// <summary>How far the torso bends forward over the patient now (radians), synced so everyone sees it.</summary>
    public float Lean { get; internal set; }

    /// <summary>The spot on the patient the bent view looks at, null while not leaning in.</summary>
    internal Vector3? LeanSpot { get; private set; }

    /// <summary>Where the eyes looked up and down before leaning in, to look back there straightening up (null: not
    /// leaning in or straightening up).</summary>
    private float? _uprightPitch;
    /// <summary>How long the hand has been off the patient (seconds).</summary>
    private float _leanLost;
    /// <summary>The player looked up or down while leaning in: the look is theirs until the surgeon straightens up.
    /// </summary>
    private bool _lookTaken;

    /// <summary>The player looks up or down: leaning in or straightening up no longer turns the eyes.</summary>
    private void TakeLook() {
        if (LeanSpot is not null || _uprightPitch is not null) {
            _lookTaken = true;
            _uprightPitch = null;
        }
    }

    /// <summary>How far a forward bend of <paramref name="lean"/> radians moves the eyes (surgeon space).</summary>
    internal static Vector3 LeanShift(float lean) =>
        new(0f, -EyeAboveHips * (1f - Mathf.Cos(lean)), -EyeAboveHips * Mathf.Sin(lean));

    /// <summary>
    /// Finds what the view leans in on, bends toward it and turns the eyes down at it, unless the player looks
    /// themselves. Zooming out, the hand off the patient, crouching or inspecting straightens back up, the eyes looking
    /// where they did before. A needle view over the patient keeps the bend under it, so letting go of Use tool comes back
    /// to it.
    /// Walking away leaves the close zoom too.
    /// </summary>
    private void UpdateLean(bool canAct, float delta) {
        var close = canAct && Zoom == ZoomFov.Length - 1 && Crouch <= 0f && !Hands[Active].Inspecting;
        if (close) {
            var tip = WorkTip();
            // Tools lying on the patient don't count: the hand is still over the patient.
            var under = SurfaceBelow(tip, false);
            _leanLost = under.Soft ? 0f : _leanLost + delta;
            if (under.Soft) {
                // Over an opening the spot stays at the skin around it, so the bend and the look don't jump as the hand
                // crosses the edge or goes down inside.
                LeanSpot = tip with { Y = under.Open ? under.Lip : under.Y };
                _uprightPitch ??= _lookTaken ? null : Pitch;
            }
        }
        if (!close || _leanLost > LeanGrace) {
            LeanSpot = null;
        }
        if (LeanSpot is not null && Velocity.Dot(GlobalBasis.Z) > LeaveSpeed) {
            Zoom = 0;
            _zoomBefore = null;
            LeanSpot = null;
        }
        var follow = Mathf.Min(delta * LeanRate, 1f);
        if (LeanSpot is not { } at) {
            Lean = Ease(Lean, 0f, follow, delta);
            if (_uprightPitch is { } upright) {
                Pitch = Mathf.Lerp(Pitch, upright, follow);
            }
            if (Lean < 0.01f) {
                // Straight again, the mouse has the look back.
                _uprightPitch = null;
                _lookTaken = false;
            }
            return;
        }
        var local = ToLocal(at);
        var ahead = Mathf.Clamp(-local.Z - LeanSetback, 0f, EyeAboveHips * Mathf.Sin(MaxLean));
        // No further than keeps the eyes clear above it: over a high spot (a raised limb) the bend stops short, or
        // doesn't start.
        var clear = Mathf.Clamp((local.Y + LeanClearance - HipHeight) / EyeAboveHips, -1f, 1f);
        Lean = Ease(Lean, Mathf.Min(Mathf.Asin(ahead / EyeAboveHips), Mathf.Acos(clear)), follow, delta);
        if (!_lookTaken) {
            var toSpot = local - (new Vector3(0f, EyeHeight, 0f) + LeanShift(Lean));
            var look = Mathf.Clamp(Mathf.Atan2(toSpot.Y, -toSpot.Z), LookPitch.X, LookPitch.Y);
            Pitch = Mathf.Lerp(Pitch, look, follow);
        }
    }

    /// <summary>A share <paramref name="follow"/> of the way from <paramref name="lean"/> to <paramref name="to"/>, no
    /// faster than <see cref="LeanSpeed"/>.</summary>
    private static float Ease(float lean, float to, float follow, float delta) =>
        Mathf.MoveToward(lean, Mathf.Lerp(lean, to, follow), LeanSpeed * delta);
}
