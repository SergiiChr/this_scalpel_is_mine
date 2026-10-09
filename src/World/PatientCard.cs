namespace Scalpel.World;

/// <summary>
/// The patient card on its hook by the table, and the copies in surgeons' hands.
/// Each surgeon starts the surgery reading their own copy.
/// Put back, a copy travels to the hook and merges with whatever card already hangs there, so the opening shows where
/// the card lives.
/// Taken, the card leaves the hook for everyone until someone puts one back.
/// Nothing here is physical: a copy only follows its reader's view.
/// </summary>
public partial class PatientCard : Node3D
{
    /// <summary>Seconds a card takes between the hook and a reader's face.</summary>
    internal const float TravelSeconds = 0.6f;
    /// <summary>How far in front of the eyes (meters) a card is read: the paper about fills the view.</summary>
    private const float ReadDistance = 0.3f;
    /// <summary>How high (meters) a travelling card arcs over a straight line: it clears the patient's head.</summary>
    private const float ArcHeight = 0.45f;

    private sealed class Copy(Node3D model, bool reading)
    {
        public Node3D Model { get; } = model;
        /// <summary>0 on the hook, 1 at the reader's face.</summary>
        public float Travel { get; set; } = reading ? 1f : 0f;
        public bool Reading { get; set; } = reading;
    }

    private Node3D _hanging = null!;
    /// <summary>The middle of the paper, local to the card model: what's held in front of the eyes.</summary>
    private Vector3 _paperMiddle;
    private readonly Dictionary<int, Copy> _copies = [];

    /// <summary>A card hangs on the hook, ready to be taken. Starts false: every surgeon holds their own copy.</summary>
    public bool OnHook { get; private set; }

    /// <summary>Takes over <paramref name="hanging"/>, the card model placed on its hook.</summary>
    public void Setup(Node3D hanging)
    {
        _hanging = hanging;
        var paper = (MeshInstance3D)hanging.FindChild("Paper", true, false);
        _paperMiddle = hanging.GlobalTransform.AffineInverse() * paper.GlobalTransform * paper.GetAabb().GetCenter();
        _hanging.Visible = false;
    }

    /// <summary>The middle of the paper of the card on the hook.</summary>
    internal Vector3 HookPaper => _hanging.GlobalTransform * _paperMiddle;

    /// <summary>The middle of the paper of <paramref name="peer"/>'s copy, while it's out of the hook.</summary>
    internal Vector3? PaperOf(int peer) =>
        _copies.TryGetValue(peer, out var copy) && copy.Model.Visible ? copy.Model.GlobalTransform * _paperMiddle : null;

    /// <summary>How far <paramref name="peer"/>'s copy is along its way: 0 on the hook, 1 at the reader's face.</summary>
    internal float TravelOf(int peer) => _copies.TryGetValue(peer, out var copy) ? copy.Travel : 0f;

    public override void _Process(double delta)
    {
        if (Surgery.Current is not { } surgery)
        {
            return;
        }
        var step = (float)delta / TravelSeconds;
        foreach (var (peer, surgeon) in surgery.Surgeons)
        {
            if (!_copies.TryGetValue(peer, out var copy))
            {
                copy = new Copy(NewModel(), surgeon.ReadingCard);
                _copies[peer] = copy;
            }
            if (surgeon.ReadingCard && !copy.Reading)
            {
                OnHook = false;
            }
            copy.Reading = surgeon.ReadingCard;
            var was = copy.Travel;
            copy.Travel = Mathf.MoveToward(copy.Travel, copy.Reading ? 1f : 0f, step);
            if (was > 0f && copy.Travel == 0f)
            {
                OnHook = true;
            }
            copy.Model.Visible = copy.Travel > 0f;
            if (copy.Model.Visible)
            {
                var held = surgeon.Camera.GlobalTransform.TranslatedLocal(new Vector3(0f, 0f, -ReadDistance))
                    .TranslatedLocal(-_paperMiddle);
                var along = Mathf.SmoothStep(0f, 1f, copy.Travel);
                var pose = _hanging.GlobalTransform.InterpolateWith(held, along);
                pose.Origin += Vector3.Up * (ArcHeight * Mathf.Sin(Mathf.Pi * along));
                copy.Model.GlobalTransform = pose;
            }
        }
        if (_copies.Count > surgery.Surgeons.Count)
        {
            ForgetLeavers(surgery);
        }
        _hanging.Visible = OnHook;
    }

    private Node3D NewModel()
    {
        var model = (Node3D)_hanging.Duplicate();
        model.TopLevel = true;
        model.Visible = true;
        AddChild(model);
        return model;
    }

    /// <summary>A surgeon who left took their copy along: the card hangs back on the hook so it isn't lost for the rest.
    /// </summary>
    private void ForgetLeavers(Surgery surgery)
    {
        foreach (var peer in _copies.Keys.Where(peer => !surgery.Surgeons.ContainsKey(peer)).ToList())
        {
            _copies[peer].Model.QueueFree();
            _copies.Remove(peer);
            OnHook = true;
        }
    }
}
