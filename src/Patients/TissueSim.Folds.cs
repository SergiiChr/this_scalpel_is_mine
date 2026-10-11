namespace Scalpel.Patients;

public sealed partial class TissueSim
{
    /// <summary>Extra tethering outside a grip footprint prevents translating the distant bed.</summary>
    private const float FoldAnchor = 0.35f;
    /// <summary>Gentle intact-edge displacement averaging rounds cell-sized creases into a wave.</summary>
    private const float FoldBend = 0.12f;
    /// <summary>Faces below this fraction of rest area need the final compression passes.</summary>
    private const float CompressedArea = 0.3f;
    /// <summary>Retain this fraction of rest area so a fold has room for the tissue underneath it.</summary>
    private const float MinimumFoldArea = 0.15f;
    /// <summary>Nonadjacent fold rows keep 3 mm clearance without changing intact spring distances.</summary>
    private const float FoldClearance = 0.003f;

    /// <summary>One fold lifecycle; scratch geometry stays in the solver, and contact survives footprint expiry.</summary>
    private sealed class FoldState
    {
        internal bool Active;
        internal bool TurnedOver;
        internal bool Bending;
        internal readonly HashSet<int> Roots = [];
        internal bool[] Near = [];
        internal bool[] Contact = [];

        internal void Reset(int count)
        {
            Active = TurnedOver = Bending = false;
            Roots.Clear();
            Near = new bool[count];
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
                Bending |= pull > DragReach * DragReach;
                TurnedOver |= pull > TurnoverReach * TurnoverReach;
                if (pull <= GripHold * GripHold) { continue; }
                Active = true;
                if (!Roots.Add(pin.Particle)) { continue; }
                for (var k = 0; k < sim.Rest.Length; k++)
                {
                    Near[k] |= sim.Rest[k].DistanceSquaredTo(sim.Rest[pin.Particle]) <= DragReach * DragReach * 4f;
                }
            }
        }

        internal void Expire(TissueSim sim)
        {
            if (!sim.IsSleeping || sim._pins.Count > 0 || Roots.Count == 0 || sim.Settled.Length != sim.Pos.Length) { return; }
            for (var k = 0; k < Near.Length; k++)
            {
                if (Near[k] && sim.Pos[k].DistanceSquaredTo(sim.Settled[k]) > LiftedOff * LiftedOff) { return; }
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
            if (_free[k] == 0f || Pos[k].DistanceSquaredTo(Settled[k]) <= LiftedOff * LiftedOff)
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

    /// <summary>Nearby folds repel in three dimensions. Intact immediate neighbours retain their spring
    /// distances; distant rows can approach each other without passing through the thin skin.</summary>
    private void SeparateFoldNeighbors()
    {
        if (!_fold.Active) { return; }
        // Clear only buckets used by the previous step, not every location visited over the session.
        foreach (var bucket in _usedFoldBuckets) { bucket.Clear(); }
        _usedFoldBuckets.Clear();
        var cells = _foldNeighbors;
        foreach (var k in _winParticles)
        {
            if (!NearFold(k)) { continue; }
            var key = (Vector3I)(Pos[k] / FoldClearance).Floor();
            var at = CellOf(k);
            for (var z = -1; z <= 1; z++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    for (var x = -1; x <= 1; x++)
                    {
                        if (!cells.TryGetValue(key + new Vector3I(x, y, z), out var near)) { continue; }
                        foreach (var other in near)
                        {
                            var grid = CellOf(other);
                            if (Math.Abs(at.X - grid.X) <= 1 && Math.Abs(at.Y - grid.Y) <= 1) { continue; }
                            var delta = Pos[k] - Pos[other];
                            var length = delta.Length();
                            var weight = _free[k] + _free[other];
                            if (length >= FoldClearance || length < 1e-8f || weight == 0f) { continue; }
                            var push = delta * ((FoldClearance - length) / (length * weight));
                            Pos[k] += push * _free[k];
                            Pos[other] -= push * _free[other];
                        }
                    }
                }
            }
            if (!cells.TryGetValue(key, out var bucket)) { cells[key] = bucket = []; }
            if (bucket.Count == 0) { _usedFoldBuckets.Add(bucket); }
            bucket.Add(k);
        }
    }

}
