namespace Scalpel.Patients;

/// <summary>Where a needle coming down onto the site rests (site space): the height its tip stops at (along the
/// site's own vertical), the point of the drawn tissue it touches there (Contact) and that tissue's layer.</summary>
internal readonly record struct ExposedTissue(float Height, Vector3 Contact, TissueDepth Layer);

public partial class PatientBody {
    /// <summary>One drawn triangle of a layer's sheet or of the walls down its cut edges (site space).</summary>
    private readonly record struct DrawnFace(Vector3 A, Vector3 B, Vector3 C, TissueDepth Layer) {
        /// <summary>The point at weights <paramref name="w"/> of A, B and C.</summary>
        public Vector3 At(Vector3 w) => (A * w.X) + (B * w.Y) + (C * w.Z);

        public DrawnFace Scaled(Vector3 by) => this with { A = A * by, B = B * by, C = C * by };
    }

    /// <summary>Every layer's drawn sheet and walls as last published, bucketed by site x and z like the skin's sheet.
    /// </summary>
    private readonly List<DrawnFace> _drawnFaces = [];
    private readonly Dictionary<Vector2I, List<int>> _drawnBuckets = [];
    /// <summary>The faces near a needle's tip (ExposedAt()), kept to fill again.</summary>
    private readonly HashSet<int> _nearFaces = [];
    /// <summary>The layers last put on their meshes, bucketed only once something asks (ExposedAt()): most frames
    /// nothing does.</summary>
    private (Surface? Sheet, Surface? Walls)[]? _drawnPending;

    /// <summary>Keeps the layers just put on their meshes (PublishLayers()) for ExposedAt().</summary>
    private void RememberDrawnTissue((Surface? Sheet, Surface? Walls)[] built) => _drawnPending = built;

    private void BucketDrawnTissue((Surface? Sheet, Surface? Walls)[] built) {
        _drawnFaces.Clear();
        foreach (var bucket in _drawnBuckets.Values) {
            bucket.Clear();
        }
        for (var layer = 0; layer < built.Length; layer++) {
            if (!_layers[layer].Visible) {
                continue;
            }
            var (sheet, walls) = built[layer];
            BucketDrawnFaces(sheet, LayerDepth[layer]);
            BucketDrawnFaces(walls, LayerDepth[layer]);
        }
    }

    private void BucketDrawnFaces(Surface? surface, TissueDepth depth) {
        if (surface is null) {
            return;
        }
        var (v, index) = (surface.Vertices, surface.Indices);
        for (var t = 0; t < index.Length; t += 3) {
            var face = new DrawnFace(v[index[t]], v[index[t + 1]], v[index[t + 2]], depth);
            var low = SkinCell(face.A.Min(face.B).Min(face.C));
            var high = SkinCell(face.A.Max(face.B).Max(face.C));
            for (var x = low.X; x <= high.X; x++) {
                for (var z = low.Y; z <= high.Y; z++) {
                    if (!_drawnBuckets.TryGetValue(new Vector2I(x, z), out var bucket)) {
                        _drawnBuckets[new Vector2I(x, z)] = bucket = [];
                    }
                    bucket.Add(_drawnFaces.Count);
                }
            }
            _drawnFaces.Add(face);
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
        var low = SkinCell(local - new Vector3(WallReach, 0f, WallReach));
        var high = SkinCell(local + new Vector3(WallReach, 0f, WallReach));
        var faces = _nearFaces;
        faces.Clear();
        for (var x = low.X; x <= high.X; x++) {
            for (var z = low.Y; z <= high.Y; z++) {
                if (_drawnBuckets.TryGetValue(new Vector2I(x, z), out var near)) {
                    faces.UnionWith(near);
                }
            }
        }
        // How low the tip goes: the highest of the tissue under it, and of each wall and lip beside it less how far out
        // from it the tip is.
        var height = float.NegativeInfinity;
        var toward = Vector2.Zero;
        ExposedTissue? under = null;
        foreach (var f in faces) {
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
        foreach (var f in faces) {
            var face = _drawnFaces[f];
            // Nearest with height counted several times over distance across: the wall at the tip's depth, not the
            // layer over it that leans out nearer.
            var w = Closest(tip * DepthWeight, face.Scaled(DepthWeight));
            var contact = face.At(w);
            var across = new Vector2(contact.X, contact.Z) - p;
            if (across.Dot(toward) < 0f || across.Length() > toward.Length() + UnderLip) {
                continue;
            }
            var distance = (contact * DepthWeight).DistanceSquaredTo(tip * DepthWeight);
            if (distance < closest) {
                closest = distance;
                best = new ExposedTissue(height, contact, face.Layer);
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
