namespace Scalpel.Patients;

/// <summary>What made a wound.</summary>
public enum WoundKind { Cut, Tear, Burn, Puncture, Gunshot, Internal }

/// <summary>
/// One wound. Skin wounds are polylines in site uv space, internal wounds are a point under the skin.
/// Closure is tracked per segment of length ("bins"), so a wound is only closed once you've sewn along all of it.
/// </summary>
public sealed class Wound
{
    /// <summary>ml/s per meter of fully deep, fully open wound.</summary>
    public const float BleedPerMeter = 35f;
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
    private float _length;

    public Wound(int id, WoundKind kind, Vector2 start, float depth)
    {
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
    /// <summary>A small cut pressed long enough: it's stopped bleeding for good.</summary>
    public bool Clotted { get; set; }
    public bool Dirty { get; set; }
    public bool MadeBySurgeon { get; set; }
    /// <summary>Closure progress per bin, 0..1.</summary>
    public float[] Bins { get; set; } = [];
    /// <summary>Muscle closure per bin, 0..1, for wounds through the muscle (sewn from inside the opening, before the
    /// skin).</summary>
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

    public bool IsInternal => Kind == WoundKind.Internal;

    /// <summary>A stab or a bullet hole: too small to reach into.</summary>
    public bool IsHole => Kind is WoundKind.Puncture or WoundKind.Gunshot;

    /// <summary>Skin closure counts for it: a skin wound that isn't a burn.</summary>
    public bool IsSkinCut => !IsInternal && Kind != WoundKind.Burn;

    public void Extend(Vector2 point)
    {
        var last = _points.Count - 1;
        if (last >= 1 && _points[last - 1].DistanceTo(point) < PointSpacingUv)
        {
            _length += _points[last - 1].DistanceTo(point) - _points[last - 1].DistanceTo(_points[last]);
            _points[last] = point;
        }
        else
        {
            _length += _points[last].DistanceTo(point);
            _points.Add(point);
        }
        ResizeBins();
    }

    public float LengthUv => _length;

    public float Closure => Bins.Length == 0 ? 0f : Bins.Average();

    /// <summary>Cut through the muscle: its muscle has to be sewn before the skin will close over it.</summary>
    public bool ThroughMuscle => IsSkinCut && Depth >= MuscleDepth;

    /// <summary>A cut or tear too short to sew, see <see cref="SmallCut"/>.</summary>
    public bool IsSmall(float siteSize) => Kind is WoundKind.Cut or WoundKind.Tear && LengthUv * siteSize < SmallCut;

    /// <summary>ml/s it bleeds. <paramref name="leak"/> (0..1) is how much blood still gets through closures and
    /// packing: thinned blood or high pressure. Cautery and clamps seal regardless. Without
    /// <paramref name="withGauze"/> it's what bleeds once gauze pressure has worn off.</summary>
    public float BleedRate(float siteSize, float bleedMult, float leak = 0f, bool withGauze = true)
    {
        if (Kind == WoundKind.Burn || Clotted)
        {
            return 0f;
        }
        var held = withGauze ? Mathf.Max(Held, Pressed) : Held;
        var baseRate = Mathf.Max(LengthUv * siteSize, 0.01f) * Depth * BleedPerMeter;
        if (Kind is WoundKind.Gunshot or WoundKind.Puncture or WoundKind.Internal)
        {
            baseRate = Mathf.Max(baseRate, Depth * 2f);
        }
        var openFactor = 1f + Opened * 0.5f;
        var sealedShare = (1f - Closure * (1f - leak)) * (1f - held * (1f - leak));
        return (baseRate * openFactor * sealedShare + Nicked * (1f - held)) * bleedMult * (1f - Cauterized) * (1f - Clamped);
    }

    public float DistanceTo(Vector2 uv) => uv.DistanceTo(ClosestPoint(uv));

    /// <summary>The point of the wound's line nearest to uv.</summary>
    public Vector2 ClosestPoint(Vector2 uv)
    {
        var best = _points[0];
        for (var i = 1; i < _points.Count; i++)
        {
            var on = Geometry2D.GetClosestPointToSegment(uv, _points[i - 1], _points[i]);
            if (on.DistanceSquaredTo(uv) < best.DistanceSquaredTo(uv))
            {
                best = on;
            }
        }
        return best;
    }

    /// <summary>Index of the closure bin nearest to uv.</summary>
    public int BinAt(Vector2 uv)
    {
        var travelled = 0f;
        var bestBin = 0;
        var bestDistance = float.PositiveInfinity;
        for (var i = 1; i < _points.Count; i++)
        {
            var a = _points[i - 1];
            var b = _points[i];
            var closest = Geometry2D.GetClosestPointToSegment(uv, a, b);
            var distance = uv.DistanceTo(closest);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestBin = (int)((travelled + a.DistanceTo(closest)) / BinLengthUv);
            }
            travelled += a.DistanceTo(b);
        }
        return Math.Clamp(bestBin, 0, Bins.Length - 1);
    }

    /// <summary>uv position of a bin's center along the polyline.</summary>
    public Vector2 BinPosition(int bin)
    {
        var target = (bin + 0.5f) * BinLengthUv;
        var travelled = 0f;
        for (var i = 1; i < _points.Count; i++)
        {
            var segment = _points[i - 1].DistanceTo(_points[i]);
            if (travelled + segment >= target)
            {
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
    private void ResizeBins()
    {
        var size = Math.Max(Math.Max(Bins.Length, Mathf.CeilToInt(_length / BinLengthUv)), 1);
        if (size != Bins.Length)
        {
            var bins = Bins;
            var muscle = Muscle;
            Array.Resize(ref bins, size);
            Array.Resize(ref muscle, size);
            Bins = bins;
            Muscle = muscle;
        }
    }
}
