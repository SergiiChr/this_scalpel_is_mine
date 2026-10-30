namespace Scalpel.Patients;

public sealed partial class TissueSim {
    /// <summary>A grip's footprint (see FoldState.Near) fades out from FoldNear to FoldFar (meters, at rest).</summary>
    private const float FoldNear = DragReach * 1.5f;
    private const float FoldFar = DragReach * 2.5f;

    /// <summary>One fold lifecycle: the grips pulling the skin far enough to fold it, and the skin around them.</summary>
    private sealed class FoldState {
        internal bool Active;
        internal bool TurnedOver;
        internal bool Bending;
        internal readonly HashSet<int> Roots = [];
        /// <summary>How much each particle belongs to a grip's footprint: 1 at the grip, 0 past FoldFar.</summary>
        internal float[] Near = [];

        internal void Reset(int count) {
            Active = TurnedOver = Bending = false;
            Roots.Clear();
            Near = new float[count];
        }

        internal void Update(TissueSim sim) {
            Bending = false;
            if (sim._pins.Count == 0) { return; }
            TurnedOver = false;
            foreach (var pin in sim._pins.Values) {
                var pull = pin.Target.DistanceSquaredTo(sim._anchorTarget[pin.Particle]);
                TurnedOver |= pull > TurnoverReach * TurnoverReach;
                if (pull <= GripHold * GripHold) { continue; }
                Active = Bending = true;
                if (!Roots.Add(pin.Particle)) { continue; }
                for (var k = 0; k < sim.Rest.Length; k++) {
                    var near = 1f - Mathf.SmoothStep(FoldNear, FoldFar, sim.Rest[k].DistanceTo(sim.Rest[pin.Particle]));
                    Near[k] = Mathf.Max(Near[k], near);
                }
            }
        }

        internal void Expire(TissueSim sim) {
            if (!sim.IsSleeping || sim._pins.Count > 0 || Roots.Count == 0 || sim.Settled.Length != sim.Pos.Length) { return; }
            for (var k = 0; k < Near.Length; k++) {
                if (Near[k] > 0f && sim.Pos[k].DistanceSquaredTo(sim.Settled[k]) > LiftedOff * LiftedOff) { return; }
            }
            Active = TurnedOver = Bending = false;
            Roots.Clear();
            Array.Clear(Near);
        }
    }

    /// <summary>Which particles a flap took with it as it was turned over (TurnedOver): they lie folded back until
    /// they come back to where they rest. Their cut edges aren't kept apart (KeepCutEdgesApart()), and they're drawn
    /// turned over.</summary>
    private bool[] _turned = [];

    private void TrackTurned() {
        Buffers.Sized(ref _turned, Pos.Length);
        foreach (var k in WindowParticles) {
            var moved = Pos[k].DistanceSquaredTo(Settled.Length == Pos.Length ? Settled[k] : Rest[k]);
            if (_fold.TurnedOver && moved > LiftedOff * LiftedOff) {
                _turned[k] = true;
            } else if (moved < LiftedOff * LiftedOff) {
                _turned[k] = false;
            }
        }
    }

    /// <summary>Track the local footprint of a grip and let only its nearby covered boundary slide.</summary>
    private void UpdateFoldState() {
        _fold.Update(this);
        if (!_fold.Active || _fold.TurnedOver) {
            return;
        }
        // Only the boundary near a grip can slide under the drape. Expanding every border coordinate with the
        // grip translates the entire site and drags its far flanks through the cloth.
        var half = Size * 0.5f;
        for (var k = 0; k < Pos.Length; k++) {
            if (!_fixed[k]) { continue; }
            var target = Rest[k];
            foreach (var pin in _pins.Values) {
                var at = pin.Target;
                var wx = 1f - Mathf.SmoothStep(DragReach, DragReach * 2f, Mathf.Abs(Rest[k].X - at.X));
                var wz = 1f - Mathf.SmoothStep(DragReach, DragReach * 2f, Mathf.Abs(Rest[k].Z - at.Z));
                if (Rest[k].Z >= half.Y - 0.00001f) {
                    target.Z = Mathf.Max(target.Z, Rest[k].Z + Mathf.Max(0f, at.Z + GripHold * 2f - half.Y) * wx);
                }
                if (Rest[k].Z <= -half.Y + 0.00001f) {
                    target.Z = Mathf.Min(target.Z, Rest[k].Z + Mathf.Min(0f, at.Z - GripHold * 2f + half.Y) * wx);
                }
                if (Rest[k].X >= half.X - 0.00001f) {
                    target.X = Mathf.Max(target.X, Rest[k].X + Mathf.Max(0f, at.X + GripHold * 2f - half.X) * wz);
                }
                if (Rest[k].X <= -half.X + 0.00001f) {
                    target.X = Mathf.Min(target.X, Rest[k].X + Mathf.Min(0f, at.X - GripHold * 2f + half.X) * wz);
                }
            }
            // Fixed means anchored at rest; an active grip may slide only its nearby perimeter under the drape.
            // Assign the contact target directly so the ordinary anchor solve cannot fight that boundary motion.
            Pos[k] = _pins.Count > 0 ? target : Pos[k].Lerp(target, 0.08f);
        }
    }
}
