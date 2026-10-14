namespace Scalpel.UI;

/// <summary>
/// The discharge line at the foot of the patient card. While the reader holds Interact the surgeon's signature writes
/// itself along it under a see-through pen, at an even speed along the stroke; let go early, it unwrites back to
/// nothing. Drawn whole it stays signed.
/// </summary>
public partial class SignatureView : Control
{
    /// <summary>How long Interact is held to sign.</summary>
    public const float SignSeconds = 2f;
    /// <summary>A part signature unwrites in at most this long.</summary>
    private const float RollbackSeconds = 0.6f;
    private const float Baseline = 76f;
    private static readonly Vector2 Area = new(340f, 100f);
    private static readonly Color BallpointBlue = new(0.12f, 0.2f, 0.52f);
    private static readonly Color Line = new(0.3f, 0.33f, 0.38f);
    private static readonly Color PenBody = new(0.16f, 0.17f, 0.2f, 0.4f);
    /// <summary>Which way the pen leans from its tip: up and to the right, as a right hand holds it.</summary>
    private static readonly Vector2 PenLean = new Vector2(0.45f, -1f).Normalized();

    private readonly Vector2[] _stroke = Stroke();
    /// <summary>Distance along the stroke to each of its points.</summary>
    private readonly float[] _along;

    /// <summary>How much is signed (0..1).</summary>
    public float Progress { get; private set; }
    public bool Complete => Progress >= 1f;

    public SignatureView()
    {
        CustomMinimumSize = Area;
        _along = new float[_stroke.Length];
        for (var i = 1; i < _stroke.Length; i++)
        {
            _along[i] = _along[i - 1] + _stroke[i].DistanceTo(_stroke[i - 1]);
        }
    }

    /// <summary>Back to an empty line, for the next time the card is read.</summary>
    public void Reset()
    {
        Progress = 0f;
        QueueRedraw();
    }

    /// <summary>Writes on while <paramref name="held"/>, unwrites otherwise. Once complete it stays so.</summary>
    public void Advance(float delta, bool held)
    {
        if (Complete)
        {
            return;
        }
        var progress = Mathf.Clamp(Progress + (held ? delta / SignSeconds : -delta / RollbackSeconds), 0f, 1f);
        if (progress != Progress)
        {
            Progress = progress;
            QueueRedraw();
        }
    }

    public override void _Draw()
    {
        DrawLine(new Vector2(0f, Baseline), new Vector2(Area.X, Baseline), Line, 1.5f);
        if (Progress <= 0f)
        {
            return;
        }
        var length = Progress * _along[^1];
        var drawn = new List<Vector2> { _stroke[0] };
        for (var i = 1; i < _stroke.Length && _along[i - 1] < length; i++)
        {
            var share = Mathf.Min((length - _along[i - 1]) / (_along[i] - _along[i - 1]), 1f);
            drawn.Add(_stroke[i - 1].Lerp(_stroke[i], share));
        }
        if (drawn.Count > 1)
        {
            DrawPolyline([.. drawn], BallpointBlue, 2.2f, true);
        }
        if (!Complete)
        {
            DrawPen(drawn[^1]);
        }
    }

    /// <summary>A ballpoint pen with its tip at <paramref name="tip"/>: a cone to the barrel, then the barrel.</summary>
    private void DrawPen(Vector2 tip)
    {
        var across = new Vector2(-PenLean.Y, PenLean.X);
        var cone = tip + (PenLean * 16f);
        var end = tip + (PenLean * 120f);
        DrawColoredPolygon([tip, cone + (across * 4.5f), cone - (across * 4.5f)], PenBody);
        DrawColoredPolygon([cone + (across * 5f), end + (across * 5f), end - (across * 5f), cone - (across * 5f)], PenBody);
    }

    /// <summary>The signature as one stroke: a tall first loop running into smaller cursive loops, then a flourish that
    /// swings back underneath.</summary>
    private static Vector2[] Stroke()
    {
        const int Loops = 120;
        const int Flourish = 40;
        var points = new Vector2[Loops + Flourish];
        for (var i = 0; i < Loops; i++)
        {
            var t = i / (float)(Loops - 1);
            // Loops come a little uneven, as a hand writes them.
            var phase = ((t * 6f) + (0.25f * Mathf.Sin(t * 13f))) * Mathf.Tau;
            // Drawn back a little each loop, so the stroke crosses itself like handwriting.
            var x = 10f + (t * 250f) - (11f * Mathf.Sin(phase));
            var height = 20f + (40f * Mathf.Exp(-t * 7f)) + (10f * Mathf.Sin(t * 17f) * Mathf.Sin(t * 5f));
            points[i] = new Vector2(x, Baseline - 8f - (height * 0.5f * (1f - Mathf.Cos(phase))));
        }
        var from = points[Loops - 1];
        var bend = new Vector2(Area.X + 10f, Baseline + 20f);
        var to = new Vector2(30f, Baseline + 12f);
        for (var i = 0; i < Flourish; i++)
        {
            var t = (i + 1) / (float)Flourish;
            points[Loops + i] = from.Lerp(bend, t).Lerp(bend.Lerp(to, t), t);
        }
        return points;
    }
}
