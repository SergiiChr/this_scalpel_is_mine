namespace Scalpel.Visuals;

/// <summary>One wetted outlet gathers liquid, stretches a neck, then releases a weighted drop. Its own seeded clock
/// keeps neighbouring streams independent and repeatable; capacity pressure retains the liquid in the neck.</summary>
internal sealed class LiquidPulse {
    private readonly Random _random;
    internal float Pending { get; private set; }
    internal float Interval { get; private set; }
    internal float Wait { get; private set; }
    internal float Width { get; private set; }
    private float _stretch;
    internal float Length => 0.006f + _stretch * Mathf.Pow(1f - Mathf.Clamp(Wait / Interval, 0f, 1f), 1.4f);

    internal LiquidPulse(int seed) {
        _random = new Random(seed);
        Reset();
    }

    internal void Add(float area) => Pending += area;

    internal float Drain() {
        var pending = Pending;
        Pending = 0f;
        return pending;
    }

    internal float Advance(float delta, bool room) {
        if (Pending <= 0f) { return 0f; }
        Wait -= delta;
        if (Wait > 0f || !room) { return 0f; }
        var released = Pending;
        Pending = 0f;
        Reset();
        return released;
    }

    private void Reset() {
        Interval = 0.065f + (float)_random.NextDouble() * 0.18f;
        Wait = Interval;
        _stretch = 0.012f + (float)_random.NextDouble() * 0.04f;
        Width = 0.0008f + (float)_random.NextDouble() * 0.0013f;
    }
}
