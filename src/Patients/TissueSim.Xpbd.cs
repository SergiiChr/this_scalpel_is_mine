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
    /// <summary>How readily the skin slides along its bed (compliance of each point's tether along the body), and how
    /// readily it lifts off it (along the normal): lifted, it sinks back once let go.</summary>
    private const float XpbdSlide = 0.0004f;
    private const float XpbdLift = 0.05f;
    /// <summary>How readily the skin bends (compliance of the curvature along each row and column of the grid): a
    /// lifted lip rises in a rounded arch, not a peak.</summary>
    private const float XpbdBend = 0.000002f;

    /// <summary>Each spring's Lagrange multiplier for this step: XPBD sums the corrections of the step's iterations.
    /// </summary>
    private float[] _lambda = [];
    /// <summary>Each point's tether multipliers for this step, along the body and off it.</summary>
    private Vector3[] _tetherLambda = [];
    /// <summary>Each point's bend multipliers for this step, along its row (X) and its column (Y).</summary>
    private Vector2[] _bendLambda = [];

    private void BeginXpbdStep() {
        if (_lambda.Length < _springs.Length) {
            _lambda = new float[_springs.Length];
        }
        if (_tetherLambda.Length != Pos.Length) {
            _tetherLambda = new Vector3[Pos.Length];
            _bendLambda = new Vector2[Pos.Length];
        }
        foreach (var s in WindowSprings) {
            _lambda[s] = 0f;
        }
        foreach (var s in _stitches) {
            _lambda[s] = 0f;
        }
        foreach (var k in WindowParticles) {
            _tetherLambda[k] = Vector3.Zero;
            _bendLambda[k] = Vector2.Zero;
        }
    }

    /// <summary>One XPBD iteration: grips, springs, stitches, the cut's edges, the bed.</summary>
    private void XpbdIteration() {
        foreach (var pin in _pins.Values) {
            Pos[pin.Particle] = pin.Target;
            Hold(pin.Particle);
        }
        var stretch = XpbdStretch / (Step * Step);
        var count = WindowSprings.Length;
        for (var n = 0; n < count; n++) {
            SolveCompliant(WindowSprings[n], stretch);
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

    /// <summary>Spring s as an XPBD distance constraint with compliance <paramref name="alpha"/> (already divided by
    /// the step squared).</summary>
    private void SolveCompliant(int s, float alpha) {
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
        var delta = (-c - (alpha * _lambda[s])) / (wa + wb + alpha);
        _lambda[s] += delta;
        var correction = d * (delta / length);
        Pos[spring.A] -= correction * wa;
        Pos[spring.B] += correction * wb;
    }

    /// <summary>
    /// The skin's curvature kept as it lay, along every row and column of the grid: a point and its two neighbours on
    /// either side keep the bend between them (|a + c - 2b|, the discrete curvature) where both springs to them hold.
    /// Across a cut there's nothing to bend, so a lip turns up freely at its edge but the skin behind it curves smoothly.
    /// </summary>
    private void BendXpbd() {
        var alpha = XpbdBend / (Step * Step);
        foreach (var b in WindowParticles) {
            var cell = CellOf(b);
            if (cell.X > 0 && cell.X < ResX) {
                var a = Index(cell.X - 1, cell.Y);
                var c = Index(cell.X + 1, cell.Y);
                if (Holds(SpringRight[a]) && Holds(SpringRight[b])) {
                    _bendLambda[b].X += SolveBend(a, b, c, alpha, _bendLambda[b].X);
                }
            }
            if (cell.Y > 0 && cell.Y < ResY) {
                var a = Index(cell.X, cell.Y - 1);
                var c = Index(cell.X, cell.Y + 1);
                if (Holds(SpringDown[a]) && Holds(SpringDown[b])) {
                    _bendLambda[b].Y += SolveBend(a, b, c, alpha, _bendLambda[b].Y);
                }
            }
        }
    }

    private bool Holds(int s) => s >= 0 && _springs[s].Active;

    /// <summary>The curvature at b between a and c brought toward its rest size; returns the multiplier's change.
    /// </summary>
    private float SolveBend(int a, int b, int c, float alpha, float lambda) {
        var (wa, wb, wc) = (_free[a], _free[b], _free[c]);
        var curve = Pos[a] + Pos[c] - (Pos[b] * 2f);
        var length = curve.Length();
        var w = wa + wc + (4f * wb);
        if (length < 0.000001f || w == 0f) {
            return 0f;
        }
        var rest = (Rest[a] + Rest[c] - (Rest[b] * 2f)).Length();
        var delta = (-(length - rest) - (alpha * lambda)) / (w + alpha);
        var n = curve * (delta / length);
        Pos[a] += n * wa;
        Pos[c] += n * wc;
        Pos[b] -= n * (2f * wb);
        return delta;
    }

    /// <summary>
    /// Every free point drawn toward its spot on the bed (<see cref="_anchorTarget"/>, which a cut moves back from its
    /// edge so the wound gapes): along the body softly, so the skin slides a little under a pull, and off it more
    /// softly still, so a lifted lip settles back down. The spring is as soft along the body near a cut as the default
    /// solver's loosened anchor there, and gives out over <see cref="AnchorReach"/>: skin pulled that far has come away
    /// from its bed. It never pulls the skin down into the body (StayOnBody()).
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
            // As soft as the anchor is weak: Anchor everywhere, LooseAnchor beside a cut, stronger where a cut's edge is
            // drawn back (GapePull, MusclePull).
            var loose = Anchor / Mathf.Max(_anchor[k], 0.0001f) / reach;
            var normal = _restNormal[k];
            var up = d.Dot(normal);
            var along = d - (normal * up);
            ref var lambda = ref _tetherLambda[k];
            var alpha = slide * loose;
            var length = along.Length();
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
