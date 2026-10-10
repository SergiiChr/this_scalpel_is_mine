namespace Scalpel.Patients;

/// <summary>
/// Procedural animation of the patient's skeleton, driven by the synced vitals on every peer.
/// Breathing follows the respiration rate, awake patients look around, talk, flinch and panic, seizures shake every
/// joint. The limb carrying the surgical site stays still so the site doesn't drift.
/// Every motion is a rotation in model space (X toward the head, Y up, Z the patient's left) about the joint.
/// </summary>
public partial class PatientAnimator : Node
{
    public const float BreathDepth = 0.018f;
    public const float TorsoTop = 0.215f;
    /// <summary>How long the eyes stay shut in a blink (seconds).</summary>
    public const float Blink = 0.12f;

    private PatientBody _body = null!;
    private BoneRig? _rig;
    private Dictionary<string, Node3D> _parts = [];
    private float _breathPhase;
    private float _beatPhase;
    private float _talkLeft;
    private float _flinch;
    private float _jolt;
    private float _lastPain;
    private string _lockedSide = "";
    /// <summary>Seconds until the next blink.</summary>
    private float _blinkIn = 3f;
    /// <summary>How much of the current blink is left.</summary>
    private float _blinkLeft;

    public void Setup(PatientBody body, Node3D model)
    {
        _body = body;
        _rig = BoneRig.Find(model);
        _parts = ModelSlot.Parts(model, "EyeL", "EyeR", "Lids");
        if (body.IsLimbSite)
        {
            _lockedSide = "L";
        }
    }

    /// <summary>Patient said something: move the jaw for a while.</summary>
    public void Talk(float seconds) => _talkLeft = seconds;

    /// <summary>A defibrillator shock: every muscle contracts at once for an instant.</summary>
    public void Jolt() => _jolt = 1f;

    public void Animate(Vitals vitals, bool alive, float delta)
    {
        if (_rig is null)
        {
            return;
        }
        var t = (float)GameClock.Seconds;
        var breathing = alive && !vitals.IsArrested;
        var rate = vitals.Anesthesia < 0.7f ? (14f + vitals.Panic * 14f) / 60f : 12f / 60f;
        _breathPhase += breathing ? delta * rate * Mathf.Tau : 0f;
        var breath = (Mathf.Sin(_breathPhase) * 0.5f + 0.5f) * BreathDepth * (1f + vitals.Panic);
        // The whole trunk rises with the surgical site; legs, neck and arms stay put so only the torso skin moves.
        _jolt = Mathf.MoveToward(_jolt, 0f, delta * 5f);
        var rise = new Vector3(0, TorsoTop * breath + _jolt * 0.025f, 0);
        _rig.Shift("Torso", rise);
        foreach (var held in new[] { "ThighL", "ThighR", "Neck", "UpperArmL", "UpperArmR" })
        {
            _rig.Hold(held, rise);
        }
        _body.SetBreathOffset(TorsoTop * breath);
        if (breathing)
        {
            _body.Breath = Mathf.Sin(_breathPhase) * 0.5f + 0.5f;
        }
        _body.Heartbeat = HeartbeatOf(vitals, alive, delta);

        var awake = alive && vitals.IsAwake;
        // Awake patients blink every few seconds, more often in pain or panic.
        _blinkIn -= awake ? delta * (1f + vitals.Pain + vitals.Panic * 2f) : 0f;
        if (_blinkIn <= 0f)
        {
            _blinkIn = (float)GD.RandRange(2.5, 6.0);
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

        if (vitals.Pain > _lastPain + 0.08f && awake)
        {
            _flinch = 1f;
        }
        _lastPain = vitals.Pain;
        _flinch = Mathf.MoveToward(_flinch, 0f, delta * 3f);
        // Looking left and right turns the head about its own long axis.
        var look = awake ? Mathf.Sin(t * 0.4f) * 0.35f : 0f;
        var head = new Vector3(look, 0, 0)
            + new Vector3((float)GD.RandRange(-1.0, 1.0), 0, (float)GD.RandRange(-1.0, 1.0)) * _flinch * 0.15f;
        var shake = 0f;
        if (vitals.Seizing)
        {
            shake = 0.25f;
        }
        else if (awake && vitals.Panic > 0.6f)
        {
            shake = (vitals.Panic - 0.6f) * 0.8f;
        }
        _rig.Rotate("Head", Basis.FromEuler(head + Jitter(shake * 0.6f, t, 1f)));
        foreach (var side in new[] { "L", "R" })
        {
            var mirror = side == "L" ? 1f : -1f;
            var still = side == _lockedSide;
            var amount = still ? 0f : shake + _flinch * 0.2f + _jolt * 0.35f;
            // Panicking patients lift their arms off the table: the arms point toward the feet, so a negative turn
            // about Z raises them.
            var lift = still || !awake ? 0f : vitals.Panic * 0.15f;
            _rig.Rotate("UpperArm" + side, Basis.FromEuler(new Vector3(0, 0, -lift) + Jitter(amount, t, 2f + mirror)));
            _rig.Rotate("Forearm" + side, Basis.FromEuler(new Vector3(0, 0, -lift * 0.8f) + Jitter(amount, t, 3f + mirror)));
            _rig.Rotate("Hand" + side, Basis.FromEuler(Jitter(amount * 1.5f, t, 4f + mirror)));
            _rig.Rotate("Thigh" + side, Basis.FromEuler(Jitter(amount * 0.4f, t, 5f + mirror)));
            _rig.Rotate("Shin" + side, Basis.FromEuler(Jitter(amount * 0.4f, t, 6f + mirror)));
        }
    }

    /// <summary>Heart contraction 0..1: a quick squeeze once per beat in sinus rhythm, a feeble quiver in V-fib, still
    /// in asystole.</summary>
    private float HeartbeatOf(Vitals vitals, bool alive, float delta)
    {
        if (!alive || vitals.Rhythm == Rhythm.Asystole)
        {
            return 0f;
        }
        if (vitals.Rhythm == Rhythm.Vfib)
        {
            return GD.Randf() * 0.15f;
        }
        _beatPhase = Mathf.PosMod(_beatPhase + delta * vitals.HeartRate / 60f, 1f);
        return Mathf.Pow(Mathf.Max(Mathf.Sin(_beatPhase * Mathf.Tau), 0f), 3f);
    }

    private void SetVisible(string part, bool value)
    {
        if (_parts.TryGetValue(part, out var node))
        {
            node.Visible = value;
        }
    }

    private static Vector3 Jitter(float amount, float t, float seed)
    {
        if (amount <= 0f)
        {
            return Vector3.Zero;
        }
        return new Vector3(Mathf.Sin(t * 37f + seed), Mathf.Sin(t * 29f + seed * 2f), Mathf.Sin(t * 43f + seed * 3f)) * amount;
    }
}
