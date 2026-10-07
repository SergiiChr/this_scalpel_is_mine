namespace Scalpel.World;

/// <summary>Something you walk up to and press interact on. Room builds these and hands them what to do.</summary>
public partial class Interactable : Area3D
{
    public const uint Layer = 64;

    public string Prompt { get; private set; } = "";
    private Action<Surgeon>? _action;
    /// <summary>True when this is on offer to that surgeon right now (the IV stand only for someone holding a bag).
    /// Unset: always.</summary>
    public Func<Surgeon, bool>? Offered { get; set; }

    public static Interactable Create(Node3D parent, string label, Vector3 size, Vector3 position, Action<Surgeon> action)
    {
        var area = new Interactable
        {
            Name = label.ToPascalCase().ValidateNodeName(),
            Prompt = label,
            _action = action,
            CollisionLayer = Layer,
            CollisionMask = 0,
            Monitoring = false,
        };
        area.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        parent.AddChild(area);
        area.Position = position;
        return area;
    }

    public bool OfferedTo(Surgeon surgeon) => Offered?.Invoke(surgeon) ?? true;

    public void Interact(Surgeon surgeon) => _action?.Invoke(surgeon);
}
