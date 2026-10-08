namespace Scalpel.Patients;

/// <summary>How deep a severed spring was cut: through the skin, into the fat, or through the muscle into the cavity.
/// </summary>
public enum TissueDepth { None, Skin, Fat, Muscle }

/// <summary>A spring of the tissue grid, or a stitch (thread, staple, suture) added across a cut.</summary>
public enum SpringKind : byte { Tissue, Stitch }

/// <summary>One spring between two particles. Springs are only ever appended, so an index names the same spring on
/// every peer.</summary>
public struct Spring
{
    public int A { get; set; }
    public int B { get; set; }
    public float Rest { get; set; }
    public bool Active { get; set; }
    public SpringKind Kind { get; set; }
    /// <summary>Stretch (relative to rest) past which it snaps.</summary>
    public float Break { get; set; }
    public TissueDepth Depth { get; set; }
    /// <summary>Where along a severed spring (0 at A, 1 at B) the blade crossed it. A tear splits it in the middle.
    /// </summary>
    public float Cross { get; set; }
    /// <summary>Which way the cut ran where it crossed a severed spring (site x, z): its edges are drawn back square
    /// to it.</summary>
    public Vector2 CutDir { get; set; }
    /// <summary>Cut through the muscle, and the muscle has been stitched: counts as cut only into the fat.</summary>
    public bool MuscleClosed { get; set; }
    /// <summary>The subcutaneous layer has been closed: the same cut then counts as skin-only.</summary>
    public bool FatClosed { get; set; }
}

/// <summary>A tool holding the skin: the particle it pins and where it pulls it (site-local).</summary>
public readonly record struct TissueGrip(int Key, int Particle, Vector3 Target);

/// <summary>A spring that snapped on the host: its ends (uv), kind and index.</summary>
public readonly record struct SnappedSpring(Vector2 A, Vector2 B, SpringKind Kind, int Spring);

/// <summary>
/// A running suture: its holes (particles) in order, the span from each hole to the next, each span's extra length
/// (<see cref="TissueSim.ThreadSlack"/>), the layer it's sewn in, its tension and whether it's tied off or torn. The
/// thread keeps its route, so pulling its free end changes every span.
/// </summary>
public sealed class SutureThread(TissueDepth layer, float tension)
{
    public List<int> Anchors { get; } = [];
    public List<int> Springs { get; } = [];
    public List<float> Slack { get; } = [];
    public TissueDepth Layer { get; set; } = layer;
    public float Tension { get; set; } = tension;
    public bool Final { get; set; }
}

/// <summary>
/// Position-based soft tissue for the surgical site skin.
/// A grid of particles joined by springs, all in site-local space. Cells are about <see cref="Cell"/> meters square on
/// every site.
/// <list type="bullet">
/// <item>Springs are a little shorter than the grid spacing, so the skin is under tension. Cut the springs along an
/// incision and the edges pull apart on their own.</item>
/// <item>Every particle is weakly anchored to where it sits on the body (fascia). Near an incision the anchor is
/// loosened, like undermined skin, and the cut edges are drawn back a little, more the deeper the cut: skin gapes, fat
/// bulges apart, cut muscle retracts.</item>
/// <item>Each severed spring remembers where along it the blade crossed (<see cref="Spring.Cross"/>), so the layers are
/// drawn split exactly along the blade's path, not along the grid (PatientBody).</item>
/// <item>Tools pin particles to their tip: forceps and a retractor stretch the skin, a spreader's jaws hold a cut's
/// edges apart. A grip drags a patch of skin around it along with falloff (a pinched fold, not a single point), so a
/// pull spreads out and the skin stretches over a wide area instead of tearing right beside the tip.</item>
/// <item>Springs stretched past their limit snap (host only). The host turns that into a tear.</item>
/// <item>Sutures add new springs across a cut. Their rest length is the stitch tension. A cut only counts as closed
/// where its edges have actually come together: a loose stitch leaves a gap that stays open (and bleeds).</item>
/// <item>A cut through the muscle retracts: the muscle pulls the edges further apart until it's stitched itself
/// (<see cref="MuscleStitch"/>). Skin can't be closed over open muscle, see Patient.CloseAt().</item>
/// <item>Only an active window is simulated: the cells around cuts, grips and skin that moved, plus a margin. The rest
/// of the grid holds still where it settled. The sim sleeps when nothing moves, so an untouched patient costs nothing.
/// </item>
/// </list>
/// </summary>
public sealed class TissueSim
{
    /// <summary>Grid spacing (meters), or wider on a big site so it has no more than MaxCells cells: a whole belly
    /// folded open moves every particle at once, and that has to fit a frame. The site's size sets how many cells it
    /// has along each side, at least MinRes.</summary>
    public const float Cell = 0.006f;
    public const int MaxCells = 1000;
    public const int MinRes = 6;
    /// <summary>A running thread's tension (the needle's wheel, see <see cref="ThreadTension"/>) per layer it's sewn
    /// in (by TissueDepth): looser than ThreadClosed the edges don't meet, fully slack from ThreadLoose, and tighter
    /// than ThreadTear it cuts through. Deeper tissue is more tethered: fat barely gives, and muscle needs a firmer
    /// pull but takes more load than skin.</summary>
    public static readonly float[] ThreadLoose = [0f, 1.23f, 1.12f, 1.02f];
    public static readonly float[] ThreadClosed = [0f, 1.08f, 0.98f, 0.86f];
    public static readonly float[] ThreadTear = [0f, 0.60f, 0.56f, 0.52f];
    public const float Tension = 0.93f;
    public const float Anchor = 0.02f;
    /// <summary>Skin pulled this far (meters) from its spot isn't held there any more, see Substep().</summary>
    public const float AnchorReach = 0.05f;
    /// <summary>A cut's edge lifted this far (meters) off its spot isn't drawn back from the cut any more.</summary>
    public const float LiftedOff = 0.005f;
    /// <summary>How far each edge of a cut is drawn back from it (meters) by cut depth (none, skin, fat, muscle), and
    /// how firmly. Skin gapes a little under its own tension, cut fat bulges apart, cut muscle retracts hard. The pull
    /// fades out over RetractSpread (meters) from the cut, so the stretch is shared by the skin around it, and over
    /// RetractTaper (meters) toward the cut's ends, where it stays closed: the cut opens like a lens.</summary>
    public static readonly float[] Retract = [0f, 0.0022f, 0.0035f, 0.015f];
    public static readonly float[] RetractSpread = [0f, 0.03f, 0.045f, 0.04f];
    public static readonly float[] RetractTaper = [0f, 0.008f, 0.012f, 0.03f];
    public const float GapePull = 0.03f;
    public const float MusclePull = 0.05f;
    public const float LooseAnchor = 0.006f;
    /// <summary>Skin this close to a cut (meters) is loosened from what's under it.</summary>
    public const float LooseRadius = 0.012f;
    /// <summary>How far (uv) a grip drags the skin around it along.</summary>
    public const float GripPatch = 0.15f;
    /// <summary>How firmly a grip drags the skin per solver iteration at its center.</summary>
    public const float GripDrag = 1f;
    /// <summary>Skin this close to a grip (meters, at rest) is held by it (see Hold()): forceps hold a few millimeters
    /// of skin, not a point, so a fine grid doesn't tear right beside the jaws.</summary>
    public const float GripHold = 0.01f;
    /// <summary>Skin under the drape stays at least this far (meters) below the height folded-out skin lies at on top
    /// of it.</summary>
    public const float UnderDrape = 0.006f;
    /// <summary>How far (meters) from its rest point skin still rests on the body (see StayOnBody()).</summary>
    public const float BodyReach = 0.01f;
    /// <summary>Pulls up to this far (meters) drag the patch fully, twice as far not at all.</summary>
    public const float DragReach = 0.035f;
    /// <summary>Skin that moved further than this (meters) from where it settled is shown simulated.</summary>
    public const float RegionMove = 0.001f;
    public const float Damping = 0.88f;
    public const int Iterations = 4;
    /// <summary>Extra solver passes over the stitches each iteration. Thread is far stiffer than skin: solved as often
    /// as the skin, a stitch would give way to the stretched skin around it and the edges would never meet.</summary>
    public const int StitchPasses = 3;
    /// <summary>Global intact-tissue elasticity, independent of suture tear-through (ThreadTear). A gentle 5 cm pull
    /// stretches intact skin; the hard 8 cm pull still tears it. Scalpel cuts sever springs directly, without this
    /// threshold.</summary>
    public const float TissueBreak = 3f;
    /// <summary>One stitch pulls together every cut spring this close to it (meters, at rest): a stitch closes a few
    /// millimeters of the cut, however fine the grid.</summary>
    public const float StitchReach = 0.005f;
    /// <summary>Closest to either end of a spring (share of its length) the lip of a cut through it is drawn.</summary>
    public const float CrossMargin = 0.2f;
    public const float Step = 1f / 30f;
    /// <summary>Most solver steps one frame may run to catch up, so a long frame doesn't turn into a longer one.
    /// </summary>
    public const int MaxCatchUp = 2;
    public const float SleepEpsilon = 0.00002f;
    public const int SleepSteps = 20;
    /// <summary>Most solver steps the skin gets to settle at build time (see Settle()).</summary>
    public const int SettleSteps = 300;
    /// <summary>Cells of still skin simulated around the active area.</summary>
    public const int WindowMargin = 4;
    /// <summary>Skin that moved further than this (meters) widens the window; less than that, it's drawn by the body
    /// model anyway.</summary>
    public const float WindowMove = 0.002f;
    /// <summary>How often (solver steps) the window is redrawn.</summary>
    public const int WindowRefresh = 6;
    /// <summary>A gap counts as open once it's pulled this far apart (meters).</summary>
    public const float OpenGap = 0.004f;
    /// <summary>A cut whose edges are this close (meters) is closed (<see cref="GapsNear"/>): no gap shows between
    /// them.</summary>
    public const float ClosedGap = 0.001f;
    /// <summary>A stitch, staple or thread span this close (meters) holds a cut shut (<see cref="HeldNear"/>): the
    /// edges between closures a little apart meet on their own, a cut's end lying shut with nothing near it isn't
    /// closed.</summary>
    public const float HoldReach = 0.015f;
    /// <summary>Most of the grid a piece of skin cut out all round may hold: past that the cut just runs round most of
    /// the site.</summary>
    public const float PieceMax = 0.25f;
    /// <summary>Cells per side of the grid that sorts skin triangles by where they lie now, for SkinHeight() once the
    /// skin moved far (a flap folded back). It covers the site and a margin around it.</summary>
    private const int Bins = 16;
    private const float BinMargin = 0.1f;

    public Vector2 Size { get; private set; } = Vector2.One;
    /// <summary>Cells along u; particles per row is ResX + 1.</summary>
    public int ResX { get; private set; } = 24;
    /// <summary>Cells along v.</summary>
    public int ResY { get; private set; } = 24;
    public Vector3[] Rest { get; private set; } = [];
    public Vector3[] Pos { get; private set; } = [];
    private Vector3[] _prev = [];
    private float[] _anchor = [];
    /// <summary>Lift (meters, along the surface normal) of stitched cut edges, see <see cref="SuturePucker"/>. A thread
    /// pulls the edges together in the surface plane: lifted apart from that, they rise into a lip instead of passing
    /// through each other.</summary>
    public float[] SutureLip { get; private set; } = [];
    private bool[] _fixed = [];
    /// <summary>True for particles where the site hangs off the body (past a limb's or the flank's edge): not drawn
    /// and not part of the region while they hang there, so the site can't stick out of the body (see
    /// <see cref="HangingOff"/>). They're still simulated, so skin next to them moves as it always did.</summary>
    public bool[] Off { get; private set; } = [];

    private Spring[] _springs = new Spring[256];
    private int _springCount;

    /// <summary>Grid springs leaving each particle (-1 past the grid's edge): to the right, down, the (i+1, j)-(i, j+1)
    /// diagonal and the other diagonal. The drawn triangles of cell (i, j) use the first three of particle (i, j) and
    /// its neighbours, see <see cref="Triangles"/>.</summary>
    public int[] SpringRight { get; private set; } = [];
    public int[] SpringDown { get; private set; } = [];
    public int[] SpringDiag { get; private set; } = [];
    public int[] SpringAnti { get; private set; } = [];
    /// <summary>Where the anchor pulls each particle: its rest position, moved back from a cut.</summary>
    private Vector3[] _anchorTarget = [];
    /// <summary>Which way is out of the body at each particle's rest point (unit), see StayOnBody().</summary>
    private Vector3[] _restNormal = [];
    /// <summary>What exposed skin folded out of the site can't go below (the drape): site-local (x, z) -> height, NaN
    /// where there's nothing. Null: nothing to lie on. Only particles marked in <see cref="Exposed"/> are held up by it.
    /// </summary>
    public Func<float, float, float>? FloorAt { get; set; }
    public bool[] Exposed { get; set; } = [];
    /// <summary>The drape's opening (site x, z): no drape anywhere in it, so skin there doesn't need FloorAt. Empty:
    /// unknown.</summary>
    public Rect2 FloorOpen { get; set; }
    /// <summary>Where each particle rests under the skin's own tension before anything touches it (see Settle()).
    /// </summary>
    public Vector3[] Settled { get; private set; } = [];
    /// <summary>Only the host decides when springs snap, so tears happen once for everyone.</summary>
    public bool Tearing { get; set; }
    /// <summary>Springs that snapped since the host last read them.</summary>
    public List<SnappedSpring> Snapped { get; } = [];
    /// <summary>Scales how far past its rest length a spring stretches before it snaps (thin skin tears sooner).
    /// </summary>
    public float BreakMult { get; set; } = 1f;
    /// <summary>True for skin taken off (cut out all round and lifted away, a graft): the skin layer has a hole there
    /// and what lies under it shows.</summary>
    public bool[] Excised { get; private set; } = [];
    /// <summary>Changes whenever springs are cut, stitched or snap (meshes rebuild their triangles).</summary>
    public int TopologyVersion { get; private set; }
    /// <summary>Counts simulation steps, so meshes only rebuild when something moved.</summary>
    public int StepsDone { get; internal set; }

    private sealed class Pin(int particle, Vector3 target)
    {
        public int Particle { get; } = particle;
        public Vector3 Target { get; set; } = target;
    }

    /// <summary>The skin a grip on a particle drags along: the particles, how strongly each, and the ones it holds.
    /// </summary>
    private sealed record Patch(int[] Around, float[] Weights, int[] Held);

    private readonly Dictionary<int, Pin> _pins = [];
    /// <summary>Particle -> the patch it drags along when gripped, for the topology it was worked out for.</summary>
    private readonly Dictionary<int, Patch> _patches = [];
    private int _patchVersion = -1;
    /// <summary>Spring indices touching each particle.</summary>
    private List<int>[] _springsOf = [];
    private int _stillSteps;
    private float _accumulator;
    private readonly Dictionary<long, int> _edgeToStitch = [];
    /// <summary>Tissue springs that are cut or snapped. Gap and mesh queries only look at these.</summary>
    private readonly List<int> _severed = [];
    /// <summary>Every stitch spring, active or not.</summary>
    private readonly List<int> _stitches = [];
    private readonly Dictionary<int, SutureThread> _threads = [];
    /// <summary>1 for particles the solver may move this step, 0 for the fixed border, pinned particles and still skin
    /// outside the active window.</summary>
    private float[] _free = [];
    /// <summary>The active window: particle columns and rows [x0, x1] x [y0, y1], empty when nothing needs
    /// simulating.</summary>
    private Rect2I _win;
    /// <summary>Movable particles inside the window, and every grid spring touching one.</summary>
    private int[] _winParticles = [];
    private int[] _winSprings = [];
    private int _winSteps;
    private bool _winDirty = true;
    /// <summary>While the skin settles at build time the whole grid is simulated.</summary>
    private bool _winLocked;
    /// <summary>Every cut as a uv segment, in the order they were made.</summary>
    private readonly List<(Vector2 A, Vector2 B)> _cutSegments = [];
    /// <summary>Strokes: runs of segments each starting where the last one ended (a blade moved without lifting it), as
    /// where it started and the point of it farthest from there. Those two are its ends.</summary>
    private readonly List<Vector2[]> _strokes = [];
    /// <summary>Which stroke each segment belongs to.</summary>
    private readonly List<int> _segmentStroke = [];
    /// <summary>The ends of the cuts (uv), worked out again whenever a cut is added.</summary>
    private readonly List<Vector2> _cutEnds = [];
    private int _cutEndsFor = -1;
    /// <summary>Set when the sim falls asleep: the next window is picked afresh, otherwise it only grows while the skin
    /// moves (shrinking a window around moving skin would hold skin at its edge where it doesn't rest, and it moves
    /// again).</summary>
    private bool _winFresh = true;
    /// <summary>Particles drawn back from a cut (see UpdateRetraction()), so the next update can let go of them.
    /// </summary>
    private readonly List<int> _retracted = [];
    /// <summary>Per particle, while UpdateRetraction() works: the summed pull away from cuts and how far it reaches.
    /// </summary>
    private Vector3[] _pull = [];
    private float[] _pullAmount = [];
    private bool _retractDirty;
    /// <summary>Farthest any particle has moved from its rest position (meters, squared), as of the last step.</summary>
    private float _farthest;
    /// <summary>Skin triangles (particle indices) and, per bin, the first index in them of each triangle overlapping
    /// it.</summary>
    private int[] _binTriangles = [];
    private readonly List<int>[] _bins = [.. Enumerable.Range(0, Bins * Bins).Select(_ => new List<int>())];
    private (int Topology, int Steps) _binsFor = (-1, -1);
    /// <summary>True for skin cut free from what's around it (see StayAboveFloor()), for the topology it was worked
    /// out for.</summary>
    private bool[] _cutFree = [];
    private int _cutFreeFor = -1;
    /// <summary>Particles off the body (see Off).</summary>
    private int[] _offList = [];
    private readonly List<int> _excisedList = [];
    private bool[] _hanging = [];
    private (int Steps, bool HasFloor) _hangingFor = (-1, false);
    /// <summary>Particles with a suture lip, for UpdateRetraction().</summary>
    private List<int> _lipped = [];

    public int SpringCount => _springCount;

    /// <summary>Every spring so far, grid springs first, then stitches in the order they were made.</summary>
    public ReadOnlySpan<Spring> Springs => new(_springs, 0, _springCount);

    public ref readonly Spring SpringAt(int s) => ref _springs[s];

    /// <summary>
    /// Lays out the grid over a site of <paramref name="siteSize"/> meters. <paramref name="heightAt"/>(uv): skin
    /// height above the site plane. <paramref name="onBody"/>(uv): false where the site is off the body.
    /// </summary>
    public void Build(Vector2 siteSize, Func<Vector2, float> heightAt, Func<Vector2, bool>? onBody = null)
    {
        Size = siteSize;
        // In double, as the sizes were laid out: a site a whole number of cells long lands right on a cell boundary,
        // and float rounding would drop that last row.
        var cell = Math.Max(Cell, Math.Sqrt((double)Size.X * Size.Y / MaxCells));
        ResX = Math.Max((int)Math.Ceiling(Size.X / cell), MinRes);
        ResY = Math.Max((int)Math.Ceiling(Size.Y / cell), MinRes);
        var count = (ResX + 1) * (ResY + 1);
        Rest = new Vector3[count];
        _anchor = new float[count];
        SutureLip = new float[count];
        _fixed = new bool[count];
        Off = new bool[count];
        Excised = new bool[count];
        _excisedList.Clear();
        for (var j = 0; j <= ResY; j++)
        {
            for (var i = 0; i <= ResX; i++)
            {
                var uv = new Vector2((float)i / ResX, (float)j / ResY);
                var k = Index(i, j);
                Rest[k] = new Vector3((uv.X - 0.5f) * Size.X, heightAt(uv), (uv.Y - 0.5f) * Size.Y);
                _anchor[k] = Anchor;
                Off[k] = onBody is not null && !onBody(uv);
                _fixed[k] = i == 0 || j == 0 || i == ResX || j == ResY;
            }
        }
        Pos = (Vector3[])Rest.Clone();
        _prev = (Vector3[])Rest.Clone();
        _anchorTarget = (Vector3[])Rest.Clone();
        _restNormal = new Vector3[count];
        for (var j = 0; j <= ResY; j++)
        {
            for (var i = 0; i <= ResX; i++)
            {
                var dx = Rest[Index(Math.Min(i + 1, ResX), j)] - Rest[Index(Math.Max(i - 1, 0), j)];
                var dz = Rest[Index(i, Math.Min(j + 1, ResY))] - Rest[Index(i, Math.Max(j - 1, 0))];
                var normal = dz.Cross(dx).Normalized();
                _restNormal[Index(i, j)] = normal.Y >= 0f ? normal : -normal;
            }
        }
        _offList = [.. Enumerable.Range(0, count).Where(k => Off[k])];
        _free = new float[count];
        SpringRight = Filled(count, -1);
        SpringDown = Filled(count, -1);
        SpringDiag = Filled(count, -1);
        SpringAnti = Filled(count, -1);
        for (var j = 0; j <= ResY; j++)
        {
            for (var i = 0; i <= ResX; i++)
            {
                var k = Index(i, j);
                if (i < ResX)
                {
                    SpringRight[k] = AddSpring(k, Index(i + 1, j));
                }
                if (j < ResY)
                {
                    SpringDown[k] = AddSpring(k, Index(i, j + 1));
                }
                if (i < ResX && j < ResY)
                {
                    SpringDiag[k] = AddSpring(Index(i + 1, j), Index(i, j + 1));
                    SpringAnti[k] = AddSpring(k, Index(i + 1, j + 1));
                }
            }
        }
        Settle();
    }

    private static int[] Filled(int count, int value)
    {
        var array = new int[count];
        Array.Fill(array, value);
        return array;
    }

    /// <summary>
    /// Lets the skin settle under its own tension before anything touches it. Over a curved body tension pulls the
    /// sheet a few millimeters off the body's shape at the site's edges; that's where it rests, not a movement (see
    /// <see cref="Region"/>). A flat site is settled at once and falls asleep within SleepSteps.
    /// Springs left stretched far at the edge of a round limb (where the site hangs off it) would snap the moment a cut
    /// wakes the skin, so their breaking point is moved past how far they're stretched at rest. Springs in the site's
    /// outermost strip (its fixed border and the row of skin along it, under the drape's frame) or to skin off the body
    /// never snap: they're never drawn, a tear there would only be a bleed nobody can see or reach.
    /// </summary>
    private void Settle()
    {
        SetWindow(new Rect2I(0, 0, ResX, ResY));
        _winLocked = true;
        for (var step = 0; step < SettleSteps && !IsSleeping; step++)
        {
            Substep();
        }
        _winLocked = false;
        Settled = (Vector3[])Pos.Clone();
        _prev = (Vector3[])Pos.Clone();
        StepsDone = 0;
        for (var s = 0; s < _springCount; s++)
        {
            ref var spring = ref _springs[s];
            var stretched = Pos[spring.A].DistanceTo(Pos[spring.B]) / spring.Rest;
            spring.Break = Mathf.Max(spring.Break, 1f + 2f * (stretched * 1.4f - 1f));
            if (AtBorder(spring.A) || AtBorder(spring.B) || Off[spring.A] || Off[spring.B])
            {
                spring.Break = float.PositiveInfinity;
            }
        }
        _winDirty = true;
    }

    /// <summary>Particle k is on the site's fixed border or right next to it.</summary>
    private bool AtBorder(int k)
    {
        var at = CellOf(k);
        return at.X <= 1 || at.Y <= 1 || at.X >= ResX - 1 || at.Y >= ResY - 1;
    }

    public int Index(int i, int j) => j * (ResX + 1) + i;

    public Vector2 UvOf(int k) => new((float)(k % (ResX + 1)) / ResX, (float)(k / (ResX + 1)) / ResY);

    /// <summary>Column and row of particle k.</summary>
    public Vector2I CellOf(int k) => new(k % (ResX + 1), k / (ResX + 1));

    public int Nearest(Vector2 uv)
    {
        var i = Math.Clamp(Mathf.RoundToInt(uv.X * ResX), 1, ResX - 1);
        var j = Math.Clamp(Mathf.RoundToInt(uv.Y * ResY), 1, ResY - 1);
        return Index(i, j);
    }

    /// <summary>Where along severed spring s the blade crossed it, in uv.</summary>
    public Vector2 CrossingUv(int s) => UvOf(_springs[s].A).Lerp(UvOf(_springs[s].B), _springs[s].Cross);

    private Vector2 MiddleUv(int s) => (UvOf(_springs[s].A) + UvOf(_springs[s].B)) * 0.5f;

    public bool AnySevered => _severed.Count > 0;

    /// <summary>Tissue springs that are cut or snapped.</summary>
    public IReadOnlyList<int> Severed => _severed;

    /// <summary>How deep spring s is cut (None while it's whole). Stitched muscle counts as cut only into the fat.
    /// </summary>
    public TissueDepth CutDepth(int s)
    {
        if (s < 0 || _springs[s].Active || _springs[s].Kind != SpringKind.Tissue)
        {
            return TissueDepth.None;
        }
        // A seam stitched to contact length is drawn as one pressed surface. A looser stitch keeps the split edges and
        // walls until it's short enough for them to meet.
        var stitch = _edgeToStitch.GetValueOrDefault(EdgeKey(_springs[s].A, _springs[s].B), -1);
        if (stitch >= 0 && _springs[stitch].Active
            && _springs[stitch].Rest <= Rest[_springs[s].A].DistanceTo(Rest[_springs[s].B]) * 1.05f)
        {
            return TissueDepth.None;
        }
        return DepthOf(s);
    }

    /// <summary>
    /// Grid points where the simulated skin has to take over from the body model: within <paramref name="reach"/>
    /// points of a cut, of skin a tool is holding, or of skin that moved visibly. 1 inside, 0 outside, one byte per
    /// particle.
    /// </summary>
    public byte[] Region(int reach = 1)
    {
        var region = new byte[Rest.Length];
        var seeds = new List<int>();
        foreach (var s in _severed)
        {
            seeds.Add(_springs[s].A);
            seeds.Add(_springs[s].B);
        }
        seeds.AddRange(_pins.Values.Select(pin => pin.Particle));
        foreach (var thread in _threads.Values)
        {
            seeds.AddRange(thread.Anchors);
        }
        seeds.AddRange(_excisedList);
        // Only skin in the window moves: everything outside it holds still where it settled.
        foreach (var k in _winParticles)
        {
            if (Pos[k].DistanceSquaredTo(Settled[k]) > RegionMove * RegionMove)
            {
                seeds.Add(k);
            }
        }
        foreach (var k in seeds)
        {
            var at = CellOf(k);
            for (var y = Math.Max(at.Y - reach, 0); y <= Math.Min(at.Y + reach, ResY); y++)
            {
                for (var x = Math.Max(at.X - reach, 0); x <= Math.Min(at.X + reach, ResX); x++)
                {
                    region[Index(x, y)] = 1;
                }
            }
        }
        // Off the body, and right next to it: triangles touching skin off the body aren't drawn, so the body model has
        // to cover up to there, or its cut-away edge (halfway between region points) would leave a gap.
        var hanging = HangingOff();
        foreach (var k in _offList)
        {
            if (!hanging[k])
            {
                continue;
            }
            var at = CellOf(k);
            for (var y = Math.Max(at.Y - 1, 0); y <= Math.Min(at.Y + 1, ResY); y++)
            {
                for (var x = Math.Max(at.X - 1, 0); x <= Math.Min(at.X + 1, ResX); x++)
                {
                    region[Index(x, y)] = 0;
                }
            }
        }
        return region;
    }

    /// <summary>Asleep once nothing has moved for SleepSteps steps, grips held still included: a moved grip wakes it.
    /// </summary>
    public bool IsSleeping => _stillSteps >= SleepSteps;

    public void Wake() => _stillSteps = 0;

    /// <summary>The topology changed: meshes, the window and the sleep state follow.</summary>
    private void Reshaped()
    {
        TopologyVersion++;
        _winDirty = true;
        Wake();
    }

    /// <summary>
    /// Severs every tissue spring the segment crosses down to the given depth and loosens the skin around it.
    /// A point exactly on the cut's line counts as on its left, so a cut through a grid point severs each spring once.
    /// </summary>
    public void Cut(Vector2 a, Vector2 b, TissueDepth depth)
    {
        if (a.IsEqualApprox(b))
        {
            return;
        }
        var count = _cutSegments.Count;
        var starts = _strokes.Count == 0 || count == 0 || !_cutSegments[count - 1].B.IsEqualApprox(a);
        if (starts)
        {
            _strokes.Add([a, a]);
        }
        var stroke = _strokes[^1];
        if (((b - stroke[0]) * Size).Length() > ((stroke[1] - stroke[0]) * Size).Length())
        {
            stroke[1] = b;
        }
        _cutSegments.Add((a, b));
        _segmentStroke.Add(_strokes.Count - 1);
        // A short cut on its own (a blade pressed in) might fall between springs: it severs the ones a cell's length of
        // its line crosses, so the sim has it at all. A stroke going on is cut where the blade really went.
        var cell = Mathf.Max(Size.X / ResX, Size.Y / ResY);
        var meters = ((b - a) * Size).Length();
        if (starts && meters < cell)
        {
            var middle = (a + b) * 0.5f;
            var half = (b - a) * (cell / meters) * 0.5f;
            a = middle - half;
            b = middle + half;
        }
        var gridEnd = new Vector2I(ResX, ResY);
        var low = new Vector2I(Mathf.FloorToInt(Mathf.Min(a.X, b.X) * ResX) - 1, Mathf.FloorToInt(Mathf.Min(a.Y, b.Y) * ResY) - 1)
            .Clamp(Vector2I.Zero, gridEnd);
        var high = new Vector2I(Mathf.CeilToInt(Mathf.Max(a.X, b.X) * ResX) + 1, Mathf.CeilToInt(Mathf.Max(a.Y, b.Y) * ResY) + 1)
            .Clamp(Vector2I.Zero, gridEnd);
        for (var j = low.Y; j <= high.Y; j++)
        {
            for (var i = low.X; i <= high.X; i++)
            {
                var k = Index(i, j);
                foreach (var s in GridSprings(k))
                {
                    if (s < 0 || _springs[s].Kind != SpringKind.Tissue)
                    {
                        continue;
                    }
                    var t = Crossing(UvOf(_springs[s].A), UvOf(_springs[s].B), a, b);
                    if (t >= 0f)
                    {
                        // A cut right through a grid point would split the triangles around it into slivers: the lip is
                        // drawn a little off it instead (at most a fifth of a cell).
                        Sever(s, depth, Mathf.Clamp(t, CrossMargin, 1f - CrossMargin), (b - a) * Size);
                    }
                }
            }
        }
        var reach = new Vector2I(Mathf.CeilToInt(LooseRadius / Size.X * ResX), Mathf.CeilToInt(LooseRadius / Size.Y * ResY));
        for (var j = Math.Max(low.Y - reach.Y, 0); j <= Math.Min(high.Y + reach.Y, ResY); j++)
        {
            for (var i = Math.Max(low.X - reach.X, 0); i <= Math.Min(high.X + reach.X, ResX); i++)
            {
                var k = Index(i, j);
                var uv = UvOf(k);
                if (((uv - Geometry2D.GetClosestPointToSegment(uv, a, b)) * Size).Length() < LooseRadius)
                {
                    _anchor[k] = LooseAnchor;
                }
            }
        }
        _retractDirty = true;
        Reshaped();
    }

    /// <summary>The grid springs leaving particle k (-1 past the grid's edge).</summary>
    private int[] GridSprings(int k) => [SpringRight[k], SpringDown[k], SpringDiag[k], SpringAnti[k]];

    /// <summary>
    /// Where segment a-b crosses the line from p to q, as a share of the way from p (0..1), or -1 if it doesn't.
    /// Half open on both: a point on the cut's line is on its left, the cut's end belongs to the next segment.
    /// </summary>
    private static float Crossing(Vector2 p, Vector2 q, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var sp = ab.Cross(p - a);
        var sq = ab.Cross(q - a);
        if (sp >= 0f == sq >= 0f)
        {
            return -1f;
        }
        var t = sp / (sp - sq);
        var along = (p.Lerp(q, t) - a).Dot(ab) / ab.LengthSquared();
        return along < 0f || along >= 1f ? -1f : t;
    }

    /// <summary>Stitches the muscle under every spring cut through it near uv. Returns how many springs it closed.
    /// </summary>
    public int MuscleStitch(Vector2 uv, float radius) => CloseLayer([uv], radius, TissueDepth.Muscle);

    /// <summary>
    /// Closes the muscle (depth Muscle) or the fat (Fat) under every spring cut through it within
    /// <paramref name="radius"/> of the line through <paramref name="points"/> (or of the one point). Fat closes only
    /// where the muscle under it is closed or uncut: deep bites through the connective tissue round the fat, it doesn't
    /// hold like skin or muscle. Returns how many springs it closed.
    /// </summary>
    public int CloseLayer(IReadOnlyList<Vector2> points, float radius, TissueDepth depth)
    {
        // At least a cell and a half of the grid: a cut's springs lie up to a cell off its line, which on a narrow site
        // (few cells across) is further than radius in uv.
        radius = Mathf.Max(radius, 1.5f / Math.Min(ResX, ResY));
        var closed = 0;
        foreach (var s in _severed)
        {
            if (DepthOf(s) == depth && DistanceToLine(MiddleUv(s), points) < radius)
            {
                if (depth == TissueDepth.Muscle)
                {
                    _springs[s].MuscleClosed = true;
                }
                else
                {
                    _springs[s].FatClosed = true;
                }
                closed++;
            }
        }
        if (closed > 0)
        {
            UpdateRetraction();
            Reshaped();
        }
        return closed;
    }

    /// <summary>True while muscle cut near uv hasn't been stitched: skin closed over it would be under too much
    /// tension.</summary>
    public bool MuscleOpenNear(Vector2 uv, float radius) =>
        _severed.Any(s => DepthOf(s) == TissueDepth.Muscle && MiddleUv(s).DistanceTo(uv) < radius);

    /// <summary>True while fat cut near uv hasn't been closed (<see cref="CloseLayer"/>), with the muscle under it
    /// closed or uncut.</summary>
    public bool FatOpenNear(Vector2 uv, float radius) =>
        _severed.Any(s => DepthOf(s) == TissueDepth.Fat && MiddleUv(s).DistanceTo(uv) < radius);

    /// <summary>True while a cut through the skin crosses within <paramref name="radius"/> (meters) of uv and isn't
    /// stitched there.</summary>
    public bool SkinOpenNear(Vector2 uv, float radius) =>
        _severed.Any(s => !Stitched(_springs[s].A, _springs[s].B) && ((CrossingUv(s) - uv) * Size).Length() < radius);

    /// <summary>How close (meters) to a point on a cut an unstitched crossing of it has to be for the cut to count as
    /// open there: half a grid cell, so wherever along a cut, one of the springs crossing it is that close.</summary>
    public float OpenReach => Mathf.Max(Size.X / ResX, Size.Y / ResY) * 0.5f;

    /// <summary>Widest the edges of a cut are pulled apart past their rest (meters, as the layers are drawn) within
    /// <paramref name="radius"/> (meters) of each of <paramref name="points"/> (uv). 0 where they meet, or nothing is
    /// cut.</summary>
    public float[] GapsNear(IReadOnlyList<Vector2> points, float radius)
    {
        var gaps = new float[points.Count];
        foreach (var s in _severed)
        {
            var spring = _springs[s];
            var gap = Pos[spring.A].DistanceTo(Pos[spring.B]) - Rest[spring.A].DistanceTo(Rest[spring.B]);
            if (gap <= 0f)
            {
                continue;
            }
            var crossing = CrossingUv(s);
            for (var i = 0; i < points.Count; i++)
            {
                if (((crossing - points[i]) * Size).Length() < radius)
                {
                    gaps[i] = Mathf.Max(gaps[i], gap);
                }
            }
        }
        return gaps;
    }

    /// <summary>True for each of <paramref name="points"/> (uv) with a stitch, staple or thread span within HoldReach
    /// holding the skin there.</summary>
    public bool[] HeldNear(IReadOnlyList<Vector2> points)
    {
        var held = new bool[points.Count];
        foreach (var s in _stitches)
        {
            if (!_springs[s].Active)
            {
                continue;
            }
            var middle = MiddleUv(s);
            for (var i = 0; i < points.Count; i++)
            {
                if (((middle - points[i]) * Size).Length() < HoldReach)
                {
                    held[i] = true;
                }
            }
        }
        return held;
    }

    /// <summary>How far (meters) from a point on a cut its edges are looked at to tell whether it's closed there: three
    /// quarters of a grid cell, so every point has a spring crossing the cut that close.</summary>
    public float SeamReach => OpenReach * 1.5f;

    /// <summary>True where a cut through the skin is closed for good at uv: its edges meet (GapsNear() within
    /// ClosedGap) and something holds them (HeldNear()). Patient.SettleClosures() counts a wound's closure the same
    /// way.</summary>
    public bool ClosedAt(Vector2 uv) => GapsNear([uv], SeamReach)[0] <= ClosedGap && HeldNear([uv])[0];

    /// <summary>
    /// Where a staple with its legs at a and b (uv, where they touch the skin as it lies now) goes across a cut: the
    /// crossing nearest its middle, with an unstitched edge there (SkinOpenNear()) and the cut not closed for good
    /// (ClosedAt()). (-1, -1) when a leg is in an opening rather than on an edge, or no open cut lies between them.
    /// </summary>
    public Vector2 StapleSpot(Vector2 a, Vector2 b)
    {
        var none = new Vector2(-1, -1);
        if (IsOpen(a, TissueDepth.Skin) || IsOpen(b, TissueDepth.Skin))
        {
            return none;
        }
        var middle = (a + b) * 0.5f;
        var best = none;
        foreach (var (from, to) in _cutSegments)
        {
            var t = Crossing(a, b, from, to);
            if (t < 0f)
            {
                continue;
            }
            var at = a.Lerp(b, t);
            if ((best.X < 0f || at.DistanceTo(middle) < best.DistanceTo(middle)) && SkinOpenNear(at, OpenReach) && !ClosedAt(at))
            {
                best = at;
            }
        }
        return best;
    }

    /// <summary>Where the line from a to b (uv) crosses a cut, nearest its middle: (-1, -1) when it crosses none.
    /// </summary>
    public Vector2 CutBetween(Vector2 a, Vector2 b)
    {
        var middle = (a + b) * 0.5f;
        var best = new Vector2(-1, -1);
        foreach (var (from, to) in _cutSegments)
        {
            var t = Crossing(a, b, from, to);
            if (t >= 0f && (best.X < 0f || a.Lerp(b, t).DistanceTo(middle) < best.DistanceTo(middle)))
            {
                best = a.Lerp(b, t);
            }
        }
        return best;
    }

    /// <summary>Raises both sides of the sewn edges within <paramref name="radius"/> of any of
    /// <paramref name="points"/> (a thread's crossings) along the surface normal. <paramref name="amount"/> is set, not
    /// added, so loosening the live thread lowers the lip again.</summary>
    public void SuturePucker(IReadOnlyList<Vector2> points, float radius, float amount)
    {
        foreach (var s in _severed)
        {
            var crossing = CrossingUv(s);
            if (!points.Any(p => p.DistanceTo(crossing) < radius))
            {
                continue;
            }
            foreach (var k in new[] { _springs[s].A, _springs[s].B })
            {
                if (SutureLip[k] == 0f && amount > 0f && !_lipped.Contains(k))
                {
                    _lipped.Add(k);
                }
                SutureLip[k] = amount;
            }
        }
        _retractDirty = true;
        Wake();
    }

    /// <summary>How deep spring s counts as cut: stitched muscle leaves only skin and fat open.</summary>
    public TissueDepth DepthOf(int s)
    {
        ref readonly var spring = ref _springs[s];
        if (spring.FatClosed && (spring.Depth == TissueDepth.Fat || spring.MuscleClosed))
        {
            return TissueDepth.Skin;
        }
        return spring.MuscleClosed ? TissueDepth.Fat : spring.Depth;
    }

    /// <summary>
    /// The particles of the piece of skin around particle k that cuts have set free all round: those reached from k
    /// without crossing a cut (a stitch across one joins), as long as that never reaches the site's border. Empty while
    /// the skin there is still joined to the rest, or was already taken off.
    /// </summary>
    public List<int> PieceOf(int k)
    {
        if (Excised[k])
        {
            return [];
        }
        IndexSprings();
        var piece = new List<int> { k };
        var seen = new HashSet<int> { k };
        for (var n = 0; n < piece.Count; n++)
        {
            var at = piece[n];
            if (AtBorder(at) || piece.Count > Rest.Length * PieceMax)
            {
                return [];
            }
            foreach (var s in _springsOf[at])
            {
                if (!_springs[s].Active && !Stitched(_springs[s].A, _springs[s].B))
                {
                    continue;
                }
                var other = _springs[s].A == at ? _springs[s].B : _springs[s].A;
                if (seen.Add(other))
                {
                    piece.Add(other);
                }
            }
        }
        return piece;
    }

    /// <summary>Takes off the piece of skin around particle k (see <see cref="PieceOf"/>): it's no longer drawn or
    /// touched, and grips on it let go. Returns how many grid points it held, 0 when there's no piece there.</summary>
    public int Excise(int k)
    {
        var piece = PieceOf(k);
        if (piece.Count == 0)
        {
            return 0;
        }
        foreach (var p in piece)
        {
            Excised[p] = true;
        }
        _excisedList.AddRange(piece);
        foreach (var key in _pins.Where(pin => Excised[pin.Value.Particle]).Select(pin => pin.Key).ToList())
        {
            _pins.Remove(key);
        }
        Reshaped();
        return piece.Count;
    }

    /// <summary>Pins the particle nearest uv to follow a tool.</summary>
    public void Grip(int key, Vector2 uv)
    {
        var k = Nearest(uv);
        _pins[key] = new Pin(k, Pos[k]);
        _winDirty = true;
        Wake();
    }

    /// <summary>
    /// Pins the particle nearest uv that lies on <paramref name="side"/> (a uv direction) of
    /// <paramref name="middle"/> now, like <see cref="Grip"/>. A spreader's jaw set right over a cut takes hold of its
    /// own edge this way, not the one across the gap. Returns the pinned particle, -1 if there is no skin there.
    /// </summary>
    public int GripBeside(int key, Vector2 uv, Vector2 middle, Vector2 side)
    {
        var best = -1;
        var bestDistance = float.PositiveInfinity;
        var at = new Vector2I(Mathf.RoundToInt(uv.X * ResX), Mathf.RoundToInt(uv.Y * ResY));
        for (var j = Math.Max(at.Y - 2, 1); j < Math.Min(at.Y + 3, ResY); j++)
        {
            for (var i = Math.Max(at.X - 2, 1); i < Math.Min(at.X + 3, ResX); i++)
            {
                var k = Index(i, j);
                var now = new Vector2(Pos[k].X / Size.X + 0.5f, Pos[k].Z / Size.Y + 0.5f);
                if (Excised[k] || Off[k] || (now - middle).Dot(side) <= 0f || now.DistanceTo(uv) >= bestDistance)
                {
                    continue;
                }
                best = k;
                bestDistance = now.DistanceTo(uv);
            }
        }
        if (best >= 0)
        {
            _pins[key] = new Pin(best, Pos[best]);
            _winDirty = true;
            Wake();
        }
        return best;
    }

    public void MoveGrip(int key, Vector3 target)
    {
        if (_pins.TryGetValue(key, out var pin))
        {
            var moved = AboveFloor(pin.Particle, target);
            if (!moved.IsEqualApprox(pin.Target))
            {
                pin.Target = moved;
                Wake();
            }
        }
    }

    public void Release(int key)
    {
        _pins.Remove(key);
        _winDirty = true;
        Wake();
    }

    /// <summary>Every grip on the skin, for syncing to clients.</summary>
    public List<TissueGrip> Grips() => [.. _pins.Select(pin => new TissueGrip(pin.Key, pin.Value.Particle, pin.Value.Target))];

    /// <summary>The particle grip <paramref name="key"/> holds, -1 when it holds none.</summary>
    public int GrippedParticle(int key) => _pins.TryGetValue(key, out var pin) ? pin.Particle : -1;

    /// <summary>Clients mirror the host's grips.</summary>
    public void SetGrips(IReadOnlyList<TissueGrip> grips)
    {
        var before = Grips();
        _pins.Clear();
        foreach (var grip in grips)
        {
            _pins[grip.Key] = new Pin(grip.Particle, grip.Target);
        }
        if (!before.Select(g => g.Key).SequenceEqual(grips.Select(g => g.Key)))
        {
            _winDirty = true;
        }
        if (!before.SequenceEqual(grips))
        {
            Wake();
        }
    }

    /// <summary>Closes the severed springs nearest uv with stitches: the nearest one within reach, and every other one
    /// within StitchReach of it. <paramref name="tension"/> scales their rest length (loose &gt; 1 &gt; tight).</summary>
    public bool Stitch(Vector2 uv, float tension, float strength)
    {
        var best = -1;
        var bestDistance = 0.05f;
        foreach (var s in _severed)
        {
            var distance = MiddleUv(s).DistanceTo(uv);
            if (distance < bestDistance && !Stitched(_springs[s].A, _springs[s].B))
            {
                bestDistance = distance;
                best = s;
            }
        }
        if (best < 0)
        {
            return false;
        }
        var center = (Rest[_springs[best].A] + Rest[_springs[best].B]) * 0.5f;
        foreach (var s in _severed.ToList())
        {
            var (a, b) = (_springs[s].A, _springs[s].B);
            if (s == best || (!Stitched(a, b) && ((Rest[a] + Rest[b]) * 0.5f).DistanceTo(center) < StitchReach))
            {
                AddSpring(a, b, SpringKind.Stitch, tension, strength);
            }
        }
        UpdateRetraction();
        Reshaped();
        return true;
    }

    /// <summary>
    /// Joins every severed edge within <paramref name="radius"/> of the line through <paramref name="points"/> with a
    /// stitch. <see cref="Stitch"/> closes a few millimeters round a point: a tied off running thread joins all it
    /// holds, so no diagonal grid edge between its bites is left showing a sliver of wall.
    /// </summary>
    public int StitchPath(IReadOnlyList<Vector2> points, float radius, float tension, float strength)
    {
        var joined = 0;
        foreach (var s in _severed)
        {
            var (a, b) = (_springs[s].A, _springs[s].B);
            if (!Stitched(a, b) && DistanceToLine(CrossingUv(s), points) < radius)
            {
                AddSpring(a, b, SpringKind.Stitch, tension, strength);
                joined++;
            }
        }
        if (joined > 0)
        {
            UpdateRetraction();
            Reshaped();
        }
        return joined;
    }

    /// <summary>
    /// How much thread (relative to the span at rest) a new hole at uv pays out from the last hole of thread
    /// <paramref name="id"/>: the skin between them as it's stretched now. A careful rhythm stays near 1; erratic
    /// placement leaves up to twelve percent more thread to manage, never so much the wheel can't take it back. Host
    /// only: the result goes to every peer with the hole.
    /// </summary>
    public float ThreadSlack(int id, Vector2 uv)
    {
        if (!_threads.TryGetValue(id, out var thread) || thread.Anchors.Count == 0)
        {
            return 1f;
        }
        var last = thread.Anchors[^1];
        var k = Nearest(uv);
        return Mathf.Clamp(Pos[last].DistanceTo(Pos[k]) / Mathf.Max(Rest[last].DistanceTo(Rest[k]), 0.0001f), 0.95f, 1.12f);
    }

    /// <summary>Adds one puncture to a continuous running suture. The first puncture is only an anchor; every later
    /// one adds a span from the previous hole, <paramref name="slack"/> (see <see cref="ThreadSlack"/>) times as long
    /// as the tension asks for.</summary>
    public bool ThreadAnchor(int id, Vector2 uv, TissueDepth layer, float tension, float strength, float slack)
    {
        var k = Nearest(uv);
        if (!_threads.TryGetValue(id, out var thread))
        {
            thread = new SutureThread(layer, tension);
        }
        if (thread.Final || (thread.Anchors.Count > 0 && thread.Anchors[^1] == k))
        {
            return false;
        }
        if (thread.Anchors.Count > 0)
        {
            thread.Slack.Add(slack);
            thread.Springs.Add(AddSpring(thread.Anchors[^1], k, SpringKind.Stitch, ThreadRestScale(tension, layer) * slack, strength));
        }
        thread.Anchors.Add(k);
        thread.Layer = layer;
        thread.Tension = tension;
        _threads[id] = thread;
        Reshaped();
        return true;
    }

    /// <summary>Tightens or loosens the whole running thread from its free end.</summary>
    public bool ThreadTension(int id, float tension)
    {
        if (!_threads.TryGetValue(id, out var thread))
        {
            return false;
        }
        for (var i = 0; i < thread.Springs.Count; i++)
        {
            ref var spring = ref _springs[thread.Springs[i]];
            if (spring.Active)
            {
                spring.Rest = Rest[spring.A].DistanceTo(Rest[spring.B]) * ThreadRestScale(tension, thread.Layer) * thread.Slack[i];
            }
        }
        thread.Tension = tension;
        Wake();
        return true;
    }

    /// <summary>A span's rest length (relative to its length at rest) at <paramref name="tension"/> in
    /// <paramref name="layer"/>. The wheel's tension isn't the length itself: at ThreadClosed the thread gives the skin
    /// back its uncut shape, tighter it gathers it fast, looser it hangs.</summary>
    private static float ThreadRestScale(float tension, TissueDepth layer)
    {
        var closed = ThreadClosed[(int)layer];
        if (tension >= closed)
        {
            return Mathf.Lerp(0.90f, 1.15f, Mathf.Clamp((tension - closed) / Mathf.Max(ThreadLoose[(int)layer] - closed, 0.01f), 0f, 1f));
        }
        var tear = ThreadTear[(int)layer];
        return Mathf.Lerp(0.25f, 0.90f, Mathf.Clamp((tension - tear) / Mathf.Max(closed - tear, 0.01f), 0f, 1f));
    }

    public void FinishThread(int id)
    {
        if (_threads.TryGetValue(id, out var thread))
        {
            thread.Final = true;
        }
    }

    /// <summary>The thread cut through the tissue: every span of it lets go, and nothing more can be added to it.
    /// </summary>
    public void SnapThread(int id)
    {
        if (!_threads.TryGetValue(id, out var thread))
        {
            return;
        }
        thread.Final = true;
        foreach (var s in thread.Springs)
        {
            _springs[s].Active = false;
        }
        UpdateRetraction();
        Reshaped();
    }

    /// <summary>Every running thread's id, tied off and torn ones too.</summary>
    public IEnumerable<int> ThreadIds => _threads.Keys;

    public SutureThread? Thread(int id) => _threads.GetValueOrDefault(id);

    /// <summary>The holes of thread <paramref name="id"/> (uv), in the order they were made.</summary>
    public List<Vector2> ThreadUvs(int id) =>
        _threads.TryGetValue(id, out var thread) ? [.. thread.Anchors.Select(UvOf)] : [];

    /// <summary>Removes stitches near uv (a closure bursting open).</summary>
    public void Burst(Vector2 uv, float radius)
    {
        foreach (var s in _stitches)
        {
            if (_springs[s].Active && MiddleUv(s).DistanceTo(uv) < radius)
            {
                _springs[s].Active = false;
            }
        }
        UpdateRetraction();
        Reshaped();
    }

    /// <summary>Snaps spring s exactly as the host's sim did (clients mirror host tears). Searching by position instead
    /// would be ambiguous: both diagonals of a grid cell share a midpoint, and several stitches can sit within any
    /// radius.</summary>
    public void SnapSpring(int s)
    {
        if (s < 0 || s >= _springCount)
        {
            GD.PushError($"Tissue out of sync: no spring {s} ({_springCount} springs)");
            return;
        }
        if (_springs[s].Kind == SpringKind.Tissue)
        {
            Sever(s, TissueDepth.Skin, 0.5f);
        }
        else
        {
            _springs[s].Active = false;
        }
        UpdateRetraction();
        Reshaped();
    }

    /// <summary>Changes with every cut, stitch, burst or snap, and is the same on peers whose tissue was cut the same
    /// way (FNV-1a, so it doesn't change between processes).</summary>
    public ulong TopologyHash()
    {
        var hash = 14695981039346656037UL;
        void Add(int value) => hash = (hash ^ (uint)value) * 1099511628211UL;
        for (var s = 0; s < _springCount; s++)
        {
            ref readonly var spring = ref _springs[s];
            Add(spring.Active ? 1 : 0);
            Add((int)spring.Depth);
            Add(spring.MuscleClosed ? 1 : 0);
            Add(spring.FatClosed ? 1 : 0);
        }
        Add(_springCount);
        foreach (var excised in Excised)
        {
            Add(excised ? 1 : 0);
        }
        return hash;
    }

    /// <summary>Seizures, coughs and bumps shake the tissue (visual, every peer). Only skin that's being simulated
    /// shakes: the rest lies still under the body model anyway.</summary>
    public void Shake(float amount)
    {
        foreach (var k in _winParticles)
        {
            _prev[k] = Pos[k] - new Vector3((float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0), (float)GD.RandRange(-1.0, 1.0)) * amount;
        }
        Wake();
    }

    /// <summary>True when uv lies inside an opening: between the lips of a cut at least <paramref name="depth"/> deep,
    /// pulled open.</summary>
    public bool IsOpen(Vector2 uv, TissueDepth depth = TissueDepth.Muscle)
    {
        if (depth <= TissueDepth.Skin && Excised[Nearest(uv)])
        {
            return true;
        }
        var cell = Mathf.Max(Size.X / ResX, Size.Y / ResY);
        foreach (var s in _severed)
        {
            if (DepthOf(s) < depth)
            {
                continue;
            }
            var gap = GapOf(s);
            if (gap <= OpenGap)
            {
                continue;
            }
            // Within half the gap of where the blade crossed, across the cut, and no further along it than the springs
            // lie.
            var offset = (uv - CrossingUv(s)) * Size;
            var across = ((UvOf(_springs[s].B) - UvOf(_springs[s].A)) * Size).Normalized();
            var side = offset.Dot(across);
            if (Mathf.Abs(side) < gap * 0.5f && (offset - across * side).Length() < cell * 0.6f)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Meters the tissue has pulled apart across springs severed at least <paramref name="depth"/> deep near
    /// uv.</summary>
    public float GapAt(Vector2 uv, float radius = 0.04f, TissueDepth depth = TissueDepth.Skin) => GapAlong([uv], radius, depth);

    /// <summary>Widest gap near a polyline (a wound), same rules as <see cref="GapAt"/>. Measured where the blade
    /// crossed each spring.</summary>
    public float GapAlong(IReadOnlyList<Vector2> points, float radius, TissueDepth depth)
    {
        var gap = 0f;
        foreach (var s in _severed)
        {
            if (DepthOf(s) >= depth && DistanceToLine(CrossingUv(s), points) < radius)
            {
                gap = Mathf.Max(gap, GapOf(s));
            }
        }
        return gap;
    }

    /// <summary>The deepest a cut goes where it crosses within <paramref name="radius"/> (uv) of uv, None where there's
    /// no cut.</summary>
    public TissueDepth DeepestCut(Vector2 uv, float radius)
    {
        var deepest = TissueDepth.None;
        foreach (var s in _severed)
        {
            if (uv.DistanceTo(CrossingUv(s)) < radius && CutDepth(s) > deepest)
            {
                deepest = CutDepth(s);
            }
        }
        return deepest;
    }

    /// <summary>
    /// Height (site-local y) of the skin at uv as it's deformed now, NaN over an opening or where no skin lies.
    /// Tools and hands touch this, not the body's rest shape, so a lifted or pressed fold is where it's drawn.
    /// Skin that only moved a little is searched for around where it rests; a flap moved far is found through bins.
    /// </summary>
    public float SkinHeight(Vector2 uv)
    {
        var p = new Vector2((uv.X - 0.5f) * Size.X, (uv.Y - 0.5f) * Size.Y);
        const int Reach = 2;
        if (_farthest > Mathf.Pow(Reach * Mathf.Min(Size.X / ResX, Size.Y / ResY) * 0.8f, 2f))
        {
            return BinnedHeight(p);
        }
        var hanging = HangingOff();
        var ci = Math.Clamp(Mathf.FloorToInt(uv.X * ResX), 0, ResX - 1);
        var cj = Math.Clamp(Mathf.FloorToInt(uv.Y * ResY), 0, ResY - 1);
        for (var j = Math.Max(cj - Reach, 0); j <= Math.Min(cj + Reach, ResY - 1); j++)
        {
            for (var i = Math.Max(ci - Reach, 0); i <= Math.Min(ci + Reach, ResX - 1); i++)
            {
                var a = Index(i, j);
                var c = a + ResX + 1;
                if (TriangleHeight(p, hanging, a, a + 1, c, SpringRight[a], SpringDiag[a], SpringDown[a]) is { } first)
                {
                    return first;
                }
                if (TriangleHeight(p, hanging, a + 1, c + 1, c, SpringDown[a + 1], SpringRight[c], SpringDiag[a]) is { } second)
                {
                    return second;
                }
            }
        }
        return float.NaN;
    }

    /// <summary>The height of triangle (a, b, c) over p, null when p isn't over it or no skin lies across its edges.
    /// </summary>
    private float? TriangleHeight(Vector2 p, bool[] hanging, int a, int b, int c, int edgeA, int edgeB, int edgeC)
    {
        if (hanging[a] || hanging[b] || hanging[c] || Excised[a] || Excised[b] || Excised[c]
            || Gaping(edgeA) || Gaping(edgeB) || Gaping(edgeC))
        {
            return null;
        }
        return HeightOver(p, Pos[a], Pos[b], Pos[c]);
    }

    private static float? HeightOver(Vector2 p, Vector3 a, Vector3 b, Vector3 c)
    {
        var weights = Barycentric(p, new Vector2(a.X, a.Z), new Vector2(b.X, b.Z), new Vector2(c.X, c.Z));
        if (weights.X >= 0f && weights.Y >= 0f && weights.Z >= 0f)
        {
            return a.Y * weights.X + b.Y * weights.Y + c.Y * weights.Z;
        }
        return null;
    }

    /// <summary>Spring s is cut and pulled open, so no skin lies across it.</summary>
    private bool Gaping(int s) => !_springs[s].Active && _springs[s].Kind == SpringKind.Tissue && GapOf(s) > 0f;

    private float BinnedHeight(Vector2 p)
    {
        SortBins();
        var cell = BinOf(p);
        if (cell.X < 0 || cell.X >= Bins || cell.Y < 0 || cell.Y >= Bins)
        {
            return float.NaN;
        }
        foreach (var t in _bins[cell.Y * Bins + cell.X])
        {
            if (HeightOver(p, Pos[_binTriangles[t]], Pos[_binTriangles[t + 1]], Pos[_binTriangles[t + 2]]) is { } height)
            {
                return height;
            }
        }
        return float.NaN;
    }

    /// <summary>Current stretch of the quad edge between two particles, relative to its rest length.</summary>
    public float Stretch(int a, int b) => Pos[a].DistanceTo(Pos[b]) / Mathf.Max(Rest[a].DistanceTo(Rest[b]), 0.0001f);

    /// <summary>
    /// True for particles off the body, false elsewhere, except for skin folded out on top of the drape (see FloorAt):
    /// that can't stick out of the body, it lies on the sheet, and is shown like any other. Skin still where it settled
    /// hasn't been folded anywhere, even where the drape's coarse grid reads lower than it (a steep flank).
    /// Worked out once per step.
    /// </summary>
    public bool[] HangingOff()
    {
        var hasFloor = FloorAt is not null;
        if (_hangingFor == (StepsDone, hasFloor) && _hanging.Length == Off.Length)
        {
            return _hanging;
        }
        _hangingFor = (StepsDone, hasFloor);
        _hanging = (bool[])Off.Clone();
        if (FloorAt is not null && Settled.Length == Pos.Length)
        {
            foreach (var k in _offList)
            {
                if (_hanging[k] && Pos[k].DistanceSquaredTo(Settled[k]) > LiftedOff * LiftedOff)
                {
                    var floorY = FloorAt(Pos[k].X, Pos[k].Z);
                    if (!float.IsNaN(floorY) && Pos[k].Y >= floorY - 0.002f)
                    {
                        _hanging[k] = false;
                    }
                }
            }
        }
        return _hanging;
    }

    /// <summary>Triangle indices of the whole grid, minus triangles spanning a gap cut at least
    /// <paramref name="depth"/> deep and pulled open, and minus triangles off the body. The meshes split triangles
    /// along the cut instead (PatientBody), this is what tests and contact count as open.</summary>
    public int[] Triangles(TissueDepth depth)
    {
        var open = new bool[_springCount];
        foreach (var s in _severed)
        {
            if (DepthOf(s) >= depth && GapOf(s) > 0f)
            {
                open[s] = true;
            }
        }
        var hanging = HangingOff();
        // Where skin was taken off, the skin layer has a hole; the layers under it are whole.
        var gone = depth <= TissueDepth.Skin ? Excised : new bool[Rest.Length];
        var triangles = new List<int>();
        for (var j = 0; j < ResY; j++)
        {
            for (var i = 0; i < ResX; i++)
            {
                var a = Index(i, j);
                var c = a + ResX + 1;
                if (gone[a] || gone[a + 1] || gone[c + 1] || gone[c])
                {
                    continue;
                }
                if (!(open[SpringRight[a]] || open[SpringDiag[a]] || open[SpringDown[a]] || hanging[a] || hanging[a + 1] || hanging[c]))
                {
                    triangles.AddRange([a, a + 1, c]);
                }
                if (!(open[SpringDown[a + 1]] || open[SpringRight[c]] || open[SpringDiag[a]] || hanging[a + 1] || hanging[c + 1] || hanging[c]))
                {
                    triangles.AddRange([a + 1, c + 1, c]);
                }
            }
        }
        return [.. triangles];
    }

    /// <summary>Triangles in the whole grid, for comparing with <see cref="Triangles"/>.</summary>
    public int TriangleCount => ResX * ResY * 2;

    /// <summary>Advances the sim by <paramref name="delta"/> seconds in steps of Step. With <paramref name="run"/>
    /// false the time only adds up, for the next call.</summary>
    public void Advance(float delta, bool run = true)
    {
        _accumulator = Mathf.Min(_accumulator + delta, Step * MaxCatchUp);
        while (run && _accumulator >= Step)
        {
            _accumulator -= Step;
            if (IsSleeping)
            {
                _accumulator = 0f;
                return;
            }
            Substep();
        }
    }

    /// <summary>One solver step: inertia, grips, springs, stitches, anchors, then snapping and retraction.</summary>
    public void Substep()
    {
        StepsDone++;
        _winSteps++;
        // Asleep (stepped anyway), the window waits for something to wake it.
        if (!_winLocked && (_winDirty || (_winSteps >= WindowRefresh && !_winFresh)))
        {
            RefreshWindow();
        }
        var pos = Pos;
        foreach (var k in _winParticles)
        {
            var p = pos[k];
            pos[k] = p + (p - _prev[k]) * Damping;
            _prev[k] = p;
            _free[k] = 1f;
        }
        foreach (var pin in _pins.Values)
        {
            _free[pin.Particle] = 0f;
        }
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            foreach (var pin in _pins.Values)
            {
                DragPatch(pin);
            }
            // Sweeping the springs forward then backward keeps corrections from always flowing one way across the grid.
            var count = _winSprings.Length;
            for (var n = 0; n < count; n++)
            {
                Solve(_winSprings[iteration % 2 == 0 ? n : count - 1 - n]);
            }
            for (var pass = 0; pass <= StitchPasses; pass++)
            {
                foreach (var s in _stitches)
                {
                    Solve(s);
                }
            }
            foreach (var k in _winParticles)
            {
                // Skin pulled far from its spot has come loose from what's under it, so a flap can be folded back. A
                // cut's edge lifted off the body isn't drawn back along it any more either: only skin lying on it is.
                var back = _anchorTarget[k] - pos[k];
                var hold = _anchor[k];
                if (hold > Anchor)
                {
                    hold = Mathf.Lerp(hold, LooseAnchor, Mathf.Clamp((pos[k].Y - _anchorTarget[k].Y) / LiftedOff, 0f, 1f));
                }
                pos[k] += back * hold * _free[k] * Mathf.Clamp(1f - back.Length() / AnchorReach, 0f, 1f);
            }
            // Inside the loop, so the springs even out what the floor pushes up instead of snapping from it.
            StayOnBody();
            StayAboveFloor();
        }
        var moved = 0f;
        var farthest = 0f;
        foreach (var k in _winParticles)
        {
            moved = Mathf.Max(moved, pos[k].DistanceSquaredTo(_prev[k]));
            farthest = Mathf.Max(farthest, pos[k].DistanceSquaredTo(Rest[k]));
        }
        _farthest = farthest;
        if (Tearing)
        {
            SnapOverstretched();
        }
        if (_retractDirty)
        {
            UpdateRetraction();
        }
        _stillSteps = moved < SleepEpsilon * SleepEpsilon ? _stillSteps + 1 : 0;
        if (_stillSteps >= SleepSteps)
        {
            _winFresh = true;
        }
    }

    /// <summary>A grip pins its particle to the tool and drags the patch around it along. The patch is dragged by
    /// translating it, which only looks right for a modest pull: a flap swung far back is left to the springs, or the
    /// translated patch would fight the way it turns.</summary>
    private void DragPatch(Pin pin)
    {
        var k = pin.Particle;
        Pos[k] = pin.Target;
        var pull = pin.Target - _anchorTarget[k];
        Hold(k);
        var drag = GripDrag * Mathf.Clamp(2f - pull.Length() / DragReach, 0f, 1f);
        if (drag <= 0f)
        {
            return;
        }
        var patch = PatchOf(k);
        for (var n = 0; n < patch.Around.Length; n++)
        {
            var j = patch.Around[n];
            var weight = patch.Weights[n];
            Pos[j] += (_anchorTarget[j] + pull * weight - Pos[j]) * drag * weight * _free[j];
        }
    }

    /// <summary>Picks the cells to simulate: around cuts, grips and skin that moved, plus WindowMargin cells of still
    /// skin. While the skin moves the window only grows; once it fell asleep the next one is picked afresh.</summary>
    private void RefreshWindow()
    {
        _winDirty = false;
        _winSteps = 0;
        var low = new Vector2I(ResX + 1, ResY + 1);
        var high = new Vector2I(-1, -1);
        var seeds = new List<int>();
        foreach (var s in _severed)
        {
            seeds.Add(_springs[s].A);
            seeds.Add(_springs[s].B);
        }
        foreach (var s in _stitches)
        {
            if (_springs[s].Active)
            {
                seeds.Add(_springs[s].A);
            }
        }
        foreach (var k in _winParticles)
        {
            if (Pos[k].DistanceSquaredTo(Settled[k]) > WindowMove * WindowMove)
            {
                seeds.Add(k);
            }
        }
        foreach (var k in seeds)
        {
            var at = CellOf(k);
            low = low.Min(at);
            high = high.Max(at);
        }
        foreach (var pin in _pins.Values)
        {
            // A grip drags a whole patch along: all of it moves.
            var at = CellOf(pin.Particle);
            var patch = new Vector2I(Mathf.CeilToInt(GripPatch * ResX), Mathf.CeilToInt(GripPatch * ResY));
            low = low.Min(at - patch);
            high = high.Max(at + patch);
        }
        var fresh = _winFresh || _win.Size == Vector2I.Zero;
        _winFresh = false;
        if (high.X < 0)
        {
            if (fresh)
            {
                SetWindow(default);
            }
            return;
        }
        var margin = new Vector2I(WindowMargin, WindowMargin);
        low = (low - margin).Max(Vector2I.Zero);
        high = (high + margin).Min(new Vector2I(ResX, ResY));
        if (!fresh)
        {
            low = low.Min(_win.Position);
            high = high.Max(_win.End);
        }
        var window = new Rect2I(low, high - low);
        if (window != _win)
        {
            SetWindow(window);
        }
    }

    private void SetWindow(Rect2I window)
    {
        var before = _win;
        foreach (var k in _winParticles)
        {
            _free[k] = 0f;
        }
        _win = window;
        if (window.Size == Vector2I.Zero)
        {
            _winParticles = [];
            _winSprings = [];
            return;
        }
        var particles = new List<int>();
        for (var j = window.Position.Y; j <= window.End.Y; j++)
        {
            for (var i = window.Position.X; i <= window.End.X; i++)
            {
                var k = Index(i, j);
                if (!_fixed[k])
                {
                    particles.Add(k);
                }
            }
        }
        // Every spring with an end inside: springs leaving each particle in the window and the ones arriving from the
        // row above and the column to the left of it.
        var springs = new List<int>();
        for (var j = Math.Max(window.Position.Y - 1, 0); j <= window.End.Y; j++)
        {
            for (var i = Math.Max(window.Position.X - 1, 0); i <= window.End.X; i++)
            {
                var k = Index(i, j);
                foreach (var s in GridSprings(k))
                {
                    if (s >= 0)
                    {
                        springs.Add(s);
                    }
                }
            }
        }
        _winParticles = [.. particles];
        _winSprings = [.. springs];
        foreach (var k in _winParticles)
        {
            _free[k] = 1f;
            // Skin coming back into the window starts out still, not with whatever motion it had when it left.
            if (!Inside(before, CellOf(k)))
            {
                _prev[k] = Pos[k];
            }
        }
    }

    /// <summary>Column and row <paramref name="at"/> lie in window <paramref name="rect"/> (both ends included);
    /// nothing lies in an empty one.</summary>
    private static bool Inside(Rect2I rect, Vector2I at) =>
        rect.Size != Vector2I.Zero && at.X >= rect.Position.X && at.Y >= rect.Position.Y && at.X <= rect.End.X && at.Y <= rect.End.Y;

    /// <summary>The skin around a grip's jaws keeps its distance to the gripped particle, as stiff as thread: it turns
    /// with a flap folded back, but the pull is shared by the ring of springs around it instead of one spring at the
    /// jaws.</summary>
    private void Hold(int k)
    {
        var p = Pos[k];
        var held = PatchOf(k).Held;
        for (var pass = 0; pass < StitchPasses; pass++)
        {
            foreach (var j in held)
            {
                if (_free[j] == 0f)
                {
                    continue;
                }
                var d = Pos[j] - p;
                var length = d.Length();
                if (length > 0.00001f)
                {
                    Pos[j] = p + d * (Rest[j].DistanceTo(Rest[k]) * Tension / length);
                }
            }
        }
    }

    /// <summary>Moves the ends of an active spring toward its rest length, by how free each end is.</summary>
    private void Solve(int s)
    {
        ref readonly var spring = ref _springs[s];
        if (!spring.Active)
        {
            return;
        }
        var wa = _free[spring.A];
        var wb = _free[spring.B];
        if (wa + wb == 0f)
        {
            return;
        }
        var d = Pos[spring.B] - Pos[spring.A];
        var length = d.Length();
        if (length < 0.00001f)
        {
            return;
        }
        var correction = d * ((length - spring.Rest) / (length * (wa + wb)));
        Pos[spring.A] += correction * wa;
        Pos[spring.B] -= correction * wb;
    }

    /// <summary>
    /// Skin that started out exposed (inside the drape's opening) and is folded out over the drape lies on it. Skin
    /// under the drape stays under it: a flap pulled out over the drape can't lift it through the sheet. Unless it's
    /// been cut free there itself: a flap cut under the drape's edge takes it along.
    /// Skin lying in place rests on the body, it doesn't sink into it: over a curved body the sheet's tension would pull
    /// it a few millimeters inside the body's shape, and the site would show recessed next to the body model around it.
    /// Only near its rest point (BodyReach): the body's tangent plane there says nothing about where a flap pulled far
    /// away may go. Not the outermost strip either: it lies under the drape's frame, never drawn, and lifted onto the
    /// body it would count as lying on top of the drape.
    /// </summary>
    private void StayOnBody()
    {
        foreach (var k in _winParticles)
        {
            if (Off[k] || _free[k] == 0f || AtBorder(k))
            {
                continue;
            }
            var moved = Pos[k] - Rest[k];
            var below = moved.Dot(_restNormal[k]);
            if (below < 0f && moved.LengthSquared() < BodyReach * BodyReach)
            {
                Pos[k] -= _restNormal[k] * below;
            }
        }
    }

    private void StayAboveFloor()
    {
        if (FloorAt is null)
        {
            return;
        }
        FindCutFree();
        foreach (var k in _winParticles)
        {
            if (_free[k] == 0f)
            {
                continue;
            }
            if (Exposed[k])
            {
                // Only skin folded out: skin still near where it rests may lie just past the opening's edge, where the
                // drape's coarse grid can read below it on a steep flank and then step up as it moves, ratcheting it up.
                if (!FloorOpen.HasPoint(new Vector2(Pos[k].X, Pos[k].Z)) && Pos[k].DistanceSquaredTo(Settled[k]) > LiftedOff * LiftedOff)
                {
                    Pos[k] = AboveFloor(k, Pos[k]);
                }
            }
            else if (!_cutFree[k] && Settled.Length == Pos.Length)
            {
                // No higher than the drape lets it, or where it rested if the drape lies lower than that.
                var floorY = FloorAt(Pos[k].X, Pos[k].Z);
                if (!float.IsNaN(floorY))
                {
                    Pos[k].Y = Mathf.Min(Pos[k].Y, Mathf.Max(floorY - UnderDrape, Settled[k].Y));
                }
            }
        }
    }

    /// <summary>Marks the ends of every cut spring and the grid points next to them.</summary>
    private void FindCutFree()
    {
        if (_cutFreeFor == TopologyVersion && _cutFree.Length == Pos.Length)
        {
            return;
        }
        _cutFreeFor = TopologyVersion;
        _cutFree = new bool[Pos.Length];
        foreach (var s in _severed)
        {
            foreach (var k in new[] { _springs[s].A, _springs[s].B })
            {
                var at = CellOf(k);
                for (var j = Math.Max(at.Y - 1, 0); j <= Math.Min(at.Y + 1, ResY); j++)
                {
                    for (var i = Math.Max(at.X - 1, 0); i <= Math.Min(at.X + 1, ResX); i++)
                    {
                        _cutFree[Index(i, j)] = true;
                    }
                }
            }
        }
    }

    /// <summary>p, where particle k is, lifted onto the floor if it's exposed skin below it.</summary>
    private Vector3 AboveFloor(int k, Vector3 p)
    {
        if (FloorAt is null || Exposed.Length <= k || !Exposed[k])
        {
            return p;
        }
        var floorY = FloorAt(p.X, p.Z);
        if (!float.IsNaN(floorY) && p.Y < floorY)
        {
            p.Y = floorY;
        }
        return p;
    }

    private void SnapOverstretched()
    {
        foreach (var s in _winSprings)
        {
            CheckSnap(s);
        }
        foreach (var s in _stitches)
        {
            CheckSnap(s);
        }
    }

    /// <summary>
    /// A spring snaps once it's stretched past its limit, and the spring going on from it the same way at either end
    /// is stretched at least halfway there too: skin tears where it's overstretched over a length, not where one short
    /// spring of the grid takes a jump (the finer the grid, the shorter the spring that would).
    /// </summary>
    private void CheckSnap(int s)
    {
        ref readonly var spring = ref _springs[s];
        if (!spring.Active)
        {
            return;
        }
        var (a, b) = (spring.A, spring.B);
        var limit = 1f + (spring.Break - 1f) * BreakMult;
        if (Pos[a].DistanceTo(Pos[b]) / spring.Rest <= limit)
        {
            return;
        }
        if (spring.Kind == SpringKind.Tissue)
        {
            var halfway = 1f + (limit - 1f) * 0.5f;
            var along = CellOf(b) - CellOf(a);
            var next = SpringBetween(CellOf(b), CellOf(b) + along);
            var previous = SpringBetween(CellOf(a) - along, CellOf(a));
            if (!(StretchedPast(next, halfway) || StretchedPast(previous, halfway)))
            {
                return;
            }
            Sever(s, TissueDepth.Skin, 0.5f);
        }
        else
        {
            _springs[s].Active = false;
        }
        Snapped.Add(new SnappedSpring(UvOf(a), UvOf(b), _springs[s].Kind, s));
        TopologyVersion++;
        _retractDirty = true;
        _winDirty = true;
    }

    /// <summary>The grid spring from grid point a to grid point b (column, row), -1 if there's none.</summary>
    private int SpringBetween(Vector2I a, Vector2I b)
    {
        if (a.X < 0 || a.Y < 0 || b.X < 0 || b.Y < 0 || a.X > ResX || b.X > ResX || a.Y > ResY || b.Y > ResY)
        {
            return -1;
        }
        var d = b - a;
        var k = Index(a.X, a.Y);
        if (d == new Vector2I(1, 0))
        {
            return SpringRight[k];
        }
        if (d == new Vector2I(0, 1))
        {
            return SpringDown[k];
        }
        if (d == new Vector2I(1, 1))
        {
            return SpringAnti[k];
        }
        return d == new Vector2I(-1, 1) ? SpringDiag[Index(a.X - 1, a.Y)] : -1;
    }

    /// <summary>Spring s is whole and stretched past <paramref name="ratio"/> of its rest length.</summary>
    private bool StretchedPast(int s, float ratio) =>
        s >= 0 && _springs[s].Active && Pos[_springs[s].A].DistanceTo(Pos[_springs[s].B]) / _springs[s].Rest > ratio;

    private int AddSpring(int a, int b, SpringKind kind = SpringKind.Tissue, float tension = Tension, float strength = TissueBreak)
    {
        if (_springCount == _springs.Length)
        {
            Array.Resize(ref _springs, _springs.Length * 2);
        }
        var s = _springCount++;
        _springs[s] = new Spring
        {
            A = a,
            B = b,
            Rest = Rest[a].DistanceTo(Rest[b]) * tension,
            Active = true,
            Kind = kind,
            Break = strength,
            Depth = TissueDepth.None,
            Cross = 0.5f,
        };
        if (kind == SpringKind.Stitch)
        {
            _edgeToStitch[EdgeKey(a, b)] = s;
            _stitches.Add(s);
        }
        return s;
    }

    /// <summary>
    /// The skin a grip on particle k drags along: particles within GripPatch reached without crossing a cut and not
    /// across one from it in a straight line, weighted falling off in a straight line from the edge of the skin the grip
    /// holds (GripHold) to 0 at the edge of the patch.
    /// </summary>
    private Patch PatchOf(int k)
    {
        if (_patchVersion != TopologyVersion)
        {
            _patches.Clear();
            _patchVersion = TopologyVersion;
        }
        if (_patches.TryGetValue(k, out var cached))
        {
            return cached;
        }
        IndexSprings();
        var around = new List<int>();
        var weights = new List<float>();
        var held = new List<int>();
        var reach = GripPatch * (Size.X + Size.Y) * 0.5f - GripHold;
        var seen = new HashSet<int> { k };
        var queue = new Queue<int>();
        queue.Enqueue(k);
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            foreach (var s in _springsOf[at])
            {
                if (!_springs[s].Active || _springs[s].Kind != SpringKind.Tissue)
                {
                    continue;
                }
                var other = _springs[s].A == at ? _springs[s].B : _springs[s].A;
                if (seen.Contains(other) || UvOf(other).DistanceTo(UvOf(k)) >= GripPatch)
                {
                    continue;
                }
                seen.Add(other);
                queue.Enqueue(other);
                // Reached round a cut's end, skin across the cut from the grip is its other edge: dragged along, that
                // edge would go the same way and the cut wouldn't open. Skin past it, in line of sight again, is still
                // reached.
                if (AcrossCut(UvOf(k), UvOf(other)))
                {
                    continue;
                }
                around.Add(other);
                var meters = Rest[other].DistanceTo(Rest[k]);
                weights.Add(Mathf.Clamp(1f - (meters - GripHold) / reach, 0f, 1f));
                if (meters < GripHold && !_fixed[other])
                {
                    held.Add(other);
                }
            }
        }
        var patch = new Patch([.. around], [.. weights], [.. held]);
        _patches[k] = patch;
        return patch;
    }

    /// <summary>Whether the straight line from a to b (uv) crosses a cut.</summary>
    private bool AcrossCut(Vector2 a, Vector2 b) => _cutSegments.Any(segment => Crossing(a, b, segment.A, segment.B) >= 0f);

    /// <summary>Fills in the springs at each particle the first time it's needed. Stitches added later aren't in it.
    /// </summary>
    private void IndexSprings()
    {
        if (_springsOf.Length > 0)
        {
            return;
        }
        _springsOf = [.. Enumerable.Range(0, Rest.Length).Select(_ => new List<int>())];
        for (var s = 0; s < _springCount; s++)
        {
            _springsOf[_springs[s].A].Add(s);
            _springsOf[_springs[s].B].Add(s);
        }
    }

    /// <summary>Severs spring s down to <paramref name="depth"/>. A tear (no cut direction) runs square to the spring
    /// it snapped.</summary>
    private void Sever(int s, TissueDepth depth, float cross, Vector2 cutDir = default)
    {
        ref var spring = ref _springs[s];
        if (spring.Active)
        {
            spring.Active = false;
            spring.Cross = cross;
            if (cutDir == Vector2.Zero)
            {
                var along = Rest[spring.B] - Rest[spring.A];
                cutDir = new Vector2(-along.Z, along.X);
            }
            spring.CutDir = cutDir.Normalized();
            _severed.Add(s);
        }
        if (depth > spring.Depth)
        {
            spring.Depth = depth;
        }
        if (depth == TissueDepth.Muscle)
        {
            spring.MuscleClosed = false;
        }
    }

    /// <summary>
    /// Particles on either side of a cut are drawn back from it, sideways along the skin, as far as the deepest cut
    /// near them retracts (Retract): less the further they are from it, and less toward the cut's ends, where its edges
    /// still hold together. Each cut spring pulls the strip of skin across from it. Stitched springs don't pull: the
    /// thread holds their edges together.
    /// </summary>
    private void UpdateRetraction()
    {
        _retractDirty = false;
        if (_pull.Length != Rest.Length)
        {
            _pull = new Vector3[Rest.Length];
            _pullAmount = new float[Rest.Length];
        }
        foreach (var k in _retracted)
        {
            _anchorTarget[k] = Rest[k];
            _pull[k] = Vector3.Zero;
            _pullAmount[k] = 0f;
            if (_anchor[k] == MusclePull || _anchor[k] == GapePull)
            {
                _anchor[k] = LooseAnchor;
            }
        }
        foreach (var k in _lipped)
        {
            _anchorTarget[k] = Rest[k];
        }
        _retracted.Clear();
        FindCutEnds();
        var cell = Mathf.Min(Size.X / ResX, Size.Y / ResY);
        foreach (var s in _severed)
        {
            var depth = DepthOf(s);
            var spring = _springs[s];
            if (depth == TissueDepth.None || Stitched(spring.A, spring.B))
            {
                continue;
            }
            var crossing = CrossingUv(s);
            var toEnd = float.PositiveInfinity;
            foreach (var end in _cutEnds)
            {
                toEnd = Mathf.Min(toEnd, ((crossing - end) * Size).Length());
            }
            var taper = Mathf.SmoothStep(0f, RetractTaper[(int)depth], toEnd);
            if (taper <= 0f)
            {
                continue;
            }
            // Square to the cut, toward A's side, rising and falling with the skin like the spring does.
            var springVector = Rest[spring.A] - Rest[spring.B];
            var square = new Vector2(-spring.CutDir.Y, spring.CutDir.X);
            if (square.Dot(new Vector2(springVector.X, springVector.Z)) < 0f)
            {
                square = -square;
            }
            var flat = new Vector2(springVector.X, springVector.Z).Dot(square);
            var across = new Vector3(square.X, springVector.Y / Mathf.Max(flat, 0.0005f), square.Y).Normalized();
            var acrossUv = new Vector2(square.X / Size.X, square.Y / Size.Y);
            var middle = Rest[spring.A].Lerp(Rest[spring.B], spring.Cross);
            var spread = RetractSpread[(int)depth];
            var reach = Mathf.CeilToInt(spread / cell);
            // The strip of skin straight across the cut from this spring, a grid point at a time.
            for (var n = -reach; n <= reach; n++)
            {
                var at = crossing + acrossUv * (n * cell);
                var i = Mathf.RoundToInt(at.X * ResX);
                var j = Mathf.RoundToInt(at.Y * ResY);
                if (i < 1 || j < 1 || i >= ResX || j >= ResY)
                {
                    continue;
                }
                var k = Index(i, j);
                var offset = new Vector2(Rest[k].X - middle.X, Rest[k].Z - middle.Z);
                var side = offset.Dot(square);
                var weight = (1f - Mathf.Abs(side) / spread) * Mathf.Clamp(1f - (offset - square * side).Length() / cell, 0f, 1f) * taper;
                // The spring's own ends: on the side they are on, even if the crossing was right at one of them.
                if (k == spring.A)
                {
                    side = 1f;
                }
                else if (k == spring.B)
                {
                    side = -1f;
                }
                if (weight <= 0f || side == 0f)
                {
                    continue;
                }
                if (_pullAmount[k] == 0f && _pull[k] == Vector3.Zero)
                {
                    _retracted.Add(k);
                }
                _pull[k] += across * Mathf.Sign(side) * weight;
                _pullAmount[k] = Mathf.Max(_pullAmount[k], Retract[(int)depth] * weight);
            }
        }
        foreach (var k in _retracted)
        {
            var direction = _pull[k];
            if (direction.LengthSquared() < 1e-8f)
            {
                continue;
            }
            var retract = _pullAmount[k];
            _anchorTarget[k] = Rest[k] + direction.Normalized() * retract;
            _anchor[k] = Mathf.Max(_anchor[k], retract >= Retract[(int)TissueDepth.Skin] * 4f ? MusclePull : GapePull);
        }
        var activeLips = new List<int>();
        foreach (var k in _lipped)
        {
            if (SutureLip[k] <= 0f)
            {
                continue;
            }
            _anchorTarget[k] += _restNormal[k] * SutureLip[k];
            _anchor[k] = Mathf.Max(_anchor[k], GapePull);
            activeLips.Add(k);
        }
        _lipped = activeLips;
    }

    /// <summary>The ends of the cuts: where each stroke started and the point of it farthest from there, unless another
    /// stroke runs through that point (a blade pressed in where the cut already runs makes a short cut inside the
    /// incision).</summary>
    private void FindCutEnds()
    {
        if (_cutEndsFor == _cutSegments.Count)
        {
            return;
        }
        _cutEndsFor = _cutSegments.Count;
        _cutEnds.Clear();
        for (var n = 0; n < _strokes.Count; n++)
        {
            foreach (var p in _strokes[n])
            {
                if (!_cutEnds.Contains(p) && !OnOtherStroke(p, n))
                {
                    _cutEnds.Add(p);
                }
            }
        }
    }

    /// <summary>p lies within a millimeter of a segment of a stroke other than stroke n.</summary>
    private bool OnOtherStroke(Vector2 p, int n)
    {
        for (var m = 0; m < _segmentStroke.Count; m++)
        {
            if (_segmentStroke[m] == n)
            {
                continue;
            }
            var nearest = Geometry2D.GetClosestPointToSegment(p, _cutSegments[m].A, _cutSegments[m].B);
            if (((nearest - p) * Size).Length() < 0.001f)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>How far severed spring s is pulled apart past its rest length (meters). Pulled together by a stitch it
    /// counts as closed only once the edges nearly meet (within half of OpenGap); a loose stitch leaves the rest of the
    /// gap open.</summary>
    public float GapOf(int s)
    {
        var (a, b) = (_springs[s].A, _springs[s].B);
        var gap = Pos[a].DistanceTo(Pos[b]) - Rest[a].DistanceTo(Rest[b]);
        return gap > OpenGap * 0.5f ? gap : 0f;
    }

    /// <summary>Re-sorts the skin triangles into bins after the skin moved or was cut. Only runs when queried.</summary>
    private void SortBins()
    {
        if (_binsFor == (TopologyVersion, StepsDone))
        {
            return;
        }
        _binsFor = (TopologyVersion, StepsDone);
        _binTriangles = Triangles(TissueDepth.Skin);
        foreach (var bin in _bins)
        {
            bin.Clear();
        }
        var last = new Vector2I(Bins - 1, Bins - 1);
        for (var t = 0; t < _binTriangles.Length; t += 3)
        {
            var low = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var high = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (var n = 0; n < 3; n++)
            {
                var at = Pos[_binTriangles[t + n]];
                low = low.Min(new Vector2(at.X, at.Z));
                high = high.Max(new Vector2(at.X, at.Z));
            }
            var from = BinOf(low).Clamp(Vector2I.Zero, last);
            var to = BinOf(high).Clamp(Vector2I.Zero, last);
            for (var y = from.Y; y <= to.Y; y++)
            {
                for (var x = from.X; x <= to.X; x++)
                {
                    _bins[y * Bins + x].Add(t);
                }
            }
        }
    }

    /// <summary>Bin of a site-local point (x, z). Out of range off the binned area.</summary>
    private Vector2I BinOf(Vector2 p)
    {
        var extent = Size * (1f + BinMargin * 2f);
        var cell = (p + extent * 0.5f) / extent * Bins;
        return new Vector2I(Mathf.FloorToInt(cell.X), Mathf.FloorToInt(cell.Y));
    }

    private static Vector3 Barycentric(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        var area = (b - a).Cross(c - a);
        if (Mathf.Abs(area) < 1e-12f)
        {
            return new Vector3(-1, -1, -1);
        }
        var wb = (p - a).Cross(c - a) / area;
        var wc = (b - a).Cross(p - a) / area;
        return new Vector3(1f - wb - wc, wb, wc);
    }

    /// <summary>A stitch joins particles a and b and still holds.</summary>
    public bool Stitched(int a, int b)
    {
        var s = _edgeToStitch.GetValueOrDefault(EdgeKey(a, b), -1);
        return s >= 0 && _springs[s].Active;
    }

    /// <summary>Distance (uv) from uv to the polyline through <paramref name="points"/>, or to the one point.</summary>
    public static float DistanceToLine(Vector2 uv, IReadOnlyList<Vector2> points)
    {
        if (points.Count == 1)
        {
            return uv.DistanceTo(points[0]);
        }
        var best = float.PositiveInfinity;
        for (var i = 1; i < points.Count; i++)
        {
            best = Mathf.Min(best, uv.DistanceTo(Geometry2D.GetClosestPointToSegment(uv, points[i - 1], points[i])));
        }
        return best;
    }

    private static long EdgeKey(int a, int b) => (long)Math.Min(a, b) * 100000 + Math.Max(a, b);
}
