namespace Scalpel.Patients;

/// <summary>The site's tissue layers: measuring the site on the body model and drawing skin, fat and muscle from the
/// tissue sim.</summary>
public partial class PatientBody
{
    /// <summary>How much each layer follows the skin's movement (deeper layers are more tethered).</summary>
    private static readonly float[] LayerFollow = [1f, 0.65f, 0.2f];
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
    private sealed class ModelPart
    {
        public TriangleMesh Mesh { get; } = new();
        public List<Vector3> Faces { get; } = [];
        public List<Vector3> Normals { get; } = [];
    }

    /// <summary>Where a ray down the site's normal meets the body model: its position, the model's smooth normal there
    /// and the part it hit.</summary>
    private readonly record struct ModelHit(Vector3 Position, Vector3 Normal, ModelPart Part);

    /// <summary>
    /// How one layer's mesh is put together (see PlanLayers()): per vertex the particle it belongs to (owner), and for
    /// a crossing the spring's other end and how far along it the blade crossed (share); its uv; the triangle
    /// indices; and per wall quad its two top vertices and a vertex of
    /// its own side's skin, to face it away from.
    /// </summary>
    private sealed class LayerPlan
    {
        public List<int> Owner { get; } = [];
        public List<int> Other { get; } = [];
        public List<float> Share { get; } = [];
        public List<Vector2> Uv { get; } = [];
        public List<int> Index { get; } = [];
        public List<int> Wall { get; } = [];
        /// <summary>Neighbours along a lip, kept separate from the other side of the opening.</summary>
        public Dictionary<int, List<int>> Lip { get; } = [];
        public List<(int A, int B)> Midpoints { get; } = [];
        public List<int> RefinedIndex { get; } = [];
        public List<(int A, int B, int C)> Centers { get; } = [];
        public Dictionary<int, int> OppositeLip { get; } = [];
        public Dictionary<(int, int), int> LipInside { get; } = [];
        public List<(int Start, float Minimum)> CutFaces { get; } = [];
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
    /// <summary>The layers' plans (see PlanLayers()) and the topology they were made for.</summary>
    private readonly List<LayerPlan> _plans = [];
    private int _planFor = -1;
    /// <summary>The particles the plans' vertices need.</summary>
    private List<int> _planned = [];
    /// <summary>The planned particles and their neighbours (for normals).</summary>
    private List<int> _around = [];
    // Scratch space, one entry per particle (per spring end for _xmap), reused between rebuilds: the vertex each
    // particle or crossing got in the plan being made (-1: none yet), a mark, the uv of each particle, and where each
    // planned particle's skin and layer lie now, which way is out of the skin and its normal.
    private int[] _vmap = [];
    private int[] _xmap = [];
    private bool[] _mark = [];
    private Vector2[] _uvOf = [];
    private Vector3[] _skinOf = [];
    private Vector3[] _pointOf = [];
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

    private void BuildSite(Color tone, Node3D model)
    {
        SiteSize = SiteDef.Size;
        _onBack = SiteDef.Back;
        Site = new Node3D { Name = "Site", Position = SiteDef.Position };
        if (_onBack)
        {
            Site.Rotation = new Vector3(Mathf.Pi, 0, 0);
        }
        _siteBaseY = Site.Position.Y;
        _bodyRoot.AddChild(Site);

        var parts = ModelParts(model);
        MeasureSite(parts);
        Tissue.Build(SiteSize, SurfaceHeight, OnBody);
        FitToModel(parts);
        _regionImage = Image.CreateEmpty(Tissue.ResX + 1, Tissue.ResY + 1, false, Image.Format.L8);
        RegionTexture = ImageTexture.CreateFromImage(_regionImage);
        var skin = Materials.SkinSite(tone, WoundMap.Texture(WoundMap.Layer.Wounds), WoundMap.Texture(WoundMap.Layer.Fluids));
        skin.SetShaderParameter("seam_map", WoundMap.Texture(WoundMap.Layer.Seams));
        SkinMaterial = skin;
        string[] names = ["Skin", "Fat", "Muscle"];
        for (var i = 0; i < 3; i++)
        {
            var layer = new MeshInstance3D { Name = names[i], Mesh = new ArrayMesh() };
            // The walls of a cut through the skin are its cut face, in its tone; fat and muscle walls are fat and muscle.
            var flesh = Materials.TissueLayerMaterial(i > 0 ? i - 1 : 2, WoundMap.Texture(WoundMap.Layer.Fluids), tone,
                cutFace: true);
            var sheet = i == 0 ? skin : Materials.TissueLayerMaterial(i - 1, WoundMap.Texture(WoundMap.Layer.Fluids), tone);
            _layerMaterials.Add((sheet, flesh));
            Site.AddChild(layer);
            _layers.Add(layer);
        }
        SiteCollider = Shapes.StaticBox(Site, new Vector3(SiteSize.X, 0.004f, SiteSize.Y), new Vector3(0, -0.002f, 0), SiteLayer);
        BuildCavity();
        Callable.From(UpdateCarve).CallDeferred();
    }

    /// <summary>The body model's skin and gown in site space, as it lies at rest.</summary>
    private List<ModelPart> ModelParts(Node3D model)
    {
        var parts = new List<ModelPart>();
        foreach (var partName in new[] { "Body", "Gown" })
        {
            if (model.FindChild(partName, true, false) is not MeshInstance3D mesh)
            {
                continue;
            }
            var part = new ModelPart();
            var toSite = Site.GlobalTransform.AffineInverse() * mesh.GlobalTransform;
            for (var s = 0; s < mesh.Mesh.GetSurfaceCount(); s++)
            {
                var arrays = mesh.Mesh.SurfaceGetArrays(s);
                var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                var normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                foreach (var i in arrays[(int)Mesh.ArrayType.Index].AsInt32Array())
                {
                    part.Faces.Add(toSite * vertices[i]);
                    part.Normals.Add((toSite.Basis * normals[i]).Normalized());
                }
            }
            part.Mesh.CreateFromFaces([.. part.Faces]);
            parts.Add(part);
        }
        return parts;
    }

    /// <summary>Where a ray straight down the site's normal at x, z (site space) first meets the body model. Null where
    /// it misses, or goes through a hole in the model (an eye socket the eye fills) and meets the inside of the body.
    /// </summary>
    private static ModelHit? ModelHitAt(List<ModelPart> parts, float x, float z)
    {
        ModelHit? best = null;
        foreach (var part in parts)
        {
            var hit = part.Mesh.IntersectRay(new Vector3(x, RayStart, z), Vector3.Down);
            if (hit.Count == 0 || (best is { } found && hit["position"].AsVector3().Y <= found.Position.Y))
            {
                continue;
            }
            var at = hit["position"].AsVector3();
            var f = hit["face_index"].AsInt32() * 3;
            var w = Geometry3D.GetTriangleBarycentricCoords(at, part.Faces[f], part.Faces[f + 1], part.Faces[f + 2]);
            var normal = (part.Normals[f] * w.X + part.Normals[f + 1] * w.Y + part.Normals[f + 2] * w.Z).Normalized();
            if (normal.Y > 0f)
            {
                best = new ModelHit(at, normal, part);
            }
        }
        return best;
    }

    /// <summary>
    /// Measures the skin's height over the site plane on the body model, so the site hugs the body as it is now, and
    /// which points are off it: where the site hangs past the body, or the body under the skin is too thin to hold
    /// skin, fat and muscle (the edge of a limb or the flank). Nothing of the site is drawn, carved or probed there.
    /// Every peer measures the same model the same way, so every player sees the same site.
    /// </summary>
    private void MeasureSite(List<ModelPart> parts)
    {
        _heights = new float[Heights * Heights];
        _onBody = new bool[Heights * Heights];
        var missed = new List<int>();
        for (var n = 0; n < Heights * Heights; n++)
        {
            var uv = new Vector2(n % Heights, n / Heights) / (Heights - 1);
            _heights[n] = float.NaN;
            if (ModelHitAt(parts, (uv.X - 0.5f) * SiteSize.X, (uv.Y - 0.5f) * SiteSize.Y) is not { } hit)
            {
                missed.Add(n);
                continue;
            }
            _heights[n] = hit.Position.Y;
            // The body's thickness under this point: where the same ray comes out of the part again.
            var exit = hit.Part.Mesh.IntersectRay(hit.Position + Vector3.Down * 0.001f, Vector3.Down);
            _onBody[n] = exit.Count > 0 && hit.Position.Y - exit["position"].AsVector3().Y > MinThickness;
        }
        // Where nothing was met (past the body's outline, over a hole) the site goes on from the points around it, so
        // heights between points stay smooth up to the edge of the body.
        while (missed.Count > 0 && missed.Count < Heights * Heights)
        {
            var left = new List<int>();
            var filled = new Dictionary<int, float>();
            foreach (var n in missed)
            {
                var sum = 0f;
                var found = 0;
                foreach (var step in new[] { Vector2I.Left, Vector2I.Right, Vector2I.Up, Vector2I.Down })
                {
                    var at = new Vector2I(n % Heights, n / Heights) + step;
                    if (at.X >= 0 && at.Y >= 0 && at.X < Heights && at.Y < Heights && !float.IsNaN(_heights[at.Y * Heights + at.X]))
                    {
                        sum += _heights[at.Y * Heights + at.X];
                        found++;
                    }
                }
                if (found > 0)
                {
                    filled[n] = sum / found;
                }
                else
                {
                    left.Add(n);
                }
            }
            foreach (var (n, height) in filled)
            {
                _heights[n] = height;
            }
            missed = left;
        }
        foreach (var n in missed)
        {
            _heights[n] = 0f;
        }
    }

    /// <summary>Lays every tissue grid point onto the body model (see _onModel): straight down the site's normal onto
    /// the skin or gown under where the sim settled it, with the model's smooth normal there. Points the ray misses
    /// keep where they settled.</summary>
    private void FitToModel(List<ModelPart> parts)
    {
        var count = Tissue.Rest.Length;
        _onModel = (Vector3[])Tissue.Settled.Clone();
        var modelNormals = new Vector3[count];
        for (var k = 0; k < count; k++)
        {
            if (ModelHitAt(parts, Tissue.Settled[k].X, Tissue.Settled[k].Z) is { } hit)
            {
                _onModel[k] = hit.Position;
                modelNormals[k] = hit.Normal;
            }
        }
        _normalFit = new Vector3[count];
        for (var k = 0; k < count; k++)
        {
            _normalFit[k] = modelNormals[k] != Vector3.Zero ? modelNormals[k] - GridNormal(_onModel, k) : Vector3.Zero;
        }
    }

    /// <summary>The normal of the grid's surface at point k, from where its neighbours lie in
    /// <paramref name="points"/>.</summary>
    private Vector3 GridNormal(Vector3[] points, int k, bool respectCuts = false)
    {
        var at = Tissue.CellOf(k);
        var left = Tissue.Index(Math.Max(at.X - 1, 0), at.Y);
        var right = Tissue.Index(Math.Min(at.X + 1, Tissue.ResX), at.Y);
        var above = Tissue.Index(at.X, Math.Max(at.Y - 1, 0));
        var below = Tissue.Index(at.X, Math.Min(at.Y + 1, Tissue.ResY));
        if (respectCuts)
        {
            bool Joined(int s) => s >= 0 && Tissue.CutDepth(s) == TissueDepth.None;
            if (!Joined(Tissue.SpringRight[left])) { left = k; }
            if (!Joined(Tissue.SpringRight[k])) { right = k; }
            if (!Joined(Tissue.SpringDown[above])) { above = k; }
            if (!Joined(Tissue.SpringDown[k])) { below = k; }
        }
        var dx = points[right] - points[left];
        var dz = points[below] - points[above];
        if (dx.LengthSquared() < 1e-12f || dz.LengthSquared() < 1e-12f)
        {
            return GridNormal(_onModel, k);
        }
        return dz.Cross(dx).Normalized();
    }

    /// <summary>
    /// Rebuilds the skin, fat and muscle meshes from the tissue sim, only where the simulated skin replaces the body
    /// (the region). Deeper layers sit lower and follow the skin less.
    /// A layer cut through is split exactly where the blade crossed each spring (<see cref="Spring.Cross"/>), not along
    /// the grid: each side of the cut keeps its part of the triangle and moves with it, so the lips pull apart along
    /// the blade's path and the cut opens from the middle and stays closed at its ends, like a zipper. Walls run down
    /// each lip through the layer's thickness (dermis under the skin, fat, muscle), so the cut has depth.
    /// Which triangles there are and how they split only changes with the cuts and the region (PlanLayers()); while
    /// the skin just moves, only the vertices move.
    /// </summary>
    private int _renderStage;
    private int _renderSteps;
    private bool _renderFold;
    private bool _renderTurnover;
    private Vector3[] _renderMoved = [];
    private readonly Vector3[][] _renderPoints = [[], [], []];
    private readonly (Godot.Collections.Array? Sheet, Godot.Collections.Array? Walls)[] _renderArrays = new (Godot.Collections.Array?, Godot.Collections.Array?)[3];

    private bool RebuildLayers()
    {
        if (_planFor != Tissue.TopologyVersion) { _renderStage = 0; }
        if (_renderStage == 0 && !_layersPrepared && (UpdateRegion() || _planFor != Tissue.TopologyVersion))
        {
            PlanLayers();
            _layersPrepared = true;
            return true;
        }
        _layersPrepared = false;
        var staged = Tissue.HasFoldFootprint;
        if (_renderStage == 0)
        {
            _renderSteps = Tissue.StepsDone;
            PlaceParticles();
            _renderFold = Tissue.HoldingFold;
            _renderTurnover = Tissue.TurningFlap;
            if (_renderMoved.Length != Tissue.Pos.Length) { _renderMoved = new Vector3[Tissue.Pos.Length]; }
            foreach (var k in _around) { _renderMoved[k] = Moved(k); }
            for (var layer = 0; layer < 3; layer++)
            {
                if (_renderPoints[layer].Length != Tissue.Pos.Length) { _renderPoints[layer] = new Vector3[Tissue.Pos.Length]; }
                foreach (var k in _around)
                {
                    _renderPoints[layer][k] = layer == 0 || !Tissue.NearFold(k) || !Tissue.Exposed[k] ? _skinOf[k] : LayerPoint(layer, k);
                }
            }
        }
        var first = staged && _renderStage > 0 ? 2 : 0;
        var last = staged && _renderStage == 0 ? 2 : 3;
        if (staged && _renderStage == 2) { first = last; }
        for (var layer = first; layer < last; layer++)
        {
            _renderArrays[layer] = FillLayer(layer, _plans[layer]);
        }
        if (staged && ++_renderStage < 3) { return true; }
        _renderStage = 0;
        _layerVersion = _planFor;
        _layerSteps = _renderSteps;
        // Publish the skin and its tethered bed together from the same captured positions.
        for (var layer = 0; layer < 3; layer++)
        {
            var instance = _layers[layer];
            var mesh = (ArrayMesh)instance.Mesh;
            mesh.ClearSurfaces();
            var plan = _plans[layer];
            instance.Visible = plan.Index.Count > 0 && (layer != 1 || FatThickness > 0.0005f);
            if (!instance.Visible) { continue; }
            var (sheet, walls) = _renderArrays[layer];
            foreach (var (arrays, material) in new[] { (sheet, _layerMaterials[layer].Sheet), (walls, _layerMaterials[layer].Walls) })
            {
                if (arrays is null) { continue; }
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
            }
        }
        if (_regionDirty)
        {
            // Carve the body only when the replacement meshes are ready, so the planning frame leaves no holes.
            _regionImage.SetData(Tissue.ResX + 1, Tissue.ResY + 1, false, Image.Format.L8,
                [.. _region.Select(texel => (byte)(texel * 255))]);
            RegionTexture.Update(_regionImage);
            _regionDirty = false;
        }
        return true;
    }

    /// <summary>
    /// Works out, per layer, the vertices (a particle, or where the blade crossed a spring seen from one end), the
    /// triangles between them and the walls down the lips of its cuts. Within a triangle, particles still joined by an
    /// uncut edge stay together; a triangle cut through the layer is drawn as one polygon per side, bounded by where
    /// the blade crossed its edges.
    /// </summary>
    private void PlanLayers()
    {
        _planFor = Tissue.TopologyVersion;
        var hanging = Tissue.HangingOff();
        var stride = Tissue.ResX + 1;
        var count = Tissue.Rest.Length;
        if (_vmap.Length != count)
        {
            _uvOf = [.. Enumerable.Range(0, count).Select(Tissue.UvOf)];
            _vmap = new int[count];
            Array.Fill(_vmap, -1);
            _mark = new bool[count];
            _pointOf = new Vector3[count];
            _skinOf = new Vector3[count];
            _outward = new Vector3[count];
            _normal = new Vector3[count];
        }
        if (_xmap.Length < Tissue.SpringCount * 2)
        {
            var grown = _xmap.Length;
            Array.Resize(ref _xmap, Tissue.SpringCount * 2 + 64);
            Array.Fill(_xmap, -1, grown, _xmap.Length - grown);
        }
        // The triangles to draw: those touching the region, none of whose corners hang off the body. Six entries
        // each: three particles, then the springs of the edges between them in order.
        var marked = new bool[Tissue.ResX * Tissue.ResY];
        var triangles = new List<int>();
        for (var k = 0; k < count; k++)
        {
            if (_region[k] == 0)
            {
                continue;
            }
            var at = Tissue.CellOf(k);
            for (var j = Math.Max(at.Y - 1, 0); j <= Math.Min(at.Y, Tissue.ResY - 1); j++)
            {
                for (var i = Math.Max(at.X - 1, 0); i <= Math.Min(at.X, Tissue.ResX - 1); i++)
                {
                    var cell = j * Tissue.ResX + i;
                    if (marked[cell])
                    {
                        continue;
                    }
                    marked[cell] = true;
                    var a = j * stride + i;
                    var c = a + stride;
                    if (!(hanging[a] || hanging[a + 1] || hanging[c]) && _region[a] + _region[a + 1] + _region[c] > 0)
                    {
                        triangles.AddRange([a, a + 1, c, Tissue.SpringRight[a], Tissue.SpringDiag[a], Tissue.SpringDown[a]]);
                    }
                    if (!(hanging[a + 1] || hanging[c + 1] || hanging[c]) && _region[a + 1] + _region[c + 1] + _region[c] > 0)
                    {
                        triangles.AddRange([a + 1, c + 1, c, Tissue.SpringDown[a + 1], Tissue.SpringRight[c], Tissue.SpringDiag[a]]);
                    }
                }
            }
        }
        var cutDepths = new List<TissueDepth>();
        for (var t = 0; t < triangles.Count; t += 6)
        {
            for (var n = 0; n < 3; n++)
            {
                cutDepths.Add(Tissue.CutDepth(triangles[t + 3 + n]));
            }
        }
        _plans.Clear();
        for (var layer = 0; layer < 3; layer++)
        {
            _plans.Add(PlanLayer(layer, triangles, cutDepths));
        }
        // Every particle a vertex needs, the lips' neighbours included, and the grid points around them for normals.
        _planned = [];
        foreach (var plan in _plans)
        {
            foreach (var k in plan.Owner.Concat(plan.Other))
            {
                if (k >= 0 && !_mark[k])
                {
                    _mark[k] = true;
                    _planned.Add(k);
                }
            }
        }
        _around = [.. _planned];
        foreach (var k in _planned)
        {
            var at = Tissue.CellOf(k);
            foreach (var n in new[] { new Vector2I(at.X - 1, at.Y), new Vector2I(at.X + 1, at.Y), new Vector2I(at.X, at.Y - 1), new Vector2I(at.X, at.Y + 1) })
            {
                if (n.X >= 0 && n.Y >= 0 && n.X <= Tissue.ResX && n.Y <= Tissue.ResY)
                {
                    var m = Tissue.Index(n.X, n.Y);
                    if (!_mark[m])
                    {
                        _mark[m] = true;
                        _around.Add(m);
                    }
                }
            }
        }
        foreach (var k in _around)
        {
            _mark[k] = false;
        }
    }

    /// <summary>One layer's plan (see <see cref="LayerPlan"/>).</summary>
    private LayerPlan PlanLayer(int layer, List<int> triangles, List<TissueDepth> cutDepths)
    {
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
        for (var t = 0; t < triangles.Count; t += 6)
        {
            var anyCut = false;
            for (var n = 0; n < 3; n++)
            {
                corners[n] = triangles[t + n];
                edges[n] = triangles[t + 3 + n];
                cut[n] = cutDepths[t / 2 + n] >= cutAt;
                anyCut |= cut[n];
                side[n] = n;
            }
            if (anyCut)
            {
                // Which side of the cut each corner is on: corners joined by an uncut edge share one.
                for (var n = 0; n < 3; n++)
                {
                    if (!cut[n])
                    {
                        var from = side[(n + 1) % 3];
                        for (var m = 0; m < 3; m++)
                        {
                            if (side[m] == from)
                            {
                                side[m] = side[n];
                            }
                        }
                    }
                }
            }
            if (anyCut && side[0] == side[1] && side[1] == side[2])
            {
                // At a cut's end only one edge is severed. Keeping the whole triangle would bridge the opening and
                // pass through the neighbouring lip when pulled. Taper the two sides to the remaining corner.
                var edge = Array.FindIndex(cut, value => value);
                var a = PlanOwn(plan, corners[edge], touched);
                var b = PlanOwn(plan, corners[(edge + 1) % 3], touched);
                var tip = PlanOwn(plan, corners[(edge + 2) % 3], touched);
                var lipA = PlanCross(plan, edges[edge], corners[edge], crossed, touched);
                var lipB = PlanCross(plan, edges[edge], corners[(edge + 1) % 3], crossed, touched);
                if (!(gone[corners[edge]] || gone[corners[(edge + 2) % 3]]))
                {
                    plan.Index.AddRange([a, lipA, tip]);
                    if (walls) { plan.Wall.AddRange([lipA, tip, a]); }
                }
                if (!(gone[corners[(edge + 1) % 3]] || gone[corners[(edge + 2) % 3]]))
                {
                    plan.Index.AddRange([lipB, b, tip]);
                    if (walls) { plan.Wall.AddRange([tip, lipB, b]); }
                }
                continue;
            }
            if (!anyCut)
            {
                if (gone[corners[0]] || gone[corners[1]] || gone[corners[2]])
                {
                    continue;
                }
                for (var n = 0; n < 3; n++)
                {
                    plan.Index.Add(PlanOwn(plan, corners[n], touched));
                }
                continue;
            }
            for (var group = 0; group < 3; group++)
            {
                if (side[0] != group && side[1] != group && side[2] != group)
                {
                    continue;
                }
                if ((side[0] == group && gone[corners[0]]) || (side[1] == group && gone[corners[1]]) || (side[2] == group && gone[corners[2]]))
                {
                    continue;
                }
                // Around the triangle's edge in its own order, so every polygon faces the way the triangle does.
                polygon.Clear();
                crossing.Clear();
                var mine = -1;
                for (var n = 0; n < 3; n++)
                {
                    var here = side[n] == group;
                    if (here)
                    {
                        mine = PlanOwn(plan, corners[n], touched);
                        polygon.Add(mine);
                        crossing.Add(false);
                    }
                    if (cut[n] && here != (side[(n + 1) % 3] == group))
                    {
                        var k = here ? corners[n] : corners[(n + 1) % 3];
                        polygon.Add(PlanCross(plan, edges[n], k, crossed, touched));
                        crossing.Add(true);
                    }
                }
                for (var n = 1; n < polygon.Count - 1; n++)
                {
                    plan.Index.AddRange([polygon[0], polygon[n], polygon[n + 1]]);
                }
                if (!walls)
                {
                    continue;
                }
                // Each stretch of the polygon's edge between two crossings is a lip of the cut: a wall goes down from it.
                for (var n = 0; n < polygon.Count; n++)
                {
                    var next = (n + 1) % polygon.Count;
                    if (crossing[n] && crossing[next])
                    {
                        plan.Wall.AddRange([polygon[n], polygon[next], mine]);
                    }
                }
            }
        }
        foreach (var k in touched)
        {
            _vmap[k] = -1;
        }
        foreach (var key in crossed)
        {
            _xmap[key] = -1;
        }
        for (var w = 0; w < plan.Wall.Count; w += 3)
        {
            var a = plan.Wall[w];
            var b = plan.Wall[w + 1];
            plan.LipInside[(Math.Min(a, b), Math.Max(a, b))] = plan.Wall[w + 2];
            foreach (var (here, next) in new[] { (a, b), (b, a) })
            {
                if (!plan.Lip.TryGetValue(here, out var neighbours))
                {
                    neighbours = [];
                    plan.Lip[here] = neighbours;
                }
                if (!neighbours.Contains(next))
                {
                    neighbours.Add(next);
                }
            }
        }
        var crossingOf = new Dictionary<(int, int), int>();
        for (var t = 0; t < plan.Index.Count; t += 3)
        {
            var a = plan.Index[t];
            var b = plan.Index[t + 1];
            var c = plan.Index[t + 2];
            if (plan.Other[a] >= 0 || plan.Other[b] >= 0 || plan.Other[c] >= 0)
            {
                var ab = plan.Uv[b] - plan.Uv[a];
                var ac = plan.Uv[c] - plan.Uv[a];
                plan.CutFaces.Add((t, (ac.Y * ab.X - ac.X * ab.Y) * SiteSize.X * SiteSize.Y * 0.02f));
            }
            SetLipInside(plan, a, b, c);
            SetLipInside(plan, b, c, a);
            SetLipInside(plan, c, a, b);
        }
        for (var v = 0; v < plan.Owner.Count; v++)
        {
            if (plan.Other[v] >= 0) { crossingOf[(plan.Owner[v], plan.Other[v])] = v; }
        }
        foreach (var (ends, v) in crossingOf)
        {
            if (crossingOf.TryGetValue((ends.Item2, ends.Item1), out var opposite))
            {
                plan.OppositeLip[v] = opposite;
            }
        }
        // Refine only faces bordering a cut. Interior skin keeps its original triangles, avoiding extra mesh
        // uploads while sewing a long incision. Curved lip midpoints are shared by the sheet and its wall.
        var midpointOf = new Dictionary<(int, int), int>();
        int Midpoint(int a, int b)
        {
            var key = (Math.Min(a, b), Math.Max(a, b));
            if (!midpointOf.TryGetValue(key, out var v))
            {
                v = plan.Owner.Count + plan.Midpoints.Count;
                midpointOf[key] = v;
                plan.Midpoints.Add((a, b));
            }
            return v;
        }
        foreach (var edge in plan.LipInside.Keys)
        {
            Midpoint(edge.Item1, edge.Item2);
        }
        var boundary = new List<int>(6);
        for (var t = 0; t < plan.Index.Count; t += 3)
        {
            var a = plan.Index[t];
            var b = plan.Index[t + 1];
            var c = plan.Index[t + 2];
            if (!midpointOf.ContainsKey((Math.Min(a, b), Math.Max(a, b)))
                && !midpointOf.ContainsKey((Math.Min(b, c), Math.Max(b, c)))
                && !midpointOf.ContainsKey((Math.Min(c, a), Math.Max(c, a))))
            {
                plan.RefinedIndex.Add(a);
                plan.RefinedIndex.Add(b);
                plan.RefinedIndex.Add(c);
                continue;
            }
            boundary.Clear();
            foreach (var (start, end) in new[] { (a, b), (b, c), (c, a) })
            {
                boundary.Add(start);
                if (midpointOf.TryGetValue((Math.Min(start, end), Math.Max(start, end)), out var mid))
                {
                    boundary.Add(mid);
                }
            }
            var center = plan.Owner.Count + plan.Midpoints.Count + plan.Centers.Count;
            plan.Centers.Add((a, b, c));
            for (var n = 0; n < boundary.Count; n++)
            {
                plan.RefinedIndex.AddRange([center, boundary[n], boundary[(n + 1) % boundary.Count]]);
            }
        }
        return plan;
    }

    private static void SetLipInside(LayerPlan plan, int a, int b, int inside)
    {
        var key = (Math.Min(a, b), Math.Max(a, b));
        if (plan.LipInside.ContainsKey(key)) { plan.LipInside[key] = inside; }
    }

    /// <summary>The vertex of particle k in the plan, added the first time it's used.</summary>
    private int PlanOwn(LayerPlan plan, int k, List<int> touched)
    {
        if (_vmap[k] < 0)
        {
            _vmap[k] = plan.Owner.Count;
            touched.Add(k);
            plan.Owner.Add(k);
            plan.Other.Add(-1);
            plan.Share.Add(0f);
            plan.Uv.Add(_uvOf[k]);
        }
        return _vmap[k];
    }

    /// <summary>
    /// The vertex where the blade crossed spring s, on the side of its end k, added the first time it's used. It lies
    /// on the traced blade path and deforms with the intact skin on its own side (see DeformedLipOffset()). A
    /// crossing at its owner coincides with that particle, so it shares its vertex rather than leaving a sliver.
    /// </summary>
    private int PlanCross(LayerPlan plan, int s, int k, List<int> crossed, List<int> touched)
    {
        ref readonly var spring = ref Tissue.SpringAt(s);
        var atStart = spring.A == k;
        if ((atStart && spring.Cross == 0f) || (!atStart && spring.Cross == 1f))
        {
            return PlanOwn(plan, k, touched);
        }
        var key = s * 2 + (atStart ? 0 : 1);
        if (_xmap[key] < 0)
        {
            _xmap[key] = plan.Owner.Count;
            crossed.Add(key);
            var other = atStart ? spring.B : spring.A;
            plan.Owner.Add(k);
            plan.Other.Add(other);
            plan.Share.Add(atStart ? spring.Cross : 1f - spring.Cross);
            plan.Uv.Add(_uvOf[spring.A].Lerp(_uvOf[spring.B], spring.Cross));
        }
        return _xmap[key];
    }

    /// <summary>Where the skin of every planned particle is now, its normal, and which way is out of it: straight up
    /// where the skin is in place, along its own normal on a flap pulled far, so a flap folded over shows its fat on
    /// top instead of drawing it under the skin, through the drape.</summary>
    private void PlaceParticles()
    {
        foreach (var k in _around)
        {
            _skinOf[k] = LayerPoint(0, k);
            _outward[k] = Vector3.Up;
        }
        ResolveClothContact();
        foreach (var k in _planned)
        {
            var distance = Moved(k).Length();
            var moved = Mathf.Clamp(distance / FlapMove, 0f, 1f);
            // The model's own normal where the skin rests, turning with the skin as it moves (a flap keeps its own).
            var normal = (GridNormal(_skinOf, k, respectCuts: true) + _normalFit[k] * (1f - moved)).Normalized();
            _normal[k] = normal;
            // A wave keeps its deeper tissue below the skin. Rotating a thick layer's offset with every small
            // crease lets it poke through the next row of skin. Only a deliberate turnover rotates the stack.
            var turned = Tissue.TurningFlap
                ? Mathf.SmoothStep(TissueSim.TurnoverReach * 0.75f, TissueSim.TurnoverReach * 1.25f, distance)
                : 0f;
            var axis = Vector3.Up.Cross(normal);
            axis = axis.IsZeroApprox() ? Vector3.Right : axis.Normalized();
            _outward[k] = turned > 0f
                ? Vector3.Up.Rotated(axis, Mathf.Acos(Mathf.Clamp(normal.Y, -1f, 1f)) * turned)
                : Vector3.Up;
        }
    }

    /// <summary>Resolve contact on moving faces, using the cloth directly at nearby samples rather than a
    /// raised envelope spanning unrelated vertices. Corrections stay on the three corners of the contact face.</summary>
    private void ResolveClothContact()
    {
        if (Tissue.FloorAt is null || !Tissue.HasFoldFootprint) { return; }
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var k in _planned)
            {
                var at = Tissue.CellOf(k);
                if (at.X >= Tissue.ResX || at.Y >= Tissue.ResY) { continue; }
                var c = k + Tissue.ResX + 1;
                Contact(k, k + 1, c);
                Contact(k + 1, c + 1, c);
            }
        }
        void Contact(int a, int b, int c)
        {
            if (!Tissue.Exposed[a] || !Tissue.Exposed[b] || !Tissue.Exposed[c]) { return; }
            if (Tissue.FloorOpen.HasPoint(new Vector2(_skinOf[a].X, _skinOf[a].Z))
                && Tissue.FloorOpen.HasPoint(new Vector2(_skinOf[b].X, _skinOf[b].Z))
                && Tissue.FloorOpen.HasPoint(new Vector2(_skinOf[c].X, _skinOf[c].Z))) { return; }
            ResolveContact(_skinOf, a, b, c, _clothFaces, 0.004f, above: true);
        }
    }

    /// <summary>One layer's sheet and the walls of its cuts, where the skin is now, as mesh arrays (null for no
    /// triangles).</summary>
    private (Godot.Collections.Array? Sheet, Godot.Collections.Array? Walls) FillLayer(int layer, LayerPlan plan)
    {
        var depth = LayerTop(layer);
        var thickness = layer switch { 0 => SkinThickness, 1 => FatThickness, _ => MuscleThickness };
        foreach (var k in _around)
        {
            _pointOf[k] = _renderPoints[layer][k] - _outward[k] * depth;
        }
        var vertexCount = plan.Owner.Count;
        var vertices = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        for (var v = 0; v < vertexCount; v++)
        {
            var k = plan.Owner[v];
            var p = _pointOf[k];
            if (plan.Other[v] >= 0)
            {
                var offset = _onModel[plan.Other[v]] - _onModel[k];
                p += DeformedLipOffset(layer, k, offset * plan.Share[v]);
            }
            vertices[v] = p;
            normals[v] = _normal[k];
        }
        ConstrainLipArea(plan, vertices);
        if (layer > 0 && Tissue.HasFoldFootprint) { KeepBelowSkin(plan, vertices, depth); }
        var faceNormals = new Vector3[vertexCount];
        for (var t = 0; t < plan.Index.Count; t += 3)
        {
            var a = plan.Index[t];
            var b = plan.Index[t + 1];
            var c = plan.Index[t + 2];
            var face = (vertices[c] - vertices[a]).Cross(vertices[b] - vertices[a]);
            faceNormals[a] += face;
            faceNormals[b] += face;
            faceNormals[c] += face;
        }
        for (var v = 0; v < vertexCount; v++)
        {
            // Keep the model's shading where the site meets it; use the actual sheet on lifted skin and lips.
            var moved = Mathf.Clamp(_renderMoved[plan.Owner[v]].Length() / FlapMove, 0f, 1f);
            if (!faceNormals[v].IsZeroApprox())
            {
                normals[v] = normals[v].Lerp(faceNormals[v].Normalized(), plan.Other[v] >= 0 ? 1f : moved).Normalized();
            }
        }
        var refinedVertices = new Vector3[vertexCount + plan.Midpoints.Count + plan.Centers.Count];
        var refinedNormals = new Vector3[refinedVertices.Length];
        var refinedUvs = new Vector2[refinedVertices.Length];
        vertices.CopyTo(refinedVertices, 0);
        normals.CopyTo(refinedNormals, 0);
        plan.Uv.CopyTo(refinedUvs);
        for (var n = 0; n < plan.Midpoints.Count; n++)
        {
            var (a, b) = plan.Midpoints[n];
            refinedVertices[vertexCount + n] = LipMidpoint(plan, vertices, a, b);
            refinedNormals[vertexCount + n] = (normals[a] + normals[b]).Normalized();
            refinedUvs[vertexCount + n] = plan.Uv[a].Lerp(plan.Uv[b], 0.5f);
        }
        for (var n = 0; n < plan.Centers.Count; n++)
        {
            var (a, b, c) = plan.Centers[n];
            var v = vertexCount + plan.Midpoints.Count + n;
            refinedVertices[v] = (vertices[a] + vertices[b] + vertices[c]) / 3f;
            refinedNormals[v] = (normals[a] + normals[b] + normals[c]).Normalized();
            refinedUvs[v] = (plan.Uv[a] + plan.Uv[b] + plan.Uv[c]) / 3f;
        }
        var wallVertices = new List<Vector3>();
        var wallNormals = new List<Vector3>();
        var wallUvs = new List<Vector2>();
        var wallColors = new List<Color>();
        var wallIndices = new List<int>();
        for (var w = 0; w < plan.Wall.Count; w += 3)
        {
            var (a, b) = (plan.Wall[w], plan.Wall[w + 1]);
            var topA = vertices[a];
            var topB = vertices[b];
            var outA = _outward[plan.Owner[a]];
            var outB = _outward[plan.Owner[b]];
            var bottomA = topA - outA * thickness;
            var bottomB = topB - outB * thickness;
            var normal = (topB - topA).Cross(bottomA - topA).Normalized();
            // Facing into the cut, away from this side's own skin.
            if (normal.Dot(topA - vertices[plan.Wall[w + 2]]) < 0f)
            {
                normal = -normal;
            }
            var mid = LipMidpoint(plan, vertices, a, b);
            var midBottom = mid - (outA + outB).Normalized() * thickness;
            var midUv = plan.Uv[a].Lerp(plan.Uv[b], 0.5f);
            foreach (var (start, end, lowStart, lowEnd, uvStart, uvEnd) in new[]
                { (topA, mid, bottomA, midBottom, plan.Uv[a], midUv), (mid, topB, midBottom, bottomB, midUv, plan.Uv[b]) })
            {
                var first = wallVertices.Count;
                wallVertices.AddRange([start, end, lowEnd, lowStart]);
                wallNormals.AddRange([normal, normal, normal, normal]);
                wallUvs.AddRange([uvStart, uvEnd, uvEnd, uvStart]);
                wallColors.AddRange([new Color(0f, 0f, 0f), new Color(0f, 0f, 0f), Colors.White, Colors.White]);
                // Godot's front faces wind clockwise seen from the side the normal points to.
                if ((lowEnd - start).Cross(end - start).Dot(normal) > 0f)
                {
                    wallIndices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
                }
                else
                {
                    wallIndices.AddRange([first, first + 2, first + 1, first, first + 3, first + 2]);
                }
            }
        }
        if (layer == 0 && Tissue.HasFoldFootprint) { IndexContact(_skinContact, refinedVertices, plan.RefinedIndex); }
        return (
            MeshArrays(refinedVertices, refinedNormals, refinedUvs, [.. plan.RefinedIndex]),
            MeshArrays([.. wallVertices], [.. wallNormals], [.. wallUvs], [.. wallIndices], [.. wallColors]));
    }

    private readonly Dictionary<Vector2I, List<ContactFace>> _skinContact = [];
    private readonly Dictionary<Vector2I, List<ContactFace>> _clothFaces = [];
    private const float ContactCell = 0.01f;
    private readonly record struct ContactFace(int Id, Vector3 A, Vector3 B, Vector3 C,
        Vector3 Low, Vector3 High, float SlopeX, float SlopeZ, float OriginY, float Winding);

    private static void IndexContact(Dictionary<Vector2I, List<ContactFace>> faces, Vector3[] vertices, List<int> indices)
    {
        foreach (var bucket in faces.Values) { bucket.Clear(); }
        for (var t = 0; t < indices.Count; t += 3)
        {
            var a = vertices[indices[t]];
            var b = vertices[indices[t + 1]];
            var c = vertices[indices[t + 2]];
            var low = a.Min(b).Min(c);
            var high = a.Max(b).Max(c);
            var ab = b - a;
            var ac = c - a;
            var det = ab.X * ac.Z - ab.Z * ac.X;
            if (Mathf.Abs(det) < 1e-10f) { continue; }
            var sx = (ab.Y * ac.Z - ac.Y * ab.Z) / det;
            var sz = (ac.Y * ab.X - ab.Y * ac.X) / det;
            var face = new ContactFace(t, a, b, c, low, high, sx, sz, a.Y - sx * a.X - sz * a.Z, MathF.Sign(det));
            var first = (Vector2I)(new Vector2(low.X, low.Z) / ContactCell).Floor();
            var last = (Vector2I)(new Vector2(high.X, high.Z) / ContactCell).Floor();
            for (var j = first.Y; j <= last.Y; j++)
            {
                for (var i = first.X; i <= last.X; i++)
                {
                    var key = new Vector2I(i, j);
                    if (!faces.TryGetValue(key, out var near)) { faces[key] = near = []; }
                    near.Add(face);
                }
            }
        }
    }

    private readonly Dictionary<int, ContactFace> _contactFaces = [];
    private readonly Vector3[] _contactPolygon = new Vector3[8];
    private readonly Vector3[] _contactClipped = new Vector3[8];

    /// <summary>The tethered bed resolves contact against nearby skin faces instead of following the whole grip.</summary>
    private void KeepBelowSkin(LayerPlan plan, Vector3[] vertices, float depth)
    {
        for (var t = 0; t < plan.Index.Count; t += 3)
        {
            var a = plan.Index[t];
            var b = plan.Index[t + 1];
            var c = plan.Index[t + 2];
            if (!Tissue.NearFold(plan.Owner[a]) && !Tissue.NearFold(plan.Owner[b]) && !Tissue.NearFold(plan.Owner[c])) { continue; }
            ResolveContact(vertices, a, b, c, _skinContact, depth + 0.0002f, above: false);
        }
    }

    private void ResolveContact(Vector3[] vertices, int a, int b, int c,
        Dictionary<Vector2I, List<ContactFace>> surface, float gap, bool above)
    {
        var low = vertices[a].Min(vertices[b]).Min(vertices[c]);
        var high = vertices[a].Max(vertices[b]).Max(vertices[c]);
        var first = (Vector2I)(new Vector2(low.X, low.Z) / ContactCell).Floor();
        var last = (Vector2I)(new Vector2(high.X, high.Z) / ContactCell).Floor();
        _contactFaces.Clear();
        for (var j = first.Y; j <= last.Y; j++)
        {
            for (var i = first.X; i <= last.X; i++)
            {
                if (surface.TryGetValue(new Vector2I(i, j), out var near))
                {
                    foreach (var face in near) { _contactFaces.TryAdd(face.Id, face); }
                }
            }
    }
    var ab = vertices[b] - vertices[a];
    var ac = vertices[c] - vertices[a];
    var determinant = ab.X * ac.Z - ab.Z * ac.X;
    if (Mathf.Abs(determinant) < 1e-10f) { return; }
    foreach (var face in _contactFaces.Values)
    {
        var (x, y, z) = (face.A, face.B, face.C);
        var skinLow = face.Low;
        var skinHigh = face.High;
        if (low.X > skinHigh.X || high.X < skinLow.X || low.Z > skinHigh.Z || high.Z < skinLow.Z
            || (above ? low.Y >= skinHigh.Y + gap : high.Y <= skinLow.Y - gap)) { continue; }
        var slopeX = face.SlopeX;
        var slopeZ = face.SlopeZ;
        var originY = face.OriginY + (above ? gap : -gap);
        var sideA = vertices[a].Y - slopeX * vertices[a].X - slopeZ * vertices[a].Z - originY;
        var sideB = vertices[b].Y - slopeX * vertices[b].X - slopeZ * vertices[b].Z - originY;
        var sideC = vertices[c].Y - slopeX * vertices[c].X - slopeZ * vertices[c].Z - originY;
        if (above ? sideA >= 0f && sideB >= 0f && sideC >= 0f : sideA <= 0f && sideB <= 0f && sideC <= 0f) { continue; }
        _contactPolygon[0] = vertices[a];
        _contactPolygon[1] = vertices[b];
        _contactPolygon[2] = vertices[c];
        var count = 3;
        Clip(x, y, face.Winding, ref count);
        Clip(y, z, face.Winding, ref count);
        Clip(z, x, face.Winding, ref count);
        for (var n = 0; n < count; n++)
        {
            var p = _contactPolygon[n];
            var roof = slopeX * p.X + slopeZ * p.Z + originY;
            var offset = p - vertices[a];
            var wb = (offset.X * ac.Z - offset.Z * ac.X) / determinant;
            var wc = (offset.Z * ab.X - offset.X * ab.Z) / determinant;
            var wa = 1f - wb - wc;
            var height = vertices[a].Y * wa + vertices[b].Y * wb + vertices[c].Y * wc;
            if (above ? roof <= height : roof >= height) { continue; }
            var correction = (height - roof) / (wa * wa + wb * wb + wc * wc);
            vertices[a].Y -= correction * wa;
            vertices[b].Y -= correction * wb;
            vertices[c].Y -= correction * wc;
        }
    }
    }


    private void Clip(Vector3 a, Vector3 b, float sign, ref int count)
    {
        var edge = b - a;
        var result = 0;
        for (var n = 0; n < count; n++)
        {
            var p = _contactPolygon[n];
            var q = _contactPolygon[(n + 1) % count];
            var dp = ((p.Z - a.Z) * edge.X - (p.X - a.X) * edge.Z) * sign;
            var dq = ((q.Z - a.Z) * edge.X - (q.X - a.X) * edge.Z) * sign;
            if (dp >= 0f) { _contactClipped[result++] = p; }
            if ((dp >= 0f) != (dq >= 0f)) { _contactClipped[result++] = p.Lerp(q, dp / (dp - dq)); }
        }
        Array.Copy(_contactClipped, _contactPolygon, result);
        count = result;
    }

    /// <summary>Virtual lips have no simulation particles of their own. Keep their faces oriented with the sheet
    /// during a wave, so the cut boundary cannot fold back through the thicker tissue underneath it.</summary>
    private void ConstrainLipArea(LayerPlan plan, Vector3[] vertices)
    {
        if (!_renderFold) { return; }
        for (var pass = 0; pass < 8; pass++)
        {
            var changed = false;
            for (var n = 0; n < plan.CutFaces.Count; n++)
            {
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
    private Vector3 DeformedLipOffset(int layer, int k, Vector3 offset)
    {
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
        var changeX = _pointOf[right] - _pointOf[left] - dx;
        var changeZ = _pointOf[below] - _pointOf[above] - dz;
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
    private static Vector3 LipMidpoint(LayerPlan plan, Vector3[] vertices, int a, int b)
    {
        var mid = (vertices[a] + vertices[b]) * 0.5f;
        if (!plan.Lip.TryGetValue(a, out var aroundA) || !aroundA.Contains(b) || aroundA.Count != 2
            || !plan.Lip.TryGetValue(b, out var aroundB) || aroundB.Count != 2)
        {
            return mid;
        }
        var before = vertices[aroundA[0] == b ? aroundA[1] : aroundA[0]];
        var after = vertices[aroundB[0] == a ? aroundB[1] : aroundB[0]];
        var bend = (vertices[a] + vertices[b] - before - after) / 16f;
        var edge = vertices[b] - vertices[a];
        var limit = edge.Length() * 0.25f;
        if (plan.LipInside.TryGetValue((Math.Min(a, b), Math.Max(a, b)), out var inside))
        {
            var toInside = vertices[inside] - mid;
            limit = Mathf.Min(limit, toInside.Slide(edge.Normalized()).Length() * 0.2f);
            // Round within this side's sheet, rather than lifting a midpoint through an adjoining face.
            bend = bend.Slide(edge.Cross(vertices[inside] - mid).Normalized());
        }
        foreach (var v in new[] { a, b })
        {
            if (plan.OppositeLip.TryGetValue(v, out var opposite))
            {
                var gap = vertices[v] - vertices[opposite];
                limit = Mathf.Min(limit, gap.Length() * 0.2f);
            }
        }
        return mid + bend.LimitLength(limit);
    }

    /// <summary>Null without any triangles: checked here, as reading the indices back out of the arrays would copy
    /// them.</summary>
    private static Godot.Collections.Array? MeshArrays(Vector3[] vertices, Vector3[] normals, Vector2[] uvs,
        int[] indices, Color[]? colors = null)
    {
        if (indices.Length == 0)
        {
            return null;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        if (colors is not null)
        {
            arrays[(int)Mesh.ArrayType.Color] = colors;
        }
        return arrays;
    }

    /// <summary>
    /// Where a layer's grid point k is now, before it's moved down to its depth: on the body model where the skin
    /// rests, moved as far as the sim moved it (Moved()). Deeper layers are tethered and follow the skin only partly (a
    /// stepped wound edge), but a flap pulled far back takes all of its layers along.
    /// </summary>
    public Vector3 LayerPoint(int layer, int k)
    {
        var moved = Moved(k);
        // Tissue at a deeply incised lip follows that local flap; the surrounding bed remains tethered. Do not
        // switch the entire site to full following just because one grip forms a fold.
        var follow = Tissue.TurningFlap ? Mathf.Lerp(LayerFollow[layer], 1f, Mathf.Clamp(moved.Length() / TissueSim.TurnoverReach, 0f, 1f)) : LayerFollow[layer];
        var point = _onModel[k] + moved * follow;
        // A sliding perimeter follows the cloth at its current position, not the body's height at its old one.
        // Apply this to the rendered stack together so hidden tissue cannot emerge through the drape on a flank.
        if (Tissue.FloorAt is not null && (moved.LengthSquared() > 0.000001f || Tissue.FoldContact(k)))
        {
            var floorY = Tissue.FloorAt(point.X, point.Z);
            var above = Tissue.Exposed[k] && (layer == 0 || follow >= 0.999f);
            if (!float.IsNaN(floorY))
            {
                point.Y = above ? Mathf.Max(point.Y, floorY)
                    : Mathf.Min(point.Y, floorY - TissueSim.UnderDrape);
            }
        }
        return point;
    }

    /// <summary>
    /// How far the sim moved grid point k since it settled, less any way back toward the body model: tension holds the
    /// sheet off a curved body, and where a cut lets go of it the skin springs back, to where it's drawn already.
    /// None on the region's edge, where the body model next to it would show a step, unless it moved further than
    /// EdgeHold (a flap folded back).
    /// </summary>
    private Vector3 Moved(int k)
    {
        var moved = Tissue.Pos[k] - Tissue.Settled[k];
        if (k < _regionEdge.Length && _regionEdge[k] && moved.Length() <= EdgeHold)
        {
            return Vector3.Zero;
        }
        var fit = _onModel[k] - Tissue.Settled[k];
        if (fit.IsZeroApprox())
        {
            return moved;
        }
        var back = fit.Normalized();
        return moved - back * Mathf.Clamp(moved.Dot(back), 0f, fit.Length());
    }

    /// <summary>Takes the tissue's current region. True when it changed.</summary>
    private bool UpdateRegion()
    {
        var region = Tissue.Region();
        if (region.AsSpan().SequenceEqual(_region))
        {
            return false;
        }
        _region = region;
        _regionEdge = new bool[region.Length];
        var last = new Vector2I(Tissue.ResX, Tissue.ResY);
        for (var k = 0; k < region.Length; k++)
        {
            if (region[k] == 1)
            {
                var at = Tissue.CellOf(k);
                foreach (var step in new[] { Vector2I.Left, Vector2I.Right, Vector2I.Up, Vector2I.Down })
                {
                    var n = (at + step).Clamp(Vector2I.Zero, last);
                    if (region[Tissue.Index(n.X, n.Y)] == 0)
                    {
                        _regionEdge[k] = true;
                    }
                }
            }
        }
        _regionDirty = true;
        return true;
    }
}
