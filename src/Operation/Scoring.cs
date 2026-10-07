namespace Scalpel.Operation;

/// <summary>One scored action: its text, points so far and how often it happened.</summary>
public sealed class ScoreEntry(string text)
{
    public string Text { get; } = text;
    public int Points { get; set; }
    public int Count { get; set; }
}

/// <summary>
/// Good and bad actions, host only. Values come from data/scoring.cfg.
/// Every scored event also rattles both surgeons by its stress value.
/// </summary>
public partial class Scoring : Node
{
    private const double ThrottleSeconds = 4.0;
    private const int LogSize = 8;

    private readonly Dictionary<string, double> _lastTime = [];

    public int Points { get; private set; }
    /// <summary>Scoring id -> what it added up to.</summary>
    public Dictionary<string, ScoreEntry> Entries { get; } = [];
    /// <summary>The latest scored actions, newest last. Shown in debug mode.</summary>
    public List<(string Text, int Points)> Recent { get; } = [];

    /// <summary>Scores an action. <paramref name="throttled"/>: repeated calls within a few seconds count once (for
    /// things that fire every frame).</summary>
    public void Add(string id, bool throttled = false)
    {
        if (!Multiplayer.IsServer() || !Db.Scoring.HasSection(id))
        {
            return;
        }
        var now = Time.GetTicksMsec() * 0.001;
        if (throttled && now - _lastTime.GetValueOrDefault(id, double.NegativeInfinity) < ThrottleSeconds)
        {
            return;
        }
        _lastTime[id] = now;
        var config = new ConfigReader(Db.Scoring, id);
        var value = config.Int("points");
        Points += value;
        if (!Entries.TryGetValue(id, out var entry))
        {
            entry = new ScoreEntry(config.String("text", id));
            Entries[id] = entry;
        }
        entry.Points += value;
        entry.Count++;
        Recent.Add((entry.Text, value));
        if (Recent.Count > LogSize)
        {
            Recent.RemoveAt(0);
        }
        var stress = config.Float("stress");
        if (stress > 0f)
        {
            Surgery.Current?.BroadcastStress(stress);
        }
    }

    /// <summary>Stars for a final score: none for a failed surgery.</summary>
    public static int StarsFor(int total, bool passed)
    {
        if (!passed)
        {
            return 0;
        }
        var stars = new ConfigReader(Db.Scoring, "stars");
        if (total >= stars.Int("three_star", 120))
        {
            return 3;
        }
        return total >= stars.Int("two_star", 60) ? 2 : 1;
    }
}
