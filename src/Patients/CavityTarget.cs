namespace Scalpel.Patients;

/// <summary>
/// Something under the skin that the scenario cares about: a bullet, an appendix, a bone to saw, fluid to drain.
/// Host owns the state and syncs it through Patient. Field meaning matches data/scenarios [patient] targets.
/// </summary>
public partial class CavityTarget : Node3D
{
    /// <summary>A small solid the resting tool tip lands on, so tools inside an open cavity come down onto the target.
    /// </summary>
    public const uint TouchLayer = 64;
    private const float TouchRadius = 0.012f;

    private StaticBody3D _touch = null!;

    public int Index { get; private set; }
    public string Kind { get; private set; } = "";
    public string RemoveWith { get; private set; } = "";
    public Vector2 RestUv { get; private set; }
    public Vector2 Uv { get; set; }
    public float Depth { get; set; }
    /// <summary>How firmly it's attached. Cutting, sawing or a slow steady pull brings it to 0.</summary>
    public float Anchor { get; set; }
    /// <summary>Suction targets: how much is left to drain.</summary>
    public float Amount { get; set; }
    public float Refill { get; private set; }
    /// <summary>Extra bleeding when it comes out (knives plug their own wound).</summary>
    public float Surge { get; private set; }
    public bool Covered { get; private set; }
    public bool Extracted { get; set; }
    /// <summary>Tool uid currently holding it, 0 if none.</summary>
    public int GrippedBy { get; set; }
    /// <summary>A cut inside the body reached it: it bleeds from where it's attached.</summary>
    public bool Opened { get; set; }

    public void Setup(int index, TargetSpec spec, bool mirrored)
    {
        Index = index;
        Kind = spec.Kind;
        RemoveWith = spec.RemoveWith;
        // uv.y runs across the body (the patient's left), so a mirrored patient flips it; uv.x runs head to feet.
        RestUv = new Vector2(spec.Uv.X, mirrored ? 1f - spec.Uv.Y : spec.Uv.Y);
        Uv = RestUv + spec.Offset;
        Depth = spec.Depth;
        Anchor = spec.Anchor;
        Amount = spec.Amount;
        Refill = spec.Refill;
        Surge = spec.Surge;
        Covered = spec.Covered;
        Name = $"{Kind.Capitalize()}_{index}";
        // Degrees around the vertical, e.g. to lay a rib across the chest.
        RotationDegrees = new Vector3(0, spec.YawDegrees, 0);
        ModelSlot.Instantiate("targets", Kind, this);
        _touch = new StaticBody3D { Name = "Touch", CollisionLayer = TouchLayer, CollisionMask = 0 };
        _touch.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = TouchRadius } });
        AddChild(_touch);
    }

    public bool IsSuctionTarget => RemoveWith == "suction";

    /// <summary>Taken out with a saw or a mallet: a bone.</summary>
    public bool IsBone => RemoveWith is "saw" or "smash";

    /// <summary>Broken bone ends to line back up (the align objective), not something to take out.</summary>
    public bool IsFragment => Kind is "fragment" or "rib";

    public bool IsAligned(float tolerance = 0.01f) => Uv.DistanceTo(RestUv) < tolerance;

    public Godot.Collections.Array State() => [Position, Anchor, Amount, Extracted, Visible];

    public void ApplyState(Godot.Collections.Array state)
    {
        Position = state[0].AsVector3();
        Anchor = state[1].AsSingle();
        Amount = state[2].AsSingle();
        Extracted = state[3].AsBool();
        Visible = state[4].AsBool();
        UpdateLook();
    }

    /// <summary>Out of the body and let go of: out of sight.</summary>
    public void SetAside()
    {
        Visible = false;
        UpdateLook();
    }

    /// <summary>Size and touch collider for the current state.</summary>
    public void UpdateLook()
    {
        // Only something still in place is in the way: taken out, sawn through or drained, tools reach past it.
        _touch.CollisionLayer = Visible && !Extracted ? TouchLayer : 0;
        if (IsSuctionTarget)
        {
            Scale = Vector3.One * Mathf.Clamp(Amount / 6f, 0.05f, 1.5f);
        }
    }
}
