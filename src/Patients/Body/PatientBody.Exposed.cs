using System.Runtime.InteropServices;

namespace Scalpel.Patients;

/// <summary>Where a needle coming down onto the site rests (site space): the height its tip stops at (along the
/// site's own vertical), the point of the drawn tissue it touches there (Contact) and that tissue's layer.</summary>
internal readonly record struct ExposedTissue(float Height, Vector3 Contact, TissueDepth Layer);

public partial class PatientBody {
    /// <summary>One drawn triangle of a layer's sheet or of the walls down its cut edges (site space).</summary>
    private readonly record struct DrawnFace(Vector3 A, Vector3 B, Vector3 C, TissueDepth Layer, bool Wall) {
        /// <summary>The point at weights <paramref name="w"/> of A, B and C.</summary>
        public Vector3 At(Vector3 w) => (A * w.X) + (B * w.Y) + (C * w.Z);

        public DrawnFace Scaled(Vector3 by) => this with { A = A * by, B = B * by, C = C * by };
    }

    /// <summary>Every layer's drawn sheet and walls as last published, bucketed by site x and z like the skin's sheet.
    /// </summary>
    private readonly List<DrawnFace> _drawnFaces = [];
    /// <summary>Each of <see cref="_drawnFaces"/>' extent across the site (x, z) and its highest point: no point of it
    /// is nearer across or higher, so ExposedAt() skips the faces that can't be what a tip touches. Padded by
    /// <see cref="BoundsSlack"/>: a point worked out from a face's corners can land a rounding error outside them.
    /// </summary>
    private readonly List<(Rect2 Across, float Top)> _drawnBounds = [];
    /// <summary><see cref="_drawnFaces"/> by bucket.</summary>
    private readonly CellGrid _drawnBuckets = new(SkinBucket);
    /// <summary>The faces near a needle's tip (ExposedAt()), kept to fill again.</summary>
    private readonly List<int> _nearFaces = [];
    /// <summary>For each face, the last ExposedAt() query that took it near (<see cref="_nearQuery"/>): a face over
    /// several buckets is taken once.</summary>
    private int[] _nearIn = [];
    private int _nearQuery;
    /// <summary>The layers last put on their meshes, bucketed only once something asks (ExposedAt()): most frames
    /// nothing does.</summary>
    private (Surface? Sheet, Surface? Walls)[]? _drawnPending;

    /// <summary>Keeps the layers just put on their meshes (PublishLayers()) for ExposedAt().</summary>
    private void RememberDrawnTissue((Surface? Sheet, Surface? Walls)[] built) => _drawnPending = built;

    private void BucketDrawnTissue((Surface? Sheet, Surface? Walls)[] built) {
        _drawnFaces.Clear();
        _drawnBounds.Clear();
        _drawnBuckets.Clear();
        for (var layer = 0; layer < built.Length; layer++) {
            if (!_layers[layer].Visible) {
                continue;
            }
            var (sheet, walls) = built[layer];
            AddDrawnFaces(sheet, LayerDepth[layer], false);
            AddDrawnFaces(walls, LayerDepth[layer], true);
        }
        _drawnBuckets.Index();
        if (_nearIn.Length < _drawnFaces.Count) {
            _nearIn = new int[_drawnFaces.Count * 2];
            _nearQuery = 0;
        }
    }

    private void AddDrawnFaces(Surface? surface, TissueDepth depth, bool wall) {
        if (surface is null) {
            return;
        }
        var (v, index) = (surface.Vertices, surface.Indices);
        // Thousands of faces every time the layers are published: written in place, not added one by one.
        var (first, count) = (_drawnFaces.Count, index.Length / 3);
        CollectionsMarshal.SetCount(_drawnFaces, first + count);
        CollectionsMarshal.SetCount(_drawnBounds, first + count);
        var faces = CollectionsMarshal.AsSpan(_drawnFaces)[first..];
        var bounds = CollectionsMarshal.AsSpan(_drawnBounds)[first..];
        for (var f = 0; f < count; f++) {
            var (a, b, c) = (v[index[f * 3]], v[index[(f * 3) + 1]], v[index[(f * 3) + 2]]);
            faces[f] = new DrawnFace(a, b, c, depth, wall);
            var (min, max) = (a.Min(b).Min(c), a.Max(b).Max(c));
            _drawnBuckets.Add(min, max);
            bounds[f] = (new Rect2(min.X, min.Z, max.X - min.X, max.Z - min.Z).Grow(BoundsSlack), max.Y + BoundsSlack);
        }
    }

    /// <summary>How steeply (meters down per meter out) a needle's tip goes down a cut's walls as it's moved out over
    /// the opening from the lip: a centimeter out it's down past skin, fat and muscle.</summary>
    private const float WallSlide = 4f;
    /// <summary>Furthest out (meters) over an opening a needle's tip still goes down a wall.</summary>
    private const float WallReach = 0.012f;
    /// <summary>How much more a height apart counts than as much across, finding the tissue a tip down a cut's wall
    /// touches.</summary>
    private static readonly Vector3 DepthWeight = new(1f, 4f, 1f);
    /// <summary>A tip this close (meters, across) to the tissue it rests on is right over it.</summary>
    private const float OverTissue = 0.0005f;
    /// <summary>How far (meters) a face's bounds are grown (<see cref="_drawnBounds"/>).</summary>
    private const float BoundsSlack = 1e-6f;
    /// <summary>How far (meters) back under the lip a needle going down a cut's wall still reaches the layers below it.
    /// </summary>
    private const float UnderLip = 0.005f;

    /// <summary>
    /// Where a needle's tip coming straight down at <paramref name="local"/> (site space) rests on the tissue as it's
    /// drawn: on the skin, or on the fat or muscle a cut lays bare. Moved out over a cut from its lip the tip goes down
    /// the wall beside it (<see cref="WallSlide"/>), touching the skin's cut face first, then the fat's and the
    /// muscle's, instead of dropping off the lip: every layer can be aimed at. Away from the simulated tissue it's the
    /// body's skin. Null off the site, and where nothing is near enough to touch in an opening through the muscle.
    /// </summary>
    internal ExposedTissue? ExposedAt(Vector3 local) {
        if (_drawnPending is { } pending) {
            _drawnPending = null;
            BucketDrawnTissue(pending);
        }
        var p = new Vector2(local.X, local.Z);
        var low = _drawnBuckets.CellOf(local - new Vector3(WallReach, 0f, WallReach));
        var high = _drawnBuckets.CellOf(local + new Vector3(WallReach, 0f, WallReach));
        var faces = _nearFaces;
        faces.Clear();
        _nearQuery++;
        for (var x = low.X; x <= high.X; x++) {
            for (var z = low.Y; z <= high.Y; z++) {
                foreach (var f in _drawnBuckets.Items(x, z)) {
                    if (_nearIn[f] != _nearQuery) {
                        _nearIn[f] = _nearQuery;
                        faces.Add(f);
                    }
                }
            }
        }
        // How low the tip goes: the highest of the tissue under it, and of each wall and lip beside it less how far out
        // from it the tip is.
        var height = float.NegativeInfinity;
        var toward = Vector2.Zero;
        ExposedTissue? under = null;
        foreach (var f in faces) {
            var (bounds, top) = _drawnBounds[f];
            var awayAtLeast = AcrossFrom(p, bounds);
            if (awayAtLeast > WallReach || top - (awayAtLeast * WallSlide) <= height) {
                continue;
            }
            var face = _drawnFaces[f];
            var nearest = face.At(ClosestAcross(p, face));
            var across = new Vector2(nearest.X, nearest.Z) - p;
            var away = across.Length();
            if (away <= WallReach && nearest.Y - (away * WallSlide) > height) {
                height = nearest.Y - (away * WallSlide);
                toward = across;
                under = new ExposedTissue(height, nearest, face.Layer);
            }
        }
        // Nothing drawn near, away from any opening: the body's own skin, where the tissue isn't simulated. Off the
        // site, nothing.
        if (under is null) {
            var uv = LocalToUv(local);
            if (!new Rect2(Vector2.Zero, Vector2.One).HasPoint(uv) || !OnBody(uv) || IsOpen(uv)) {
                return null;
            }
            var skin = local with { Y = SurfaceHeight(uv) };
            return new ExposedTissue(skin.Y, skin, TissueDepth.Skin);
        }
        // Right over tissue, not out over an opening: that's what it touches.
        if (toward.Length() < OverTissue) {
            return under;
        }
        // What it touches there: the drawn tissue nearest the tip on the side it came down, the wall at its depth below
        // that lip. Not the far side's wall, nor an edge drawn back out of sight under the layer over it: spread first.
        var tip = local with { Y = height };
        ExposedTissue? best = null;
        var closest = float.MaxValue;
        var reach = toward.Length() + UnderLip;
        foreach (var f in faces) {
            if (AcrossFrom(p, _drawnBounds[f].Across) > reach) {
                continue;
            }
            var face = _drawnFaces[f];
            // Nearest with height counted several times over distance across: the wall at the tip's depth, not the
            // layer over it that leans out nearer.
            var w = Closest(tip * DepthWeight, face.Scaled(DepthWeight));
            var contact = face.At(w);
            var across = new Vector2(contact.X, contact.Z) - p;
            if (across.Dot(toward) < 0f || across.Length() > reach) {
                continue;
            }
            var distance = (contact * DepthWeight).DistanceSquaredTo(tip * DepthWeight);
            if (distance < closest) {
                closest = distance;
                // On a layer's wall, its top edge: the hole goes into the layer's surface, wherever down its face the
                // tip touched.
                var top = Mathf.Max(face.A.Y, Mathf.Max(face.B.Y, face.C.Y));
                best = new ExposedTissue(height, face.Wall ? contact with { Y = top } : contact, face.Layer);
            }
        }
        return best;
    }

    /// <summary>Where a needle's tip coming straight down (the world's vertical, which leans off the site's on a curved
    /// body) from <paramref name="point"/> (world space) rests on the tissue (<see cref="ExposedAt"/>), and how high
    /// (world) it stops.</summary>
    internal (ExposedTissue Tissue, float Y)? ExposedUnder(Vector3 point) {
        (ExposedTissue, float)? touched = null;
        for (var i = 0; i < 3; i++) {
            var local = Site.ToLocal(point);
            if (ExposedAt(local) is not { } tissue) {
                return touched;
            }
            point.Y = Site.ToGlobal(local with { Y = tissue.Height }).Y;
            touched = (tissue, point.Y);
        }
        return touched;
    }

    /// <summary>How far across the site <paramref name="p"/> is from <paramref name="bounds"/>.</summary>
    private static float AcrossFrom(Vector2 p, Rect2 bounds) => p.Clamp(bounds.Position, bounds.End).DistanceTo(p);

    /// <summary>Weights of the point of <paramref name="face"/> nearest to <paramref name="p"/> across the site (x, z).
    /// </summary>
    private static Vector3 ClosestAcross(Vector2 p, DrawnFace face) {
        var (a, b, c) = (new Vector2(face.A.X, face.A.Z), new Vector2(face.B.X, face.B.Z), new Vector2(face.C.X, face.C.Z));
        var w = Barycentric(p, a, b, c);
        if (w.IsFinite() && Mathf.Min(w.X, Mathf.Min(w.Y, w.Z)) >= 0f) {
            return w;
        }
        // Outside it (or a wall seen edge on): the nearest point on its edges.
        return NearestOnEdges(face.Scaled(new Vector3(1f, 0f, 1f)), new Vector3(p.X, 0f, p.Y));
    }

    /// <summary>Weights of the point of <paramref name="face"/> nearest to <paramref name="p"/>.</summary>
    private static Vector3 Closest(Vector3 p, DrawnFace face) {
        var normal = (face.B - face.A).Cross(face.C - face.A);
        var area = normal.LengthSquared();
        if (area > 0f) {
            // Onto its plane, then its weights there: inside, that's the nearest point.
            var onPlane = p - (normal * ((p - face.A).Dot(normal) / area));
            var wb = (onPlane - face.A).Cross(face.C - face.A).Dot(normal) / area;
            var wc = (face.B - face.A).Cross(onPlane - face.A).Dot(normal) / area;
            var w = new Vector3(1f - wb - wc, wb, wc);
            if (Mathf.Min(w.X, Mathf.Min(w.Y, w.Z)) >= 0f) {
                return w;
            }
        }
        return NearestOnEdges(face, p);
    }

    /// <summary>Weights of the point on <paramref name="face"/>'s edges nearest to <paramref name="p"/>.</summary>
    private static Vector3 NearestOnEdges(DrawnFace face, Vector3 p) {
        var best = Vector3.Zero;
        var nearest = float.MaxValue;
        foreach (var (i, j) in Edges) {
            var (from, to) = (Corner(face, i), Corner(face, j));
            var along = to - from;
            var t = along.LengthSquared() > 0f ? Mathf.Clamp((p - from).Dot(along) / along.LengthSquared(), 0f, 1f) : 0f;
            var distance = p.DistanceSquaredTo(from + (along * t));
            if (distance < nearest) {
                nearest = distance;
                best = Vector3.Zero;
                best[i] = 1f - t;
                best[j] = t;
            }
        }
        return best;
    }

    private static readonly (int, int)[] Edges = [(0, 1), (1, 2), (2, 0)];

    private static Vector3 Corner(DrawnFace face, int i) => i switch { 0 => face.A, 1 => face.B, _ => face.C };
}
