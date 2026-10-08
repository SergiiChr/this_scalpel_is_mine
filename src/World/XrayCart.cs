using GodotArray = Godot.Collections.Array;

namespace Scalpel.World;

/// <summary>Something an X-ray shows: what it is, where on the site (uv), how deep and turned how far (yaw).</summary>
public readonly record struct XrayShape(string Kind, Vector2 Uv, float Depth, float Yaw)
{
    public GodotArray ToVariant() => [Kind, Uv, Depth, Yaw];

    public static XrayShape FromVariant(Variant data)
    {
        var shape = data.AsGodotArray();
        return new XrayShape(shape[0].AsString(), shape[1].AsVector2(), shape[2].AsSingle(), shape[3].AsSingle());
    }
}

/// <summary>A developed X-ray: what it shows, of which site, and when it came out of the cart.</summary>
public sealed record XrayPrint(IReadOnlyList<XrayShape> Shapes, string Site, ulong PrintedAtMsec);

/// <summary>
/// Mobile X-ray unit. Push it next to the table, take an exposure, and an instant print slides out that develops over
/// a few seconds. It shows metal (bullets, knives, retained tools), bone and masses. The host owns its position and the
/// print; everyone can read the print.
/// </summary>
public partial class XrayCart : Node3D
{
    public const float ExposeTime = 2.5f;
    public const float Cooldown = 30f;
    /// <summary>Seconds a film takes to develop.</summary>
    public const float DevelopTime = 6f;
    private const float Reach = 1.5f;
    private const float PushOffset = 0.85f;
    private const float SyncInterval = 0.1f;
    /// <summary>The developed film lies on the cart's base, in front of the column.</summary>
    private static readonly Vector3 PrintSpot = new(0f, 0.34f, 0.1f);

    /// <summary>The last print, null until the first exposure.</summary>
    public XrayPrint? Print { get; private set; }
    /// <summary>The peer pushing the cart, 0 for nobody.</summary>
    public int Pusher { get; private set; }
    /// <summary>Half the room's floor minus the cart's size: pushing it never shoves it into a wall. Zero means no
    /// walls.</summary>
    public Vector2 Bounds { get; set; }
    private float _cooldown;
    private float _exposing;
    private float _syncAccumulated;
    private Node3D _film = null!;
    private Surgery _surgery = null!;

    public void Build(Surgery surgery)
    {
        _surgery = surgery;
        ModelSlot.Instantiate("props", "xray", this);
        _film = ModelSlot.Instantiate("props", "xray_print", this,
            new Dictionary<string, Material> { ["tint"] = Materials.Glow(new Color(0.05f, 0.06f, 0.07f)) });
        _film.Position = PrintSpot;
        _film.Visible = false;
        var body = new AnimatableBody3D();
        body.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(0.6f, 1.7f, 0.7f) },
            Position = new Vector3(0f, 0.85f, 0f),
        });
        AddChild(body);
        Interactable.Create(this, "Push / let go of the X-ray cart", new Vector3(0.5f, 0.2f, 0.2f),
            new Vector3(0f, 0.93f, 0.38f), _ => RpcId(Net.HostId, MethodName.RequestPush));
        Interactable.Create(this, "Take an X-ray", new Vector3(0.35f, 0.25f, 0.1f), new Vector3(0f, 0.6f, 0.38f),
            _ => RpcId(Net.HostId, MethodName.RequestExpose));
        Interactable.Create(this, "Look at the X-ray film", new Vector3(0.36f, 0.1f, 0.44f), PrintSpot, _ => OpenPrint());
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!Multiplayer.IsServer())
        {
            return;
        }
        var dt = (float)delta;
        _cooldown = Mathf.Max(_cooldown - dt, 0f);
        if (_exposing > 0f)
        {
            _exposing -= dt;
            if (_exposing <= 0f)
            {
                Develop();
            }
        }
        if (!_surgery.Surgeons.TryGetValue(Pusher, out var surgeon))
        {
            return;
        }
        var target = surgeon.GlobalPosition - (surgeon.GlobalBasis.Z * PushOffset);
        if (Bounds != Vector2.Zero)
        {
            target = new Vector3(Mathf.Clamp(target.X, -Bounds.X, Bounds.X), 0f, Mathf.Clamp(target.Z, -Bounds.Y, Bounds.Y));
        }
        var follow = Mathf.Min(dt * 8f, 1f);
        GlobalPosition = GlobalPosition.Lerp(target with { Y = 0f }, follow);
        Rotation = Rotation with { Y = Mathf.LerpAngle(Rotation.Y, surgeon.Rotation.Y + Mathf.Pi, follow) };
        _syncAccumulated += dt;
        if (_syncAccumulated >= SyncInterval)
        {
            _syncAccumulated = 0f;
            Rpc(MethodName.SyncPosition, GlobalPosition, Rotation.Y);
        }
    }

    private void OpenPrint()
    {
        if (Print is null)
        {
            _surgery.Hud.Toast("No print yet. Take an X-ray first.");
        }
        else
        {
            _surgery.Hud.OpenXray(this);
        }
    }

    /// <summary>0..1, how far the print has developed.</summary>
    public float Developed() => Print is null
        ? 0f
        : Mathf.Clamp((Time.GetTicksMsec() - Print.PrintedAtMsec) / (DevelopTime * 1000f), 0f, 1f);

    private void Develop()
    {
        var patient = _surgery.Patient;
        var body = patient.Body;
        var shapes = new GodotArray();
        foreach (var target in patient.Targets.Where(target => !target.Extracted || target.IsBone))
        {
            shapes.Add(new XrayShape(target.Kind, target.Uv, target.Depth, target.Rotation.Y).ToVariant());
        }
        foreach (var tool in _surgery.Tools.Tools.Values.Where(tool => tool.State == ToolState.Inside))
        {
            shapes.Add(new XrayShape("tool", body.WorldToUv(tool.GlobalPosition), 0.05f, tool.GlobalRotation.Y).ToVariant());
        }
        foreach (var organ in body.Organs)
        {
            shapes.Add(new XrayShape("organ", body.LocalToUv(organ.Position), -organ.Position.Y, 0f).ToVariant());
        }
        _surgery.Scoring.Add("xray_used", true);
        Rpc(MethodName.PrintReady, shapes, patient.Scenario.Site);
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true)]
    private void RequestPush()
    {
        var peer = Net.Instance.Sender();
        Pusher = Pusher == peer ? 0 : peer;
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true)]
    private void RequestExpose()
    {
        var peer = Net.Instance.Sender();
        if (_exposing > 0f || _cooldown > 0f)
        {
            _surgery.Tell(peer, $"The X-ray is warming up ({Mathf.CeilToInt(_cooldown + _exposing)} s).");
            return;
        }
        var patient = _surgery.Patient.GlobalPosition;
        if (new Vector2(GlobalPosition.X, GlobalPosition.Z).DistanceTo(new Vector2(patient.X, patient.Z)) > Reach)
        {
            _surgery.Tell(peer, "Push the cart up to the table first.");
            return;
        }
        _exposing = ExposeTime;
        _cooldown = Cooldown;
        _surgery.Sound("xray_expose", GlobalPosition);
        _surgery.Announce("X-ray! Everyone step back.");
    }

    [Rpc(CallLocal = true)]
    private void PrintReady(GodotArray shapes, string site)
    {
        Print = new XrayPrint([.. shapes.Select(XrayShape.FromVariant)], site, Time.GetTicksMsec());
        _film.Visible = true;
        Sfx.Play("print_whir", GlobalPosition);
        _surgery.Hud.Toast("The print slides out of the X-ray. Give it a few seconds.");
    }

    [Rpc(TransferMode = MultiplayerPeer.TransferModeEnum.UnreliableOrdered)]
    private void SyncPosition(Vector3 position, float yaw)
    {
        GlobalPosition = position;
        Rotation = Rotation with { Y = yaw };
    }
}
