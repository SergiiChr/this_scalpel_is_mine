namespace Scalpel.Patients;

/// <summary>The site's skin as it's drawn now, copied for a worker: where it lies over each uv and whether the body
/// holds it there (<see cref="PatientBody.SkinPoint"/>, <see cref="PatientBody.LiquidSupport"/>), apart from the
/// body's own state, which the main thread keeps changing. <see cref="PatientBody.SnapshotSkin"/> fills it; its
/// buffers are kept between fills.</summary>
internal sealed class SkinSnapshot {
    private float[] _heights = [], _support = [];
    private Vector3[] _moved = [];
    private bool[] _simulated = [];
    private int _side, _resX, _resY;
    private Vector2 _siteSize;

    /// <summary>Takes a copy of the skin as drawn now.</summary>
    /// <param name="heights">The skin's height over the site plane on a <paramref name="side"/> square grid, empty
    /// when the site has none yet.</param>
    /// <param name="support">The body's thickness under the site on the same grid, negative off it.</param>
    /// <param name="moved">Per sheet particle (<paramref name="resX"/> + 1 by <paramref name="resY"/> + 1), how far
    /// the simulated skin is drawn from the body's own, and <paramref name="simulated"/> where it replaces it.</param>
    internal void Fill(ReadOnlySpan<float> heights, int side, ReadOnlySpan<float> support, ReadOnlySpan<Vector3> moved,
        ReadOnlySpan<bool> simulated, int resX, int resY, Vector2 siteSize) {
        _heights = Copy(heights, _heights);
        _support = Copy(support, _support);
        _moved = Copy(moved, _moved);
        _simulated = Copy(simulated, _simulated);
        (_side, _resX, _resY, _siteSize) = (side, resX, resY, siteSize);
    }

    private static T[] Copy<T>(ReadOnlySpan<T> from, T[] into) {
        if (into.Length != from.Length) { into = new T[from.Length]; }
        from.CopyTo(into);
        return into;
    }

    /// <summary>Where the skin at <paramref name="uv"/> is drawn now (site space).</summary>
    internal Vector3 Point(Vector2 uv) {
        var k = Nearest(uv);
        var moved = k < _simulated.Length && _simulated[k] ? _moved[k] : Vector3.Zero;
        return new Vector3((uv.X - 0.5f) * _siteSize.X, Bilinear(_heights, uv, 0f), (uv.Y - 0.5f) * _siteSize.Y) + moved;
    }

    /// <summary>Whether the simulated skin, not the body's own, is drawn at <paramref name="uv"/>.</summary>
    internal bool Simulated(Vector2 uv) {
        var k = Nearest(uv);
        return k < _simulated.Length && _simulated[k];
    }

    /// <summary>The body's thickness under <paramref name="uv"/>, negative where the site hangs off it.</summary>
    internal float Support(Vector2 uv) => Bilinear(_support, uv, 1f);

    private int Nearest(Vector2 uv) {
        var i = Math.Clamp(Mathf.RoundToInt(uv.X * _resX), 1, _resX - 1);
        var j = Math.Clamp(Mathf.RoundToInt(uv.Y * _resY), 1, _resY - 1);
        return j * (_resX + 1) + i;
    }

    private float Bilinear(float[] grid, Vector2 uv, float none) {
        if (grid.Length != _side * _side || _side < 2) { return none; }
        var p = uv.Clamp(Vector2.Zero, Vector2.One) * (_side - 1);
        var x = Math.Min((int)p.X, _side - 2);
        var y = Math.Min((int)p.Y, _side - 2);
        var f = p - new Vector2(x, y);
        return Mathf.Lerp(Mathf.Lerp(grid[y * _side + x], grid[y * _side + x + 1], f.X),
            Mathf.Lerp(grid[(y + 1) * _side + x], grid[(y + 1) * _side + x + 1], f.X), f.Y);
    }
}
