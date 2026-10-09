namespace Scalpel.UI;

/// <summary>
/// The aim at the active tool tip: a dot, for blades a line along the edge where it will cut, for a spreader a &lt; and a
/// &gt; at its tips, moving apart as it opens, for a stapler a ring where each leg goes in. Beside it, the name of the
/// tool the hand would pick up and the tool's effort levels.
/// </summary>
public partial class Hud
{
    /// <summary>What each effort level (0..3) does, for tools that take one (see LevelSteps()).</summary>
    private static readonly Dictionary<string, string[]> LevelStepNames = new Dictionary<string, string[]>
    {
        ["cut"] = ["Resting on skin", "Skin", "Fat", "Muscle, into cavity"],
        ["tension"] = ["Off", "Loose", "Correct", "Tight"],
        ["inject"] = ["Not pushed", "A third in", "Two thirds in", "All in"],
        ["effort"] = ["Off", "Low", "Medium", "High"],
    };
    /// <summary>Length of the blade edge line drawn on the skin (m).</summary>
    private const float BladeLine = 0.04f;
    /// <summary>Size (pixels) of the &lt; and &gt; marking a spreader's tips.</summary>
    private const float JawMark = 9f;
    /// <summary>How deep (meters) under the skin's lip the &lt; and &gt; show a spreader's tips going in, aimed across a
    /// cut.</summary>
    public const float JawDepth = 0.01f;
    /// <summary>Radius (meters) of the rings on the skin marking where a stapler's legs go in.</summary>
    public const float LegRing = 0.003f;
    private static readonly Color AimColor = new(1f, 1f, 0.9f);
    private static readonly Color AimWorking = new(1f, 0.42f, 0.35f);

    private Panel _dot = null!;
    private Line2D _blade = null!;
    private readonly Line2D[] _jaws = new Line2D[2];
    private readonly Line2D[] _legs = new Line2D[2];
    private Label _dotLabel = null!;
    /// <summary>Effort levels of the active tool beside the aim, when the tool has any.</summary>
    private RichTextLabel _levels = null!;

    /// <summary>Where the spreader's &lt; and &gt; point (world), as last drawn.</summary>
    public Vector3[] JawMarks { get; } = new Vector3[2];
    /// <summary>Where the stapler's rings lie (world), one per leg, as last drawn.</summary>
    public Vector3[][] LegRings { get; } = [[], []];
    /// <summary>The aim shows as a dot (not a blade line, jaw marks or leg rings).</summary>
    internal bool DotShown => _dot.Visible;
    /// <summary>The aim shows a stapler's two leg rings.</summary>
    internal bool LegRingsShown => _legs.All(ring => ring.Visible);
    /// <summary>Where the shown &lt; and &gt; point (screen).</summary>
    internal Vector2[] JawPoints => [.. _jaws.Where(jaw => jaw.Visible && jaw.Points.Length == 3).Select(jaw => jaw.Points[1])];

    /// <summary>A dark line with a light one on top (its child), so it shows on pale skin and in blood alike.</summary>
    private Line2D AimLine()
    {
        var dark = new Line2D { Width = 5f, DefaultColor = new Color(0f, 0f, 0f, 0.6f) };
        var light = new Line2D { Width = 2.5f, DefaultColor = AimColor };
        foreach (var line in (Line2D[])[dark, light])
        {
            line.BeginCapMode = Line2D.LineCapMode.Round;
            line.EndCapMode = Line2D.LineCapMode.Round;
            line.JointMode = Line2D.LineJointMode.Round;
        }
        dark.AddChild(light);
        _root.AddChild(dark);
        return dark;
    }

    /// <summary>Draws <paramref name="line"/> and its light top through <paramref name="points"/>, red while the tool is
    /// working.</summary>
    private static void DrawAim(Line2D line, Vector2[] points, bool working)
    {
        line.Points = points;
        var light = line.GetChild<Line2D>(0);
        light.Points = points;
        light.DefaultColor = working ? AimWorking : AimColor;
    }

    private void BuildAim()
    {
        _dot = new Panel { Size = new Vector2(8f, 8f), MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = new StyleBoxFlat { BgColor = new Color(1f, 1f, 0.9f, 0.85f), BorderColor = new Color(0f, 0f, 0f, 0.6f) };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(4);
        _dot.AddThemeStyleboxOverride("panel", style);
        _root.AddChild(_dot);
        _blade = AimLine();
        for (var side = 0; side < 2; side++)
        {
            _jaws[side] = AimLine();
            _legs[side] = AimLine();
        }
        _dotLabel = Ui.Label("", 16, Ui.Ink);
        _dotLabel.AutowrapMode = TextServer.AutowrapMode.Off;
        _root.AddChild(_dotLabel);
        _levels = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            AutowrapMode = TextServer.AutowrapMode.Off,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(260f, 0f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _levels.AddThemeFontSizeOverride("normal_font_size", 15);
        _levels.AddThemeConstantOverride("outline_size", 4);
        _levels.AddThemeColorOverride("font_outline_color", new Color(0f, 0f, 0f, 0.8f));
        _root.AddChild(_levels);
    }

    private void UpdateAim(Surgeon me)
    {
        var camera = me.Camera;
        var aim = me.AimPoint();
        var tool = me.HeldTool(me.Active);
        var shown = Overlay is null && !camera.IsPositionBehind(aim);
        var action = shown ? tool?.Def.Action ?? "" : "";
        _dot.Visible = shown && action is not ("cut" or "spread" or "staple");
        _blade.Visible = action == "cut";
        for (var side = 0; side < 2; side++)
        {
            _jaws[side].Visible = action == "spread";
            _legs[side].Visible = action == "staple";
        }
        if (!shown)
        {
            _dotLabel.Text = "";
            _levels.Visible = false;
            return;
        }
        var at = camera.UnprojectPosition(aim);
        _dot.Position = at - (_dot.Size * 0.5f);
        switch (action)
        {
            case "cut":
                // Laid on the skin from end to end, so it shows where the edge comes down.
                var edge = ToolActions.BladeDirection(tool!) * BladeLine * 0.5f;
                var hand = me.Hands[me.Active];
                Vector3[] line = [me.OnSurface(aim - edge), aim, me.OnSurface(aim + edge)];
                DrawAim(_blade, [.. line.Select(camera.UnprojectPosition)], hand.Lowered && hand.Level > 0);
                break;
            case "spread":
                DrawSpreader(me, camera, tool!);
                break;
            case "staple":
                DrawStapler(me, camera, tool!);
                break;
        }
        _dotLabel.Text = IsInstanceValid(me.Hovered) ? me.Hovered!.Label() : "";
        _dotLabel.Position = at + new Vector2(10f, -10f);
        var levels = LevelText(me);
        _levels.Text = levels;
        _levels.Visible = levels.Length > 0;
        _levels.Position = at + new Vector2(14f, 12f);
    }

    /// <summary>
    /// A &lt; at the spreader's tip toward -X and a &gt; at the other, each pointing out from the middle: &lt;&gt; closed,
    /// &lt; &gt; open. Aimed across a cut, each hangs from the cut's lip on its side down its wall, where the tip goes in;
    /// set, they're at the tips; elsewhere on what's under them.
    /// </summary>
    private void DrawSpreader(Surgeon me, Camera3D camera, SurgicalTool tool)
    {
        var body = Surgery.Patient.Body;
        var tips = ToolActions.SidePoints(tool, tool.Spread);
        var cut = tool.InWound
            ? new Vector2(-1f, -1f)
            : body.Tissue.CutBetween(body.WorldToUv(tips[0]), body.WorldToUv(tips[1]));
        var lips = new Vector3[2];
        var acrossCut = !tool.InWound && cut.X >= 0f;
        for (var side = 0; side < 2; side++)
        {
            if (tool.InWound)
            {
                JawMarks[side] = tips[side];
            }
            else if (acrossCut)
            {
                var halfGap = body.Tissue.GapsNear([cut], body.Tissue.SeamReach)[0] * 0.5f;
                var middle = body.UvToWorld(cut);
                var outward = ((tips[side] - middle) * new Vector3(1f, 0f, 1f)).Normalized();
                lips[side] = me.OnSurface(middle + (outward * (halfGap + 0.001f)));
                JawMarks[side] = lips[side] - (Vector3.Up * JawDepth);
            }
            else
            {
                JawMarks[side] = me.OnSurface(tips[side]);
            }
        }
        Vector2[] ends = [camera.UnprojectPosition(JawMarks[0]), camera.UnprojectPosition(JawMarks[1])];
        var across = (ends[1] - ends[0]).Normalized();
        if (across == Vector2.Zero)
        {
            across = Vector2.Right;
        }
        // Close together each mark flattens to half the space between the tips, so <> never cross into an X.
        var depth = Mathf.Clamp(ends[0].DistanceTo(ends[1]) * 0.5f, JawMark * 0.3f, JawMark);
        for (var side = 0; side < 2; side++)
        {
            var outward = across * (side == 1 ? 1f : -1f);
            var back = ends[side] - (outward * depth);
            var arm = new Vector2(-outward.Y, outward.X) * JawMark * 0.7f;
            Vector2[] mark = acrossCut
                // A stem from the lip down to the mark: the tip goes in there.
                ? [camera.UnprojectPosition(lips[side]), ends[side], back + arm, ends[side], back - arm]
                : [back + arm, ends[side], back - arm];
            DrawAim(_jaws[side], mark, tool.InWound);
        }
    }

    /// <summary>A ring lying on the skin where each of the stapler's legs goes in: on a cut's edge it reaches for, or
    /// down in the opening when it can't reach one.</summary>
    private void DrawStapler(Surgeon me, Camera3D camera, SurgicalTool tool)
    {
        var legs = ToolActions.StapleLegs(tool, Surgery.Patient.Body);
        var across = ToolActions.SideAxis(tool) * LegRing;
        var along = ToolActions.BladeDirection(tool) * LegRing;
        for (var side = 0; side < 2; side++)
        {
            // The ring lies in the plane of the skin under its middle, found a ring's radius out each way.
            var middle = me.OnSurface(legs[side]);
            var u = Tilted(me.OnSurface(legs[side] + across) - middle);
            var v = Tilted(me.OnSurface(legs[side] + along) - middle);
            var ring = Enumerable.Range(0, 13)
                .Select(i => Mathf.Tau * i / 12f)
                .Select(angle => middle + (u * Mathf.Cos(angle)) + (v * Mathf.Sin(angle)))
                .ToArray();
            LegRings[side] = ring;
            DrawAim(_legs[side], [.. ring.Select(camera.UnprojectPosition)], false);
        }
    }

    /// <summary>A ring's radius <paramref name="outward"/> along the surface, tilted no steeper than 45 degrees: past the
    /// edge of an arm the surface a radius out drops away to whatever is below, and the ring would stand on end.
    /// </summary>
    private static Vector3 Tilted(Vector3 outward)
    {
        var flat = new Vector2(outward.X, outward.Z).Length();
        return outward with { Y = Mathf.Clamp(outward.Y, -flat, flat) };
    }

    /// <summary>The names of a tool's effort levels, null when it takes none.</summary>
    public static string[]? LevelSteps(SurgicalTool? tool)
    {
        if (tool is null || !ToolActions.LevelNames.ContainsKey(tool.Def.Action))
        {
            return null;
        }
        var kind = Patient.TensionedClosures.Contains(tool.Def.Id) ? "tension"
            : LevelStepNames.ContainsKey(tool.Def.Action) ? tool.Def.Action
            : "effort";
        return LevelStepNames[kind];
    }

    /// <summary>The levels, the current one marked; red while the tool is working at it.</summary>
    private static string LevelText(Surgeon me)
    {
        var tool = me.HeldTool(me.Active);
        if (LevelSteps(tool) is not { } steps)
        {
            return "";
        }
        var hand = me.Hands[me.Active];
        var working = ToolActions.InUse(tool!.Def.Action, hand.Lowered, hand.Trigger, hand.Level);
        var lines = new List<string> { ToolActions.LevelNames[tool.Def.Action] };
        for (var level = 0; level < 4; level++)
        {
            var text = $"{level}  {steps[level]}";
            lines.Add(level == hand.Level
                ? $"[color={(working ? "#ff6a5a" : "#fff4c8")}][b]▶ {text}[/b][/color]"
                : $"[color=#ffffff80]   {text}[/color]");
        }
        return string.Join("\n", lines);
    }
}
