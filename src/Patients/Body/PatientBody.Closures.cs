namespace Scalpel.Patients;

/// <summary>A staple as drawn: the wire's path (site space) and the layer it holds.</summary>
public sealed record DrawnStaple(TissueDepth Layer, Vector3[] Path);

/// <summary>
/// A running thread as last drawn: its holes, tension, which spans still hold, the live free end's tip (null when it
/// isn't drawn), whether it's tied off or torn, the routed spans and the free end (site space), and how hard the
/// pressure marks around its holes show.
/// </summary>
public sealed record DrawnSuture(
    Vector3[] Holes,
    float Tension,
    bool[] Live,
    Vector3? Tip,
    bool Final,
    IReadOnlyList<Vector3[]> Routes,
    Vector3[] LivePath,
    float PressureAmount,
    int PressureHoles);

/// <summary>Closures drawn on the site: running threads and staples, riding the tissue they hold.</summary>
public partial class PatientBody
{
    private const float SutureRadius = 0.00028f;
    private const float SutureEntryDepth = 0.00045f;
    private const int SutureSamples = 9;
    private const float SuturePressureSize = 0.008f;
    private const float StapleRadius = 0.0005f;
    private const float StapleLeg = 0.002f;
    private const int StapleSamples = 6;
    /// <summary>The skin is drawn this much toward the camera (skin.gdshader): what lies on it is lifted as much.
    /// </summary>
    private const float SkinBias = 0.001f;
    /// <summary>Under this (meters) a hole is drawn where it was: the sim settles for a while after every pull.</summary>
    private const float HoleStill = 0.0002f;

    /// <summary>A staple put in: the layer it holds and, per leg, the grid point it rides and its offset from it.
    /// </summary>
    private sealed record Staple(TissueDepth Layer, (int Particle, Vector3 Offset)[] Legs);

    private readonly List<Staple> _staples = [];
    private readonly Dictionary<int, DrawnSuture> _sutureDrawn = [];
    private (int Steps, int Topology, int Count) _staplesDrawnFor = (-1, -1, -1);
    private Node3D _sutureRoot = null!;
    private Node3D _stapleRoot = null!;
    private Material _sutureMaterial = null!;
    private Material _stapleMaterial = null!;
    private PlaneMesh _suturePressureMesh = null!;
    private int _sutureSteps = -1;
    private int _sutureTopology = -1;

    /// <summary>Every staple as drawn, in the order they went in.</summary>
    public List<DrawnStaple> DrawnStaples { get; private set; } = [];

    /// <summary>How thread <paramref name="id"/> was last drawn, null before it's drawn.</summary>
    public DrawnSuture? DrawnThread(int id) => _sutureDrawn.GetValueOrDefault(id);

    /// <summary>The node holding thread <paramref name="id"/>'s meshes, null before it's drawn.</summary>
    public Node3D? ThreadNode(int id) => _sutureRoot.GetNodeOrNull<Node3D>($"Suture{id}");

    /// <summary>The mesh every staple is drawn in, null before the first staple.</summary>
    public MeshInstance3D? StapleWire => _stapleRoot.GetNodeOrNull<MeshInstance3D>("Wire");

    private void BuildClosureDrawing()
    {
        _sutureRoot = new Node3D { Name = "Sutures" };
        Site.AddChild(_sutureRoot);
        var thread = Materials.ToonUnique(new Color(0.08f, 0.12f, 0.18f), 0f, false, 0.45f);
        thread.SetShaderParameter("camera_bias", 0.001f);
        _sutureMaterial = thread;
        _stapleRoot = new Node3D { Name = "Staples" };
        Site.AddChild(_stapleRoot);
        var steel = Materials.FamilyUnique("metal", new Color(0.8f, 0.82f, 0.86f), 0.25f);
        steel.SetShaderParameter("camera_bias", 0.001f);
        _stapleMaterial = steel;
        var pressure = new ShaderMaterial { Shader = GD.Load<Shader>("res://assets/shaders/suture_pressure.gdshader") };
        pressure.SetShaderParameter("pressure_texture", GD.Load<Texture2D>("res://assets/sprites/suture_pressure.svg"));
        _suturePressureMesh = new PlaneMesh { Size = Vector2.One * SuturePressureSize, Material = pressure };
    }

    /// <summary>A staple put in with its legs at a and b (uv, where they touched the tissue) through
    /// <paramref name="layer"/>. Each leg rides the grid point nearest it as the tissue lies now, so the staple moves
    /// with the edges it holds.</summary>
    public void AddStaple(Vector2 a, Vector2 b, TissueDepth layer)
    {
        var legs = new[] { a, b }.Select(uv =>
        {
            var p = (uv - new Vector2(0.5f, 0.5f)) * Tissue.Size;
            var best = 0;
            for (var k = 0; k < Tissue.Pos.Length; k++)
            {
                if (Flat(Tissue.Pos[k]).DistanceSquaredTo(p) < Flat(Tissue.Pos[best]).DistanceSquaredTo(p))
                {
                    best = k;
                }
            }
            return (best, new Vector3(p.X - Tissue.Pos[best].X, 0f, p.Y - Tissue.Pos[best].Z));
        });
        _staples.Add(new Staple(layer, [.. legs]));
    }

    private static Vector2 Flat(Vector3 p) => new(p.X, p.Z);

    /// <summary>Draws every staple as a wire bridge between its legs, the legs going into the layer it holds: one mesh
    /// for all.</summary>
    private void UpdateStaples()
    {
        var drawnFor = (Tissue.StepsDone, Tissue.TopologyVersion, _staples.Count);
        if (_staples.Count == 0 || drawnFor == _staplesDrawnFor)
        {
            return;
        }
        _staplesDrawnFor = drawnFor;
        DrawnStaples = [.. _staples.Select(staple => new DrawnStaple(staple.Layer, StaplePath(staple)))];
        if (StapleWire is not { } wire)
        {
            wire = new MeshInstance3D { Name = "Wire", MaterialOverride = _stapleMaterial };
            _stapleRoot.AddChild(wire);
        }
        wire.Mesh = Shapes.Tubes(DrawnStaples.Select(staple => staple.Path), StapleRadius, StapleRadius, 6);
    }

    private Vector3[] StaplePath(Staple staple)
    {
        var layer = (int)staple.Layer - 1;
        var tops = staple.Legs
            .Select(leg => LayerPoint(layer, leg.Particle) + leg.Offset + Vector3.Up * (StapleRadius - LayerTop(layer)))
            .ToArray();
        var path = new List<Vector3> { tops[0] - Vector3.Up * StapleLeg };
        for (var i = 0; i <= StapleSamples; i++)
        {
            var p = tops[0].Lerp(tops[1], (float)i / StapleSamples);
            if (staple.Layer == TissueDepth.Skin)
            {
                // A straight crown would sink into a rounded limb between its legs: it rides on the skin, as drawn.
                p.Y = Mathf.Max(p.Y, SkinHeight(LocalToUv(p)) + StapleRadius + SkinBias);
            }
            path.Add(p);
        }
        path.Add(tops[1] - Vector3.Up * StapleLeg);
        return [.. path];
    }

    /// <summary>Draws each running thread as tubes from hole to hole, plus the live free end from its newest hole to
    /// the needle. Routed spans are rebuilt only when their holes move, tension changes or they let go. The free end
    /// follows its needle.</summary>
    private void UpdateSutures()
    {
        var tissueChanged = _sutureSteps != Tissue.StepsDone || _sutureTopology != Tissue.TopologyVersion;
        if (tissueChanged)
        {
            _sutureSteps = Tissue.StepsDone;
            _sutureTopology = Tissue.TopologyVersion;
        }
        foreach (var id in Tissue.ThreadIds)
        {
            var before = _sutureDrawn.GetValueOrDefault(id);
            if (!tissueChanged && before is { Final: true })
            {
                continue;
            }
            _sutureDrawn[id] = DrawThread(id, Tissue.Thread(id)!, before);
        }
    }

    private DrawnSuture DrawThread(int id, SutureThread info, DrawnSuture? drawn)
    {
        var layer = (int)info.Layer;
        var holes = info.Anchors.Select(k => LayerPoint(layer - 1, k) - Vector3.Up * LayerTop(layer - 1)).ToArray();
        var live = info.Springs.Select(s => Tissue.SpringAt(s).Active).ToArray();
        var oldHoles = drawn?.Holes ?? [];
        var moved = new bool[holes.Length];
        for (var i = 0; i < holes.Length; i++)
        {
            moved[i] = i >= oldHoles.Length || oldHoles[i].DistanceSquaredTo(holes[i]) > HoleStill * HoleStill;
            if (!moved[i])
            {
                holes[i] = oldHoles[i];
            }
        }
        var retension = drawn is null || drawn.Tension != info.Tension;
        var root = ThreadNode(id);
        if (root is null)
        {
            root = new Node3D { Name = $"Suture{id}" };
            _sutureRoot.AddChild(root);
        }
        // A running stitch alternates between exposed and subcutaneous passes. Each exposed cable's length comes from
        // the same spring that pulls the skin, so loose thread bows on the surface and tightened thread straightens it.
        var slack = Mathf.Clamp(Mathf.InverseLerp(TissueSim.ThreadTear[layer], TissueSim.ThreadLoose[layer], info.Tension), 0f, 1f);
        var wasLive = drawn?.Live ?? [];
        var routesChanged = retension || moved.Contains(true) || !wasLive.SequenceEqual(live);
        var routes = drawn?.Routes ?? [];
        if (routesChanged)
        {
            var spans = new List<Vector3[]>();
            for (var i = 1; i < holes.Length; i++)
            {
                if (live[i - 1] && i % 2 != 0)
                {
                    spans.Add(SutureCablePath(holes[i - 1], holes[i], Tissue.SpringAt(info.Springs[i - 1]).Rest, info.Layer));
                }
            }
            routes = spans;
            SetTubes(root, "Routed", spans);
        }
        Vector3? tip = null;
        if (!info.Final && Surgery.Current?.Tools.SutureTip(id) is { } worldTip)
        {
            tip = Site.ToLocal(worldTip);
        }
        var oldTip = drawn?.Tip;
        var tipChanged = tip.HasValue != oldTip.HasValue
            || (tip is { } now && oldTip is { } then && now.DistanceSquaredTo(then) > HoleStill * HoleStill);
        var livePath = drawn?.LivePath ?? [];
        if (tipChanged || moved[^1] || retension)
        {
            livePath = LiveSuturePath(holes[^1], tip, slack, info.Layer);
            SetTubes(root, "Live", [livePath]);
        }
        else
        {
            // Compare subsequent movement against the endpoint actually in the mesh, not the previous frame.
            tip = oldTip;
        }
        var holding = live.Contains(true);
        var tied = info.Final && holding;
        var finalChanged = (drawn?.Final ?? false) != info.Final;
        if (moved.Contains(true) || !wasLive.SequenceEqual(live) || finalChanged)
        {
            var routed = holes.Length > 1 && holding;
            SetTubes(root, "StartKnot", [routed ? TerminalKnot(holes[0], holes[1] - holes[0], -1f) : []]);
            SetTubes(root, "EndKnot", [tied ? TerminalKnot(holes[^1], holes[^2] - holes[^1], 1f) : []]);
        }
        var pressure = drawn?.PressureAmount ?? 0f;
        if (routesChanged || finalChanged)
        {
            pressure = SuturePressure(root, holes, info.Layer, info.Tension, holding);
        }
        return new DrawnSuture(holes, info.Tension, live, tip, info.Final, routes, livePath, pressure, holes.Length);
    }

    /// <summary>A cable between fixed ends at equilibrium. Its arc length comes from the simulated thread spring;
    /// gravity gives a free strand its sag, while an exposed stitch settles sideways onto its sewn layer, one radius
    /// above it.</summary>
    private Vector3[] SutureCablePath(Vector3 from, Vector3 to, float paidLength, TissueDepth layer, bool freeEnd = false)
    {
        var delta = to - from;
        var chord = delta.Length();
        if (chord < 0.0001f)
        {
            return [];
        }
        var along = delta / chord;
        var length = Mathf.Max(paidLength, chord);
        // For a shallow parabolic cable, excess arc length is approximately 8*sag^2/(3*chord).
        var sag = Mathf.Min(Mathf.Sqrt(Mathf.Max((length - chord) * chord * 0.375f, 0f)), chord * 0.3f);
        var localGravity = (Site.GlobalBasis.Inverse() * Vector3.Down).Normalized();
        var sagDirection = (localGravity - along * localGravity.Dot(along)).Normalized();
        if (sagDirection == Vector3.Zero)
        {
            sagDirection = along.Cross(Vector3.Up).Normalized();
        }
        if (!freeEnd)
        {
            // Gravity presses the cable into its layer. Once supported, its spare length lies in the tangent plane
            // instead of forming airborne arches.
            sagDirection = new Vector3(sagDirection.X, 0f, sagDirection.Z).Normalized();
            if (sagDirection == Vector3.Zero)
            {
                sagDirection = along.Cross(Vector3.Up).Normalized();
            }
        }
        var path = new Vector3[SutureSamples];
        for (var sample = 0; sample < SutureSamples; sample++)
        {
            var t = (float)sample / (SutureSamples - 1);
            var p = from.Lerp(to, t) + sagDirection * sag * 4f * t * (1f - t);
            if (sample > 0 && sample < SutureSamples - 1)
            {
                var uv = LocalToUv(p);
                if (new Rect2(Vector2.Zero, Vector2.One).HasPoint(uv))
                {
                    var height = layer == TissueDepth.Skin ? SkinHeight(uv) : SutureLayerHeight(uv, layer);
                    p.Y = Mathf.Max(p.Y, height + SutureRadius * 1.15f);
                }
            }
            else if (!freeEnd)
            {
                p -= Vector3.Up * SutureEntryDepth;
            }
            path[sample] = p;
        }
        return path;
    }

    /// <summary>Deep cables rest on their own drawn layer, not on the skin covering it. Interpolates the same grid used
    /// by its mesh.</summary>
    private float SutureLayerHeight(Vector2 uv, TissueDepth layer)
    {
        var index = (int)layer - 1;
        var grid = uv.Clamp(Vector2.Zero, Vector2.One) * new Vector2(Tissue.ResX, Tissue.ResY);
        var cell = new Vector2I(Math.Min((int)grid.X, Tissue.ResX - 1), Math.Min((int)grid.Y, Tissue.ResY - 1));
        var f = grid - (Vector2)cell;
        var a = LayerPoint(index, Tissue.Index(cell.X, cell.Y)).Y;
        var b = LayerPoint(index, Tissue.Index(cell.X + 1, cell.Y)).Y;
        var c = LayerPoint(index, Tissue.Index(cell.X, cell.Y + 1)).Y;
        var d = LayerPoint(index, Tissue.Index(cell.X + 1, cell.Y + 1)).Y;
        // The layer mesh uses the b-c diagonal rather than a bilinear patch.
        var height = f.X + f.Y <= 1f
            ? a + (b - a) * f.X + (c - a) * f.Y
            : d + (c - d) * (1f - f.X) + (b - d) * (1f - f.Y);
        return height - LayerTop(index);
    }

    /// <summary>The free strand pays out from its newest puncture, sags under gravity and ends exactly at the moving
    /// needle tip.</summary>
    private Vector3[] LiveSuturePath(Vector3 hole, Vector3? tip, float slack, TissueDepth layer)
    {
        if (tip is not { } end)
        {
            return [];
        }
        var path = SutureCablePath(hole, end, hole.DistanceTo(end) * Mathf.Lerp(1.02f, 1.15f, slack), layer, freeEnd: true);
        // Unlike both puncture ends of an exposed span, the needle end must meet the metal tip exactly.
        if (path.Length > 0)
        {
            path[0] -= Vector3.Up * SutureEntryDepth;
            path[^1] = end;
        }
        return path;
    }

    /// <summary>A compact figure-eight loop at a terminal puncture reads as tied thread without becoming a large
    /// decorative bow.</summary>
    private static Vector3[] TerminalKnot(Vector3 hole, Vector3 direction, float handedness)
    {
        var forward = new Vector3(direction.X, 0f, direction.Z).Normalized();
        if (forward == Vector3.Zero)
        {
            forward = Vector3.Right;
        }
        var side = Vector3.Up.Cross(forward).Normalized() * handedness;
        var center = hole + Vector3.Up * SutureRadius * 1.3f;
        var path = new Vector3[17];
        for (var sample = 0; sample < 17; sample++)
        {
            var angle = Mathf.Tau * sample / 16f;
            path[sample] = center + forward * Mathf.Sin(angle) * 0.00105f + side * Mathf.Sin(angle * 2f) * 0.00062f
                + Vector3.Up * (1f - Mathf.Cos(angle * 2f)) * 0.00012f;
        }
        return path;
    }

    /// <summary>Skin-aligned compression creases around every puncture. Correct tension leaves a faint mark;
    /// tightening toward the tear threshold enlarges and darkens it. Loose, deep-layer and torn-through threads show
    /// none. Returns how strongly they show (0..1).</summary>
    private float SuturePressure(Node3D threadRoot, Vector3[] holes, TissueDepth layer, float tension, bool holding)
    {
        var marks = threadRoot.GetNodeOrNull<MultiMeshInstance3D>("Pressure");
        if (marks is null)
        {
            marks = new MultiMeshInstance3D
            {
                Name = "Pressure",
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Multimesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = _suturePressureMesh },
            };
            threadRoot.AddChild(marks);
        }
        var skin = layer == TissueDepth.Skin && holding;
        var amount = skin
            ? Mathf.Clamp(Mathf.InverseLerp(TissueSim.ThreadClosed[(int)layer] + 0.04f, TissueSim.ThreadTear[(int)layer], tension), 0f, 1f)
            : 0f;
        marks.Multimesh.InstanceCount = holes.Length;
        for (var i = 0; i < holes.Length; i++)
        {
            var neighbor = holes[Math.Min(i + 1, holes.Length - 1)] - holes[Math.Max(i - 1, 0)];
            var yaw = Mathf.Atan2(neighbor.X, neighbor.Z) + (i % 2 == 0 ? 0.35f : -0.2f);
            var basis = new Basis(Vector3.Up, yaw).Scaled(Vector3.One * Mathf.Lerp(0.68f, 1.15f, amount));
            marks.Multimesh.SetInstanceTransform(i, new Transform3D(basis, holes[i]));
        }
        marks.SetInstanceShaderParameter("pressure", amount);
        // Kept submitted at zero alpha while loose so the pressure shader is compiled before tightening becomes visible.
        marks.Visible = skin;
        return amount;
    }

    /// <summary>All of a thread's paths under one name share a mesh: separate tubes, one mesh build, so tightening
    /// that moves every puncture together doesn't spike a frame.</summary>
    private void SetTubes(Node3D threadRoot, string meshName, IReadOnlyList<Vector3[]> paths)
    {
        var tubes = threadRoot.GetNodeOrNull<MeshInstance3D>(meshName);
        if (tubes is null)
        {
            tubes = new MeshInstance3D { Name = meshName, MaterialOverride = _sutureMaterial };
            threadRoot.AddChild(tubes);
        }
        var drawn = paths.Where(path => path.Length > 1).ToList();
        tubes.Mesh = drawn.Count > 0 ? Shapes.Tubes(drawn, SutureRadius, SutureRadius, 6) : null;
    }
}
