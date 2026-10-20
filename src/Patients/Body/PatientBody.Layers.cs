using System.Threading.Tasks;

namespace Scalpel.Patients;

/// <summary>The site's tissue layers: measuring the site on the body model and drawing skin, fat and muscle from the
/// tissue sim.</summary>
public partial class PatientBody {
    /// <summary>How far (meters) each layer slides along with the skin before what's under it holds it back: the fat
    /// hangs on the skin and goes wherever it goes, the muscle sits on bone and fascia and gives a little. A layer
    /// slides along fully up to its tether and then stays, instead of following a share of every movement: scaled
    /// down movement folds a layer over itself where the skin above it bunches up. See <see cref="LayerPoint"/>.
    /// </summary>
    private static readonly float[] LayerTether = [float.PositiveInfinity, float.PositiveInfinity, 0.008f];
    /// <summary>How far (meters) the muscle rises with the skin over it before the skin and its fat peel off it (see
    /// <see cref="LayerPoint"/>).</summary>
    private const float MuscleLift = 0.004f;
    internal static readonly TissueDepth[] LayerDepth = [TissueDepth.Skin, TissueDepth.Fat, TissueDepth.Muscle];
    /// <summary>Movement over which the rendered skin adopts the deformed sheet's normals.</summary>
    private const float FlapMove = 0.015f;
    /// <summary>Points per side the site's skin heights are measured at on the body model.</summary>
    private const int Heights = 33;
    /// <summary>Skin on the region's edge that moved less than this (meters) is drawn right on the body model next to
    /// it: a cut drawing its edges back reaches a few millimeters, a flap folded back moves centimeters.</summary>
    private const float EdgeHold = 0.005f;
    /// <summary>Rays onto the body model start this far out along the site's normal (meters).</summary>
    private const float RayStart = 0.15f;
    /// <summary>Body thinner than this under the skin (meters) can't hold the site's skin, fat and muscle: off the body.
    /// </summary>
    private const float MinThickness = 0.035f;

    /// <summary>One part of the body model (skin or gown) in site space, for measuring the site against: its
    /// triangles, three corners each as the mesh numbers them, with their smooth normals.</summary>
    private sealed class ModelPart {
        public TriangleMesh Mesh { get; } = new();
        public List<Vector3> Faces { get; } = [];
        public List<Vector3> Normals { get; } = [];
    }

    /// <summary>Where a ray down the site's normal meets the body model: its position, the model's smooth normal there
    /// and the part it hit.</summary>
    private readonly record struct ModelHit(Vector3 Position, Vector3 Normal, ModelPart Part);

    /// <summary>
    /// How one layer's mesh is put together (see PlanLayers()): per vertex the particle it belongs to (owner), and for
    /// a crossing the spring's other end, how far along it the blade crossed (share) and the lip neighbour it slides
    /// with (slide, -1 for none); its uv; the triangle indices; and per wall quad its two top vertices and a vertex of
    /// its own side's skin, to face it away from.
    /// </summary>
    private sealed class LayerPlan {
        public List<int> Owner { get; } = [];
        public List<int> Other { get; } = [];
        public List<float> Share { get; } = [];
        public List<int> Slide { get; } = [];
        public List<Vector2> Uv { get; } = [];
        public List<int> Index { get; } = [];
        public List<int> Wall { get; } = [];
        /// <summary>Neighbours along a lip, kept separate from the other side of the opening.</summary>
        public Dictionary<int, List<int>> Lip { get; } = [];
        public List<(int A, int B)> Midpoints { get; } = [];
        /// <summary>The refined vertex of each lip edge's midpoint, by its two ends (lower first).</summary>
        public Dictionary<(int, int), int> MidpointOf { get; } = [];
        public List<int> RefinedIndex { get; } = [];
        public List<(int A, int B, int C)> Centers { get; } = [];
        public Dictionary<int, int> OppositeLip { get; } = [];
        public Dictionary<(int, int), int> LipInside { get; } = [];
        public List<(int Start, float Minimum)> CutFaces { get; } = [];
        /// <summary>Where a cut ends inside a triangle: the vertex of that point, by the triangle's three corners and
        /// its weights between them, so it moves with the skin around it.</summary>
        public Dictionary<int, (int A, int B, int C, Vector3 Weights)> Apex { get; } = [];
        /// <summary>Per cut's end, the triangle it ends in drawn two ways (see PlanLayers()): closed, the lips meeting
        /// where the cut crosses the triangle's edge (in Index from Closed on, three triangles), and pointed, the
        /// lips meeting at its apex.</summary>
        public List<(int Apex, int Closed, int[] Pointed)> Ends { get; } = [];
        /// <summary>Per triangle of Index, where its pieces start in RefinedIndex and how many indices they take.
        /// </summary>
        public List<(int Start, int Count)> RefinedOf { get; } = [];
        /// <summary>Per vertex, the vertices it shares a triangle edge with (see SmoothSheet()), made on first use.
        /// </summary>
        public List<int>[]? Neighbours { get; set; }
    }

    private readonly List<MeshInstance3D> _layers = [];
    /// <summary>Per layer: the material of its sheet and of the walls of its cuts.</summary>
    private readonly List<(Material Sheet, Material Walls)> _layerMaterials = [];
    private int _layerVersion = -1;
    private int _layerSteps = -1;
    private bool _rebuiltLast;
    private bool _layersPrepared;
    private bool _regionDirty;
    /// <summary>The skin's height over the site plane, measured on the body model at Heights points per side (see
    /// MeasureSite()).</summary>
    private float[] _heights = [];
    /// <summary>Per measured point: true on the body, false where the site hangs off it or the body is too thin under
    /// it for the layers.</summary>
    private bool[] _onBody = [];
    private float[] _liquidSupport = [];
    /// <summary>The layers' plans (see PlanLayers()) and the topology they were made for.</summary>
    private readonly List<LayerPlan> _plans = [];
    private int _planFor = -1;
    /// <summary>The particles the plans' vertices need.</summary>
    private List<int> _planned = [];
    /// <summary>The planned particles and their neighbours (for normals).</summary>
    private List<int> _around = [];
    // Scratch space, one entry per particle (per spring end for _xmap), reused between rebuilds: the vertex each
    // particle or crossing got in the plan being made (-1: none yet), a mark, the uv of each particle, and where each
    // planned particle's skin lies now and each layer's (one array per layer: the layers are filled at once), which way
    // is out of the skin and its normal.
    private int[] _vmap = [];
    private int[] _xmap = [];
    private bool[] _mark = [];
    private Vector2[] _uvOf = [];
    private Vector3[] _skinOf = [];
    private Vector3[][] _pointOf = [[], [], []];
    private Vector3[] _outward = [];
    private Vector3[] _normal = [];
    /// <summary>
    /// Where each grid point's skin lies on the body model (site space). The layers are drawn from these, not from where
    /// the sim settled (tension pulls the sheet a few millimeters off a round limb): resting skin lies exactly on the
    /// model and shades like it, so the simulated skin shows no step or seam where it takes over from the model.
    /// </summary>
    private Vector3[] _onModel = [];
    /// <summary>How far the model's own smooth normal at each grid point is from the normal the grid's shape gives it.
    /// </summary>
    private Vector3[] _normalFit = [];
    /// <summary>Where the simulated skin replaces the body model, one byte per tissue grid point (see
    /// <see cref="TissueSim.Region"/>).</summary>
    private byte[] _region = [];
    /// <summary>True for region points next to one outside it, where the body model takes over: they're drawn right on
    /// the model.</summary>
    private bool[] _regionEdge = [];
    private Image _regionImage = null!;

    /// <summary>Where the simulated skin replaces the body model, one texel per tissue grid point.</summary>
    public ImageTexture RegionTexture { get; private set; } = null!;

    /// <summary>The skin, fat and muscle meshes.</summary>
    public IReadOnlyList<MeshInstance3D> Layers => _layers;

    private void BuildSite(Color tone, Node3D model) {
        SiteSize = SiteDef.Size;
        _onBack = SiteDef.Back;
        Site = new Node3D { Name = "Site", Position = SiteDef.Position };
        if (_onBack) {
            Site.Rotation = new Vector3(Mathf.Pi, 0, 0);
        }
        _siteBaseY = Site.Position.Y;
        _bodyRoot.AddChild(Site);

        var parts = ModelParts(model);
        var thickness = MeasureSite(parts);
        MeasureLimbBones();
        // Only where the body is thick enough to hold the site's layers: a limb's muscle reaches down to its bones.
        _liquidSupport = new float[thickness.Length];
        for (var n = 0; n < _onBody.Length; n++) {
            _liquidSupport[n] = thickness[n] - Mathf.Max(MinThickness, MuscleBottom);
            _onBody[n] = _liquidSupport[n] > 0f;
        }
        Tissue.Build(SiteSize, SurfaceHeight, OnBody);
        FitToModel(parts);
        _regionImage = Image.CreateEmpty(Tissue.ResX + 1, Tissue.ResY + 1, false, Image.Format.L8);
        RegionTexture = ImageTexture.CreateFromImage(_regionImage);
        var skin = Materials.SkinSite(tone, WoundMap.Texture(WoundMap.Layer.Wounds), WoundMap.Texture(WoundMap.Layer.Fluids));
        skin.SetShaderParameter("seam_map", WoundMap.Texture(WoundMap.Layer.Seams));
        skin.SetShaderParameter("bleed_map", WoundMap.Texture(WoundMap.Layer.Bleeds));
        SkinMaterial = skin;
        string[] names = ["Skin", "Fat", "Muscle"];
        for (var i = 0; i < 3; i++) {
            var layer = new MeshInstance3D { Name = names[i], Mesh = new ArrayMesh() };
            // The walls of a cut through the skin are its cut face, in its tone; fat and muscle walls are fat and muscle.
            var (fluids, bleeds) = (WoundMap.Texture(WoundMap.Layer.Fluids), WoundMap.Texture(WoundMap.Layer.Bleeds));
            var flesh = Materials.TissueLayerMaterial(i > 0 ? i - 1 : 2, fluids, bleeds, tone, cutFace: true);
            var sheet = i == 0 ? skin : Materials.TissueLayerMaterial(i - 1, fluids, bleeds, tone);
            _layerMaterials.Add((sheet, flesh));
            Site.AddChild(layer);
            _layers.Add(layer);
        }
        SiteCollider = Shapes.StaticBox(Site, new Vector3(SiteSize.X, 0.004f, SiteSize.Y), new Vector3(0, -0.002f, 0), SiteLayer);
        BuildCavity();
        Callable.From(UpdateCarve).CallDeferred();
    }

    /// <summary>The body model's skin and gown in site space, as it lies at rest.</summary>
    private List<ModelPart> ModelParts(Node3D model) {
        var parts = new List<ModelPart>();
        foreach (var partName in new[] { "Body", "Gown" }) {
            if (model.FindChild(partName, true, false) is MeshInstance3D mesh) {
                parts.Add(ModelPartOf(mesh));
            }
        }
        return parts;
    }

    /// <summary>A part of the body model in site space, as it lies at rest.</summary>
    private ModelPart ModelPartOf(MeshInstance3D mesh) {
        var part = new ModelPart();
        var toSite = Site.GlobalTransform.AffineInverse() * mesh.GlobalTransform;
        for (var s = 0; s < mesh.Mesh.GetSurfaceCount(); s++) {
            var arrays = mesh.Mesh.SurfaceGetArrays(s);
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            foreach (var i in arrays[(int)Mesh.ArrayType.Index].AsInt32Array()) {
                part.Faces.Add(toSite * vertices[i]);
                part.Normals.Add((toSite.Basis * normals[i]).Normalized());
            }
        }
        part.Mesh.CreateFromFaces([.. part.Faces]);
        return part;
    }

    /// <summary>Where a ray straight down the site's normal at x, z (site space) first meets the body model. Null where
    /// it misses, or goes through a hole in the model (an eye socket the eye fills) and meets the inside of the body.
    /// </summary>
    private static ModelHit? ModelHitAt(List<ModelPart> parts, float x, float z) {
        ModelHit? best = null;
        foreach (var part in parts) {
            var hit = part.Mesh.IntersectRay(new Vector3(x, RayStart, z), Vector3.Down);
            if (hit.Count == 0 || (best is { } found && hit["position"].AsVector3().Y <= found.Position.Y)) {
                continue;
            }
            var at = hit["position"].AsVector3();
            var f = hit["face_index"].AsInt32() * 3;
            var w = Geometry3D.GetTriangleBarycentricCoords(at, part.Faces[f], part.Faces[f + 1], part.Faces[f + 2]);
            var normal = (part.Normals[f] * w.X + part.Normals[f + 1] * w.Y + part.Normals[f + 2] * w.Z).Normalized();
            if (normal.Y > 0f) {
                best = new ModelHit(at, normal, part);
            }
        }
        return best;
    }

    /// <summary>
    /// Measures the skin's height over the site plane on the body model, so the site hugs the body as it is now, and
    /// the body's thickness under each point (0 where the site hangs past it), for which points are off it: there, or
    /// where the body under the skin is too thin to hold skin, fat and muscle (the edge of a limb or the flank),
    /// nothing of the site is drawn, carved or probed. Every peer measures the same model the same way, so every player
    /// sees the same site.
    /// </summary>
    private float[] MeasureSite(List<ModelPart> parts) {
        _heights = new float[Heights * Heights];
        _onBody = new bool[Heights * Heights];
        var thickness = new float[Heights * Heights];
        var missed = new List<int>();
        for (var n = 0; n < Heights * Heights; n++) {
            var uv = new Vector2(n % Heights, n / Heights) / (Heights - 1);
            _heights[n] = float.NaN;
            if (ModelHitAt(parts, (uv.X - 0.5f) * SiteSize.X, (uv.Y - 0.5f) * SiteSize.Y) is not { } hit) {
                missed.Add(n);
                continue;
            }
            _heights[n] = hit.Position.Y;
            // The body's thickness under this point: where the same ray comes out of the part again.
            var exit = hit.Part.Mesh.IntersectRay(hit.Position + Vector3.Down * 0.001f, Vector3.Down);
            thickness[n] = exit.Count > 0 ? hit.Position.Y - exit["position"].AsVector3().Y : 0f;
        }
        // Where nothing was met (past the body's outline, over a hole) the site goes on from the points around it, so
        // heights between points stay smooth up to the edge of the body.
        while (missed.Count > 0 && missed.Count < Heights * Heights) {
            var left = new List<int>();
            var filled = new Dictionary<int, float>();
            foreach (var n in missed) {
                var sum = 0f;
                var found = 0;
                foreach (var step in new[] { Vector2I.Left, Vector2I.Right, Vector2I.Up, Vector2I.Down }) {
                    var at = new Vector2I(n % Heights, n / Heights) + step;
                    if (at.X >= 0 && at.Y >= 0 && at.X < Heights && at.Y < Heights && !float.IsNaN(_heights[at.Y * Heights + at.X])) {
                        sum += _heights[at.Y * Heights + at.X];
                        found++;
                    }
                }
                if (found > 0) {
                    filled[n] = sum / found;
                } else {
                    left.Add(n);
                }
            }
            foreach (var (n, height) in filled) {
                _heights[n] = height;
            }
            missed = left;
        }
        foreach (var n in missed) {
            _heights[n] = 0f;
        }
        return thickness;
    }

    /// <summary>Lays every tissue grid point onto the body model (see _onModel): straight down the site's normal onto
    /// the skin or gown under where the sim settled it, with the model's smooth normal there. Points the ray misses
    /// keep where they settled.</summary>
    private void FitToModel(List<ModelPart> parts) {
        var count = Tissue.Rest.Length;
        _onModel = (Vector3[])Tissue.Settled.Clone();
        var modelNormals = new Vector3[count];
        for (var k = 0; k < count; k++) {
            if (ModelHitAt(parts, Tissue.Settled[k].X, Tissue.Settled[k].Z) is { } hit) {
                _onModel[k] = hit.Position;
                modelNormals[k] = hit.Normal;
            }
        }
        _normalFit = new Vector3[count];
        for (var k = 0; k < count; k++) {
            _normalFit[k] = modelNormals[k] != Vector3.Zero ? modelNormals[k] - GridNormal(_onModel, k) : Vector3.Zero;
        }
    }

    /// <summary>The normal of the grid's surface at point k, from where its neighbours lie in
    /// <paramref name="points"/>.</summary>
    private Vector3 GridNormal(Vector3[] points, int k, bool respectCuts = false) {
        var at = Tissue.CellOf(k);
        var left = Tissue.Index(Math.Max(at.X - 1, 0), at.Y);
        var right = Tissue.Index(Math.Min(at.X + 1, Tissue.ResX), at.Y);
        var above = Tissue.Index(at.X, Math.Max(at.Y - 1, 0));
        var below = Tissue.Index(at.X, Math.Min(at.Y + 1, Tissue.ResY));
        if (respectCuts) {
            bool Joined(int s) => s >= 0 && Tissue.CutDepth(s) == TissueDepth.None;
            if (!Joined(Tissue.SpringRight[left])) { left = k; }
            if (!Joined(Tissue.SpringRight[k])) { right = k; }
            if (!Joined(Tissue.SpringDown[above])) { above = k; }
            if (!Joined(Tissue.SpringDown[k])) { below = k; }
        }
        var dx = points[right] - points[left];
        var dz = points[below] - points[above];
        if (dx.LengthSquared() < 1e-12f || dz.LengthSquared() < 1e-12f) {
            return GridNormal(_onModel, k);
        }
        return dz.Cross(dx).Normalized();
    }

    private int _renderSteps;
    private bool _renderFold;
    private bool _renderFootprint;
    private bool _renderTurnover;
    private Vector3[] _renderMoved = [];
    private readonly Vector3[][] _renderPoints = [[], [], []];
    /// <summary>Positions captured (RebuildLayers()) and waiting to be filled into the layers (BuildLayers()).
    /// </summary>
    private bool _buildPending;
    /// <summary>The layers as filled and waiting to be put on their meshes (PublishLayers()), null for none.</summary>
    private (Surface? Sheet, Surface? Walls)[]? _built;

    /// <summary>
    /// Rebuilds the skin, fat and muscle meshes from the tissue sim, only where the simulated skin replaces the body
    /// (the region). Deeper layers sit lower and follow the skin less.
    /// A layer cut through is split exactly where the blade crossed each spring (<see cref="Spring.Cross"/>), not along
    /// the grid: each side of the cut keeps its part of the triangle and moves with it, so the lips pull apart along
    /// the blade's path and the cut opens from the middle and stays closed at its ends, like a zipper. Walls run down
    /// each lip through the layer's thickness (dermis under the skin, fat, muscle), so the cut has depth.
    /// Which triangles there are and how they split only changes with the cuts and the region (PlanLayers()); while
    /// the skin just moves, only the vertices move.
    /// This frame only captures where the skin is: BuildLayers() fills the meshes from that and PublishLayers() shows
    /// them.
    /// </summary>
    private bool RebuildLayers() {
        if (!_layersPrepared && (UpdateRegion() || _planFor != Tissue.TopologyVersion)) {
            PlanLayers();
            _layersPrepared = true;
            return true;
        }
        _layersPrepared = false;
        _renderSteps = Tissue.StepsDone;
        PlaceParticles();
        _renderFold = Tissue.HoldingFold;
        _renderFootprint = Tissue.HasFoldFootprint;
        _renderTurnover = Tissue.TurningFlap;
        if (_renderMoved.Length != Tissue.Pos.Length) { _renderMoved = new Vector3[Tissue.Pos.Length]; }
        foreach (var k in _around) { _renderMoved[k] = Moved(k); }
        for (var layer = 0; layer < 3; layer++) {
            if (_renderPoints[layer].Length != Tissue.Pos.Length) { _renderPoints[layer] = new Vector3[Tissue.Pos.Length]; }
            foreach (var k in _around) {
                // The layers under the skin move with it where the drape's cloth pushed it (PlaceParticles()):
                // placed from the sim alone they would come out beside or through the skin at the opening's rim.
                // Without a drape (outside the operating room) nothing pushes it.
                var pushed = Tissue.Exposed.Length > k && Tissue.Exposed[k];
                _renderPoints[layer][k] = layer == 0 || !Tissue.NearFold(k) || !pushed
                    ? _skinOf[k]
                    : LayerPoint(layer, k) + (_skinOf[k] - LayerPoint(0, k));
            }
        }
        _buildPending = true;
        return true;
    }

    /// <summary>Fills the captured layers' arrays. It reads the tissue (Turned(), CutDepth()), so it runs right before
    /// the frame's solver step on the same thread, on the worker while the frame draws (StepTissue()).</summary>
    private void BuildLayers() {
        if (!_buildPending) { return; }
        _buildPending = false;
        var built = new (Surface?, Surface?)[3];
        // The skin first: the layers under it are kept under its sheet (UnderSkin()).
        built[0] = FillLayer(0, _plans[0]);
        Parallel.For(1, 3, layer => built[layer] = FillLayer(layer, _plans[layer]));
        _built = built;
    }

    /// <summary>Puts the layers BuildLayers() filled on their meshes: the skin and its tethered bed together, from the
    /// same captured positions.</summary>
    private void PublishLayers() {
        if (_built is not { } built) { return; }
        _built = null;
        _layerVersion = _planFor;
        _layerSteps = _renderSteps;
        for (var layer = 0; layer < 3; layer++) {
            var instance = _layers[layer];
            var mesh = (ArrayMesh)instance.Mesh;
            mesh.ClearSurfaces();
            var plan = _plans[layer];
            instance.Visible = plan.Index.Count > 0 && (layer != 1 || FatThickness > 0.0005f);
            if (!instance.Visible) { continue; }
            var (sheet, walls) = built[layer];
            foreach (var (surface, material) in new[] { (sheet, _layerMaterials[layer].Sheet), (walls, _layerMaterials[layer].Walls) }) {
                if (surface is null) { continue; }
                using var arrays = surface.ToArrays();
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
            }
        }
        if (_regionDirty) {
            // Carve the body only when the replacement meshes are ready, so the planning frame leaves no holes.
            _regionImage.SetData(Tissue.ResX + 1, Tissue.ResY + 1, false, Image.Format.L8,
                [.. _region.Select(texel => (byte)(texel * 255))]);
            RegionTexture.Update(_regionImage);
            _regionDirty = false;
        }
    }

    /// <summary>
    /// Works out, per layer, the vertices (a particle, or where the blade crossed a spring seen from one end), the
    /// triangles between them and the walls down the lips of its cuts. Within a triangle, particles still joined by an
    /// uncut edge stay together; a triangle cut through the layer is drawn as one polygon per side, bounded by where
    /// the blade crossed its edges.
    /// </summary>
    private void PlanLayers() {
        _planFor = Tissue.TopologyVersion;
        var hanging = Tissue.HangingOff();
        var stride = Tissue.ResX + 1;
        var count = Tissue.Rest.Length;
        if (_vmap.Length != count) {
            _uvOf = [.. Enumerable.Range(0, count).Select(Tissue.UvOf)];
            _vmap = new int[count];
            Array.Fill(_vmap, -1);
            _mark = new bool[count];
            _pointOf = [new Vector3[count], new Vector3[count], new Vector3[count]];
            _skinOf = new Vector3[count];
            _outward = new Vector3[count];
            _normal = new Vector3[count];
        }
        if (_xmap.Length < Tissue.SpringCount * 2) {
            var grown = _xmap.Length;
            Array.Resize(ref _xmap, Tissue.SpringCount * 2 + 64);
            Array.Fill(_xmap, -1, grown, _xmap.Length - grown);
        }
        // The triangles to draw: those touching the region, none of whose corners hang off the body. Six entries
        // each: three particles, then the springs of the edges between them in order.
        var marked = new bool[Tissue.ResX * Tissue.ResY];
        var triangles = new List<int>();
        for (var k = 0; k < count; k++) {
            if (_region[k] == 0) {
                continue;
            }
            var at = Tissue.CellOf(k);
            for (var j = Math.Max(at.Y - 1, 0); j <= Math.Min(at.Y, Tissue.ResY - 1); j++) {
                for (var i = Math.Max(at.X - 1, 0); i <= Math.Min(at.X, Tissue.ResX - 1); i++) {
                    var cell = j * Tissue.ResX + i;
                    if (marked[cell]) {
                        continue;
                    }
                    marked[cell] = true;
                    var a = j * stride + i;
                    var c = a + stride;
                    if (!(hanging[a] || hanging[a + 1] || hanging[c]) && _region[a] + _region[a + 1] + _region[c] > 0) {
                        triangles.AddRange([a, a + 1, c, Tissue.SpringRight[a], Tissue.SpringDiag[a], Tissue.SpringDown[a]]);
                    }
                    if (!(hanging[a + 1] || hanging[c + 1] || hanging[c]) && _region[a + 1] + _region[c + 1] + _region[c] > 0) {
                        triangles.AddRange([a + 1, c + 1, c, Tissue.SpringDown[a + 1], Tissue.SpringRight[c], Tissue.SpringDiag[a]]);
                    }
                }
            }
        }
        var cutDepths = new List<TissueDepth>();
        for (var t = 0; t < triangles.Count; t += 6) {
            for (var n = 0; n < 3; n++) {
                cutDepths.Add(Tissue.CutDepth(triangles[t + 3 + n]));
            }
        }
        _plans.Clear();
        for (var layer = 0; layer < 3; layer++) {
            _plans.Add(PlanLayer(layer, triangles, cutDepths));
        }
        // Every particle a vertex needs, the lips' neighbours included, and the grid points around them for normals.
        _planned = [];
        foreach (var plan in _plans) {
            foreach (var k in plan.Owner.Concat(plan.Other).Concat(plan.Slide)) {
                if (k >= 0 && !_mark[k]) {
                    _mark[k] = true;
                    _planned.Add(k);
                }
            }
        }
        _around = [.. _planned];
        foreach (var k in _planned) {
            var at = Tissue.CellOf(k);
            foreach (var n in new[] { new Vector2I(at.X - 1, at.Y), new Vector2I(at.X + 1, at.Y), new Vector2I(at.X, at.Y - 1), new Vector2I(at.X, at.Y + 1) }) {
                if (n.X >= 0 && n.Y >= 0 && n.X <= Tissue.ResX && n.Y <= Tissue.ResY) {
                    var m = Tissue.Index(n.X, n.Y);
                    if (!_mark[m]) {
                        _mark[m] = true;
                        _around.Add(m);
                    }
                }
            }
        }
        foreach (var k in _around) {
            _mark[k] = false;
        }
    }

    /// <summary>One layer's plan (see <see cref="LayerPlan"/>).</summary>
    private LayerPlan PlanLayer(int layer, List<int> triangles, List<TissueDepth> cutDepths) {
        var plan = new LayerPlan();
        var cutAt = LayerDepth[layer];
        var walls = layer != 1 || FatThickness > 0.0005f;
        // Skin taken off leaves a hole in the skin layer only: no sheet there, and no wall on the piece's side of the
        // cut.
        var gone = layer == 0 ? Tissue.Excised : new bool[Tissue.Rest.Length];
        var crossed = new List<int>();
        var touched = new List<int>();
        var corners = new int[3];
        var edges = new int[3];
        var cut = new bool[3];
        var side = new int[3];
        var polygon = new List<int>();
        var crossing = new List<bool>();
        for (var t = 0; t < triangles.Count; t += 6) {
            var anyCut = false;
            for (var n = 0; n < 3; n++) {
                corners[n] = triangles[t + n];
                edges[n] = triangles[t + 3 + n];
                cut[n] = cutDepths[t / 2 + n] >= cutAt;
                anyCut |= cut[n];
                side[n] = n;
            }
            if (anyCut) {
                // Which side of the cut each corner is on: corners joined by an uncut edge share one.
                for (var n = 0; n < 3; n++) {
                    if (!cut[n]) {
                        var from = side[(n + 1) % 3];
                        for (var m = 0; m < 3; m++) {
                            if (side[m] == from) {
                                side[m] = side[n];
                            }
                        }
                    }
                }
            }
            if (anyCut && side[0] == side[1] && side[1] == side[2]) {
                // At a cut's end only one edge is severed: the cut runs into this triangle and stops. Its lips run on
                // from that edge and meet where the blade's path stops, so the opening narrows to a point there: a cut
                // ends in a V, not square across the last spring it cut. The opening there is too narrow to show a
                // wall; one drawn down from lips pulled about would stand up through the skin beside them.
                var edge = Array.FindIndex(cut, value => value);
                if (gone[corners[0]] || gone[corners[1]] || gone[corners[2]]) {
                    continue;
                }
                var a = PlanOwn(plan, corners[edge], touched);
                var b = PlanOwn(plan, corners[(edge + 1) % 3], touched);
                var tip = PlanOwn(plan, corners[(edge + 2) % 3], touched);
                var lipA = PlanCross(plan, edges[edge], corners[edge], crossed, touched);
                var lipB = PlanCross(plan, edges[edge], corners[(edge + 1) % 3], crossed, touched);
                var apex = PlanApex(plan, a, b, tip, lipA, Tissue.SpringAt(edges[edge]).CutDir);
                plan.Ends.Add((apex, plan.Index.Count, [a, lipA, apex, apex, lipB, b, a, apex, tip, apex, b, tip]));
                plan.Index.AddRange([a, lipA, tip, lipA, lipB, tip, lipB, b, tip]);
                continue;
            }
            if (!anyCut) {
                if (gone[corners[0]] || gone[corners[1]] || gone[corners[2]]) {
                    continue;
                }
                for (var n = 0; n < 3; n++) {
                    plan.Index.Add(PlanOwn(plan, corners[n], touched));
                }
                continue;
            }
            for (var group = 0; group < 3; group++) {
                if (side[0] != group && side[1] != group && side[2] != group) {
                    continue;
                }
                if ((side[0] == group && gone[corners[0]]) || (side[1] == group && gone[corners[1]]) || (side[2] == group && gone[corners[2]])) {
                    continue;
                }
                // Around the triangle's edge in its own order, so every polygon faces the way the triangle does.
                polygon.Clear();
                crossing.Clear();
                var mine = -1;
                for (var n = 0; n < 3; n++) {
                    var here = side[n] == group;
                    if (here) {
                        mine = PlanOwn(plan, corners[n], touched);
                        polygon.Add(mine);
                        crossing.Add(false);
                    }
                    if (cut[n] && here != (side[(n + 1) % 3] == group)) {
                        var k = here ? corners[n] : corners[(n + 1) % 3];
                        polygon.Add(PlanCross(plan, edges[n], k, crossed, touched));
                        crossing.Add(true);
                    }
                }
                for (var n = 1; n < polygon.Count - 1; n++) {
                    plan.Index.AddRange([polygon[0], polygon[n], polygon[n + 1]]);
                }
                if (!walls) {
                    continue;
                }
                // Each stretch of the polygon's edge between two crossings is a lip of the cut: a wall goes down from it.
                for (var n = 0; n < polygon.Count; n++) {
                    var next = (n + 1) % polygon.Count;
                    if (crossing[n] && crossing[next]) {
                        plan.Wall.AddRange([polygon[n], polygon[next], mine]);
                    }
                }
            }
        }
        foreach (var k in touched) {
            _vmap[k] = -1;
        }
        foreach (var key in crossed) {
            _xmap[key] = -1;
        }
        for (var w = 0; w < plan.Wall.Count; w += 3) {
            var a = plan.Wall[w];
            var b = plan.Wall[w + 1];
            plan.LipInside[(Math.Min(a, b), Math.Max(a, b))] = plan.Wall[w + 2];
            foreach (var (here, next) in new[] { (a, b), (b, a) }) {
                if (!plan.Lip.TryGetValue(here, out var neighbours)) {
                    neighbours = [];
                    plan.Lip[here] = neighbours;
                }
                if (!neighbours.Contains(next)) {
                    neighbours.Add(next);
                }
            }
        }
        var crossingOf = new Dictionary<(int, int), int>();
        for (var t = 0; t < plan.Index.Count; t += 3) {
            var a = plan.Index[t];
            var b = plan.Index[t + 1];
            var c = plan.Index[t + 2];
            if (plan.Other[a] >= 0 || plan.Other[b] >= 0 || plan.Other[c] >= 0) {
                var ab = plan.Uv[b] - plan.Uv[a];
                var ac = plan.Uv[c] - plan.Uv[a];
                plan.CutFaces.Add((t, (ac.Y * ab.X - ac.X * ab.Y) * SiteSize.X * SiteSize.Y * 0.02f));
            }
            SetLipInside(plan, a, b, c);
            SetLipInside(plan, b, c, a);
            SetLipInside(plan, c, a, b);
        }
        for (var v = 0; v < plan.Owner.Count; v++) {
            if (plan.Other[v] >= 0) { crossingOf[(plan.Owner[v], plan.Other[v])] = v; }
        }
        foreach (var (ends, v) in crossingOf) {
            if (crossingOf.TryGetValue((ends.Item2, ends.Item1), out var opposite)) {
                plan.OppositeLip[v] = opposite;
            }
        }
        // Refine only faces bordering a cut. Interior skin keeps its original triangles, avoiding extra mesh
        // uploads while sewing a long incision. Curved lip midpoints are shared by the sheet and its wall.
        var midpointOf = plan.MidpointOf;
        int Midpoint(int a, int b) {
            var key = (Math.Min(a, b), Math.Max(a, b));
            if (!midpointOf.TryGetValue(key, out var v)) {
                v = plan.Owner.Count + plan.Midpoints.Count;
                midpointOf[key] = v;
                plan.Midpoints.Add((a, b));
            }
            return v;
        }
        foreach (var edge in plan.LipInside.Keys) {
            Midpoint(edge.Item1, edge.Item2);
        }
        var boundary = new List<int>(6);
        for (var t = 0; t < plan.Index.Count; t += 3) {
            var a = plan.Index[t];
            var b = plan.Index[t + 1];
            var c = plan.Index[t + 2];
            var refinedFrom = plan.RefinedIndex.Count;
            if (!midpointOf.ContainsKey((Math.Min(a, b), Math.Max(a, b)))
                && !midpointOf.ContainsKey((Math.Min(b, c), Math.Max(b, c)))
                && !midpointOf.ContainsKey((Math.Min(c, a), Math.Max(c, a)))) {
                plan.RefinedIndex.Add(a);
                plan.RefinedIndex.Add(b);
                plan.RefinedIndex.Add(c);
                plan.RefinedOf.Add((refinedFrom, 3));
                continue;
            }
            boundary.Clear();
            foreach (var (start, end) in new[] { (a, b), (b, c), (c, a) }) {
                boundary.Add(start);
                if (midpointOf.TryGetValue((Math.Min(start, end), Math.Max(start, end)), out var mid)) {
                    boundary.Add(mid);
                }
            }
            var center = plan.Owner.Count + plan.Midpoints.Count + plan.Centers.Count;
            plan.Centers.Add((a, b, c));
            for (var n = 0; n < boundary.Count; n++) {
                plan.RefinedIndex.AddRange([center, boundary[n], boundary[(n + 1) % boundary.Count]]);
            }
            plan.RefinedOf.Add((refinedFrom, plan.RefinedIndex.Count - refinedFrom));
        }
        return plan;
    }

    private static void SetLipInside(LayerPlan plan, int a, int b, int inside) {
        var key = (Math.Min(a, b), Math.Max(a, b));
        if (plan.LipInside.ContainsKey(key)) { plan.LipInside[key] = inside; }
    }

    /// <summary>
    /// The vertex where a cut that crossed edge ab of triangle (a, b, c) at <paramref name="entry"/> (uv) stops inside
    /// it (TissueSim.CutEndIn()), on along the blade's path from the edge. At least a quarter of the way across the
    /// triangle, so a blade that stopped right past the edge (or started right at it) still ends its cut in a point,
    /// not square across the edge. A tear, with no blade path, runs half way across along
    /// <paramref name="cutDir"/>.
    /// </summary>
    private int PlanApex(LayerPlan plan, int a, int b, int c, int lipA, Vector2 cutDir) {
        var (ua, ub, uc) = (plan.Uv[a], plan.Uv[b], plan.Uv[c]);
        var entry = plan.Uv[lipA];
        var traced = Tissue.CutEndIn(ua, ub, uc, entry);
        // In uv, toward c's side of the edge.
        var along = traced is { } end && end.DistanceSquaredTo(entry) > 1e-10f
            ? (end - entry).Normalized()
            : new Vector2(cutDir.X / SiteSize.X, cutDir.Y / SiteSize.Y).Normalized();
        var edge = ub - ua;
        if (Mathf.Sign(edge.Cross(along)) != Mathf.Sign(edge.Cross(uc - ua))) {
            along = -along;
        }
        var across = AcrossTriangle(entry, along, ua, ub, uc);
        var reach = traced is { } stop ? Mathf.Max(stop.DistanceTo(entry), across * MinApexShare) : across * 0.5f;
        var at = entry + along * Mathf.Min(reach, across * 0.9f);
        var weights = Barycentric(at, ua, ub, uc);
        var v = plan.Owner.Count;
        plan.Owner.Add(plan.Owner[c]);
        plan.Other.Add(-1);
        plan.Share.Add(0f);
        plan.Slide.Add(-1);
        plan.Uv.Add(at);
        plan.Apex[v] = (a, b, c, weights);
        return v;
    }

    /// <summary>Least share of the way across its triangle a cut's end lies, see PlanApex().</summary>
    private const float MinApexShare = 0.25f;

    /// <summary>How far (uv) from <paramref name="from"/> on edge ab the triangle (a, b, c) reaches along
    /// <paramref name="along"/>.</summary>
    private static float AcrossTriangle(Vector2 from, Vector2 along, Vector2 a, Vector2 b, Vector2 c) {
        var nearest = float.PositiveInfinity;
        foreach (var (p, q) in new[] { (b, c), (c, a) }) {
            var side = q - p;
            var det = along.Cross(side);
            if (Mathf.Abs(det) < 1e-12f) {
                continue;
            }
            var t = (p - from).Cross(side) / det;
            var u = (p - from).Cross(along) / det;
            if (t > 0f && u >= -0.001f && u <= 1.001f) {
                nearest = Mathf.Min(nearest, t);
            }
        }
        return float.IsPositiveInfinity(nearest) ? 0f : nearest;
    }

    /// <summary>Weights of p between a, b and c (uv).</summary>
    private static Vector3 Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c) {
        var area = (b - a).Cross(c - a);
        var wb = (p - a).Cross(c - a) / area;
        var wc = (b - a).Cross(p - a) / area;
        return new Vector3(1f - wb - wc, wb, wc);
    }

    /// <summary>The vertex of particle k in the plan, added the first time it's used.</summary>
    private int PlanOwn(LayerPlan plan, int k, List<int> touched) {
        if (_vmap[k] < 0) {
            _vmap[k] = plan.Owner.Count;
            touched.Add(k);
            plan.Owner.Add(k);
            plan.Other.Add(-1);
            plan.Share.Add(0f);
            plan.Slide.Add(-1);
            plan.Uv.Add(_uvOf[k]);
        }
        return _vmap[k];
    }

    /// <summary>
    /// The vertex where the blade crossed spring s, on the side of its end k, added the first time it's used. It lies
    /// as far from k as it did at rest, so each lip moves with its own side. A diagonal spring's far end lies a cell
    /// along the cut from k: there the lip moves like k's neighbour that way (if they're still joined), so the lip
    /// doesn't step from cell to cell. A crossing at its owner coincides with that particle, so it shares its vertex
    /// rather than leaving a sliver.
    /// </summary>
    private int PlanCross(LayerPlan plan, int s, int k, List<int> crossed, List<int> touched) {
        ref readonly var spring = ref Tissue.SpringAt(s);
        var atStart = spring.A == k;
        if ((atStart && spring.Cross == 0f) || (!atStart && spring.Cross == 1f)) {
            return PlanOwn(plan, k, touched);
        }
        var key = s * 2 + (atStart ? 0 : 1);
        if (_xmap[key] < 0) {
            _xmap[key] = plan.Owner.Count;
            crossed.Add(key);
            var other = atStart ? spring.B : spring.A;
            var slide = -1;
            var cell = Tissue.CellOf(k);
            var step = Tissue.CellOf(other) - cell;
            step = Mathf.Abs(spring.CutDir.X) >= Mathf.Abs(spring.CutDir.Y) ? new Vector2I(step.X, 0) : new Vector2I(0, step.Y);
            if (step != Vector2I.Zero) {
                var m = Tissue.Index(cell.X + step.X, cell.Y + step.Y);
                var low = Math.Min(k, m);
                var joined = step.X != 0 ? Tissue.SpringRight[low] : Tissue.SpringDown[low];
                if (joined >= 0 && Tissue.SpringAt(joined).Active) {
                    slide = m;
                }
            }
            plan.Owner.Add(k);
            plan.Other.Add(other);
            plan.Share.Add(atStart ? spring.Cross : 1f - spring.Cross);
            plan.Slide.Add(slide);
            plan.Uv.Add(_uvOf[spring.A].Lerp(_uvOf[spring.B], spring.Cross));
        }
        return _xmap[key];
    }

    /// <summary>Where the skin of every planned particle is now, its normal, and which way is out of it: straight up
    /// where the skin is in place, along its own normal on a flap pulled far, so a flap folded over shows its fat on
    /// top instead of drawing it under the skin, through the drape.</summary>
    private void PlaceParticles() {
        foreach (var k in _around) {
            _skinOf[k] = LayerPoint(0, k);
            _outward[k] = Vector3.Up;
        }
        ResolveClothContact();
        foreach (var k in _planned) {
            var distance = Moved(k).Length();
            var moved = Mathf.Clamp(distance / FlapMove, 0f, 1f);
            // The model's own normal where the skin rests, turning with the skin as it moves (a flap keeps its own).
            var normal = (GridNormal(_skinOf, k, respectCuts: true) + _normalFit[k] * (1f - moved)).Normalized();
            _normal[k] = normal;
            // The layers under skin that has moved lie along its normal: offset straight down from a wave's steep
            // flank, the fat would come out through the skin of the next row down it. Skin lying where it rests
            // keeps the plain vertical offset, so its small creases don't swing a thick layer about.
            var turned = Tissue.TurningFlap
                ? Mathf.SmoothStep(TissueSim.TurnoverReach * 0.75f, TissueSim.TurnoverReach * 1.25f, distance)
                : 0f;
            var lean = Mathf.Max(turned, moved);
            _outward[k] = lean > 0f ? Vector3.Up.Slerp(normal, lean).Normalized() : Vector3.Up;
        }
    }

    /// <summary>Resolve contact on moving faces, using the cloth directly at nearby samples rather than a
    /// raised envelope spanning unrelated vertices. Corrections stay on the three corners of the contact face.</summary>
    private void ResolveClothContact() {
        if (Tissue.FloorAt is null || !Tissue.HasFoldFootprint) { return; }
        for (var pass = 0; pass < 2; pass++) {
            foreach (var k in _planned) {
                var at = Tissue.CellOf(k);
                if (at.X >= Tissue.ResX || at.Y >= Tissue.ResY) { continue; }
                var c = k + Tissue.ResX + 1;
                Contact(k, k + 1, c);
                Contact(k + 1, c + 1, c);
            }
        }
        void Contact(int a, int b, int c) {
            if (!Tissue.Exposed[a] || !Tissue.Exposed[b] || !Tissue.Exposed[c]) { return; }
            if (Tissue.FloorOpen.HasPoint(new Vector2(_skinOf[a].X, _skinOf[a].Z))
                && Tissue.FloorOpen.HasPoint(new Vector2(_skinOf[b].X, _skinOf[b].Z))
                && Tissue.FloorOpen.HasPoint(new Vector2(_skinOf[c].X, _skinOf[c].Z))) { return; }
            // Only a face with a corner the sim lays on the cloth or its skirt: one a grip only slid a few millimeters
            // past the opening's edge stays on the body under the drape's rim. Pushed up over the cloth, it would stand
            // up there as a peak.
            if (!Tissue.OnClothOrSkirt(a) && !Tissue.OnClothOrSkirt(b) && !Tissue.OnClothOrSkirt(c)) { return; }
            _clothFaces.PushTriangle(_skinOf, a, b, c, 0.004f, above: true);
        }
    }

    /// <summary>Rows down each wall of a cut: enough for its rounded profile (<see cref="WallBulge"/>).</summary>
    internal const int WallRows = 4;
    /// <summary>Per layer, how far (shares of its thickness) its cut face bulges into the cut halfway down: soft fat
    /// swells out, muscle a little, the skin's cut face stays flat.</summary>
    private static readonly float[] WallBulge = [0f, 0.25f, 0.08f];
    /// <summary>At most how far (shares of the gap from its lip across to the other lip) a wall bulges into the cut: a
    /// limb's fat and muscle are thick and its cuts narrow, and bulging by their thickness alone the walls of both
    /// sides would meet and close the cut over what lies in it.</summary>
    private const float WallBulgeOfGap = 0.1f;

    /// <summary>One layer's sheet and the walls of its cuts, where the skin was captured (null for no triangles).
    /// </summary>
    private (Surface? Sheet, Surface? Walls) FillLayer(int layer, LayerPlan plan) {
        var depth = LayerTop(layer);
        var thickness = layer switch { 0 => SkinThickness, 1 => FatThickness, _ => MuscleThickness };
        // A flap folded back takes the skin, the fat and the fascia over the muscle, not a limb's muscle down to its
        // bones: that stays as the wound's bed, and walls that deep would stick out of the folded flap's edges. Skin
        // pulled aside less than its anchors give (a retracted edge) keeps its muscle.
        float WallThickness(int k) {
            if (layer < 2) {
                return thickness;
            }
            var folded = Mathf.Max(TurnedAmount(k, _renderMoved[k], _renderTurnover),
                Mathf.SmoothStep(TissueSim.AnchorReach * 0.5f, TissueSim.AnchorReach, _renderMoved[k].Length()));
            return Mathf.Lerp(thickness, Mathf.Min(DefaultMuscle, thickness), folded);
        }
        var pointOf = _pointOf[layer];
        foreach (var k in _around) {
            pointOf[k] = _renderPoints[layer][k] - Outward(layer, k) * depth;
        }
        var vertexCount = plan.Owner.Count;
        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        for (var v = 0; v < vertexCount; v++) {
            var k = plan.Owner[v];
            var p = pointOf[k];
            if (plan.Other[v] >= 0) {
                var offset = _onModel[plan.Other[v]] - _onModel[k];
                if (_renderTurnover) {
                    // Pulled into a fold or turned over, the lip turns and squashes with its own side: at its rest
                    // offset it would reach across the cut into the other side. Lying still it keeps its rest offset,
                    // which draws a straight lip through the grid's cells where the sheet's small deformations would
                    // step it.
                    p += DeformedLipOffset(layer, k, offset * plan.Share[v]);
                } else {
                    if (plan.Slide[v] >= 0) {
                        var m = plan.Slide[v];
                        offset += (pointOf[m] - _onModel[m]) - (p - _onModel[k]);
                    }
                    p += offset * plan.Share[v];
                }
            }
            vertices[v] = p;
            normals[v] = _normal[k];
        }
        foreach (var (v, (a, b, c, w)) in plan.Apex) {
            vertices[v] = vertices[a] * w.X + vertices[b] * w.Y + vertices[c] * w.Z;
        }
        SmoothSheet(layer, plan, vertices);
        // Each vertex's uv as drawn: a lip's eased along with it.
        var uvs = plan.Uv.ToArray();
        SmoothLips(layer, plan, vertices, uvs);
        ConstrainLipArea(plan, vertices);
        if (layer != 0) {
            for (var v = 0; v < vertexCount; v++) {
                vertices[v] = UnderSkin(vertices[v], plan.Owner[v]);
            }
        }
        var faceNormals = new Vector3[vertexCount];
        for (var t = 0; t < plan.Index.Count; t += 3) {
            var a = plan.Index[t];
            var b = plan.Index[t + 1];
            var c = plan.Index[t + 2];
            var face = (vertices[c] - vertices[a]).Cross(vertices[b] - vertices[a]);
            faceNormals[a] += face;
            faceNormals[b] += face;
            faceNormals[c] += face;
        }
        for (var v = 0; v < vertexCount; v++) {
            // Keep the model's shading where the site meets it; use the actual sheet on lifted skin and lips.
            var moved = Mathf.Clamp(_renderMoved[plan.Owner[v]].Length() / FlapMove, 0f, 1f);
            if (!faceNormals[v].IsZeroApprox()) {
                normals[v] = normals[v].Lerp(faceNormals[v].Normalized(), plan.Other[v] >= 0 || plan.Apex.ContainsKey(v) ? 1f : moved).Normalized();
            }
        }
        var refinedVertices = new Vector3[vertexCount + plan.Midpoints.Count + plan.Centers.Count];
        var refinedNormals = new Vector3[refinedVertices.Length];
        var refinedUvs = new Vector2[refinedVertices.Length];
        vertices.CopyTo(refinedVertices, 0);
        normals.CopyTo(refinedNormals, 0);
        uvs.CopyTo(refinedUvs, 0);
        for (var n = 0; n < plan.Midpoints.Count; n++) {
            var (a, b) = plan.Midpoints[n];
            refinedVertices[vertexCount + n] = LipMidpoint(plan, vertices, a, b);
            refinedNormals[vertexCount + n] = (normals[a] + normals[b]).Normalized();
            refinedUvs[vertexCount + n] = MidpointUv(vertices[a], vertices[b], refinedVertices[vertexCount + n], uvs[a], uvs[b]);
        }
        for (var n = 0; n < plan.Centers.Count; n++) {
            var (a, b, c) = plan.Centers[n];
            var v = vertexCount + plan.Midpoints.Count + n;
            refinedVertices[v] = (vertices[a] + vertices[b] + vertices[c]) / 3f;
            refinedNormals[v] = (normals[a] + normals[b] + normals[c]).Normalized();
            refinedUvs[v] = (uvs[a] + uvs[b] + uvs[c]) / 3f;
        }
        // The midpoints and centers belong to the grid point of their first corner.
        var refinedOwner = new int[refinedVertices.Length];
        plan.Owner.CopyTo(refinedOwner);
        for (var n = 0; n < plan.Midpoints.Count; n++) {
            refinedOwner[vertexCount + n] = plan.Owner[plan.Midpoints[n].A];
        }
        for (var n = 0; n < plan.Centers.Count; n++) {
            refinedOwner[vertexCount + plan.Midpoints.Count + n] = plan.Owner[plan.Centers[n].A];
        }
        // Each cut's end pointed where the skin around it rests, closed where it was pulled, spread or folded about
        // (or anywhere while a grip folds the skin): drawn that far from its rest shape, the end's triangle has no
        // room left for the point.
        var pointedFrom = new HashSet<int>();
        foreach (var (apex, closed, _) in _renderFold || _renderFootprint || _renderTurnover ? [] : plan.Ends) {
            var (a, b, c, _) = plan.Apex[apex];
            var moved = Mathf.Max(_renderMoved[plan.Owner[a]].LengthSquared(),
                Mathf.Max(_renderMoved[plan.Owner[b]].LengthSquared(), _renderMoved[plan.Owner[c]].LengthSquared()));
            if (moved <= TissueSim.LiftedOff * TissueSim.LiftedOff) {
                pointedFrom.Add(closed);
            }
        }
        var index = plan.RefinedIndex;
        if (pointedFrom.Count > 0) {
            index = new List<int>(plan.RefinedIndex.Count + pointedFrom.Count * 3);
            for (var t = 0; t < plan.RefinedOf.Count; t++) {
                // The closed end's three triangles start at an index pointedFrom holds.
                var end = pointedFrom.Contains(t * 3) || pointedFrom.Contains(t * 3 - 3) || pointedFrom.Contains(t * 3 - 6);
                if (!end) {
                    var (start, count) = plan.RefinedOf[t];
                    index.AddRange(plan.RefinedIndex.GetRange(start, count));
                }
            }
            foreach (var (_, closed, pointed) in plan.Ends) {
                if (pointedFrom.Contains(closed)) {
                    index.AddRange(pointed);
                }
            }
        }
        if (layer == 0) {
            // As drawn, curved lips included: what lies under the skin is kept under these triangles.
            RememberSkinSheet(refinedVertices, index);
        } else {
            for (var v = vertexCount; v < refinedVertices.Length; v++) {
                refinedVertices[v] = UnderSkin(refinedVertices[v], refinedOwner[v]);
            }
        }
        var wallVertices = new List<Vector3>();
        var wallNormals = new List<Vector3>();
        var wallUvs = new List<Vector2>();
        var wallColors = new List<Color>();
        // How deep under the skin each wall corner is, for bleed spots.
        var wallDepths = new List<Vector2>();
        var wallIndices = new List<int>();
        // Each lip vertex's wall normal, averaged over the walls on both sides of it: a wall shaded flat stretch by
        // stretch reads as a row of cards standing in the cut.
        var lipNormals = new Dictionary<int, Vector3>();
        Vector3 WallNormal(int w) {
            var (a, b) = (plan.Wall[w], plan.Wall[w + 1]);
            var bottom = vertices[a] - Outward(layer, plan.Owner[a]) * WallThickness(plan.Owner[a]);
            var normal = (vertices[b] - vertices[a]).Cross(bottom - vertices[a]).Normalized();
            // Facing into the cut, away from this side's own skin.
            return normal.Dot(vertices[a] - vertices[plan.Wall[w + 2]]) < 0f ? -normal : normal;
        }
        for (var w = 0; w < plan.Wall.Count; w += 3) {
            var normal = WallNormal(w);
            foreach (var v in (int[])[plan.Wall[w], plan.Wall[w + 1]]) {
                lipNormals[v] = lipNormals.GetValueOrDefault(v) + normal;
            }
        }
        // Each lip vertex's way down and depth, eased along the lip like the lip itself: they follow its own grid
        // point, and neighbours along a lip pulled about ride different ones, so the walls' bottoms would run in steps.
        // Depth only eases shallower: a folded flap's walls stay as shallow as its fascia (WallThickness()).
        var lipDown = new Dictionary<int, (Vector3 Out, float Thick)>();
        foreach (var v in plan.Lip.Keys) {
            lipDown[v] = (Outward(layer, plan.Owner[v]), WallThickness(plan.Owner[v]));
        }
        for (var pass = 0; pass < LipSmoothing * 2; pass++) {
            var eased = new Dictionary<int, (Vector3 Out, float Thick)>();
            foreach (var (v, neighbours) in plan.Lip) {
                if (neighbours.Count == 2) {
                    var (out0, thick0) = lipDown[neighbours[0]];
                    var (out1, thick1) = lipDown[neighbours[1]];
                    var (outV, thickV) = lipDown[v];
                    eased[v] = ((outV * 2f + out0 + out1).Normalized(), Mathf.Min(thickV, (thickV * 2f + thick0 + thick1) * 0.25f));
                }
            }
            foreach (var (v, down) in eased) {
                lipDown[v] = down;
            }
        }
        var bulge = WallBulge[layer];
        // How far a wall down from lip vertex v bulges halfway down: by its thickness, but only a small share of the
        // cut's gape there, so the walls never close it (a lip with nothing across from it is a cut's closed end).
        float Swell(int v, float thick) => Mathf.Min(bulge * thick,
            plan.OppositeLip.TryGetValue(v, out var opposite) ? vertices[v].DistanceTo(vertices[opposite]) * WallBulgeOfGap : 0f);
        for (var w = 0; w < plan.Wall.Count; w += 3) {
            var (a, b) = (plan.Wall[w], plan.Wall[w + 1]);
            var topA = vertices[a];
            var topB = vertices[b];
            var (outA, thickA) = lipDown[a];
            var (outB, thickB) = lipDown[b];
            var normalA = lipNormals[a].Normalized();
            var normalB = lipNormals[b].Normalized();
            var mid = plan.MidpointOf.TryGetValue((Math.Min(a, b), Math.Max(a, b)), out var refined)
                ? refinedVertices[refined]
                : LipMidpoint(plan, vertices, a, b);
            var midOut = (outA + outB).Normalized();
            var midNormal = (normalA + normalB).Normalized();
            var midThick = (thickA + thickB) * 0.5f;
            var midUv = MidpointUv(topA, topB, mid, uvs[a], uvs[b]);
            var (swellA, swellB) = (Swell(a, thickA), Swell(b, thickB));
            // Three columns along the lip (its two ends and its midpoint), each a soft rounded profile down the wall.
            (Vector3 Top, Vector3 Out, Vector3 Normal, float Thick, float Swell, Vector2 Uv, int Owner)[] columns = [
                (topA, outA, normalA, thickA, swellA, uvs[a], plan.Owner[a]),
                (mid, midOut, midNormal, midThick, (swellA + swellB) * 0.5f, midUv, plan.Owner[a]),
                (topB, outB, normalB, thickB, swellB, uvs[b], plan.Owner[b])];
            var first = wallVertices.Count;
            foreach (var (top, outward, normal, thick, most, uv, owner) in columns) {
                for (var row = 0; row <= WallRows; row++) {
                    var t = (float)row / WallRows;
                    // The tissue bulges into the cut halfway down, and its normal tilts up above that and down below.
                    var swell = most * Mathf.Sin(Mathf.Pi * t);
                    var slope = thick > 0f ? most / thick * Mathf.Pi * Mathf.Cos(Mathf.Pi * t) : 0f;
                    wallVertices.Add(UnderSkin(top - outward * (thick * t) + normal * swell, owner));
                    wallNormals.Add((normal + outward * slope).Normalized());
                    wallUvs.Add(uv);
                    wallColors.Add(new Color(t, t, t));
                    wallDepths.Add(new Vector2(depth + thick * t, 0f));
                }
            }
            for (var column = 0; column < 2; column++) {
                for (var row = 0; row < WallRows; row++) {
                    var start = first + column * (WallRows + 1) + row;
                    var (end, lowStart, lowEnd) = (start + WallRows + 1, start + 1, start + WallRows + 2);
                    var normal = wallNormals[start];
                    // Godot's front faces wind clockwise seen from the side the normal points to.
                    if ((wallVertices[lowEnd] - wallVertices[start]).Cross(wallVertices[end] - wallVertices[start]).Dot(normal) > 0f) {
                        wallIndices.AddRange([start, end, lowEnd, start, lowEnd, lowStart]);
                    } else {
                        wallIndices.AddRange([start, lowEnd, end, start, lowStart, lowEnd]);
                    }
                }
            }
        }
        return (
            Surface.Of(refinedVertices, refinedNormals, refinedUvs, [.. index]),
            Surface.Of([.. wallVertices], [.. wallNormals], [.. wallUvs], [.. wallIndices], [.. wallColors], [.. wallDepths]));
    }

    /// <summary>The skin's sheet as last filled (FillLayer()), its triangles bucketed by site x and z, for
    /// UnderSkin(). The layers under the skin are filled after it from the same captured positions.</summary>
    private Vector3[] _skinSheet = [];
    private List<int> _skinSheetIndex = [];
    private readonly Dictionary<Vector2I, List<int>> _skinBuckets = [];
    private const float SkinBucket = 0.01f;
    /// <summary>How far (meters) what lies under the skin stays below the skin drawn over it.</summary>
    private const float UnderSkinAbove = 0.0003f;

    private void RememberSkinSheet(Vector3[] vertices, List<int> index) {
        _skinSheet = vertices;
        _skinSheetIndex = index;
        foreach (var bucket in _skinBuckets.Values) {
            bucket.Clear();
        }
        for (var t = 0; t < index.Count; t += 3) {
            var (a, b, c) = (vertices[index[t]], vertices[index[t + 1]], vertices[index[t + 2]]);
            var low = SkinCell(a.Min(b).Min(c));
            var high = SkinCell(a.Max(b).Max(c));
            for (var x = low.X; x <= high.X; x++) {
                for (var z = low.Y; z <= high.Y; z++) {
                    if (!_skinBuckets.TryGetValue(new Vector2I(x, z), out var bucket)) {
                        _skinBuckets[new Vector2I(x, z)] = bucket = [];
                    }
                    bucket.Add(t);
                }
            }
        }
    }

    private static Vector2I SkinCell(Vector3 at) =>
        new(Mathf.FloorToInt(at.X / SkinBucket), Mathf.FloorToInt(at.Z / SkinBucket));

    /// <summary>
    /// <paramref name="point"/> of a layer under the skin, owned by grid point <paramref name="k"/>, kept below the
    /// skin drawn over it: skin crumpled up (pushed along by a spreader's jaw, say) or a lip lifted over the skin beside
    /// it would otherwise have the layers drawn at a fixed depth under them come up through that skin. A flap turned
    /// over is left alone: its fat is drawn on top of it.
    /// </summary>
    private Vector3 UnderSkin(Vector3 point, int k) {
        if (_renderTurnover || Tissue.Turned(k) || !_skinBuckets.TryGetValue(SkinCell(point), out var bucket)) {
            return point;
        }
        var ceiling = float.NaN;
        foreach (var t in bucket) {
            var (a, b, c) = (_skinSheet[_skinSheetIndex[t]], _skinSheet[_skinSheetIndex[t + 1]], _skinSheet[_skinSheetIndex[t + 2]]);
            var area = (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
            if (Mathf.Abs(area) < 1e-12f) {
                continue;
            }
            var wb = ((point.X - a.X) * (c.Z - a.Z) - (point.Z - a.Z) * (c.X - a.X)) / area;
            var wc = ((b.X - a.X) * (point.Z - a.Z) - (b.Z - a.Z) * (point.X - a.X)) / area;
            var wa = 1f - wb - wc;
            if (wa < -0.001f || wb < -0.001f || wc < -0.001f) {
                continue;
            }
            var height = a.Y * wa + b.Y * wb + c.Y * wc;
            if (float.IsNaN(ceiling) || height > ceiling) {
                ceiling = height;
            }
        }
        if (!float.IsNaN(ceiling) && point.Y > ceiling - UnderSkinAbove) {
            point.Y = ceiling - UnderSkinAbove;
        }
        return point;
    }

    /// <summary>Which way down from the skin a layer's grid point k lies: along the skin's own normal where it has
    /// moved (_outward, PlaceParticles()) for a layer that moves with the skin, straight down for a tethered layer
    /// that stayed on its bed under the skin's wave, unless a flap is turning right over and takes everything.
    /// </summary>
    private Vector3 Outward(int layer, int k) =>
        float.IsPositiveInfinity(LayerTether[layer]) || _renderTurnover ? _outward[k] : Vector3.Up;

    private readonly ContactSurface _clothFaces = new();

    /// <summary>Passes of <see cref="SmoothLips"/>.</summary>
    private const int LipSmoothing = 2;
    /// <summary>Per layer: the layers are filled at once, on their own threads.</summary>
    private readonly Dictionary<int, (Vector3 Vertex, Vector2 Uv)>[] _easedLips = [[], [], []];

    /// <summary>
    /// Each crossing on a lip rides the grid point on its side of the cut, and neighbours along the lip ride different
    /// grid points: pulled or lifted unevenly, the lip zigzags and the walls down from it stand in the cut like a row
    /// of cards. Eased toward its neighbours along the lip, it runs smooth; a lip's ends stay where they are. Its uv
    /// is eased with it: easing slides a crossing along the lip too, and with the uv of where it was cut the wound
    /// map's bleed spots on its walls would stretch into ovals where crossings spread and squeeze where they bunch.
    /// </summary>
    private void SmoothLips(int layer, LayerPlan plan, Vector3[] vertices, Vector2[] uvs) {
        var easedLips = _easedLips[layer];
        for (var pass = 0; pass < LipSmoothing; pass++) {
            easedLips.Clear();
            foreach (var (v, neighbours) in plan.Lip) {
                if (neighbours.Count == 2) {
                    var (n0, n1) = (neighbours[0], neighbours[1]);
                    easedLips[v] = (vertices[v].Lerp((vertices[n0] + vertices[n1]) * 0.5f, 0.5f), uvs[v].Lerp((uvs[n0] + uvs[n1]) * 0.5f, 0.5f));
                }
            }
            foreach (var (v, (vertex, uv)) in easedLips) {
                vertices[v] = vertex;
                uvs[v] = uv;
            }
        }
    }

    /// <summary>Passes of <see cref="SmoothSheet"/>.</summary>
    private const int SheetSmoothing = 10;
    /// <summary>Per layer, like <see cref="_easedLips"/>.</summary>
    private readonly Vector3[][] _easedSheet = [[], [], []];

    /// <summary>
    /// A tethered layer beside a cut goes with the skin's slide over it (LayerPoint()), wrinkles and all, while its
    /// height stays down on its bed: drawn as is, the muscle under a lip pulled aside creases cell by cell and its cut
    /// edge runs in steps. Where the skin over it moved, each of its grid points is eased toward its neighbours on
    /// its own side of the cut; the lips are left to <see cref="SmoothLips"/>.
    /// </summary>
    private void SmoothSheet(int layer, LayerPlan plan, Vector3[] vertices) {
        if (float.IsPositiveInfinity(LayerTether[layer]) || _renderTurnover) {
            return;
        }
        var neighbours = plan.Neighbours ??= SheetNeighbours(plan);
        var eased = _easedSheet[layer];
        if (eased.Length < vertices.Length) {
            eased = _easedSheet[layer] = new Vector3[vertices.Length];
        }
        for (var pass = 0; pass < SheetSmoothing; pass++) {
            for (var v = 0; v < vertices.Length; v++) {
                eased[v] = vertices[v];
                var k = plan.Owner[v];
                var cell = Tissue.CellOf(k);
                if (plan.Other[v] >= 0 || plan.Apex.ContainsKey(v) || neighbours[v].Count == 0
                    || cell.X == 0 || cell.Y == 0 || cell.X == Tissue.ResX || cell.Y == Tissue.ResY) {
                    continue;
                }
                var sum = Vector3.Zero;
                foreach (var n in neighbours[v]) {
                    sum += vertices[n];
                }
                var weight = 0.5f * Mathf.Clamp(_renderMoved[k].Length() / FlapMove, 0f, 1f);
                eased[v] = vertices[v].Lerp(sum / neighbours[v].Count, weight);
            }
            Array.Copy(eased, vertices, vertices.Length);
        }
    }

    private static List<int>[] SheetNeighbours(LayerPlan plan) {
        var neighbours = new List<int>[plan.Owner.Count];
        for (var v = 0; v < neighbours.Length; v++) {
            neighbours[v] = [];
        }
        for (var t = 0; t < plan.Index.Count; t += 3) {
            for (var n = 0; n < 3; n++) {
                var (a, b) = (plan.Index[t + n], plan.Index[t + ((n + 1) % 3)]);
                if (!neighbours[a].Contains(b)) { neighbours[a].Add(b); }
                if (!neighbours[b].Contains(a)) { neighbours[b].Add(a); }
            }
        }
        return neighbours;
    }

    /// <summary>The uv of a lip edge's midpoint <paramref name="mid"/> (LipMidpoint()), between its ends' as far along
    /// the edge as its bend put it: halfway, a bend along the lip would stretch the wound map's bleed spots on the walls
    /// on one side of it and squeeze them on the other, like a lip eased without its uv (SmoothLips()).</summary>
    private static Vector2 MidpointUv(Vector3 a, Vector3 b, Vector3 mid, Vector2 uvA, Vector2 uvB) {
        var edge = b - a;
        var length = edge.LengthSquared();
        return length > 1e-12f ? uvA.Lerp(uvB, Mathf.Clamp((mid - a).Dot(edge) / length, 0f, 1f)) : uvA.Lerp(uvB, 0.5f);
    }

    /// <summary>Virtual lips have no simulation particles of their own. Keep their faces oriented with the sheet
    /// during a wave, so the cut boundary cannot fold back through the thicker tissue underneath it.</summary>
    private void ConstrainLipArea(LayerPlan plan, Vector3[] vertices) {
        if (!_renderFold) { return; }
        for (var pass = 0; pass < 8; pass++) {
            var changed = false;
            for (var n = 0; n < plan.CutFaces.Count; n++) {
                var (t, minimum) = plan.CutFaces[pass % 2 == 0 ? n : plan.CutFaces.Count - 1 - n];
                var a = plan.Index[t];
                var b = plan.Index[t + 1];
                var c = plan.Index[t + 2];
                var face = (vertices[c] - vertices[a]).Cross(vertices[b] - vertices[a]);
                var area = face.Length();
                if (area >= minimum) { continue; }
                var normal = area > 1e-9f ? face / area : Vector3.Up;
                var ga = (vertices[c] - vertices[b]).Cross(normal);
                var gb = normal.Cross(vertices[c] - vertices[a]);
                var gc = (vertices[b] - vertices[a]).Cross(normal);
                var wa = plan.Other[a] >= 0 ? 1f : 0f;
                var wb = plan.Other[b] >= 0 ? 1f : 0f;
                var wc = plan.Other[c] >= 0 ? 1f : 0f;
                var weight = ga.LengthSquared() * wa + gb.LengthSquared() * wb + gc.LengthSquared() * wc;
                if (weight < 1e-12f) { continue; }
                var correction = (minimum - area) / weight;
                vertices[a] += ga * (correction * wa);
                vertices[b] += gb * (correction * wb);
                vertices[c] += gc * (correction * wc);
                changed = true;
            }
            if (!changed) { break; }
        }
    }

    /// <summary>A blade crossing rides the deformation of its own side, including rotation and compression. A
    /// fixed rest-space offset would keep pointing through the skin when its owner turns into a large fold.</summary>
    private Vector3 DeformedLipOffset(int layer, int k, Vector3 offset) {
        var at = Tissue.CellOf(k);
        var left = Tissue.Index(Math.Max(at.X - 1, 0), at.Y);
        var right = Tissue.Index(Math.Min(at.X + 1, Tissue.ResX), at.Y);
        var above = Tissue.Index(at.X, Math.Max(at.Y - 1, 0));
        var below = Tissue.Index(at.X, Math.Min(at.Y + 1, Tissue.ResY));
        bool Joined(int s) => s >= 0 && Tissue.CutDepth(s) < LayerDepth[layer];
        if (!Joined(Tissue.SpringRight[left])) { left = k; }
        if (!Joined(Tissue.SpringRight[k])) { right = k; }
        if (!Joined(Tissue.SpringDown[above])) { above = k; }
        if (!Joined(Tissue.SpringDown[k])) { below = k; }
        var dx = _onModel[right] - _onModel[left];
        var dz = _onModel[below] - _onModel[above];
        var det = dx.X * dz.Z - dx.Z * dz.X;
        if (Mathf.Abs(det) < 1e-10f) { return offset; }
        var u = (offset.X * dz.Z - offset.Z * dz.X) / det;
        var v = (offset.Z * dx.X - offset.X * dx.Z) / det;
        var changeX = _pointOf[layer][right] - _pointOf[layer][left] - dx;
        var changeZ = _pointOf[layer][below] - _pointOf[layer][above] - dz;
        var transformed = offset + changeX * u + changeZ * v;
        // A lip is extrapolated beyond its owner's last intact cell. Under compression its reach must shrink
        // with that cell, or it can extend back through the folded skin a few rows ahead of it.
        var axisX = Vector3.Right + changeX * (dz.Z / det) - changeZ * (dx.Z / det);
        var axisZ = Vector3.Back - changeX * (dz.X / det) + changeZ * (dx.X / det);
        var xx = axisX.LengthSquared();
        var zz = axisZ.LengthSquared();
        var xz = axisX.Dot(axisZ);
        var leastStretch = Mathf.Sqrt(Mathf.Max(0f, (xx + zz - Mathf.Sqrt((xx - zz) * (xx - zz) + 4f * xz * xz)) * 0.5f));
        return _renderTurnover ? transformed : transformed.LimitLength(offset.Length() * Mathf.Min(1f, leastStretch));
    }

    /// <summary>The midpoint of a lip segment, using its same-side neighbours as cubic tangents. Interior edges
    /// stay straight. Limit the curve's deviation so a short segment at a cut junction cannot overshoot into skin.</summary>
    private static Vector3 LipMidpoint(LayerPlan plan, Vector3[] vertices, int a, int b) {
        var mid = (vertices[a] + vertices[b]) * 0.5f;
        if (!plan.Lip.TryGetValue(a, out var aroundA) || !aroundA.Contains(b) || aroundA.Count != 2
            || !plan.Lip.TryGetValue(b, out var aroundB) || aroundB.Count != 2) {
            return mid;
        }
        var before = vertices[aroundA[0] == b ? aroundA[1] : aroundA[0]];
        var after = vertices[aroundB[0] == a ? aroundB[1] : aroundB[0]];
        var bend = (vertices[a] + vertices[b] - before - after) / 16f;
        var edge = vertices[b] - vertices[a];
        var limit = edge.Length() * 0.25f;
        if (plan.LipInside.TryGetValue((Math.Min(a, b), Math.Max(a, b)), out var inside)) {
            var toInside = vertices[inside] - mid;
            limit = Mathf.Min(limit, toInside.Slide(edge.Normalized()).Length() * 0.2f);
            // Round within this side's sheet, rather than lifting a midpoint through an adjoining face.
            bend = bend.Slide(edge.Cross(vertices[inside] - mid).Normalized());
        }
        foreach (var v in new[] { a, b }) {
            if (plan.OppositeLip.TryGetValue(v, out var opposite)) {
                var gap = vertices[v] - vertices[opposite];
                limit = Mathf.Min(limit, gap.Length() * 0.2f);
            }
        }
        return mid + bend.LimitLength(limit);
    }

    /// <summary>One surface's arrays, filled on any thread and wrapped for Godot only when published (ToArrays(), which
    /// the caller disposes once the surface is added, see <see cref="Shapes.SetPacked{T}"/>).</summary>
    private sealed record Surface(Vector3[] Vertices, Vector3[] Normals, Vector2[] Uvs, int[] Indices, Color[]? Colors,
        Vector2[]? Uvs2) {
        /// <summary>Null without any triangles: checked here, as reading the indices back out of the arrays would copy
        /// them.</summary>
        public static Surface? Of(Vector3[] vertices, Vector3[] normals, Vector2[] uvs, int[] indices,
            Color[]? colors = null, Vector2[]? uvs2 = null) =>
            indices.Length == 0 ? null : new(vertices, normals, uvs, indices, colors, uvs2);

        public Godot.Collections.Array ToArrays() {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            Shapes.SetPacked(arrays, Mesh.ArrayType.Vertex, Vertices);
            Shapes.SetPacked(arrays, Mesh.ArrayType.Normal, Normals);
            Shapes.SetPacked(arrays, Mesh.ArrayType.TexUV, Uvs);
            Shapes.SetPacked(arrays, Mesh.ArrayType.Index, Indices);
            if (Colors is not null) {
                Shapes.SetPacked(arrays, Mesh.ArrayType.Color, Colors);
            }
            if (Uvs2 is not null) {
                Shapes.SetPacked(arrays, Mesh.ArrayType.TexUV2, Uvs2);
            }
            return arrays;
        }
    }

    /// <summary>
    /// Where a layer's grid point k is now, before it's moved down to its depth: on the body model where the skin
    /// rests, moved as far as the sim moved it (Moved()). The skin and its fat move as one. The muscle slides along
    /// only as far as its tether lets it (LayerTether) and stays down on its bed while the skin lifts off it, so a
    /// pull on whole skin slides the skin over it. Beside a cut through it the muscle is cut free and goes with the
    /// skin however far (TissueSim.Freed()): the wound's edge moves as one block of skin, fat and muscle along the
    /// body. Lifted, the skin and its fat peel off the muscle: it rises with them only <see cref="MuscleLift"/>, so a
    /// wound's edge held up shows the muscle's cut edge under it. A flap turned right over takes everything.
    /// </summary>
    public Vector3 LayerPoint(int layer, int k) {
        var moved = Moved(k);
        var tethered = !float.IsPositiveInfinity(LayerTether[layer]);
        var turned = tethered ? TurnedAmount(k, moved, Tissue.TurningFlap) : 1f;
        var freed = tethered ? Mathf.Max(Tissue.Freed(LayerDepth[layer], k), turned) : 1f;
        var lift = new Vector3(0f, moved.Y, 0f);
        var slide = moved - lift;
        var tether = freed >= 0.999f ? float.PositiveInfinity : LayerTether[layer] / (1f - freed);
        var follow = Mathf.Min(1f, tether / Mathf.Max(slide.Length(), 0.00001f));
        var rise = moved.Y * Mathf.Max(freed, follow);
        var point = _onModel[k] + slide * follow + Vector3.Up * Mathf.Lerp(Mathf.Min(rise, MuscleLift), rise, turned);
        // Skin folded out over the drape lies on the cloth where it is now, not at the body's height under its old
        // spot, and the layers that come with it lie on it too; skin under the drape stays under it. A tethered
        // layer that stayed behind under exposed skin is left where it is: it's under the skin, whatever lies over.
        if (Tissue.FloorAt is not null && (moved.LengthSquared() > 0.000001f || Tissue.FoldContact(k))) {
            var floorY = Tissue.FloorAt(point.X, point.Z);
            if (!float.IsNaN(floorY)) {
                if (!Tissue.Exposed[k]) {
                    point.Y = Mathf.Min(point.Y, floorY - TissueSim.UnderDrape);
                } else if ((layer == 0 || follow >= 0.999f) && Tissue.OnClothOrSkirt(k)) {
                    // Only where the sim lays it on the cloth: exposed skin a grip slid a few millimeters past the
                    // opening's edge stays on the body under the drape's rim, it isn't drawn up onto the cloth.
                    point.Y = Mathf.Max(point.Y, floorY);
                }
            }
        }
        return point;
    }

    /// <summary>How far grid point k is part of a flap turned over (0..1), <paramref name="moved"/> as far as it has:
    /// all the way once turned, right to its hinge, and kept once the hands have let go of it; while one is turning,
    /// by how far toward turned over it has gone.</summary>
    private float TurnedAmount(int k, Vector3 moved, bool turning) =>
        Tissue.Turned(k) ? 1f : turning ? Mathf.Clamp(moved.Length() / TissueSim.TurnoverReach, 0f, 1f) : 0f;

    /// <summary>
    /// How far the sim moved grid point k since it settled, less any way back toward the body model: tension holds the
    /// sheet off a curved body, and where a cut lets go of it the skin springs back, to where it's drawn already.
    /// None on the region's edge, where the body model next to it would show a step, unless it moved further than
    /// EdgeHold (a flap folded back).
    /// </summary>
    private Vector3 Moved(int k) {
        var moved = Tissue.Pos[k] - Tissue.Settled[k];
        if (k < _regionEdge.Length && _regionEdge[k] && moved.Length() <= EdgeHold) {
            return Vector3.Zero;
        }
        var fit = _onModel[k] - Tissue.Settled[k];
        if (fit.IsZeroApprox()) {
            return moved;
        }
        var back = fit.Normalized();
        return moved - back * Mathf.Clamp(moved.Dot(back), 0f, fit.Length());
    }

    /// <summary>Takes the tissue's current region. True when it changed.</summary>
    private bool UpdateRegion() {
        var region = Tissue.Region();
        if (region.AsSpan().SequenceEqual(_region)) {
            return false;
        }
        _region = region;
        _regionEdge = new bool[region.Length];
        var last = new Vector2I(Tissue.ResX, Tissue.ResY);
        for (var k = 0; k < region.Length; k++) {
            if (region[k] == 1) {
                var at = Tissue.CellOf(k);
                foreach (var step in new[] { Vector2I.Left, Vector2I.Right, Vector2I.Up, Vector2I.Down }) {
                    var n = (at + step).Clamp(Vector2I.Zero, last);
                    if (region[Tissue.Index(n.X, n.Y)] == 0) {
                        _regionEdge[k] = true;
                    }
                }
            }
        }
        _regionDirty = true;
        return true;
    }
}
