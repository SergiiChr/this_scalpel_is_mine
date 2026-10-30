namespace Scalpel.Patients;

/// <summary>
/// A strip of tape stuck on the skin: the points it was pressed down at (grid points, and where each went down at rest,
/// riding its point like a thread's hole), its half width (meters), the stitches it holds a cut's edges shut with, and
/// whether it's cut off the roll or has come off the skin.
/// </summary>
public sealed class TapeStrip(float halfWidth) {
    public List<int> Anchors { get; } = [];
    public List<Vector2> Points { get; } = [];
    public List<int> Holds { get; } = [];
    public float HalfWidth { get; } = halfWidth;
    public bool Final { get; set; }
    public bool Peeled { get; set; }
}

public sealed partial class TissueSim {
    private readonly Dictionary<int, TapeStrip> _tapes = [];
    /// <summary>The strip each of its stitches belongs to.</summary>
    private readonly Dictionary<int, int> _tapeOfSpring = [];

    /// <summary>Every strip's id, the ones that came off too.</summary>
    public IEnumerable<int> TapeIds => _tapes.Keys;

    public TapeStrip? Tape(int id) => _tapes.GetValueOrDefault(id);

    /// <summary>The strip whose stitch spring <paramref name="s"/> is, 0 for none.</summary>
    public int TapeOf(int s) => _tapeOfSpring.GetValueOrDefault(s);

    /// <summary>Presses strip <paramref name="id"/> down at <paramref name="uv"/>, held by grid point
    /// <paramref name="point"/>, starting it there if it's new. False when it's done or already pressed down there.
    /// </summary>
    public bool TapeAnchor(int id, Vector2 uv, int point, float halfWidth) {
        if (!_tapes.TryGetValue(id, out var tape)) {
            tape = new TapeStrip(halfWidth);
            _tapes[id] = tape;
        }
        if (tape.Final || (tape.Anchors.Count > 0 && tape.Anchors[^1] == point)) {
            return false;
        }
        tape.Anchors.Add(point);
        tape.Points.Add(uv);
        Wake();
        return true;
    }

    /// <summary>
    /// Sticks the newest stretch of strip <paramref name="id"/> (between its last two points) down: every cut edge under
    /// it (<see cref="UnderStretch"/>) is held at <paramref name="tension"/> by a stitch. Returns how many edges it holds.
    /// </summary>
    public int TapeStretch(int id, float tension) {
        var tape = _tapes[id];
        var held = 0;
        foreach (var s in UnderStretch(tape)) {
            var hold = AddSpring(_springs[s].A, _springs[s].B, SpringKind.Stitch, tension);
            tape.Holds.Add(hold);
            _tapeOfSpring[hold] = id;
            held++;
        }
        if (held > 0) {
            UpdateRetraction();
            Reshaped();
        }
        return held;
    }

    /// <summary>Host: how far the cut edges under the newest stretch of strip <paramref name="id"/> lie apart, as the
    /// stretch a stitch at <paramref name="tension"/> joining them would start at (its length over its rest length):
    /// what the tape has to pull them together against. 0 with none under it.</summary>
    public float TapeLoad(int id, float tension) =>
        UnderStretch(_tapes[id])
            .Select(s => Pos[_springs[s].A].DistanceTo(Pos[_springs[s].B]) / (Rest[_springs[s].A].DistanceTo(Rest[_springs[s].B]) * tension))
            .DefaultIfEmpty(0f).Max();

    /// <summary>Whether a stitch of <paramref name="strength"/> can pull edges together <paramref name="stretch"/> apart.
    /// </summary>
    public bool Holds(float stretch, float strength) => stretch <= strength;

    /// <summary>The cut edges under the newest stretch of <paramref name="tape"/>: severed springs crossed within its
    /// half width and not held yet. Not where the muscle under it is still cut: that holds the skin apart, and tape
    /// can't reach it.</summary>
    private IEnumerable<int> UnderStretch(TapeStrip tape) {
        if (tape.Anchors.Count < 2) {
            yield break;
        }
        var (from, to) = (tape.Points[^2] * Size, tape.Points[^1] * Size);
        foreach (var s in _severed.ToList()) {
            var at = CrossingUv(s) * Size;
            if (!Stitched(_springs[s].A, _springs[s].B) && CutDepth(s) != TissueDepth.Muscle
                && at.DistanceTo(Geometry2D.GetClosestPointToSegment(at, from, to)) <= tape.HalfWidth) {
                yield return s;
            }
        }
    }

    /// <summary>How far strip <paramref name="id"/>'s hold on the edges is stretched now: its most stretched stitch's
    /// length over its rest length, 0 for none.</summary>
    internal float TapeStrain(int id) =>
        _tapes[id].Holds.Where(s => _springs[s].Active)
            .Select(s => Pos[_springs[s].A].DistanceTo(Pos[_springs[s].B]) / _springs[s].Rest)
            .DefaultIfEmpty(0f).Max();

    /// <summary>The strip is cut off the roll: nothing more is pressed down on it.</summary>
    public void FinishTape(int id) {
        if (_tapes.TryGetValue(id, out var tape)) {
            tape.Final = true;
        }
    }

    /// <summary>The strip comes off the skin: every edge it held lets go.</summary>
    public void PeelTape(int id) {
        if (!_tapes.TryGetValue(id, out var tape)) {
            return;
        }
        tape.Final = true;
        tape.Peeled = true;
        foreach (var s in tape.Holds) {
            _springs[s].Active = false;
        }
        UpdateRetraction();
        Reshaped();
    }
}
