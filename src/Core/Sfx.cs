namespace Scalpel.Core;

/// <summary>
/// Plays sounds by id from data/audio.cfg. Missing files are skipped, so audio can be filled in gradually.
/// An autoload node (players need a parent and contact loops fade in <see cref="_Process"/>) with a static API.
/// </summary>
public partial class Sfx : Node
{
    /// <summary>Most contact loops playing at once; a new one replaces the quietest of lower priority.</summary>
    internal const int ContactLimit = 3;
    /// <summary>A contact loop not refreshed for this long fades out.</summary>
    private const ulong ContactTimeoutMsec = 320;
    private const float Silent = -60f;

    private static readonly Dictionary<string, int> ContactPriority = new()
    {
        ["contact_cut"] = 3, ["contact_suction"] = 2, ["contact_swab"] = 1,
    };

    private static Sfx? _instance;
    private static readonly Dictionary<string, AudioStream?> Cache = [];

    /// <summary>A looping contact sound of one tool.</summary>
    private sealed class ContactSound(string id, AudioStreamPlayer3D player)
    {
        public string Id { get; } = id;
        public AudioStreamPlayer3D Player { get; } = player;
        public ulong LastMsec;
        public float Level = Silent;

        public float Rank => ContactPriority[Id] * 100f + Level;
    }

    /// <summary>Tool uid -> its contact loop.</summary>
    private readonly Dictionary<long, ContactSound> _contacts = [];

    /// <summary>How many contact loops play now.</summary>
    internal static int ContactCount => _instance?._contacts.Count ?? 0;

    /// <summary>Set by the local surgeon's Hard of hearing quirk.</summary>
    public static bool Deaf { get; set; }

    public override void _EnterTree() => _instance = this;

    public override void _Process(double delta)
    {
        var now = Time.GetTicksMsec();
        foreach (var (uid, contact) in _contacts.ToList())
        {
            var fresh = !Deaf && now - contact.LastMsec < ContactTimeoutMsec;
            var target = fresh ? contact.Level : Silent;
            contact.Player.VolumeDb = Mathf.MoveToward(contact.Player.VolumeDb, target, (float)delta * (fresh ? 90f : 110f));
            if (!fresh && contact.Player.VolumeDb <= -55f)
            {
                contact.Player.QueueFree();
                _contacts.Remove(uid);
            }
        }
    }

    /// <summary>
    /// A tool rubbing on something (cutting, suction, swabbing) at a bounded rate from the host. Missing updates fade
    /// out on their own, including when a tool is released, a peer disconnects or the action changes.
    /// </summary>
    public static void Contact(long uid, string id, Vector3 at, float strength)
    {
        if (Deaf || _instance is null || !ContactPriority.ContainsKey(id) || strength <= 0f)
        {
            return;
        }
        if (Stream(id) is not AudioStreamWav stream)
        {
            return;
        }
        var contacts = _instance._contacts;
        if (contacts.TryGetValue(uid, out var current) && current.Id != id)
        {
            current.Player.QueueFree();
            contacts.Remove(uid);
        }
        if (!contacts.TryGetValue(uid, out var contact))
        {
            if (contacts.Count >= ContactLimit)
            {
                var (weakest, weakestContact) = contacts.MinBy(pair => pair.Value.Rank);
                if (weakestContact.Rank >= ContactPriority[id] * 100f)
                {
                    return;
                }
                weakestContact.Player.QueueFree();
                contacts.Remove(weakest);
            }
            var player = new AudioStreamPlayer3D { Stream = Looped(stream), Bus = "SFX", VolumeDb = Silent, Position = at };
            _instance.AddChild(player);
            player.Play();
            contact = new ContactSound(id, player);
            contacts[uid] = contact;
        }
        contact.Player.Position = at;
        contact.LastMsec = Time.GetTicksMsec();
        contact.Level = -29f + 17f * Mathf.Clamp(strength, 0f, 1f);
    }

    /// <summary>The contact loop playing for tool <paramref name="uid"/>, null when none is.</summary>
    internal static AudioStreamPlayer3D? ContactPlayer(long uid) => _instance?._contacts.GetValueOrDefault(uid)?.Player;

    /// <summary>Makes every contact loop stale, as if its tool stopped touching anything longer ago than a loop is kept
    /// without a refresh.</summary>
    internal static void ExpireContacts()
    {
        foreach (var contact in _instance?._contacts.Values ?? Enumerable.Empty<ContactSound>())
        {
            contact.LastMsec = Time.GetTicksMsec() - ContactTimeoutMsec - 1;
        }
    }

    /// <summary>Plays a sound once, in 3D at <paramref name="at"/> or flat when it's null.</summary>
    public static void Play(string id, Vector3? at = null, string bus = "SFX", float volumeDb = 0f)
    {
        if (_instance is null || Deaf || Stream(id) is not { } stream)
        {
            return;
        }
        if (at is { } position)
        {
            var spatial = new AudioStreamPlayer3D { Stream = stream, Bus = bus, VolumeDb = volumeDb, Position = position };
            _instance.AddChild(spatial);
            spatial.Finished += spatial.QueueFree;
            spatial.Play();
        }
        else
        {
            var flat = new AudioStreamPlayer { Stream = stream, Bus = bus, VolumeDb = volumeDb };
            _instance.AddChild(flat);
            flat.Finished += flat.QueueFree;
            flat.Play();
        }
    }

    /// <summary>Looping background sound (room tone, engine, street). Returns the player so the caller can stop it.
    /// </summary>
    public static AudioStreamPlayer PlayLoop(string id, Node parent, float volumeDb = -12f)
    {
        var player = new AudioStreamPlayer { Bus = "SFX", VolumeDb = volumeDb };
        parent.AddChild(player);
        if (Stream(id) is AudioStreamWav stream && !Deaf)
        {
            player.Stream = Looped(stream);
            player.Play();
        }
        return player;
    }

    /// <summary>Optional recorded patient lines: assets/audio/voice/&lt;trigger&gt;_&lt;index&gt;.ogg.</summary>
    public static void PlayVoice(string voiceId, Vector3 at)
    {
        var path = $"res://assets/audio/voice/{voiceId}.ogg";
        if (!Deaf && ResourceLoader.Exists(path))
        {
            Cache[voiceId] = GD.Load<AudioStream>(path);
            Play(voiceId, at, "Voice");
        }
    }

    private static AudioStreamWav Looped(AudioStreamWav stream)
    {
        var looped = (AudioStreamWav)stream.Duplicate();
        looped.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
        looped.LoopEnd = (int)(stream.GetLength() * stream.MixRate);
        return looped;
    }

    private static AudioStream? Stream(string id)
    {
        if (!Cache.TryGetValue(id, out var stream))
        {
            var path = new ConfigReader(Db.Audio, "sfx").String(id);
            stream = path.Length > 0 && ResourceLoader.Exists(path) ? GD.Load<AudioStream>(path) : null;
            Cache[id] = stream;
        }
        return stream;
    }
}
