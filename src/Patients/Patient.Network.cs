using GodotDictionary = Godot.Collections.Dictionary;

namespace Scalpel.Patients;

/// <summary>What the host sends every peer: paint ops, tissue changes and the patient's state.</summary>
public partial class Patient
{
    private ulong _paintSeed;

    /// <summary>Host: paint on every peer.</summary>
    public void Paint(WoundMap.Layer layer, int channel, Vector2 a, Vector2 b, float radius, float value, WoundMap.Mode mode,
        float jitter = 0f)
    {
        _paintSeed++;
        Rpc(MethodName.PaintOnPeer, (int)layer, channel, a, b, radius, value, (int)mode, jitter, _paintSeed);
    }

    /// <summary>Host: paints several channels of one disk on every peer.</summary>
    public void PaintOps(WoundMap.Layer layer, Vector2 uv, float radius, IEnumerable<PaintOp> ops)
    {
        var flat = ops.SelectMany(op => new float[] { op.Channel, op.Value, (float)op.Mode }).ToArray();
        Rpc(MethodName.PaintOpsOnPeer, (int)layer, uv, radius, flat);
    }

    /// <summary>Host: the patient's state, 5 times a second, to every client.</summary>
    private void BroadcastState()
    {
        var grips = new Godot.Collections.Array();
        foreach (var grip in Body.Tissue.Grips())
        {
            grips.Add(new Godot.Collections.Array { grip.Key, grip.Particle, grip.Target });
        }
        var targets = new Godot.Collections.Array(Targets.Select(target => (Variant)target.State()));
        Rpc(MethodName.Sync, Vitals.ToVariant(), targets, Body.OrganStates(), grips, CavityBloodMl,
            BleedSource.ToVariant(Body.Blood.Sources));
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PaintOnPeer(int layer, int channel, Vector2 a, Vector2 b, float radius, float value, int mode, float jitter, ulong seed)
    {
        if (a == b)
        {
            Body.WoundMap.Disk((WoundMap.Layer)layer, channel, a, radius, value, (WoundMap.Mode)mode);
        }
        else
        {
            Body.WoundMap.Stroke((WoundMap.Layer)layer, channel, a, b, radius, value, (WoundMap.Mode)mode, jitter, seed);
        }
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void PaintOpsOnPeer(int layer, Vector2 uv, float radius, float[] flat)
    {
        var ops = new List<PaintOp>();
        for (var i = 0; i + 2 < flat.Length; i += 3)
        {
            ops.Add(new PaintOp((int)flat[i], flat[i + 1], (WoundMap.Mode)(int)flat[i + 2]));
        }
        Body.WoundMap.DiskOps((WoundMap.Layer)layer, uv, radius, ops);
    }

    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void Sync(GodotDictionary vitals, Godot.Collections.Array targets, Vector3[] organs,
        Godot.Collections.Array grips, float cavityMl, Godot.Collections.Array bleeds)
    {
        Body.Blood.Sources = BleedSource.FromVariant(bleeds);
        Vitals.Apply(vitals);
        for (var i = 0; i < Math.Min(targets.Count, Targets.Count); i++)
        {
            Targets[i].ApplyState(targets[i].AsGodotArray());
        }
        Body.ApplyOrganStates(organs);
        Body.SetCavityBlood(cavityMl / CavityFullMl);
        Body.Tissue.SetGrips([.. grips.Select(entry => entry.AsGodotArray())
            .Select(grip => new TissueGrip(grip[0].AsInt32(), grip[1].AsInt32(), grip[2].AsVector3()))]);
    }

    /// <summary>Host: a spring in the tissue sim was stretched too far and snapped.</summary>
    private void OnSnap(SnappedSpring snapped)
    {
        var mid = (snapped.A + snapped.B) * 0.5f;
        Rpc(MethodName.TissueSnap, snapped.Spring);
        if (Surgery.Current is not { Running: true })
        {
            return;
        }
        var wound = NearestWound(mid, 0.04f, false);
        if (snapped.Kind == SpringKind.Stitch)
        {
            if (wound is not null)
            {
                wound.Bins[wound.BinAt(mid)] = 0.3f;
                Paint(WoundMap.Layer.Wounds, WoundMap.Stitch, mid, mid, 0.008f, 0f, WoundMap.Mode.Min);
            }
            Session.Scoring.Add("suture_tear_through");
            TearNotice("A stitch tore through the skin.");
            Hurt(0.3f, mid);
            return;
        }
        // Neighbouring springs tend to go together; grow the fresh tear instead of making one wound per spring.
        if (wound is { Kind: WoundKind.Tear } && wound.Points[^1].DistanceTo(mid) < 0.06f)
        {
            Paint(WoundMap.Layer.Wounds, WoundMap.Cut, wound.Points[^1], mid, 0.005f, 0.6f, WoundMap.Mode.Max, 0.006f);
            wound.Extend(mid);
            return;
        }
        AddTear(snapped.A, snapped.B);
        TearNotice("The skin tore!");
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TissueCut(Vector2 a, Vector2 b, int depth) => Body.Tissue.Cut(a, b, (TissueDepth)depth);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TissueExcise(int k) => Body.Tissue.Excise(k);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TissueStitch(Vector2 uv, float tension, float strength) => Body.Tissue.Stitch(uv, tension, strength);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TissueStaple(Vector2 a, Vector2 b, int layer) => Body.AddStaple(a, b, (TissueDepth)layer);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TissueStitchPath(Vector2[] points, float radius, float tension, float strength) =>
        Body.Tissue.StitchPath(points, radius, tension, strength);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SutureAnchor(int id, Vector2 uv, int layer, float tension, float strength, float slack) =>
        Body.Tissue.ThreadAnchor(id, uv, (TissueDepth)layer, tension, strength, slack);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SutureTension(int id, float tension) => Body.Tissue.ThreadTension(id, tension);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SutureFinish(int id) => Body.Tissue.FinishThread(id);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SutureSnap(int id) => Body.Tissue.SnapThread(id);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TissueMuscle(Vector2 uv, float radius) => Body.Tissue.MuscleStitch(uv, radius);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TissueCloseLayer(Vector2[] points, float radius, int depth) =>
        Body.Tissue.CloseLayer(points, radius, (TissueDepth)depth);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TissuePucker(Vector2[] points, float radius, float amount) => Body.Tissue.SuturePucker(points, radius, amount);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TissueBurst(Vector2 uv, float radius) => Body.Tissue.Burst(uv, radius);

    /// <summary>The host's sim already snapped the spring; clients mirror it.</summary>
    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void TissueSnap(int spring) => Body.Tissue.SnapSpring(spring);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void IvPlaced(Vector3 point)
    {
        IvSet = true;
        ConnectIv(point);
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void IvRemoved()
    {
        IvSet = false;
        IvInVein = false;
        Surgery.Current?.Room.IvLine?.Detach();
    }

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetOrganDamage(int index, float amount) => Body.SetOrganDamage(index, amount);

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void Vocal(string sound) => Sfx.Play(sound, Body.GlobalPosition + new Vector3(0.7f, 0.2f, 0), "Voice");

    [Rpc(CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
    private void SetOrientation(int value) => Body.SetPose((PatientPose)value);
}
