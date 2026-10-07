namespace Scalpel.World;

/// <summary>
/// What the bedside monitor shows, drawn in 2D and put on the screen through a SubViewport. Laid out like a real
/// patient monitor: sweeping waveforms on the left, numbers in their wave's color on the right, alarms in the top bar
/// and lab results along the bottom.
/// </summary>
public partial class MonitorScreen : Control
{
    public static readonly Vector2 ScreenSize = new(760f, 500f);
    private static readonly Color Background = new(0.01f, 0.015f, 0.02f);
    private static readonly Color Bar = new(0.11f, 0.13f, 0.17f);
    private static readonly Color Line = new(0.2f, 0.23f, 0.28f);
    private static readonly Color Ecg = new(0.3f, 1f, 0.4f);
    private static readonly Color Pleth = new(0.3f, 0.85f, 1f);
    private static readonly Color Nibp = new(1f, 0.45f, 0.45f);
    private static readonly Color Temp = new(0.9f, 0.9f, 0.9f);
    private static readonly Color Resp = new(1f, 0.9f, 0.3f);
    private static readonly Color Lab = new(1f, 0.75f, 0.35f);
    private static readonly Color Alarm = new(0.9f, 0.12f, 0.1f);
    private static readonly string[] WaveLabels = ["II", "Pleth", "Resp"];
    private static readonly Color[] WaveColors = [Ecg, Pleth, Resp];

    private const float Header = 34f;
    /// <summary>Waves run from WaveLeft to just short of WaveWidth, past the row labels.</summary>
    private const float WaveLeft = 56f;
    private const float WaveWidth = 480f;
    private const float WaveRow = 106f;
    private const float LabTop = Header + (WaveRow * 3f) + 8f;
    /// <summary>Samples a second.</summary>
    private const float SampleHz = 120f;
    /// <summary>Seconds across the screen before the sweep starts over.</summary>
    private const float SweepSeconds = 6f;
    /// <summary>Blank stretch ahead of the sweep, in samples.</summary>
    private const int Gap = 18;

    private readonly Font _font = ThemeDB.FallbackFont;
    /// <summary>One ring of samples per wave (ecg, pleth, resp), written at _head.</summary>
    private readonly float[][] _waves;
    private int _head;
    private float _carry;
    private float _time;
    /// <summary>Breath cycle 0..1, kept apart from _time so a changing rate doesn't make the trace jump.</summary>
    private float _breath;
    private float _sinceBeat = 10f;
    private float _period = 0.8f;
    private Rhythm _rhythm = Rhythm.Sinus;
    private float _pulse = 1f;
    private int _respRate;
    private Readout? _readout;
    private List<string> _alarms = [];
    private string _lab = "";

    /// <summary>The numbers on the right, as shown.</summary>
    private sealed record Readout(
        string HeartRate, string Rhythm, string Spo2, string Nibp, string MeanPressure, string Temperature, string Resp);

    public MonitorScreen()
    {
        Size = ScreenSize;
        _waves = [.. Enumerable.Range(0, 3).Select(_ => new float[(int)(SampleHz * SweepSeconds)])];
    }

    /// <summary>A new heartbeat: the next QRS starts now.</summary>
    public void Beat(float heartRate)
    {
        _sinceBeat = 0f;
        _period = 60f / Mathf.Max(heartRate, 1f);
    }

    public void ShowLab(string text) => _lab = text.TrimPrefix("LAB: ");

    public void Tick(Vitals vitals, bool alive, List<string> alarms, float delta)
    {
        _rhythm = alive ? vitals.Rhythm : Rhythm.Asystole;
        _pulse = _rhythm == Rhythm.Sinus ? Mathf.Clamp(vitals.Systolic / 120f, 0f, 1.2f) : 0f;
        _respRate = vitals.Anesthesia > 0.7f || !alive ? 0 : (int)(14 + (vitals.Panic * 12));
        _alarms = alarms;
        _readout = new Readout(
            $"{(int)vitals.HeartRate}",
            vitals.RhythmName,
            _pulse > 0f ? $"{(int)vitals.Spo2}" : "-?-",
            $"{(int)vitals.Systolic}/{(int)(vitals.Systolic * 0.65f)}",
            $"({(int)(vitals.Systolic * (1f + (0.65f * 2f)) / 3f)})",
            $"{vitals.Temperature:0.0}",
            _respRate > 0 ? $"{_respRate}" : "--");
        _carry += delta;
        const float step = 1f / SampleHz;
        while (_carry >= step)
        {
            _carry -= step;
            _time += step;
            _sinceBeat += step;
            _waves[0][_head] = EcgSample();
            _waves[1][_head] = PlethSample();
            _breath = Mathf.PosMod(_breath + (_respRate / 60f / SampleHz), 1f);
            _waves[2][_head] = Mathf.Sin(_breath * Mathf.Tau) * 0.8f;
            _head = (_head + 1) % _waves[0].Length;
        }
        QueueRedraw();
    }

    /// <summary>Lead II, -1..1: P, QRS and T around each beat, chaos in V-fib, a faint wobble in asystole.</summary>
    private float EcgSample()
    {
        var t = _sinceBeat;
        return _rhythm switch
        {
            Rhythm.Vfib => (Mathf.Sin(_time * Mathf.Tau * 4.7f) * 0.45f)
                + (Mathf.Sin((_time * Mathf.Tau * 6.3f) + 1f) * 0.25f)
                + (Mathf.Sin(_time * Mathf.Tau * 2.9f) * 0.2f),
            Rhythm.Asystole => Mathf.Sin(_time * Mathf.Tau * 0.7f) * 0.02f,
            _ => Bump(t, _period - 0.14f, 0.025f, 0.12f) + Bump(t, 0f, 0.01f, -0.12f) + Bump(t, 0.025f, 0.014f, 1f)
                + Bump(t, 0.055f, 0.012f, -0.3f) + Bump(t, 0.26f, 0.05f, 0.25f),
        };
    }

    /// <summary>Finger pulse: a quick rise after each beat and a smaller bump past the dicrotic notch.</summary>
    private float PlethSample() =>
        ((Bump(_sinceBeat, 0.28f, 0.08f, 1.4f) + Bump(_sinceBeat, 0.5f, 0.08f, 0.5f)) * _pulse) - (0.7f * _pulse);

    private static float Bump(float t, float center, float width, float height) =>
        height * Mathf.Exp(-Mathf.Pow((t - center) / width, 2f));

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, ScreenSize), Background);
        if (_readout is not { } readout)
        {
            return;
        }
        var flash = Mathf.PosMod(Time.GetTicksMsec() * 0.002f, 1f) > 0.5f;
        var alarms = string.Concat(_alarms);
        DrawHeader(flash);
        for (var i = 0; i < 3; i++)
        {
            var top = Header + (WaveRow * i);
            Text(WaveLabels[i], new Vector2(10f, top + 20f), 16, WaveColors[i]);
            DrawWave(_waves[i], top + (WaveRow * 0.55f), WaveRow * 0.36f, WaveColors[i]);
            DrawLine(new Vector2(0f, top + WaveRow), new Vector2(ScreenSize.X, top + WaveRow), Line);
        }
        DrawLine(new Vector2(WaveWidth, Header), new Vector2(WaveWidth, LabTop - 8f), Line);
        var at = new Vector2(WaveWidth + 14f, Header);
        DrawNumber("HR", "bpm", readout.HeartRate, at, Ecg, 66, _rhythm != Rhythm.Sinus && flash);
        Text(readout.Rhythm, at + new Vector2(0f, 92f), 15, Ecg);
        at.Y += WaveRow;
        DrawNumber("SpO2", "%", readout.Spo2, at, Pleth, 66, alarms.Contains("SpO2") && flash);
        at.Y += WaveRow;
        DrawNumber("NIBP", "mmHg", readout.Nibp, at, Nibp, 40, alarms.Contains("NIBP") && flash);
        Text(readout.MeanPressure, at + new Vector2(0f, 66f), 18, Nibp);
        Text($"T  {readout.Temperature} °C", at + new Vector2(0f, 96f), 17, Temp);
        Text($"RR  {readout.Resp}", new Vector2(ScreenSize.X - 12f, at.Y + 96f), 17, Resp, HorizontalAlignment.Right);
        DrawLab();
    }

    private void DrawHeader(bool flash)
    {
        DrawRect(new Rect2(0f, 0f, ScreenSize.X, Header), Bar);
        Text("OR 1    Adult", new Vector2(10f, 23f), 16, Temp);
        Text(Time.GetTimeStringFromSystem()[..5], new Vector2(ScreenSize.X - 10f, 23f), 16, Temp, HorizontalAlignment.Right);
        if (_alarms.Count == 0)
        {
            return;
        }
        var box = new Rect2(170f, 3f, 420f, Header - 6f);
        DrawRect(box, flash ? Alarm : Alarm.Darkened(0.5f));
        Text(string.Join("   ", _alarms), new Vector2(box.GetCenter().X, 23f), 17, Colors.White, HorizontalAlignment.Center);
    }

    /// <summary>Ring buffer as a sweep: drawn up to the write head, a gap, then the old trace after it.</summary>
    private void DrawWave(float[] wave, float middle, float halfHeight, Color color)
    {
        var step = (WaveWidth - WaveLeft - 10f) / wave.Length;
        var before = new List<Vector2>();
        var after = new List<Vector2>();
        for (var i = 0; i < wave.Length; i++)
        {
            if (Mathf.PosMod(i - _head, wave.Length) < Gap)
            {
                continue;
            }
            var point = new Vector2(WaveLeft + (i * step), middle - (Mathf.Clamp(wave[i], -1.2f, 1.2f) * halfHeight));
            (i < _head ? before : after).Add(point);
        }
        foreach (var line in (List<Vector2>[])[before, after])
        {
            if (line.Count > 1)
            {
                DrawPolyline([.. line], color, 2f, true);
            }
        }
    }

    private void DrawNumber(string title, string unit, string value, Vector2 at, Color color, int fontSize, bool alarm)
    {
        var width = ScreenSize.X - at.X - 8f;
        if (alarm)
        {
            DrawRect(new Rect2(at.X - 8f, at.Y + 2f, width + 14f, WaveRow - 4f), Alarm);
        }
        var ink = alarm ? Colors.White : color;
        Text(title, at + new Vector2(0f, 20f), 16, ink);
        Text(unit, at + new Vector2(width - 4f, 20f), 13, ink, HorizontalAlignment.Right);
        Text(value, at + new Vector2(width - 4f, 28f + (fontSize * 0.8f)), fontSize, ink, HorizontalAlignment.Right);
    }

    private void DrawLab()
    {
        Text("Lab", new Vector2(10f, LabTop + 14f), 15, Lab.Darkened(0.3f));
        var any = _lab.Length > 0;
        DrawMultilineString(_font, new Vector2(56f, LabTop + 14f), any ? _lab : "No results yet.",
            HorizontalAlignment.Left, ScreenSize.X - 66f, 16, 6, any ? Lab : Lab.Darkened(0.5f));
    }

    /// <summary>Text with its baseline at <paramref name="position"/>.Y; right aligned text ends at its X, centered text
    /// centers on it.</summary>
    private void Text(string text, Vector2 position, int fontSize, Color color,
        HorizontalAlignment align = HorizontalAlignment.Left)
    {
        var width = _font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X;
        var x = align switch
        {
            HorizontalAlignment.Right => position.X - width,
            HorizontalAlignment.Center => position.X - (width * 0.5f),
            _ => position.X,
        };
        DrawString(_font, new Vector2(x, position.Y), text, HorizontalAlignment.Left, -1, fontSize, color);
    }
}
