namespace Scalpel.Patients;

/// <summary>
/// The skin solver being tried out in the Skin Lab (<see cref="ScenarioDef.SkinSolver"/> "xpbd"): extended position
/// based dynamics. Every spring is a distance constraint with a compliance (its softness in meters per newton-ish,
/// not a share of the error fixed per pass), so how stiff the skin is no longer depends on how often it's solved, and
/// every point is tethered to its own spot on the bed under it: it slides along the bed on a soft spring and never sinks
/// into it. A grip pins its point and nothing more; the skin around follows by its springs alone. None of the default
/// solver's fold, area, upright or patch passes run.
/// </summary>
public sealed partial class TissueSim {
    /// <summary>Whether this sheet runs on the XPBD solver.</summary>
    public bool Xpbd { get; set; }

    /// <summary>A tissue spring's compliance (m/N for a unit-mass grid): how far it gives under a pull. 0 would be
    /// inextensible; skin stretches noticeably before it tears.</summary>
    private const float XpbdStretch = 0.00002f;
    /// <summary>A tissue spring's compliance pressed shorter: skin hardly resists being squeezed, it gathers. A cut's
    /// lips draw back into the skin behind them, between two cuts as well.</summary>
    private const float XpbdSqueeze = 0.002f;
    /// <summary>How readily the skin slides along its bed (compliance of each point's tether along the body), and how
    /// readily it lifts off it (along the normal): lifted, it sinks back once let go. Much softer, skin a grip pushes
    /// ahead of it buckles up off the bed in a crumple instead of sliding along it.</summary>
    private const float XpbdSlide = 0.0004f;
    private const float XpbdLift = 0.01f;
    /// <summary>How much more readily skin beside a cut slides along its bed than skin elsewhere. Few iterations a step
    /// leave a soft tether far stiffer than its compliance says, so it takes this much for the tension to show.</summary>
    private const float XpbdBesideCut = 36f;
    /// <summary>How far (meters) a cut's lip drawn on past where the cut settles it is still held there firmly.</summary>
    private const float XpbdLipHold = 0.003f;
    /// <summary>How many grid steps around a stitch's ends the skin follows the stitch rather than the cut.</summary>
    private const int XpbdSewnReach = 2;
    /// <summary>How readily the skin bends (compliance of the curvature along each row, column and diagonal of the grid):
    /// a lifted lip rises in a rounded arch, not a peak, without lifting the skin across the cut with it.</summary>
    private const float XpbdBend = 0.002f;

    /// <summary>Each spring's Lagrange multiplier for this step: XPBD sums the corrections of the step's iterations.
    /// </summary>
    private float[] _lambda = [];
    /// <summary>Each point's tether multipliers for this step, along the body and off it.</summary>
    private Vector3[] _tetherLambda = [];
    /// <summary>Each point's bend multipliers for this step, four to a point: along its row, its column and its two
    /// diagonals.</summary>
    private float[] _bendLambda = [];
    /// <summary>Points a stitch, staple or strip holds this step, and the skin around them: the closure, not the cut,
    /// says where they go.</summary>
    private bool[] _sewn = [];

    private void BeginXpbdStep() {
        if (_lambda.Length < _springs.Length) {
            _lambda = new float[_springs.Length];
        }
        if (_tetherLambda.Length != Pos.Length) {
            _tetherLambda = new Vector3[Pos.Length];
            _bendLambda = new float[Pos.Length * 4];
            _sewn = new bool[Pos.Length];
        }
        Array.Clear(_sewn);
        foreach (var s in _stitches) {
            if (!_springs[s].Active) {
                continue;
            }
            foreach (var end in (ReadOnlySpan<int>)[_springs[s].A, _springs[s].B]) {
                var at = CellOf(end);
                for (var j = Math.Max(at.Y - XpbdSewnReach, 0); j <= Math.Min(at.Y + XpbdSewnReach, ResY); j++) {
                    for (var i = Math.Max(at.X - XpbdSewnReach, 0); i <= Math.Min(at.X + XpbdSewnReach, ResX); i++) {
                        _sewn[Index(i, j)] = true;
                    }
                }
            }
        }
        foreach (var s in WindowSprings) {
            _lambda[s] = 0f;
        }
        foreach (var s in _stitches) {
            _lambda[s] = 0f;
        }
        foreach (var k in WindowParticles) {
            _tetherLambda[k] = Vector3.Zero;
            Array.Clear(_bendLambda, k * 4, 4);
        }
    }

    /// <summary>One XPBD iteration: grips, springs, stitches, bending, the cut's edges, the bed.</summary>
    private void XpbdIteration() {
        foreach (var pin in _pins.Values) {
            DragPatch(pin);
        }
        var stretch = XpbdStretch / (Step * Step);
        var squeeze = XpbdSqueeze / (Step * Step);
        var count = WindowSprings.Length;
        for (var n = 0; n < count; n++) {
            SolveCompliant(WindowSprings[n], stretch, squeeze);
        }
        for (var pass = 0; pass <= StitchPasses; pass++) {
            foreach (var s in _stitches) {
                Solve(s);
            }
        }
        BendXpbd();
        KeepCutEdgesApart();
        Tether();
        StayOnBody();
        StayAboveFloor();
    }

    /// <summary>Spring s as an XPBD distance constraint with compliance <paramref name="alpha"/> stretched and
    /// <paramref name="shorter"/> squeezed (both already divided by the step squared).</summary>
    private void SolveCompliant(int s, float alpha, float shorter) {
        ref readonly var spring = ref _springs[s];
        if (!spring.Active || Resting(s)) {
            return;
        }
        var wa = _free[spring.A];
        var wb = _free[spring.B];
        if (wa + wb == 0f) {
            return;
        }
        var d = Span(spring);
        var length = d.Length();
        if (length < 0.00001f) {
            return;
        }
        var c = length - spring.Rest;
        if (c < 0f) {
            alpha = shorter;
        }
        var delta = (-c - (alpha * _lambda[s])) / (wa + wb + alpha);
        _lambda[s] += delta;
        var correction = d * (delta / length);
        Pos[spring.A] -= correction * wa;
        Pos[spring.B] += correction * wb;
    }

    /// <summary>
    /// The skin's curvature kept as it lay, along every row, column and diagonal of the grid: a point and its two
    /// neighbours on either side keep the bend between them (a + c - 2b, the discrete curvature) as it was at rest, in
    /// direction as well as size, where both springs to them hold. Folded back over, the curvature points the other way,
    /// so a fold is held back as firmly as a bend; along the diagonals a narrow strip between two cuts can't twist.
    /// Across a cut there's nothing to bend, so a lip turns up freely at its edge but the skin behind it curves smoothly.
    /// </summary>
    private void BendXpbd() {
        var alpha = XpbdBend / (Step * Step);
        foreach (var b in WindowParticles) {
            var cell = CellOf(b);
            if (cell.X > 0 && cell.X < ResX) {
                var a = Index(cell.X - 1, cell.Y);
                BendAlong(a, b, Index(cell.X + 1, cell.Y), SpringRight[a], SpringRight[b], alpha, (b * 4) + 0);
            }
            if (cell.Y > 0 && cell.Y < ResY) {
                var a = Index(cell.X, cell.Y - 1);
                BendAlong(a, b, Index(cell.X, cell.Y + 1), SpringDown[a], SpringDown[b], alpha, (b * 4) + 1);
            }
            if (cell.X > 0 && cell.X < ResX && cell.Y > 0 && cell.Y < ResY) {
                var a = Index(cell.X - 1, cell.Y - 1);
                BendAlong(a, b, Index(cell.X + 1, cell.Y + 1), SpringAnti[a], SpringAnti[b], alpha, (b * 4) + 2);
                BendAlong(Index(cell.X - 1, cell.Y + 1), b, Index(cell.X + 1, cell.Y - 1), SpringDiag[Index(cell.X - 1, cell.Y)],
                    SpringDiag[Index(cell.X, cell.Y - 1)], alpha, (b * 4) + 3);
            }
        }
    }

    /// <summary>The curvature at b between a and c (joined to it by springs <paramref name="ab"/> and
    /// <paramref name="bc"/>) brought toward its rest curvature, with its multiplier at <paramref name="slot"/>.</summary>
    private void BendAlong(int a, int b, int c, int ab, int bc, float alpha, int slot) {
        if (!Holds(ab) || !Holds(bc)) {
            return;
        }
        var (wa, wb, wc) = (_free[a], _free[b], _free[c]);
        var w = wa + wc + (4f * wb);
        var off = Pos[a] + Pos[c] - (Pos[b] * 2f) - (Rest[a] + Rest[c] - (Rest[b] * 2f));
        var length = off.Length();
        if (length < 0.000001f || w == 0f) {
            return;
        }
        var delta = (-length - (alpha * _bendLambda[slot])) / (w + alpha);
        _bendLambda[slot] += delta;
        var n = off * (delta / length);
        Pos[a] += n * wa;
        Pos[c] += n * wc;
        Pos[b] -= n * (2f * wb);
    }

    private bool Holds(int s) => s >= 0 && _springs[s].Active;

    /// <summary>
    /// Every free point drawn toward its spot on the bed (<see cref="_anchorTarget"/>, which a cut moves back from its
    /// edge so the wound gapes): along the body softly, so the skin slides a little under a pull, and off it more
    /// softly still, so a lifted lip settles back down. It gives out over <see cref="AnchorReach"/>: skin pulled that
    /// far has come away from its bed. It never pulls the skin down into the body (StayOnBody()).
    /// </summary>
    private void Tether() {
        var slide = XpbdSlide / (Step * Step);
        var lift = XpbdLift / (Step * Step);
        foreach (var k in WindowParticles) {
            if (_free[k] == 0f) {
                continue;
            }
            var d = Pos[k] - _anchorTarget[k];
            var reach = Mathf.Clamp(1f - (d.Length() / AnchorReach), 0f, 1f);
            if (reach <= 0f) {
                continue;
            }
            // Beside a cut (its anchor loosened, or its edge drawn back) it slides far more readily: the skin's own tension
            // opens the cut there, as wide as the default solver's.
            var loose = 1f / reach;
            var normal = _restNormal[k];
            var up = d.Dot(normal);
            var along = d - (normal * up);
            ref var lambda = ref _tetherLambda[k];
            // Loosened beside a cut it gives way to the lips. A lip itself is held to where the cut draws it back as
            // firmly as its pull says (GapePull, MusclePull), so the cut opens that far; drawn on past there by a hand,
            // it gives as readily as the skin beside it.
            var length = along.Length();
            var beside = XpbdBesideCut;
            if (_anchor[k] > Anchor && !_sewn[k]) {
                var past = Pos[k].DistanceTo(Rest[k]) - _anchorTarget[k].DistanceTo(Rest[k]);
                beside = Mathf.Lerp(1f, XpbdBesideCut, Mathf.SmoothStep(0f, XpbdLipHold, past));
            }
            var alpha = slide * loose * beside;
            if (length > 0.000001f) {
                var delta = (-length - (alpha * lambda.X)) / (1f + alpha);
                lambda.X += delta;
                Pos[k] += along * (delta / length);
            }
            if (up > 0f) {
                alpha = lift * loose;
                var delta = (-up - (alpha * lambda.Y)) / (1f + alpha);
                lambda.Y += delta;
                Pos[k] += normal * delta;
            }
        }
    }
}
