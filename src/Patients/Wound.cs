namespace Scalpel.Patients;

/// <summary>What made a wound.</summary>
public enum WoundKind { Cut, Tear, Burn, Puncture, Gunshot, Internal }

/// <summary>
/// One wound. Skin wounds are polylines in site uv space, internal wounds are a point under the skin.
/// Closure is tracked per segment of length ("bins"), so a wound is only closed once you've sewn along all of it.
/// </summary>
public sealed class Wound {
    /// <summary>ml/s per meter of fully deep, fully open wound.</summary>
    public const float BleedPerMeter = 35f;
    /// <summary>A clean incision parts the tissue with few vessels torn open, and the small ones it cuts close up on
    /// their own: it bleeds this share of what a jagged cut or a tear as long and deep bleeds.</summary>
    public const float CleanIncisionShare = 0.15f;
    public const float BinLengthUv = 0.015f;
    /// <summary>A stroke reports a point every frame; closer than this the last point just moves, so long cuts stay
    /// cheap to query.</summary>
    public const float PointSpacingUv = 0.006f;
    /// <summary>Wound depth (0..1) from which it goes through the muscle, see Patient.TissueDepthOf().</summary>
    public const float MuscleDepth = 0.7f;
    /// <summary>Seconds gauze pressure keeps holding a wound after the gauze comes off.</summary>
    public const float PressureHold = 15f;
    /// <summary>Seconds over which held pressure then wears off and the bleeding comes back.</summary>
    public const float PressureFade = 10f;
    /// <summary>Cuts shorter than this (meters) are too small to sew: gauze pressed on them stops them for good.
    /// </summary>
    public const float SmallCut = 0.01f;
    /// <summary>Seconds of gauze pressure, without a break, that stop a small cut for good.</summary>
    public const float SmallCutPress = 10f;
    /// <summary>A break in gauze pressure longer than this (seconds) starts a small cut's count over.</summary>
    public const float PressBreak = 1f;

    private readonly List<Vector2> _points;
    /// <summary>Bins cut open again (<see cref="Recut"/>) and how far cautery has sealed each since, until it seals
    /// them as far as the rest.</summary>
    private readonly Dictionary<int, float> _reopened = [];
    /// <summary>Per bin: cut too fast or with a dull blade there (<see cref="MarkJagged"/>).</summary>
    private bool[] _jagged = [];
    private float _length;

    public Wound(int id, WoundKind kind, Vector2 start, float depth) {
        Id = id;
        Kind = kind;
        _points = [start];
        Depth = depth;
        ResizeBins();
    }

    public int Id { get; }
    public WoundKind Kind { get; }
    public IReadOnlyList<Vector2> Points => _points;
    /// <summary>0..1. 0.7+ goes through the skin and can be opened.</summary>
    public float Depth { get; set; }
    /// <summary>Internal wounds: meters below the skin surface.</summary>
    public float DepthM { get; set; }
    public float Opened { get; set; }
    public float Cauterized { get; set; }
    public float Clamped { get; set; }
    /// <summary>Pressure from the patient's own hands, decays over time.</summary>
    public float Held { get; set; }
    /// <summary>Pressure from gauze, 0..1. It holds for <see cref="PressureHold"/> after the gauze comes off, then
    /// wears off.</summary>
    public float Pressed { get; set; }
    /// <summary>Seconds since gauze last pressed on it.</summary>
    public float SincePressed { get; set; } = float.PositiveInfinity;
    /// <summary>Seconds of gauze pressure without a break, toward stopping a small cut.</summary>
    public float PressedFor { get; set; }
    /// <summary>A small cut pressed long enough: it's stopped bleeding for good, until it's torn or cut further.
    /// </summary>
    public bool Clotted { get; set; }
    /// <summary>Has dirt in it (glass went in through it) until enough liquid is run through it (Patient.Spill()).
    /// </summary>
    public bool Dirty { get; set; }
    /// <summary>ml of liquid run through it so far.</summary>
    public float Rinsed { get; set; }
    public bool MadeBySurgeon { get; set; }
    /// <summary>At least one stretch was cut too fast or with a dull blade.</summary>
    internal bool Jagged => _jagged.Any(value => value);
    /// <summary>Closure progress per bin, 0..1.</summary>
    public float[] Bins { get; set; } = [];
    /// <summary>Muscle closure per bin, 0..1, for wounds through the muscle (sewn on the cut's muscle faces, before
    /// the skin).</summary>
    public float[] Muscle { get; set; } = [];
    /// <summary>Weighted closure quality, lower bursts easier.</summary>
    public float ClosureQuality { get; set; } = 1f;
    /// <summary>ml/s from vessels a staple went through (Patient.StapleBleed()): it bleeds through the closure until
    /// cauterized (which seals it for good, Patient.CauterizeAt()), clamped or pressed.</summary>
    public float Nicked { get; set; }
    /// <summary>ml/s at the last simulation tick (host).</summary>
    public float Bleeding { get; set; }
    /// <summary>ml/s at the last simulation tick without gauze pressure (host).</summary>
    public float LastingBleeding { get; set; }
    /// <summary>Blood (ml) held in the cut itself, on the first layer it hasn't cut through, while it doesn't reach the
    /// cavity (host).</summary>
    public float PooledMl { get; set; }

    public bool IsInternal => Kind == WoundKind.Internal;

    /// <summary>A stab or a bullet hole: too small to reach into.</summary>
    public bool IsHole => Kind is WoundKind.Puncture or WoundKind.Gunshot;

    /// <summary>Skin closure counts for it: a skin wound that isn't a burn.</summary>
    public bool IsSkinCut => !IsInternal && Kind != WoundKind.Burn;

    public void Extend(Vector2 point) {
        // Torn or cut further, it's fresh damage: it bleeds again, however small it was, and needs a full press again.
        Clotted = false;
        PressedFor = 0f;
        var last = _points.Count - 1;
        // Compact forward samples, but preserve a turnaround: returning near the previous point
        // must not erase the blade's initial footprint or the endpoint still needing closure.
        if (last >= 1 && _points[last - 1].DistanceTo(point) < PointSpacingUv
            && (_points[last] - _points[last - 1]).Dot(point - _points[last]) >= 0f) {
            _length += _points[last - 1].DistanceTo(point) - _points[last - 1].DistanceTo(_points[last]);
            _points[last] = point;
        } else {
            _length += _points[last].DistanceTo(point);
            _points.Add(point);
        }
        ResizeBins();
    }

    /// <summary>A continuous blade can reverse across its initial footprint or withdraw along its cut.
    /// Keep one incision's extent instead of counting that same stretch twice.</summary>
    internal void FollowStroke(Vector2 a, Vector2 b, float reach, bool throughMuscle) {
        if (_points.Count == 2 && (_points[1] - _points[0]).Dot(b - a) < 0f) {
            _points.Reverse();
            Array.Reverse(Bins);
            Array.Reverse(Muscle);
            Array.Reverse(_jagged);
            var reopened = _reopened.ToArray();
            _reopened.Clear();
            foreach (var (bin, seal) in reopened) { _reopened[Bins.Length - 1 - bin] = seal; }
        }
        if (Runs(a, b, reach)) {
            Recut(a, b, throughMuscle);
        } else {
            Extend(b);
        }
    }

    /// <summary>
    /// Cut again from <paramref name="a"/> to <paramref name="b"/> along its own line: fresh damage there. It bleeds
    /// again however it was stopped, its skin (and its muscle, <paramref name="throughMuscle"/>) is open again along
    /// that stretch, and the cautery's seal no longer holds there (<see cref="BleedRate"/>).
    /// </summary>
    public void Recut(Vector2 a, Vector2 b, bool throughMuscle) {
        Clotted = false;
        PressedFor = 0f;
        var (from, to) = (BinAt(a), BinAt(b));
        for (var bin = Math.Min(from, to); bin <= Math.Max(from, to); bin++) {
            Bins[bin] = 0f;
            if (throughMuscle) {
                Muscle[bin] = 0f;
            }
            _reopened[bin] = 0f;
        }
    }

    /// <summary>The stretch from <paramref name="a"/> to <paramref name="b"/> was cut too fast or with a dull blade:
    /// there it bleeds as a tear does (<see cref="CleanIncisionShare"/>).</summary>
    public void MarkJagged(Vector2 a, Vector2 b) {
        var (from, to) = (BinAt(a), BinAt(b));
        for (var bin = Math.Min(from, to); bin <= Math.Max(from, to); bin++) {
            _jagged[bin] = true;
        }
    }

    /// <summary>Seals it further with cautery at <paramref name="uv"/>, up to <paramref name="most"/>. A stretch cut
    /// open again within <paramref name="reach"/> of it is sealed again from scratch, as far as the rest at most.
    /// </summary>
    public void Cauterize(float amount, float most, Vector2 uv, float reach) {
        Cauterized = Mathf.Min(Cauterized + amount, most);
        foreach (var bin in _reopened.Keys.Where(bin => BinPosition(bin).DistanceTo(uv) < reach).ToList()) {
            _reopened[bin] = Mathf.Min(_reopened[bin] + amount, most);
            if (_reopened[bin] >= Cauterized) {
                _reopened.Remove(bin);
            }
        }
    }

    /// <summary>The stroke from <paramref name="a"/> to <paramref name="b"/> follows its line: it goes along it rather
    /// than across, every point of it within <paramref name="reach"/> of the line and next to the stretch the point
    /// before was by. A stroke between the two arms of a U stays close to the line but jumps from one arm to the other.
    /// </summary>
    public bool Runs(Vector2 a, Vector2 b, float reach) {
        if (Mathf.Abs(AlongAt(b) - AlongAt(a)) < a.DistanceTo(b) * 0.5f) {
            return false;
        }
        var steps = Math.Max(Mathf.CeilToInt(a.DistanceTo(b) / PointSpacingUv), 1);
        var step = a.DistanceTo(b) / steps;
        var along = AlongAt(a);
        for (var i = 0; i <= steps; i++) {
            var point = a.Lerp(b, (float)i / steps);
            var next = AlongAt(point);
            if (DistanceTo(point) >= reach || Mathf.Abs(next - along) > step + reach) {
                return false;
            }
            along = next;
        }
        return true;
    }

    public float LengthUv => _length;

    public float Closure => Bins.Length == 0 ? 0f : Bins.Average();

    /// <summary>Cut through the muscle: its muscle has to be sewn before the skin will close over it.</summary>
    public bool ThroughMuscle => IsSkinCut && Depth >= MuscleDepth;

    /// <summary>A surgeon's cut made cleanly at bin <paramref name="bin"/>, see <see cref="CleanIncisionShare"/>.
    /// </summary>
    private bool CleanAt(int bin) => Kind == WoundKind.Cut && MadeBySurgeon && !_jagged[bin];

    /// <summary>A cut or tear too short to sew, see <see cref="SmallCut"/>.</summary>
    public bool IsSmall(float siteSize) => Kind is WoundKind.Cut or WoundKind.Tear && LengthUv * siteSize < SmallCut;

    /// <summary>ml/s it bleeds. <paramref name="leak"/> (0..1) is how much blood still gets through closures and
    /// packing: thinned blood or high pressure. Cautery and clamps seal regardless. Without
    /// <paramref name="withGauze"/> it's what bleeds once gauze pressure has worn off.</summary>
    public float BleedRate(float siteSize, float bleedMult, float leak = 0f, bool withGauze = true) {
        if (Kind == WoundKind.Burn) {
            return 0f;
        }
        var held = withGauze ? Mathf.Max(Held, Pressed) : Held;
        // Each stretch bleeds as it was cut (cleanly or not), less what its closure and the cautery's seal hold back. Cut
        // open again, a stretch has only the seal cautery has given it since (Recut()).
        var through = 0f;
        for (var i = 0; i < Bins.Length; i++) {
            var seal = _reopened.TryGetValue(i, out var resealed) ? resealed : Cauterized;
            through += (CleanAt(i) ? CleanIncisionShare : 1f) * (1f - (Bins[i] * (1f - leak))) * (1f - seal);
        }
        through /= Bins.Length;
        var baseRate = Mathf.Max(LengthUv * siteSize, 0.01f) * Depth * BleedPerMeter;
        if (Kind is WoundKind.Gunshot or WoundKind.Puncture or WoundKind.Internal) {
            baseRate = Mathf.Max(baseRate, Depth * 2f);
        }
        var openFactor = 1f + Opened * 0.5f;
        // A clotted cut is dry, but a vessel a staple goes through later bleeds regardless.
        var own = Clotted ? 0f : baseRate * openFactor * through * (1f - (held * (1f - leak)));
        return (own + (Nicked * (1f - held) * (1f - Cauterized))) * bleedMult * (1f - Clamped);
    }

    public float DistanceTo(Vector2 uv) => uv.DistanceTo(ClosestPoint(uv));

    /// <summary>The point of the wound's line nearest to uv.</summary>
    public Vector2 ClosestPoint(Vector2 uv) {
        var best = _points[0];
        for (var i = 1; i < _points.Count; i++) {
            var on = Geometry2D.GetClosestPointToSegment(uv, _points[i - 1], _points[i]);
            if (on.DistanceSquaredTo(uv) < best.DistanceSquaredTo(uv)) {
                best = on;
            }
        }
        return best;
    }

    /// <summary>Index of the closure bin nearest to uv.</summary>
    public int BinAt(Vector2 uv) => Math.Clamp((int)(AlongAt(uv) / BinLengthUv), 0, Bins.Length - 1);

    /// <summary>How far along its line (uv) the point nearest to uv lies.</summary>
    public float AlongAt(Vector2 uv) {
        var travelled = 0f;
        var best = 0f;
        var bestDistance = float.PositiveInfinity;
        for (var i = 1; i < _points.Count; i++) {
            var a = _points[i - 1];
            var b = _points[i];
            var closest = Geometry2D.GetClosestPointToSegment(uv, a, b);
            var distance = uv.DistanceTo(closest);
            if (distance < bestDistance) {
                bestDistance = distance;
                best = travelled + a.DistanceTo(closest);
            }
            travelled += a.DistanceTo(b);
        }
        return best;
    }

    /// <summary>uv position of a bin's center along the polyline.</summary>
    public Vector2 BinPosition(int bin) {
        var target = (bin + 0.5f) * BinLengthUv;
        var travelled = 0f;
        for (var i = 1; i < _points.Count; i++) {
            var segment = _points[i - 1].DistanceTo(_points[i]);
            if (travelled + segment >= target) {
                return _points[i - 1].Lerp(_points[i], (target - travelled) / Mathf.Max(segment, 0.0001f));
            }
            travelled += segment;
        }
        return _points[^1];
    }

    /// <summary>uv positions of every bin's center.</summary>
    public List<Vector2> BinPositions() => [.. Enumerable.Range(0, Bins.Length).Select(BinPosition)];

    public Vector2 Midpoint => _points.Count > 1 ? BinPosition(Bins.Length / 2) : _points[0];

    /// <summary>Where it bleeds from: the least closed stretch, nearest the middle among equals.</summary>
    public Vector2 BleedPoint => _points.Count > 1
        ? BinPosition(Enumerable.Range(0, Bins.Length).MinBy(i => (Mathf.Round(Bins[i] * 10f), Math.Abs(i - Bins.Length / 2))))
        : _points[0];

    /// <summary>Bins only grow: moving the last point back a little never throws away closure progress.</summary>
    private void ResizeBins() {
        var size = Math.Max(Math.Max(Bins.Length, Mathf.CeilToInt(_length / BinLengthUv)), 1);
        if (size != Bins.Length) {
            var bins = Bins;
            var muscle = Muscle;
            Array.Resize(ref bins, size);
            Array.Resize(ref muscle, size);
            Array.Resize(ref _jagged, size);
            Bins = bins;
            Muscle = muscle;
        }
    }
}
