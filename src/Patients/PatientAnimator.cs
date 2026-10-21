namespace Scalpel.Patients;

/// <summary>
/// Procedural animation of the patient's skeleton, driven by the synced vitals on every peer.
/// Breathing follows the respiration rate, awake patients look around, talk, flinch and panic, seizures shake every
/// joint, the limb carrying the surgical site too, though it only thrashes (the site rides it, PatientBody.RideLimb()).
/// A part a surgeon's hand holds (Surgeon.HoldPatient()) stays still: an arm, a leg or the head.
/// Every motion is a rotation in model space (X toward the head, Y up, Z the patient's left) about the joint.
/// </summary>
public partial class PatientAnimator : Node {
    public const float BreathDepth = 0.018f;
    public const float TorsoTop = 0.215f;
    /// <summary>How long the eyes stay shut in a blink (seconds).</summary>
    public const float Blink = 0.12f;

    /// <summary>The bones that stay put while the torso rises with a breath.</summary>
    private static readonly string[] HeldStill = ["ThighL", "ThighR", "Neck", "UpperArmL", "UpperArmR"];
    private static readonly Limb[] Limbs = [Limb.Of("L"), Limb.Of("R")];

    /// <summary>One side's limb bones, named once: the animator asks for them every frame.</summary>
    private sealed record Limb(
        string Side, string UpperArm, string Forearm, string Hand, string Thigh, string Shin, string Arm, string Leg) {
        public static Limb Of(string side) => new(
            side, "UpperArm" + side, "Forearm" + side, "Hand" + side, "Thigh" + side, "Shin" + side, "arm" + side, "leg" + side);
    }

    private PatientBody _body = null!;
    private BoneRig? _rig;
    private Dictionary<string, Node3D> _parts = [];
    private float _breathPhase;
    private float _beatPhase;
    private float _talkLeft;
    private float _flinch;
    private float _jolt;
    private float _lastPain;
    /// <summary>How still each part a hand can hold is kept (0 free, 1 held), eased so it settles rather than snaps.
    /// </summary>
    private readonly Dictionary<string, float> _still = [];
    /// <summary>Reused for the parts held this frame.</summary>
    private readonly HashSet<string> _held = [];
    /// <summary>Seconds until the next blink.</summary>
    private float _blinkIn = 3f;
    /// <summary>How much of the current blink is left.</summary>
    private float _blinkLeft;
    /// <summary>Blinks, flinches and quivers, seeded per session: the same session moves the same way every time.
    /// </summary>
    private readonly RandomNumberGenerator _rng = new();

    public void Setup(PatientBody body, Node3D model) {
        _body = body;
        _rng.Seed = Net.Instance.SessionSeed + 11;
        _rig = BoneRig.Find(model);
        _parts = ModelSlot.Parts(model, "EyeL", "EyeR", "Lids");
    }

    internal BoneRig? Rig => _rig;

    /// <summary>The part a hand holding <paramref name="bone"/> keeps still: "armL", "legR", "head", or "" for the trunk,
    /// which only breathes.</summary>
    internal static string PartOf(string bone) => bone switch {
        "Neck" or "Head" or "Jaw" => "head",
        _ when bone.StartsWith("UpperArm", StringComparison.Ordinal) || bone.StartsWith("Forearm", StringComparison.Ordinal)
            || bone.StartsWith("Hand", StringComparison.Ordinal) => "arm" + bone[^1],
        _ when bone.StartsWith("Thigh", StringComparison.Ordinal) || bone.StartsWith("Shin", StringComparison.Ordinal)
            || bone.StartsWith("Foot", StringComparison.Ordinal) => "leg" + bone[^1],
        _ => "",
    };

    /// <summary>How still <paramref name="part"/> is held this frame (0..1), eased toward whether a hand holds it.
    /// </summary>
    private float Still(string part, float delta) {
        var still = Mathf.MoveToward(_still.GetValueOrDefault(part), _held.Contains(part) ? 1f : 0f, delta * 4f);
        _still[part] = still;
        return still;
    }

    /// <summary>Patient said something: move the jaw for a while.</summary>
    public void Talk(float seconds) => _talkLeft = seconds;

    /// <summary>A defibrillator shock: every muscle contracts at once for an instant.</summary>
    public void Jolt() => _jolt = 1f;

    /// <summary>Poses the patient for this frame. <paramref name="heldBones"/>: the rig bones surgeons' hands hold.
    /// </summary>
    public void Animate(Vitals vitals, bool alive, float delta, IReadOnlyList<string>? heldBones = null) {
        if (_rig is null) {
            return;
        }
        _held.Clear();
        foreach (var bone in heldBones ?? []) {
            _held.Add(PartOf(bone));
        }
        var t = (float)GameClock.Seconds;
        var breathing = alive && !vitals.IsArrested;
        var rate = vitals.IsUnder ? 12f / 60f : (14f + vitals.Panic * 14f) / 60f;
        _breathPhase += breathing ? delta * rate * Mathf.Tau : 0f;
        var breath = (Mathf.Sin(_breathPhase) * 0.5f + 0.5f) * BreathDepth * (1f + vitals.Panic);
        // The whole trunk rises with the surgical site; legs, neck and arms stay put so only the torso skin moves.
        _jolt = Mathf.MoveToward(_jolt, 0f, delta * 5f);
        var rise = new Vector3(0, TorsoTop * breath + _jolt * 0.025f, 0);
        _rig.Shift("Torso", rise);
        foreach (var held in HeldStill) {
            _rig.Hold(held, rise);
        }
        _body.SetBreathOffset(TorsoTop * breath);
        if (breathing) {
            _body.Breath = Mathf.Sin(_breathPhase) * 0.5f + 0.5f;
        }
        _body.Heartbeat = HeartbeatOf(vitals, alive, delta);

        var awake = alive && vitals.IsAwake;
        // Awake patients blink every few seconds, more often in pain or panic.
        _blinkIn -= awake ? delta * (1f + vitals.Pain + vitals.Panic * 2f) : 0f;
        if (_blinkIn <= 0f) {
            _blinkIn = _rng.RandfRange(2.5f, 6f);
            _blinkLeft = Blink;
        }
        _blinkLeft = Mathf.Max(_blinkLeft - delta, 0f);
        var open = awake && _blinkLeft <= 0f;
        SetVisible("EyeL", open);
        SetVisible("EyeR", open);
        SetVisible("Lids", !open);
        _talkLeft = Mathf.Max(_talkLeft - delta, 0f);
        var jawOpen = (_talkLeft > 0f ? Mathf.Abs(Mathf.Sin(t * 11f)) * 0.25f : 0f) + (vitals.Pain > 0.6f && awake ? 0.15f : 0f);
        // The jaw hinges about the ear-to-ear axis; a positive turn swings the chin toward the chest.
        _rig.Rotate("Jaw", new Basis(Vector3.Back, jawOpen));

        if (vitals.Pain > _lastPain + 0.08f && awake) {
            _flinch = 1f;
        }
        _lastPain = vitals.Pain;
        _flinch = Mathf.MoveToward(_flinch, 0f, delta * 3f);
        // Looking left and right turns the head about its own long axis.
        var look = awake ? Mathf.Sin(t * 0.4f) * 0.35f : 0f;
        var head = new Vector3(look, 0, 0)
            + new Vector3(_rng.RandfRange(-1f, 1f), 0, _rng.RandfRange(-1f, 1f)) * _flinch * 0.15f;
        var shake = 0f;
        if (vitals.Seizing) {
            shake = 0.25f;
        } else if (awake && vitals.Panic > 0.6f) {
            shake = (vitals.Panic - 0.6f) * 0.8f;
        }
        var headFree = 1f - Still("head", delta);
        _rig.Rotate("Head", Basis.FromEuler((head + Jitter(shake * 0.6f, t, 1f)) * headFree));
        foreach (var limb in Limbs) {
            var mirror = limb.Side == "L" ? 1f : -1f;
            var amount = shake + _flinch * 0.2f + _jolt * 0.35f;
            // The limb the site is on only thrashes: a flinch or a calm patient's raised arm would pull the site out
            // from under a blade or a needle at every touch.
            var site = _body.SitePart;
            var arm = 1f - Still(limb.Arm, delta);
            var leg = 1f - Still(limb.Leg, delta);
            var armAmount = (limb.Arm == site ? shake : amount) * arm;
            var legAmount = (limb.Leg == site ? shake : amount) * leg;
            // Panicking patients lift their arms off the table: the arms point toward the feet, so a negative turn
            // about Z raises them.
            var lift = awake && limb.Arm != site ? vitals.Panic * 0.15f * arm : 0f;
            _rig.Rotate(limb.UpperArm, Basis.FromEuler(new Vector3(0, 0, -lift) + Jitter(armAmount, t, 2f + mirror)));
            _rig.Rotate(limb.Forearm, Basis.FromEuler(new Vector3(0, 0, -lift * 0.8f) + Jitter(armAmount, t, 3f + mirror)));
            _rig.Rotate(limb.Hand, Basis.FromEuler(Jitter(armAmount * 1.5f, t, 4f + mirror)));
            _rig.Rotate(limb.Thigh, Basis.FromEuler(Jitter(legAmount * 0.4f, t, 5f + mirror)));
            _rig.Rotate(limb.Shin, Basis.FromEuler(Jitter(legAmount * 0.4f, t, 6f + mirror)));
        }
        _body.RideLimb();
    }

    /// <summary>Heart contraction 0..1: a quick squeeze once per beat in sinus rhythm, a feeble quiver in V-fib, still
    /// in asystole.</summary>
    private float HeartbeatOf(Vitals vitals, bool alive, float delta) {
        if (!alive || vitals.Rhythm == Rhythm.Asystole) {
            return 0f;
        }
        if (vitals.Rhythm == Rhythm.Vfib) {
            return _rng.Randf() * 0.15f;
        }
        _beatPhase = Mathf.PosMod(_beatPhase + delta * vitals.HeartRate / 60f, 1f);
        return Mathf.Pow(Mathf.Max(Mathf.Sin(_beatPhase * Mathf.Tau), 0f), 3f);
    }

    private void SetVisible(string part, bool value) {
        if (_parts.TryGetValue(part, out var node)) {
            node.Visible = value;
        }
    }

    private static Vector3 Jitter(float amount, float t, float seed) {
        if (amount <= 0f) {
            return Vector3.Zero;
        }
        return new Vector3(Mathf.Sin(t * 37f + seed), Mathf.Sin(t * 29f + seed * 2f), Mathf.Sin(t * 43f + seed * 3f)) * amount;
    }
}
