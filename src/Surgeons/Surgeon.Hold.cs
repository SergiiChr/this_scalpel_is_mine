namespace Scalpel.Surgeons;

/// <summary>An empty hand holding the patient: pressed flat on the skin where it aims, it keeps that part still and the
/// cuts under its palm from bleeding (Patient.PressByHand()).</summary>
public partial class Surgeon {
    /// <summary>How far (meters) a hand holding the patient sinks into the skin, pressing it.</summary>
    internal const float PressSink = 0.004f;
    /// <summary>Skin facing further from straight up than this (the cosine) is too steep to lay a hand flat on.
    /// </summary>
    private const float SteepestPress = 0.3f;
    /// <summary>How far the fingers curl round a limb they hold, on top of the press grip's own.</summary>
    private const float LimbWrap = 0.55f;

    /// <summary>Where an empty hand holds the patient: the rig bone under it, and the spot on the skin and the skin's
    /// normal there in that bone's frame, so the hand stays on the skin however the part moves.</summary>
    private readonly record struct PatientHold(string Bone, Vector3 At, Vector3 Normal);

    private readonly PatientHold?[] _holds = new PatientHold?[2];

    /// <summary>Whether the hand holds the patient.</summary>
    internal bool HoldsPatient(int hand) => _holds[hand] is not null;

    /// <summary>Where on the patient the empty active hand would lie and the skin's normal there: on the skin it aims
    /// at, or over an opening at its lip. Null when it aims at nothing of the patient's, or at skin too steep to lay a
    /// hand flat on.</summary>
    internal (Vector3 At, Vector3 Normal)? HoldSpot() {
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
        return normal.Dot(Vector3.Up) < SteepestPress ? null : (at, normal);
    }

    /// <summary>Lays the empty active hand flat on the patient at <paramref name="at"/> (see <see cref="HoldSpot"/>),
    /// its fingers round the limb there if it's on one.</summary>
    private void HoldPatient(Vector3 at, Vector3 normal) {
        var body = Session.Patient.Body;
        var hand = Hands[Active];
        hand.PressWrap = body.LimbRingAt(at) is null ? 0f : LimbWrap;
        var bone = body.RigBoneAt(at);
        var pose = body.RigBonePose(bone).AffineInverse();
        _holds[Active] = new PatientHold(bone, pose * at, (pose.Basis * normal).Normalized());
        hand.Lowered = false;
        hand.Trigger = false;
    }

    /// <summary>The hand lets go of the patient, from where it held.</summary>
    private void LetGoOfPatient(int index) {
        _holds[index] = null;
        Hands[index].PressNormal = null;
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
        hand.PressBone = hold.Bone;
        hand.Lifted = false;
        hand.Raise = 0f;
        hand.Tremor = Vector3.Zero;
        hand.Target = at - (normal * PressSink);
        hand.LocalTarget = ToLocal(hand.Target);
        _strain[hand.Index] = false;
        return true;
    }
}
