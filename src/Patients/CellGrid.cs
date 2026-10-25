namespace Scalpel.Patients;

/// <summary>
/// Items (triangles, by the order they were added) bucketed by the cells <paramref name="cell"/> (meters) wide their
/// extent across the site (x, z) covers. Filled again each time what it indexes moves: in two passes, counting then
/// placing, into flat arrays it keeps, so a fill neither looks up nor grows a list per cell. The arrays only grow, to
/// twice what a fill needs: moving skin changes the cell count every fill. Each cell holds its items in the order
/// they were added.
/// </summary>
internal sealed class CellGrid(float cell) {
    private (Vector2I Low, Vector2I High)[] _spans = [];
    private int _count;
    /// <summary>Each cell's items in <see cref="_items"/> from its start to the next cell's.</summary>
    private int[] _start = [0];
    private int[] _items = [];
    private Vector2I _from;
    private Vector2I _size;

    public Vector2I CellOf(Vector3 at) => new(Mathf.FloorToInt(at.X / cell), Mathf.FloorToInt(at.Z / cell));

    /// <summary>Starts a new fill: <see cref="Add"/> the items, then <see cref="Index"/>.</summary>
    public void Clear() => _count = 0;

    /// <summary>The next item, over <paramref name="low"/> to <paramref name="high"/> across.</summary>
    public void Add(Vector3 low, Vector3 high) {
        if (_count == _spans.Length) {
            Array.Resize(ref _spans, Math.Max(_count * 2, 256));
        }
        _spans[_count++] = (CellOf(low), CellOf(high));
    }

    public void Index() {
        var spans = _spans.AsSpan(0, _count);
        var (from, to) = (new Vector2I(int.MaxValue, int.MaxValue), new Vector2I(int.MinValue, int.MinValue));
        foreach (var (low, high) in spans) {
            (from, to) = (from.Min(low), to.Max(high));
        }
        _from = from;
        _size = _count > 0 ? to - from + Vector2I.One : Vector2I.Zero;
        var cells = _size.X * _size.Y;
        if (_start.Length < cells + 1) {
            _start = new int[(cells + 1) * 2];
        }
        var start = _start.AsSpan(0, cells + 1);
        start.Clear();
        foreach (var (low, high) in spans) {
            for (var z = low.Y; z <= high.Y; z++) {
                for (var x = low.X; x <= high.X; x++) {
                    start[At(x, z) + 1]++;
                }
            }
        }
        for (var c = 1; c < start.Length; c++) {
            start[c] += start[c - 1];
        }
        if (_items.Length < start[^1]) {
            _items = new int[start[^1] * 2];
        }
        // Each cell's start moves past every item placed in it, then back one cell.
        for (var item = 0; item < spans.Length; item++) {
            var (low, high) = spans[item];
            for (var z = low.Y; z <= high.Y; z++) {
                for (var x = low.X; x <= high.X; x++) {
                    _items[start[At(x, z)]++] = item;
                }
            }
        }
        for (var c = start.Length - 1; c > 0; c--) {
            start[c] = start[c - 1];
        }
        start[0] = 0;
    }

    /// <summary>The items over cell (<paramref name="x"/>, <paramref name="z"/>) (<see cref="CellOf"/>).</summary>
    public ReadOnlySpan<int> Items(int x, int z) {
        if (x < _from.X || z < _from.Y || x - _from.X >= _size.X || z - _from.Y >= _size.Y) {
            return [];
        }
        var c = At(x, z);
        return _items.AsSpan(_start[c], _start[c + 1] - _start[c]);
    }

    /// <summary>The items over <paramref name="at"/>'s cell.</summary>
    public ReadOnlySpan<int> Items(Vector3 at) {
        var c = CellOf(at);
        return Items(c.X, c.Y);
    }

    private int At(int x, int z) => x - _from.X + ((z - _from.Y) * _size.X);
}
