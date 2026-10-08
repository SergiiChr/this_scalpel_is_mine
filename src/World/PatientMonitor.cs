namespace Scalpel.World;

/// <summary>
/// Bedside monitor: vitals on a screen (see <see cref="MonitorScreen"/>) and a pulse oximeter beep generated in code.
/// Beep pitch follows SpO2 like real monitors, so you can hear oxygen dropping without looking.
/// </summary>
public partial class PatientMonitor : Node3D
{
    private const float SampleRate = 22050f;
    private const float BeepLength = 0.09f;

    private MonitorScreen _screen = null!;
    private AudioStreamGeneratorPlayback? _playback;
    private float _beatTimer;
    private float _toneLeft;
    private float _toneFrequency = 880f;
    private float _phase;
    private float _alarmTimer;

    public void Build()
    {
        ModelSlot.Instantiate("props", "monitor", this,
            new Dictionary<string, Material> { ["screen"] = Materials.Glow(new Color(0.02f, 0.05f, 0.04f)) });
        BuildScreen();
        var player = new AudioStreamPlayer3D
        {
            Stream = new AudioStreamGenerator { MixRate = SampleRate, BufferLength = 0.2f },
            Bus = "SFX",
            UnitSize = 4f,
        };
        AddChild(player);
        if (DisplayServer.GetName() != "headless")
        {
            player.Play();
            _playback = (AudioStreamGeneratorPlayback)player.GetStreamPlayback();
        }
    }

    /// <summary>The screen is 2D, drawn in its own viewport and shown on a quad over the casing's glass.</summary>
    private void BuildScreen()
    {
        var viewport = new SubViewport { Size = (Vector2I)MonitorScreen.ScreenSize, Disable3D = true };
        AddChild(viewport);
        _screen = new MonitorScreen();
        viewport.AddChild(_screen);
        AddChild(new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(0.38f, 0.25f) },
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoTexture = viewport.GetTexture(),
            },
            Position = new Vector3(0f, 0.01f, 0.0155f),
        });
    }

    public void ShowLab(string text) => _screen.ShowLab(text);

    /// <summary>Alarm messages for the top bar, worst first.</summary>
    public static List<string> Alarms(Vitals vitals, bool alive)
    {
        var alarms = new List<string>();
        if (vitals.IsArrested || !alive)
        {
            alarms.Add("*** " + (alive ? vitals.RhythmName : "ASYSTOLE"));
        }
        if (vitals.Spo2 < 88f)
        {
            alarms.Add("** SpO2 LOW");
        }
        if (vitals.Systolic < 80f)
        {
            alarms.Add("** NIBP LOW");
        }
        return alarms;
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        var patient = Surgery.Current!.Patient;
        var vitals = patient.Vitals;
        var alarms = Alarms(vitals, patient.Alive);
        var beat = false;
        if (vitals.HeartRate > 1f && vitals.Rhythm == Rhythm.Sinus && patient.Alive)
        {
            _beatTimer -= dt;
            if (_beatTimer <= 0f)
            {
                _beatTimer = 60f / vitals.HeartRate;
                _screen.Beat(vitals.HeartRate);
                beat = true;
            }
        }
        _screen.Tick(vitals, patient.Alive, alarms, dt);
        if (_playback is null || Sfx.Deaf)
        {
            return;
        }
        ScheduleTones(vitals, patient.Alive, beat, alarms.Count > 0, dt);
        FillBuffer(_playback);
    }

    private void ScheduleTones(Vitals vitals, bool alive, bool beat, bool alarm, float delta)
    {
        if (vitals.Rhythm == Rhythm.Asystole || !alive)
        {
            _toneFrequency = 960f;
            _toneLeft = 0.1f;
            return;
        }
        if (beat)
        {
            _toneFrequency = Mathf.Remap(Mathf.Clamp(vitals.Spo2, 70f, 100f), 70f, 100f, 440f, 880f);
            _toneLeft = BeepLength;
        }
        _alarmTimer -= delta;
        if (alarm && _alarmTimer <= 0f)
        {
            _alarmTimer = 1.2f;
            _toneFrequency = 1300f;
            _toneLeft = 0.25f;
        }
    }

    private void FillBuffer(AudioStreamGeneratorPlayback playback)
    {
        var frames = playback.GetFramesAvailable();
        for (var i = 0; i < frames; i++)
        {
            var sample = 0f;
            if (_toneLeft > 0f)
            {
                sample = Mathf.Sin(_phase * Mathf.Tau) * 0.25f;
                _phase = Mathf.PosMod(_phase + (_toneFrequency / SampleRate), 1f);
                _toneLeft -= 1f / SampleRate;
            }
            playback.PushFrame(new Vector2(sample, sample));
        }
    }
}
