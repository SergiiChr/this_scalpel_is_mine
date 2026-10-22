namespace Scalpel.Visuals;

/// <summary>
/// The operating table's shape for the liquid on it: where a falling drop lands (the mattress pads' upward faces and
/// the frame's), and every triangle of pads and frame, which the bed's <see cref="SurfaceFilm"/> spreads blood over.
/// </summary>
internal sealed class BedLiquidSurface {
    private readonly record struct Face(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal, Rect2 Bounds);
    private readonly List<Face> _pads = [];
    private readonly List<Face> _landingFaces = [];
    private readonly float _bottom;
    private readonly float _padTop;
    private readonly float _landingBottom;
    private readonly float _landingTop;
    private readonly Rect2 _landingBounds;
    private const int ContactColumns = 32, ContactRows = 16;
    private readonly List<int>[] _contactCells = Enumerable.Range(0, ContactColumns * ContactRows).Select(_ => new List<int>()).ToArray();

    /// <summary>Pads and frame, three corners per triangle in world space, wound as the meshes are.</summary>
    internal Vector3[] Triangles { get; }

    /// <summary>Just above the frame's lowest point: what hangs below it hangs off the bed.</summary>
    internal float DripHeight => _bottom + 0.003f;

    internal readonly record struct Hit(Vector3 At, bool Frame);

    internal BedLiquidSurface(MeshInstance3D pads, MeshInstance3D frame) {
        var frameVertices = frame.Mesh.GetFaces().Select(p => frame.GlobalTransform * p).ToArray();
        var padVertices = pads.Mesh.GetFaces().Select(p => pads.GlobalTransform * p).ToArray();
        Triangles = [.. padVertices, .. frameVertices];
        _bottom = frameVertices.Min(p => p.Y);
        _pads.AddRange(UpwardFaces(padVertices));
        _padTop = padVertices.Max(p => p.Y);
        _landingFaces.AddRange(_pads);
        _landingFaces.AddRange(UpwardFaces(frameVertices));
        var landingVertices = _landingFaces.SelectMany(face => new[] { face.A, face.B, face.C }).ToArray();
        _landingBottom = landingVertices.Min(p => p.Y);
        _landingTop = landingVertices.Max(p => p.Y);
        _landingBounds = new Rect2(Xz(landingVertices[0]), Vector2.Zero);
        foreach (var vertex in landingVertices) { _landingBounds = _landingBounds.Expand(Xz(vertex)); }
        for (var i = 0; i < _landingFaces.Count; i++) {
            var face = _landingFaces[i];
            var low = ContactCell(face.Bounds.Position); var high = ContactCell(face.Bounds.End);
            for (var y = low.Y; y <= high.Y; y++) {
                for (var x = low.X; x <= high.X; x++) { _contactCells[y * ContactColumns + x].Add(i); }
            }
        }
    }

    private static IEnumerable<Face> UpwardFaces(Vector3[] vertices) {
        for (var i = 0; i + 2 < vertices.Length; i += 3) {
            var (a, b, c) = (vertices[i], vertices[i + 1], vertices[i + 2]);
            var normal = (c - a).Cross(b - a).Normalized();
            if (normal.Y > 0.001f) { yield return new Face(a, b, c, normal, new Rect2(Xz(a), Vector2.Zero).Expand(Xz(b)).Expand(Xz(c))); }
        }
    }

    private static Vector2 Xz(Vector3 p) => new(p.X, p.Z);

    private static float Height(Face face, Vector2 at) => face.A.Y
        - (face.Normal.X * (at.X - face.A.X) + face.Normal.Z * (at.Y - face.A.Z)) / face.Normal.Y;

    private static bool Covers(Face face, Vector2 p) {
        var a = Xz(face.A); var b = Xz(face.B); var c = Xz(face.C);
        return (b - a).Cross(p - a) >= -0.000003f * (b - a).Length() && (c - b).Cross(p - b) >= -0.000003f * (c - b).Length()
            && (a - c).Cross(p - c) >= -0.000003f * (a - c).Length();
    }

    /// <summary>The mattress's top at <paramref name="at"/> (x, z), or null off the pads.</summary>
    internal float? HeightAt(Vector2 at) {
        float? highest = null;
        foreach (var face in _pads) {
            if (!face.Bounds.Grow(0.000003f).HasPoint(at) || !Covers(face, at)) { continue; }
            var height = Height(face, at);
            if (!highest.HasValue || height > highest.Value) { highest = height; }
        }
        return highest;
    }

    /// <summary>Whether <paramref name="at"/> lies on the mattress or sunk into it, as skin pressed on it does.</summary>
    internal bool Touches(Vector3 at) => at.Y < _padTop + ContactGap && HeightAt(new Vector2(at.X, at.Z)) is { } top
        && at.Y < top + ContactGap;

    private const float ContactGap = 0.003f;

    private Vector2I ContactCell(Vector2 at) => new(
        Math.Clamp((int)((at.X - _landingBounds.Position.X) / _landingBounds.Size.X * ContactColumns), 0, ContactColumns - 1),
        Math.Clamp((int)((at.Y - _landingBounds.Position.Y) / _landingBounds.Size.Y * ContactRows), 0, ContactRows - 1));

    internal Vector3? Landing(Vector3 from, Vector3 to) => Contact(from, to)?.At;

    /// <summary>Where a drop moving from <paramref name="from"/> to <paramref name="to"/> first meets the top of a pad
    /// or of the frame, or null.</summary>
    internal Hit? Contact(Vector3 from, Vector3 to) {
        var motion = to - from;
        if (motion.Y >= 0f || to.Y > _landingTop || from.Y < _landingBottom) { return null; }
        var bounds = new Rect2(Xz(from), Vector2.Zero).Expand(Xz(to)).Grow(0.000001f);
        if (!_landingBounds.Intersects(bounds, includeBorders: true)) { return null; }
        Hit? closest = null;
        var closestProgress = float.PositiveInfinity;
        var low = ContactCell(bounds.Position); var high = ContactCell(bounds.End);
        for (var row = low.Y; row <= high.Y; row++) {
            for (var column = low.X; column <= high.X; column++) {
                foreach (var index in _contactCells[row * ContactColumns + column]) {
                    var face = _landingFaces[index];
                    // A face may span several cells; visit it once without per-drop allocation or mutable stamps.
                    var first = ContactCell(face.Bounds.Position);
                    if (column != Math.Max(low.X, first.X) || row != Math.Max(low.Y, first.Y)) { continue; }
                    if (!face.Bounds.Grow(0.000003f).Intersects(bounds)) { continue; }
                    var start = face.Normal.Dot(from - face.A);
                    var end = face.Normal.Dot(to - face.A);
                    if (start < 0f || end > 0f) { continue; }
                    var progress = start / (start - end);
                    if (progress >= closestProgress) { continue; }
                    var hit = from.Lerp(to, progress);
                    if (!Covers(face, Xz(hit))) { continue; }
                    closest = new Hit(hit, index >= _pads.Count);
                    closestProgress = progress;
                }
            }
        }
        return closest;
    }
}
