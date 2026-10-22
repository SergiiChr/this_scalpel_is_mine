using GodotDictionary = Godot.Collections.Dictionary;

namespace Scalpel.Core;

/// <summary>A host saved for joining again from the multiplayer menu.</summary>
public sealed record KnownHost(string Name, string Ip, int Port);

/// <summary>A surgery played on this computer: when it ended (local time, as text) and its post-op report.</summary>
public sealed record PlayedGame(string PlayedAt, SurgeryReport Report);

/// <summary>
/// Save file: unlocked quirks and run modifiers, best scenario results, known hosts. Stored in user://save.cfg and read on first use.
/// The host's save is what counts for scenario progress. Quirk unlocks are always local, and so are played games, kept
/// in user://history.cfg: written only as a game ends, so the other saves don't rewrite its reports.
/// </summary>
public static class Progress {
    private const string Path = "user://save.cfg";
    internal const string HistoryPath = "user://history.cfg";
    /// <summary>Played games kept: the oldest one goes when another is recorded past it.</summary>
    internal const int HistorySize = 100;

    /// <summary>Quirk unlock keys (<see cref="QuirkDef.UnlockKey"/>) and "modifier:id" for run modifiers.</summary>
    private static readonly HashSet<string> Unlocked = [];
    /// <summary>Scenario id -> best star count (0-3).</summary>
    private static readonly Dictionary<string, int> BestStarsById = [];
    private static readonly List<KnownHost> Hosts = [];
    /// <summary>Newest first.</summary>
    private static readonly List<PlayedGame> Games = [];

    static Progress() {
        LoadHistory();
        var file = new ConfigFile();
        if (file.Load(Path) != Error.Ok) {
            return;
        }
        PlayerName = file.GetValue("player", "name", PlayerName).AsString();
        Unlocked.UnionWith(file.GetValue("codex", "unlocked", Array.Empty<string>()).AsStringArray());
        foreach (var (id, stars) in file.GetValue("scenarios", "best_stars", new GodotDictionary()).AsGodotDictionary()) {
            BestStarsById[id.AsString()] = stars.AsInt32();
        }
        foreach (var host in file.GetValue("network", "known_hosts", new Godot.Collections.Array()).AsGodotArray()) {
            var entry = host.AsGodotDictionary();
            Hosts.Add(new KnownHost(entry.String("name"), entry.String("ip"), entry.Int("port")));
        }
    }

    /// <summary>Raised when a quirk is seen for the first time: the quirk and the variant rolled.</summary>
    public static event Action<QuirkDef, string>? QuirkUnlocked;

    public static string PlayerName { get; set; } = "Doctor";

    public static IReadOnlyDictionary<string, int> BestStars => BestStarsById;

    public static IReadOnlyList<KnownHost> KnownHosts => Hosts;

    /// <summary>Surgeries played on this computer, newest first.</summary>
    public static IReadOnlyList<PlayedGame> History => Games;

    public static void Save() {
        var file = new ConfigFile();
        file.SetValue("player", "name", PlayerName);
        file.SetValue("codex", "unlocked", Unlocked.ToArray());
        var stars = new GodotDictionary();
        foreach (var (id, best) in BestStarsById) {
            stars[id] = best;
        }
        file.SetValue("scenarios", "best_stars", stars);
        var hosts = new Godot.Collections.Array();
        foreach (var host in Hosts) {
            hosts.Add(new GodotDictionary { ["name"] = host.Name, ["ip"] = host.Ip, ["port"] = host.Port });
        }
        file.SetValue("network", "known_hosts", hosts);
        file.Save(Path);
    }

    public static bool IsUnlocked(QuirkDef quirk) => Unlocked.Contains(quirk.UnlockKey);

    /// <summary>Unlocks <paramref name="quirk"/> until the game quits, without saving it or announcing it.</summary>
    internal static void UnlockForSession(QuirkDef quirk) => Unlocked.Add(quirk.UnlockKey);

    public static void Unlock(QuirkDef? quirk, string variant = "") {
        if (quirk is null || !Unlocked.Add(quirk.UnlockKey)) {
            return;
        }
        Save();
        QuirkUnlocked?.Invoke(quirk, variant);
    }

    public static bool IsModifierUnlocked(string id) => Unlocked.Contains(ModifierKey(id));

    /// <summary>Unlocks a run modifier until the game quits, without saving it.</summary>
    internal static void UnlockModifierForSession(string id) => Unlocked.Add(ModifierKey(id));

    /// <summary>A run modifier unlocks the first time a run starts with it.</summary>
    public static void UnlockModifier(string id) {
        if (Unlocked.Add(ModifierKey(id))) {
            Save();
        }
    }

    private static string ModifierKey(string id) => $"modifier:{id}";

    public static void RecordResult(string scenarioId, int stars) {
        BestStarsById[scenarioId] = Math.Max(BestStarsById.GetValueOrDefault(scenarioId), stars);
        Save();
    }

    /// <summary>Adds a game to the history as played now, or at <paramref name="playedAt"/>.</summary>
    public static void RecordGame(SurgeryReport report, string? playedAt = null) {
        Games.Insert(0, new PlayedGame(playedAt ?? Time.GetDatetimeStringFromSystem(false, true), report));
        if (Games.Count > HistorySize) {
            Games.RemoveAt(HistorySize);
        }
        var games = new Godot.Collections.Array();
        foreach (var game in Games) {
            games.Add(new GodotDictionary { ["played_at"] = game.PlayedAt, ["report"] = game.Report.ToVariant() });
        }
        var file = new ConfigFile();
        file.SetValue("history", "games", games);
        file.Save(HistoryPath);
    }

    /// <summary>Reads the history. A game that doesn't read (a hand edit, a write cut short) is left out rather than
    /// keeping the game from starting.</summary>
    private static void LoadHistory() {
        var file = new ConfigFile();
        if (file.Load(HistoryPath) != Error.Ok) {
            return;
        }
        foreach (var game in file.GetValue("history", "games", new Godot.Collections.Array()).AsGodotArray()) {
            try {
                var entry = game.AsGodotDictionary();
                Games.Add(new PlayedGame(entry.String("played_at"), SurgeryReport.FromVariant(entry["report"].AsGodotDictionary())));
            } catch (Exception e) when (e is KeyNotFoundException or InvalidCastException or ArgumentOutOfRangeException
                or IndexOutOfRangeException or NullReferenceException) {
                GD.PushWarning($"Left a played game out of the history: {e.Message}");
            }
        }
    }

    public static void RememberHost(string name, string ip, int port) {
        Hosts.RemoveAll(host => host.Ip == ip && host.Port == port);
        Hosts.Insert(0, new KnownHost(name, ip, port));
        Save();
    }

    public static void ForgetHost(string ip, int port) {
        Hosts.RemoveAll(host => host.Ip == ip && host.Port == port);
        Save();
    }
}
