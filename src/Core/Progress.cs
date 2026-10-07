using GodotDictionary = Godot.Collections.Dictionary;

namespace Scalpel.Core;

/// <summary>A host saved for joining again from the multiplayer menu.</summary>
public sealed record KnownHost(string Name, string Ip, int Port);

/// <summary>
/// Save file: unlocked quirks, best scenario results, known hosts. Stored in user://save.cfg and read on first use.
/// The host's save is what counts for scenario progress. Quirk unlocks are always local.
/// </summary>
public static class Progress
{
    private const string Path = "user://save.cfg";

    private static readonly HashSet<string> UnlockedQuirks = [];
    /// <summary>Scenario id -> best star count (0-3).</summary>
    private static readonly Dictionary<string, int> BestStarsById = [];
    private static readonly List<KnownHost> Hosts = [];

    static Progress()
    {
        var file = new ConfigFile();
        if (file.Load(Path) != Error.Ok)
        {
            return;
        }
        PlayerName = file.GetValue("player", "name", PlayerName).AsString();
        UnlockedQuirks.UnionWith(file.GetValue("codex", "unlocked", Array.Empty<string>()).AsStringArray());
        foreach (var (id, stars) in file.GetValue("scenarios", "best_stars", new GodotDictionary()).AsGodotDictionary())
        {
            BestStarsById[id.AsString()] = stars.AsInt32();
        }
        foreach (var host in file.GetValue("network", "known_hosts", new Godot.Collections.Array()).AsGodotArray())
        {
            var entry = host.AsGodotDictionary();
            Hosts.Add(new KnownHost(entry.String("name"), entry.String("ip"), entry.Int("port")));
        }
    }

    /// <summary>Raised when a quirk is seen for the first time: the quirk and the variant rolled.</summary>
    public static event Action<QuirkDef, string>? QuirkUnlocked;

    public static string PlayerName { get; set; } = "Doctor";

    public static IReadOnlyDictionary<string, int> BestStars => BestStarsById;

    public static IReadOnlyList<KnownHost> KnownHosts => Hosts;

    public static void Save()
    {
        var file = new ConfigFile();
        file.SetValue("player", "name", PlayerName);
        file.SetValue("codex", "unlocked", UnlockedQuirks.ToArray());
        var stars = new GodotDictionary();
        foreach (var (id, best) in BestStarsById)
        {
            stars[id] = best;
        }
        file.SetValue("scenarios", "best_stars", stars);
        var hosts = new Godot.Collections.Array();
        foreach (var host in Hosts)
        {
            hosts.Add(new GodotDictionary { ["name"] = host.Name, ["ip"] = host.Ip, ["port"] = host.Port });
        }
        file.SetValue("network", "known_hosts", hosts);
        file.Save(Path);
    }

    public static bool IsUnlocked(QuirkDef quirk) => UnlockedQuirks.Contains(quirk.UnlockKey);

    public static void Unlock(QuirkDef? quirk, string variant = "")
    {
        if (quirk is null || !UnlockedQuirks.Add(quirk.UnlockKey))
        {
            return;
        }
        Save();
        QuirkUnlocked?.Invoke(quirk, variant);
    }

    public static void RecordResult(string scenarioId, int stars)
    {
        BestStarsById[scenarioId] = Math.Max(BestStarsById.GetValueOrDefault(scenarioId), stars);
        Save();
    }

    public static void RememberHost(string name, string ip, int port)
    {
        Hosts.RemoveAll(host => host.Ip == ip && host.Port == port);
        Hosts.Insert(0, new KnownHost(name, ip, port));
        Save();
    }

    public static void ForgetHost(string ip, int port)
    {
        Hosts.RemoveAll(host => host.Ip == ip && host.Port == port);
        Save();
    }
}
