namespace Scalpel.Surgeons;

/// <summary>An empty hand holding the patient: pressed flat on the skin where it aims, it keeps that part still and the
/// cuts under its palm from bleeding (Patient.PressByHand()).</summary>
public partial class Surgeon {
    /// <summary>How far (meters) a hand holding the patient sinks into the skin, pressing it.</summary>
    internal const float PressSink = 0.004f;
    /// <summary>Skin facing further from straight up than this (the cosine) is too steep to lay a hand flat on.
    /// </summary>
    private const float SteepestPress = 0.3f;
    /// <summary>Over an opening the fingers straighten out of the press grip's slight curl (see
    /// <see cref="SurgeonHand.PressWrap"/>), lying across it on the skin beyond instead of dipping into it.</summary>
    private const float OpeningWrap = -1f;

    /// <summary>Where an empty hand holds the patient: the rig bone under it, and the spot on the skin and the skin's
    /// normal there in that bone's frame, so the hand stays on the skin however the part moves, and which way its
    /// fingers point round a limb it grasps (zero off a limb) in that frame too. Over an opening it rests on the lip
    /// without pressing in, where there is no skin under it to press.</summary>
    private readonly record struct PatientHold(string Bone, Vector3 At, Vector3 Normal, Vector3 Across, bool OverOpening);

    private readonly PatientHold?[] _holds = new PatientHold?[2];

    /// <summary>Whether the hand holds the patient.</summary>
    internal bool HoldsPatient(int hand) => _holds[hand] is not null;

    /// <summary>Where on the patient the empty active hand would lie and the skin's normal there: on the skin it aims
    /// at, or over an opening at its lip. Null when it aims at nothing of the patient's, or at skin too steep to lay a
    /// hand flat on.</summary>
    internal (Vector3 At, Vector3 Normal, bool OverOpening)? HoldSpot() {
        var body = Session.Patient.Body;
        var aim = WorkTip();
        var under = SurfaceBelow(aim, false);
        if (HeldTool(Active) is not null || !under.Found || aim.Y - under.Y > AimDrop) {
            return null;
        }
        var at = aim with { Y = under.Open ? under.Lip : under.Y };
        // A centimeter in, the probe finds the body part even where its box is thinner than the limb.
        var zone = body.Probe(at + (Vector3.Down * 0.01f)).Zone;
        if (zone is not (SiteZone.Site or SiteZone.Cavity or SiteZone.Body)) {
            return null;
        }
        var normal = zone is not SiteZone.Body ? body.Site.GlobalBasis.Y.Normalized()
            : Rays.Cast(this, at + (Vector3.Up * 0.05f), at + (Vector3.Down * 0.05f), PatientBody.SurfaceLayer)?.Normal ?? Vector3.Up;
        return normal.Dot(Vector3.Up) < SteepestPress ? null : (at, normal, under.Open);
    }

    /// <summary>Lays the empty active hand flat on the patient at <paramref name="at"/> (see <see cref="HoldSpot"/>),
    /// its fingers round the limb there if it's on one.</summary>
    private void HoldPatient(Vector3 at, Vector3 normal, bool overOpening) {
        var body = Session.Patient.Body;
        var hand = Hands[Active];
        // The bone under the spot held: where a limb's grasp puts the hand reaches round past it.
        var bone = body.RigBoneAt(at);
        var across = Vector3.Zero;
        if (body.LimbRingAt(at) is { } ring) {
            // A limb is grasped: the palm on its near side, the fingers over the top (and what's held there) and round
            // the far side.
            (at, normal, across) = hand.GraspFit(ring, at, GlobalPosition);
        }
        // Round a limb the fingers close until they meet its skin (SurgeonHand.GraspWith()).
        hand.PressWrap = overOpening && across == Vector3.Zero ? OpeningWrap : 0f;
        var pose = body.RigBonePose(bone).AffineInverse();
        _holds[Active] = new PatientHold(bone, pose * at, (pose.Basis * normal).Normalized(), pose.Basis * across,
            overOpening);
        hand.Lowered = false;
        hand.Trigger = false;
    }

    /// <summary>The hand lets go of the patient, from where it held.</summary>
    private void LetGoOfPatient(int index) {
        _holds[index] = null;
        Hands[index].PressNormal = null;
        Hands[index].PressAcross = Vector3.Zero;
        Hands[index].PressBone = "";
    }

    /// <summary>A hand holding the patient stays on the spot it holds, flat on the skin there and pressing into it, as
    /// that part moves and whichever way the surgeon turns. Walked away out of reach, it lets go. False when it doesn't
    /// hold the patient (any more).</summary>
    private bool KeepHold(SurgeonHand hand) {
        if (_holds[hand.Index] is not { } hold) {
            return false;
        }
        var pose = Session.Patient.Body.RigBonePose(hold.Bone);
        var at = pose * hold.At;
        if (at.DistanceTo(Shoulder(hand.Index)) > Reach) {
            LetGoOfPatient(hand.Index);
            Session.Hud.Toast("You let go of the patient.");
            return false;
        }
        var normal = (pose.Basis * hold.Normal).Normalized();
        hand.PressNormal = normal;
        hand.PressAcross = (pose.Basis * hold.Across).Normalized();
        hand.PressBone = hold.Bone;
        hand.Lifted = false;
        hand.Raise = 0f;
        hand.Tremor = Vector3.Zero;
        // A hand grasping a limb rests on it (GraspFit()): pressed in, its flat palm would cut into the limb's round.
        hand.Target = hold.OverOpening || hold.Across != Vector3.Zero ? at : at - (normal * PressSink);
        hand.LocalTarget = ToLocal(hand.Target);
        _strain[hand.Index] = false;
        return true;
    }
}
