namespace Scalpel.Patients;

public sealed partial class TissueSim
{
    /// <summary>Extra tethering outside a grip's footprint, so a pull doesn't slide the whole site along. It fades in
    /// from FoldNear to FoldFar (meters, at rest) from the grip: a hard edge would crease the skin along it.</summary>
    private const float FoldAnchor = 0.35f;
    private const float FoldNear = DragReach * 1.5f;
    private const float FoldFar = DragReach * 2.5f;
    /// <summary>Gentle intact-edge displacement averaging rounds cell-sized creases into a wave.</summary>
    private const float FoldBend = 0.12f;
    /// <summary>The least two grid points two cells apart along a row or column come to, as a share of their rest
    /// distance (see KeepBendsRound()). 0.8 lets each of the two particles between turn about 70 degrees: a hump,
    /// not a crease.</summary>
    private const float BendSpan = 0.8f;
    /// <summary>The least a spring's reach along the body, as a share of its rest reach (see KeepUpright()): the
    /// skin can rise steeply but not overhang. 0.4 lets it tilt up to about 66 degrees.</summary>
    internal const float UprightShare = 0.4f;
    /// <summary>Faces below this fraction of rest area need the final compression passes.</summary>
    private const float CompressedArea = 0.3f;
    /// <summary>Retain this fraction of rest area so a fold has room for the tissue underneath it.</summary>
    private const float MinimumFoldArea = 0.15f;
    /// <summary>How far (meters) skin folded over other skin is kept above it: a little over the skin's own thickness,
    /// so the sheet under the fold stays out of the fold's skin. Not the fat's thickness too: lifting a fold that
    /// high over what it covers turns its edge up into a wall that the skin beside it folds over.</summary>
    private const float FoldClearance = 0.003f;

    /// <summary>One fold lifecycle; scratch geometry stays in the solver, and contact survives footprint expiry.</summary>
    private sealed class FoldState
    {
        internal bool Active;
        internal bool TurnedOver;
        internal bool Bending;
        internal readonly HashSet<int> Roots = [];
        /// <summary>How much each particle belongs to a grip's footprint: 1 at the grip, 0 past FoldFar.</summary>
        internal float[] Near = [];
        internal bool[] Contact = [];

        internal void Reset(int count)
        {
            Active = TurnedOver = Bending = false;
            Roots.Clear();
            Near = new float[count];
            Contact = new bool[count];
        }

        internal void Update(TissueSim sim)
        {
            Bending = false;
            if (sim._pins.Count == 0) { return; }
            TurnedOver = false;
            foreach (var pin in sim._pins.Values)
            {
                var pull = pin.Target.DistanceSquaredTo(sim._anchorTarget[pin.Particle]);
                TurnedOver |= pull > TurnoverReach * TurnoverReach;
                if (pull <= GripHold * GripHold) { continue; }
                Active = Bending = true;
                if (!Roots.Add(pin.Particle)) { continue; }
                for (var k = 0; k < sim.Rest.Length; k++)
                {
                    var near = 1f - Mathf.SmoothStep(FoldNear, FoldFar, sim.Rest[k].DistanceTo(sim.Rest[pin.Particle]));
                    Near[k] = Mathf.Max(Near[k], near);
                }
            }
        }

        internal void Expire(TissueSim sim)
        {
            if (!sim.IsSleeping || sim._pins.Count > 0 || Roots.Count == 0 || sim.Settled.Length != sim.Pos.Length) { return; }
            for (var k = 0; k < Near.Length; k++)
            {
                if (Near[k] > 0f && sim.Pos[k].DistanceSquaredTo(sim.Settled[k]) > LiftedOff * LiftedOff) { return; }
            }
            Active = TurnedOver = Bending = false;
            Roots.Clear();
            Array.Clear(Near);
            // Contact is intentionally retained: the cloth constraint must still cover the released skirt.
        }
    }

    /// <summary>Resists cell-sized creases during a large pull. Average displacement only over intact edges, so a
    /// fold forms a broad wave while its two cut lips remain free to move independently.</summary>
    private void RelaxFolds()
    {
        if (!_fold.Bending) { return; }
        if (_bend.Length != Pos.Length)
        {
            _bend = new Vector3[Pos.Length];
        }
        foreach (var k in _winParticles)
        {
            // Within the grip's footprint whether lifted or not: skin pushed along the body ahead of a grip would
            // otherwise crease cell by cell instead of bunching into one wave.
            if (_free[k] == 0f || (_fold.Near[k] <= 0f && Pos[k].DistanceSquaredTo(Settled[k]) <= LiftedOff * LiftedOff))
            {
                _bend[k] = Vector3.Zero;
                continue;
            }
            var sum = Vector3.Zero;
            var count = 0;
            foreach (var s in _springsOf[k])
            {
                ref readonly var spring = ref _springs[s];
                if (spring.Kind != SpringKind.Tissue || !spring.Active)
                {
                    continue;
                }
                var other = spring.A == k ? spring.B : spring.A;
                sum += Pos[other] - Rest[other];
                count++;
            }
            // The nearly stationary perimeter belongs to the body/drape contact constraint; bending must not drag
            // it across that boundary by a fraction of a cell.
            _bend[k] = count > 1 ? (sum / count - (Pos[k] - Rest[k])) * FoldBend * _free[k] : Vector3.Zero;
        }
        foreach (var k in _winParticles)
        {
            Pos[k] += _bend[k];
        }
    }

    /// <summary>Skin doesn't crease cell by cell: a grid point and the one two along its row or column, joined by
    /// whole springs, stay BendSpan of their rest distance apart. A spring turns any way for free, so skin pushed
    /// along by a grip, or crushed by a cut's lips drawing back into it, would otherwise fold back on itself a cell
    /// at a time, a row ducking under the one before it with every spring still at its length, instead of rising
    /// into one hump. Everywhere the sheet moves, until a flap is deliberately turned over.</summary>
    private void KeepBendsRound()
    {
        if (_fold.TurnedOver) { return; }
        foreach (var k in _winParticles)
        {
            if (_turned[k]) { continue; }
            var at = CellOf(k);
            if (at.X + 2 <= ResX && SpringWhole(SpringRight[k]) && SpringWhole(SpringRight[k + 1]) && !_turned[k + 2])
            {
                RoundBend(k, k + 2);
            }
            var below = k + ResX + 1;
            if (at.Y + 2 <= ResY && SpringWhole(SpringDown[k]) && SpringWhole(SpringDown[below]) && !_turned[below + ResX + 1])
            {
                RoundBend(k, below + ResX + 1);
            }
        }
    }

    /// <summary>Which particles a flap took with it as it was turned over (TurnedOver): they lie folded back, and
    /// stay exempt from the sheet's shape constraints (KeepBendsRound(), KeepUpright()) until they come back to
    /// where they rest, or the constraints would unfold the flap once the hands let go.</summary>
    private bool[] _turned = [];

    private void TrackTurned()
    {
        if (_turned.Length != Pos.Length)
        {
            _turned = new bool[Pos.Length];
        }
        foreach (var k in _winParticles)
        {
            var moved = Pos[k].DistanceSquaredTo(Settled.Length == Pos.Length ? Settled[k] : Rest[k]);
            if (_fold.TurnedOver && moved > LiftedOff * LiftedOff)
            {
                _turned[k] = true;
            }
            else if (moved < LiftedOff * LiftedOff)
            {
                _turned[k] = false;
            }
        }
    }

    private bool SpringWhole(int s) => s >= 0 && _springs[s].Active;

    private void RoundBend(int a, int b)
    {
        var minimum = Rest[a].DistanceTo(Rest[b]) * BendSpan;
        var d = Pos[b] - Pos[a];
        var length = d.Length();
        var share = _free[a] + _free[b];
        if (length >= minimum || length < 0.000001f || share <= 0f)
        {
            return;
        }
        var apart = d * ((minimum - length) / length / share);
        Pos[a] -= apart * _free[a];
        Pos[b] += apart * _free[b];
    }

    /// <summary>Skin pushed along, by a grip or by a cut's lips drawing back, rises into a mound; it doesn't curl
    /// over itself. Each whole spring along a row or column keeps UprightShare of its rest reach along the body, so
    /// the sheet stays a surface over the body with what lies under it fitting underneath, until a flap is
    /// deliberately turned over.</summary>
    private void KeepUpright()
    {
        if (_fold.TurnedOver) { return; }
        foreach (var k in _winParticles)
        {
            if (_turned[k]) { continue; }
            var at = CellOf(k);
            if (at.X < ResX && SpringWhole(SpringRight[k]) && !_turned[k + 1]) { Upright(k, k + 1); }
            if (at.Y < ResY && SpringWhole(SpringDown[k]) && !_turned[k + ResX + 1]) { Upright(k, k + ResX + 1); }
        }
    }

    private void Upright(int a, int b)
    {
        var restWay = new Vector2(Rest[b].X - Rest[a].X, Rest[b].Z - Rest[a].Z);
        var reach = restWay.Length();
        var way = restWay / reach;
        var along = new Vector2(Pos[b].X - Pos[a].X, Pos[b].Z - Pos[a].Z).Dot(way);
        var least = reach * UprightShare;
        var share = _free[a] + _free[b];
        if (along >= least || share <= 0f)
        {
            return;
        }
        var apart = new Vector3(way.X, 0f, way.Y) * ((least - along) / share);
        Pos[a] -= apart * _free[a];
        Pos[b] += apart * _free[b];
    }

    /// <summary>Track the local footprint of a grip and let only its nearby covered boundary slide.</summary>
    private void UpdateFoldState()
    {
        _fold.Update(this);
        if (!_fold.Active || _fold.TurnedOver)
        {
            return;
        }
        // Only the boundary near a grip can slide under the drape. Expanding every border coordinate with the
        // grip translates the entire site and drags its far flanks through the cloth.
        var half = Size * 0.5f;
        for (var k = 0; k < Pos.Length; k++)
        {
            if (!_fixed[k]) { continue; }
            var target = Rest[k];
            foreach (var pin in _pins.Values)
            {
                var at = pin.Target;
                var wx = 1f - Mathf.SmoothStep(DragReach, DragReach * 2f, Mathf.Abs(Rest[k].X - at.X));
                var wz = 1f - Mathf.SmoothStep(DragReach, DragReach * 2f, Mathf.Abs(Rest[k].Z - at.Z));
                if (Rest[k].Z >= half.Y - 0.00001f)
                {
                    target.Z = Mathf.Max(target.Z, Rest[k].Z + Mathf.Max(0f, at.Z + GripHold * 2f - half.Y) * wx);
                }
                if (Rest[k].Z <= -half.Y + 0.00001f)
                {
                    target.Z = Mathf.Min(target.Z, Rest[k].Z + Mathf.Min(0f, at.Z - GripHold * 2f + half.Y) * wx);
                }
                if (Rest[k].X >= half.X - 0.00001f)
                {
                    target.X = Mathf.Max(target.X, Rest[k].X + Mathf.Max(0f, at.X + GripHold * 2f - half.X) * wz);
                }
                if (Rest[k].X <= -half.X + 0.00001f)
                {
                    target.X = Mathf.Min(target.X, Rest[k].X + Mathf.Min(0f, at.X - GripHold * 2f + half.X) * wz);
                }
            }
            // Fixed means anchored at rest; an active grip may slide only its nearby perimeter under the drape.
            // Assign the contact target directly so the ordinary anchor solve cannot fight that boundary motion.
            Pos[k] = _pins.Count > 0 ? target : Pos[k].Lerp(target, 0.08f);
        }
    }

    private void UpdateFoldContact()
    {
        // A dormant footprint retains its final contact skirt for a flap resting on cloth.
        if (_fold.Roots.Count == 0) { return; }
        Array.Clear(_fold.Contact);
        foreach (var k in _winParticles)
        {
            if (!NearFold(k) || Pos[k].DistanceSquaredTo(Settled[k]) < 0.000001f) { continue; }
            var at = CellOf(k);
            // Contact covers whole faces. Include the stationary row joining a moving fold back to the body;
            // otherwise a triangle crosses the cloth between its raised and anchored corners.
            for (var j = Math.Max(0, at.Y - 1); j <= Math.Min(ResY, at.Y + 1); j++)
            {
                for (var i = Math.Max(0, at.X - 1); i <= Math.Min(ResX, at.X + 1); i++)
                {
                    _fold.Contact[Index(i, j)] = true;
                }
            }
        }
    }

    private void FindCompressedCells()
    {
        _foldCells.Clear();
        if (!_fold.Active || _fold.TurnedOver) { return; }
        if (_foldCellMarks.Length != ResX * ResY) { _foldCellMarks = new bool[ResX * ResY]; }
        Array.Clear(_foldCellMarks);
        var threshold = Size.X / ResX * (Size.Y / ResY) * CompressedArea;
        float Area(int a, int b, int c) => (Pos[c] - Pos[a]).Cross(Pos[b] - Pos[a]).Length();
        foreach (var k in _winParticles)
        {
            if (!NearFold(k)) { continue; }
            var at = CellOf(k);
            if (at.X >= ResX || at.Y >= ResY) { continue; }
            var c = k + ResX + 1;
            if (Area(k, k + 1, c) >= threshold && Area(k + 1, c + 1, c) >= threshold
                && Area(k, c + 1, c) >= threshold && Area(k, k + 1, c + 1) >= threshold) { continue; }
            // Include neighbours: correcting a compressed cell also moves their shared corners.
            for (var j = Math.Max(0, at.Y - 1); j <= Math.Min(ResY - 1, at.Y + 1); j++)
            {
                for (var i = Math.Max(0, at.X - 1); i <= Math.Min(ResX - 1, at.X + 1); i++)
                {
                    _foldCellMarks[j * ResX + i] = true;
                }
            }
        }
        for (var cell = 0; cell < _foldCellMarks.Length; cell++)
        {
            if (_foldCellMarks[cell]) { _foldCells.Add(Index(cell % ResX, cell / ResX)); }
        }
    }

    private bool PreserveLocalArea(bool reverse = false, bool compressedOnly = false)
    {
        if (!_fold.Active || _fold.TurnedOver) { return false; }
        var changed = false;
        var minimum = Size.X / ResX * (Size.Y / ResY) * MinimumFoldArea;
        var count = compressedOnly ? _foldCells.Count : _winParticles.Length;
        for (var n = 0; n < count; n++)
        {
            var index = reverse ? count - 1 - n : n;
            var k = compressedOnly ? _foldCells[index] : _winParticles[index];
            if (!NearFold(k)) { continue; }
            var cell = CellOf(k);
            if (cell.X >= ResX || cell.Y >= ResY) { continue; }
            var c = k + ResX + 1;
            changed |= KeepArea(k, k + 1, c, minimum);
            changed |= KeepArea(k + 1, c + 1, c, minimum);
            changed |= KeepArea(k, c + 1, c, minimum);
            changed |= KeepArea(k, k + 1, c + 1, minimum);
        }
        return changed;
    }

    private bool KeepArea(int a, int b, int c, float minimum)
    {
        if (Excised[a] || Excised[b] || Excised[c]) { return false; }
        ref var pa = ref Pos[a];
        ref var pb = ref Pos[b];
        ref var pc = ref Pos[c];
        var face = (pc - pa).Cross(pb - pa);
        var area = face.Length();
        if (area >= minimum * 0.99f) { return false; }
        // Work in the face's own plane: a vertical fold has the same area as a resting cell.
        var normal = area > 1e-9f ? face / area : _restNormal[a];
        var ga = (pc - pb).Cross(normal);
        var gb = normal.Cross(pc - pa);
        var gc = (pb - pa).Cross(normal);
        var wa = _free[a];
        var wb = _free[b];
        var wc = _free[c];
        var weight = ga.LengthSquared() * wa + gb.LengthSquared() * wb + gc.LengthSquared() * wc;
        if (weight < 1e-12f) { return false; }
        var correction = (minimum - area) / weight;
        pa += ga * correction * wa;
        pb += gb * correction * wb;
        pc += gc * correction * wc;
        return true;
    }

    /// <summary>Skin folded over other skin lies on it, not in it: where two rows that weren't neighbours at rest
    /// have passed one another (their order across the site is the reverse of what it was) and lie over one another,
    /// the upper one stays FoldClearance above the lower. Only up: the lower row stays on the body, and the sheet
    /// keeps its shape across. Rows merely pushed close together (a cut's lips drawn back into the skin beside them)
    /// are left alone.</summary>
    private void SeparateFoldNeighbors()
    {
        if (!_fold.Active) { return; }
        // Clear only buckets used by the previous step, not every location visited over the session.
        foreach (var bucket in _usedFoldBuckets) { bucket.Clear(); }
        _usedFoldBuckets.Clear();
        var cells = _foldNeighbors;
        var across = Mathf.Min(Size.X / ResX, Size.Y / ResY) * 0.7f;
        foreach (var k in _winParticles)
        {
            if (!NearFold(k)) { continue; }
            var key = new Vector3I(Mathf.FloorToInt(Pos[k].X / across), 0, Mathf.FloorToInt(Pos[k].Z / across));
            var at = CellOf(k);
            for (var z = -1; z <= 1; z++)
            {
                for (var x = -1; x <= 1; x++)
                {
                    if (!cells.TryGetValue(key + new Vector3I(x, 0, z), out var near)) { continue; }
                    foreach (var other in near)
                    {
                        var grid = CellOf(other);
                        if ((Math.Abs(at.X - grid.X) <= 1 && Math.Abs(at.Y - grid.Y) <= 1)
                            || Rest[k].DistanceSquaredTo(Rest[other]) <= FoldClearance * FoldClearance) { continue; }
                        var delta = Pos[k] - Pos[other];
                        var flat = new Vector2(delta.X, delta.Z);
                        var restDelta = Rest[k] - Rest[other];
                        if (flat.LengthSquared() >= across * across || flat.Dot(new Vector2(restDelta.X, restDelta.Z)) > 0f) { continue; }
                        var (upper, lower) = delta.Y >= 0f ? (k, other) : (other, k);
                        var gap = Mathf.Abs(delta.Y);
                        if (gap >= FoldClearance || _free[upper] == 0f) { continue; }
                        Pos[upper].Y += (FoldClearance - gap) * _free[upper];
                    }
                }
            }
            if (!cells.TryGetValue(key, out var bucket)) { cells[key] = bucket = []; }
            if (bucket.Count == 0) { _usedFoldBuckets.Add(bucket); }
            bucket.Add(k);
        }
    }

}
