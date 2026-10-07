namespace Scalpel.Patients;

/// <summary>The site's tissue layers: measuring the site on the body model and drawing skin, fat and muscle from the
/// tissue sim.</summary>
public partial class PatientBody
{
    /// <summary>How much each layer follows the skin's movement (deeper layers are more tethered).</summary>
    private static readonly float[] LayerFollow = [1f, 0.8f, 0.55f];
    private static readonly TissueDepth[] LayerDepth = [TissueDepth.Skin, TissueDepth.Fat, TissueDepth.Muscle];
    /// <summary>Skin pulled this far (meters) takes its deeper layers fully along, see <see cref="LayerPoint"/>.
    /// </summary>
    private const float FlapMove = 0.04f;
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
    /// a crossing the spring's other end, how far along it the blade crossed (share) and the lip neighbour it slides
    /// with (slide, -1 for none); its uv; the triangle indices; and per wall quad its two top vertices and a vertex of
    /// its own side's skin, to face it away from.
    /// </summary>
    private sealed class LayerPlan
    {
        public List<int> Owner { get; } = [];
        public List<int> Other { get; } = [];
        public List<float> Share { get; } = [];
        public List<int> Slide { get; } = [];
        public List<Vector2> Uv { get; } = [];
        public List<int> Index { get; } = [];
        public List<int> Wall { get; } = [];
    }

    private readonly List<MeshInstance3D> _layers = [];
    /// <summary>Per layer: the material of its sheet and of the walls of its cuts.</summary>
    private readonly List<(Material Sheet, Material Walls)> _layerMaterials = [];
    private int _layerVersion = -1;
    private int _layerSteps = -1;
    private bool _rebuiltLast;
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
            var flesh = Materials.TissueLayerMaterial(i > 0 ? i - 1 : 2, WoundMap.Texture(WoundMap.Layer.Fluids), tone);
            _layerMaterials.Add((i == 0 ? skin : flesh, flesh));
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
    private Vector3 GridNormal(Vector3[] points, int k)
    {
        var at = Tissue.CellOf(k);
        var dx = points[Tissue.Index(Math.Min(at.X + 1, Tissue.ResX), at.Y)] - points[Tissue.Index(Math.Max(at.X - 1, 0), at.Y)];
        var dz = points[Tissue.Index(at.X, Math.Min(at.Y + 1, Tissue.ResY))] - points[Tissue.Index(at.X, Math.Max(at.Y - 1, 0))];
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
    private void RebuildLayers()
    {
        _layerVersion = Tissue.TopologyVersion;
        _layerSteps = Tissue.StepsDone;
        if (UpdateRegion() || _planFor != Tissue.TopologyVersion)
        {
            PlanLayers();
        }
        PlaceParticles();
        for (var layer = 0; layer < 3; layer++)
        {
            var instance = _layers[layer];
            var mesh = (ArrayMesh)instance.Mesh;
            mesh.ClearSurfaces();
            var plan = _plans[layer];
            // No fat on this part of the body: the muscle lies right under the skin.
            instance.Visible = plan.Index.Count > 0 && (layer != 1 || FatThickness > 0.0005f);
            if (!instance.Visible)
            {
                continue;
            }
            var (sheet, walls) = FillLayer(layer, plan);
            foreach (var (arrays, material) in new[] { (sheet, _layerMaterials[layer].Sheet), (walls, _layerMaterials[layer].Walls) })
            {
                if (arrays[(int)Mesh.ArrayType.Index].AsInt32Array().Length == 0)
                {
                    continue;
                }
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
            }
        }
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
            foreach (var k in plan.Owner.Concat(plan.Other).Concat(plan.Slide))
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
            if (!anyCut || (side[0] == side[1] && side[1] == side[2]))
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
                        polygon.Add(PlanCross(plan, edges[n], k, crossed));
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
        return plan;
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
            plan.Slide.Add(-1);
            plan.Uv.Add(_uvOf[k]);
        }
        return _vmap[k];
    }

    /// <summary>
    /// The vertex where the blade crossed spring s, on the side of its end k, added the first time it's used. It lies
    /// as far from k as it did at rest, so each lip moves with its own side. A diagonal spring's far end lies a cell
    /// along the cut from k: there the lip moves like k's neighbour that way (if they're still joined), so the lip
    /// doesn't step from cell to cell.
    /// </summary>
    private int PlanCross(LayerPlan plan, int s, int k, List<int> crossed)
    {
        ref readonly var spring = ref Tissue.SpringAt(s);
        var atStart = spring.A == k;
        var key = s * 2 + (atStart ? 0 : 1);
        if (_xmap[key] < 0)
        {
            _xmap[key] = plan.Owner.Count;
            crossed.Add(key);
            var other = atStart ? spring.B : spring.A;
            var slide = -1;
            var cell = Tissue.CellOf(k);
            var step = Tissue.CellOf(other) - cell;
            step = Mathf.Abs(spring.CutDir.X) >= Mathf.Abs(spring.CutDir.Y) ? new Vector2I(step.X, 0) : new Vector2I(0, step.Y);
            if (step != Vector2I.Zero)
            {
                var m = Tissue.Index(cell.X + step.X, cell.Y + step.Y);
                var low = Math.Min(k, m);
                var joined = step.X != 0 ? Tissue.SpringRight[low] : Tissue.SpringDown[low];
                if (joined >= 0 && Tissue.SpringAt(joined).Active)
                {
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
    private void PlaceParticles()
    {
        foreach (var k in _around)
        {
            _skinOf[k] = LayerPoint(0, k);
        }
        foreach (var k in _planned)
        {
            var moved = Mathf.Clamp(Moved(k).Length() / FlapMove, 0f, 1f);
            // The model's own normal where the skin rests, turning with the skin as it moves (a flap keeps its own).
            var normal = (GridNormal(_skinOf, k) + _normalFit[k] * (1f - moved)).Normalized();
            _normal[k] = normal;
            _outward[k] = Vector3.Up.Lerp(normal, moved).Normalized();
        }
    }

    /// <summary>One layer's sheet and the walls of its cuts, where the skin is now, as mesh arrays.</summary>
    private (Godot.Collections.Array Sheet, Godot.Collections.Array Walls) FillLayer(int layer, LayerPlan plan)
    {
        var depth = LayerTop(layer);
        var thickness = layer switch { 0 => SkinThickness, 1 => FatThickness, _ => MuscleThickness };
        foreach (var k in _planned)
        {
            _pointOf[k] = LayerPoint(layer, k) - _outward[k] * depth;
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
                if (plan.Slide[v] >= 0)
                {
                    var m = plan.Slide[v];
                    offset += (_pointOf[m] - _onModel[m]) - (p - _onModel[k]);
                }
                p += offset * plan.Share[v];
            }
            vertices[v] = p;
            normals[v] = _normal[k];
        }
        var wallVertices = new List<Vector3>();
        var wallNormals = new List<Vector3>();
        var wallUvs = new List<Vector2>();
        var wallIndices = new List<int>();
        for (var w = 0; w < plan.Wall.Count; w += 3)
        {
            var (a, b) = (plan.Wall[w], plan.Wall[w + 1]);
            var topA = vertices[a];
            var topB = vertices[b];
            var bottomA = topA - _outward[plan.Owner[a]] * thickness;
            var bottomB = topB - _outward[plan.Owner[b]] * thickness;
            var normal = (topB - topA).Cross(bottomA - topA).Normalized();
            // Facing into the cut, away from this side's own skin.
            if (normal.Dot(topA - vertices[plan.Wall[w + 2]]) < 0f)
            {
                normal = -normal;
            }
            var first = wallVertices.Count;
            wallVertices.AddRange([topA, topB, bottomB, bottomA]);
            wallNormals.AddRange([normal, normal, normal, normal]);
            wallUvs.AddRange([plan.Uv[a], plan.Uv[b], plan.Uv[b], plan.Uv[a]]);
            // Godot's front faces wind clockwise seen from the side the normal points to.
            if ((bottomB - topA).Cross(topB - topA).Dot(normal) > 0f)
            {
                wallIndices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
            }
            else
            {
                wallIndices.AddRange([first, first + 2, first + 1, first, first + 3, first + 2]);
            }
        }
        return (
            MeshArrays(vertices, normals, [.. plan.Uv], [.. plan.Index]),
            MeshArrays([.. wallVertices], [.. wallNormals], [.. wallUvs], [.. wallIndices]));
    }

    private static Godot.Collections.Array MeshArrays(Vector3[] vertices, Vector3[] normals, Vector2[] uvs, int[] indices)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;
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
        return _onModel[k] + moved * Mathf.Lerp(LayerFollow[layer], 1f, Mathf.Clamp(moved.Length() / FlapMove, 0f, 1f));
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
        // One byte per particle, row by row: the image's own layout.
        _regionImage.SetData(Tissue.ResX + 1, Tissue.ResY + 1, false, Image.Format.L8, [.. region.Select(texel => (byte)(texel * 255))]);
        RegionTexture.Update(_regionImage);
        return true;
    }
}
