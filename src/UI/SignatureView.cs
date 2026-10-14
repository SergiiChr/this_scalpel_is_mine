namespace Scalpel.UI;

/// <summary>
/// The discharge line at the foot of the patient card. While the reader holds Interact the surgeon's signature writes
/// itself stroke by stroke, in the order a hand writes it, under a see-through pen and at an even speed; let go early,
/// it unwrites back to nothing. Drawn whole it stays signed.
/// </summary>
public partial class SignatureView : Control {
    /// <summary>How long Interact is held to sign.</summary>
    public const float SignSeconds = 2f;
    /// <summary>A part signature unwrites in at most this long.</summary>
    private const float RollbackSeconds = 0.6f;
    private const float Width = 250f;
    /// <summary>Where the line runs, as a share of the width: under the letters, above the long tails.</summary>
    private const float LineAt = 0.57f;
    private static readonly Color BallpointBlue = new(0.12f, 0.2f, 0.52f);
    private static readonly Color Line = new(0.3f, 0.33f, 0.38f);
    private static readonly Color PenBody = new(0.16f, 0.17f, 0.2f, 0.4f);
    /// <summary>Which way the pen leans from its tip: up and to the right, as a right hand holds it.</summary>
    private static readonly Vector2 PenLean = new Vector2(0.45f, -1f).Normalized();

    /// <summary>The author's signature traced from a photo, one array per pen stroke in writing order, x across 0..1.
    /// </summary>
    private static readonly Vector2[][] Strokes =
    [
        [new(0.1175f, 0.1604f), new(0.1252f, 0.1852f), new(0.129f, 0.2093f), new(0.1321f, 0.2427f),
            new(0.1306f, 0.246f), new(0.1271f, 0.2471f), new(0.1307f, 0.248f), new(0.1326f, 0.2513f),
            new(0.1364f, 0.3002f), new(0.1391f, 0.3096f), new(0.1376f, 0.319f), new(0.1407f, 0.3422f),
            new(0.1441f, 0.4374f), new(0.1421f, 0.4597f), new(0.1389f, 0.4743f)],
        [new(0f, 0.458f), new(0.0419f, 0.3714f), new(0.0843f, 0.2985f), new(0.1204f, 0.2521f), new(0.1311f, 0.2456f),
            new(0.1335f, 0.2326f), new(0.1424f, 0.2256f), new(0.1792f, 0.1845f), new(0.2204f, 0.1501f),
            new(0.2702f, 0.1252f), new(0.2899f, 0.1218f), new(0.3079f, 0.124f), new(0.3293f, 0.1352f),
            new(0.3393f, 0.1449f), new(0.3453f, 0.1535f), new(0.3552f, 0.1775f), new(0.3613f, 0.2007f),
            new(0.3662f, 0.253f), new(0.3662f, 0.2753f), new(0.3572f, 0.3345f), new(0.3447f, 0.3885f),
            new(0.3296f, 0.4322f), new(0.3302f, 0.4424f), new(0.3206f, 0.452f), new(0.2818f, 0.5309f),
            new(0.2667f, 0.5549f), new(0.25f, 0.5763f), new(0.229f, 0.598f), new(0.2075f, 0.6112f),
            new(0.2024f, 0.612f), new(0.1995f, 0.6097f), new(0.1998f, 0.5969f)],
        [new(0.1286f, 0.5395f), new(0.1198f, 0.5532f), new(0.1043f, 0.5952f), new(0.066f, 0.6827f)],
        [new(0.3036f, 0.428f), new(0.307f, 0.426f), new(0.3148f, 0.4254f), new(0.3242f, 0.427f),
            new(0.3284f, 0.4305f), new(0.3331f, 0.4494f), new(0.3318f, 0.4717f), new(0.3328f, 0.5f),
            new(0.3371f, 0.5232f), new(0.3422f, 0.5369f), new(0.3475f, 0.5455f), new(0.3593f, 0.5562f),
            new(0.3679f, 0.5584f), new(0.3842f, 0.5556f), new(0.3945f, 0.5472f), new(0.4088f, 0.5292f),
            new(0.4255f, 0.4974f), new(0.4339f, 0.4691f), new(0.4374f, 0.4503f), new(0.4372f, 0.4314f),
            new(0.4322f, 0.4185f), new(0.428f, 0.4137f), new(0.4228f, 0.4117f), new(0.416f, 0.4123f),
            new(0.4074f, 0.4181f), new(0.3925f, 0.4374f), new(0.3753f, 0.4751f), new(0.3667f, 0.5026f),
            new(0.3645f, 0.5292f), new(0.3668f, 0.5412f), new(0.3732f, 0.5531f), new(0.3757f, 0.5557f),
            new(0.3799f, 0.5566f)],
        [new(0.5172f, 0.4031f), new(0.5219f, 0.3877f), new(0.5223f, 0.3782f), new(0.5161f, 0.3619f),
            new(0.5111f, 0.3555f), new(0.5026f, 0.3512f), new(0.4957f, 0.3505f), new(0.4889f, 0.3521f),
            new(0.4839f, 0.3559f), new(0.4769f, 0.3654f), new(0.4686f, 0.3851f), new(0.4644f, 0.3997f),
            new(0.4605f, 0.4288f), new(0.4605f, 0.4443f), new(0.464f, 0.4614f), new(0.4734f, 0.4733f),
            new(0.4777f, 0.4761f), new(0.4837f, 0.4768f), new(0.4923f, 0.4742f), new(0.4993f, 0.4683f),
            new(0.5072f, 0.4571f), new(0.5155f, 0.441f), new(0.5256f, 0.4337f), new(0.5266f, 0.4273f),
            new(0.5249f, 0.5575f), new(0.5334f, 0.6509f), new(0.5326f, 0.7067f), new(0.5289f, 0.7393f),
            new(0.5233f, 0.7624f), new(0.5081f, 0.8027f), new(0.4991f, 0.8146f), new(0.494f, 0.8171f),
            new(0.4871f, 0.817f), new(0.4784f, 0.8096f), new(0.4727f, 0.7933f), new(0.47f, 0.7753f),
            new(0.4708f, 0.7453f), new(0.473f, 0.7273f), new(0.4788f, 0.6895f), new(0.4899f, 0.6407f),
            new(0.4945f, 0.6235f), new(0.509f, 0.5849f), new(0.5163f, 0.5688f), new(0.5248f, 0.5616f),
            new(0.5254f, 0.5386f), new(0.5364f, 0.5283f), new(0.5501f, 0.4983f), new(0.5689f, 0.4545f),
            new(0.5869f, 0.4048f), new(0.6002f, 0.3619f), new(0.6088f, 0.3499f), new(0.6058f, 0.3259f),
            new(0.6019f, 0.3208f), new(0.5886f, 0.2753f), new(0.579f, 0.2281f), new(0.5729f, 0.175f),
            new(0.5738f, 0.1338f), new(0.5785f, 0.1029f), new(0.5911f, 0.066f), new(0.6015f, 0.0555f),
            new(0.6063f, 0.0534f), new(0.6175f, 0.0542f), new(0.6286f, 0.0617f), new(0.6389f, 0.078f),
            new(0.6423f, 0.0866f), new(0.6467f, 0.1252f), new(0.6458f, 0.1604f), new(0.6434f, 0.1818f),
            new(0.6309f, 0.2479f), new(0.6149f, 0.313f), new(0.6127f, 0.3189f), new(0.6069f, 0.3253f),
            new(0.6055f, 0.3293f), new(0.6178f, 0.3954f), new(0.6199f, 0.4262f), new(0.6185f, 0.44f),
            new(0.6123f, 0.4666f), new(0.6072f, 0.4794f), new(0.5968f, 0.494f), new(0.5849f, 0.5019f),
            new(0.581f, 0.5005f), new(0.5791f, 0.4966f), new(0.5837f, 0.4751f), new(0.5907f, 0.4588f),
            new(0.6071f, 0.4305f), new(0.6108f, 0.4258f), new(0.6183f, 0.4207f), new(0.6193f, 0.401f),
            new(0.6306f, 0.3928f), new(0.6672f, 0.3491f), new(0.6917f, 0.3242f), new(0.7038f, 0.3179f),
            new(0.7091f, 0.3107f), new(0.7027f, 0.3216f), new(0.7018f, 0.3422f), new(0.712f, 0.3731f),
            new(0.7233f, 0.3889f), new(0.7358f, 0.3951f), new(0.7427f, 0.3955f), new(0.753f, 0.3934f),
            new(0.7683f, 0.3843f), new(0.7807f, 0.3696f), new(0.7945f, 0.3431f), new(0.7996f, 0.3225f),
            new(0.8007f, 0.3002f), new(0.7979f, 0.2762f), new(0.7929f, 0.2616f), new(0.7856f, 0.2566f),
            new(0.7719f, 0.2591f), new(0.7599f, 0.267f), new(0.7486f, 0.2796f), new(0.7413f, 0.2933f),
            new(0.7326f, 0.3165f), new(0.7308f, 0.3285f), new(0.7322f, 0.3388f), new(0.7364f, 0.3473f),
            new(0.7427f, 0.3551f), new(0.7513f, 0.3604f), new(0.7624f, 0.3611f), new(0.7684f, 0.36f),
            new(0.7766f, 0.3549f), new(0.7949f, 0.336f), new(0.8005f, 0.3202f), new(0.8067f, 0.3147f),
            new(0.8326f, 0.271f), new(0.837f, 0.2673f), new(0.8426f, 0.2656f), new(0.8442f, 0.2669f),
            new(0.8448f, 0.2727f), new(0.8441f, 0.3019f), new(0.8489f, 0.3379f), new(0.853f, 0.3473f),
            new(0.8585f, 0.3535f), new(0.8636f, 0.3548f), new(0.8698f, 0.3514f), new(0.878f, 0.337f),
            new(0.8885f, 0.3096f), new(0.916f, 0.187f), new(0.9344f, 0.1261f), new(0.9573f, 0.0712f),
            new(0.9741f, 0.0386f), new(0.9853f, 0.012f), new(0.994f, 0f)],
        [new(0.5017f, 0.2959f), new(0.4994f, 0.2925f), new(0.4997f, 0.289f), new(0.5069f, 0.282f),
            new(0.5609f, 0.2456f), new(0.5772f, 0.2364f), new(0.6089f, 0.2137f), new(0.6372f, 0.1996f),
            new(0.6433f, 0.1942f), new(0.6792f, 0.1759f), new(0.8036f, 0.1227f), new(0.8379f, 0.1071f),
            new(0.8473f, 0.1012f)],
        [new(0.9906f, 0.3259f), new(0.9917f, 0.331f), new(0.999f, 0.3413f), new(1f, 0.3465f)],
    ];

    private readonly Vector2[][] _strokes = [.. Strokes.Select(stroke => stroke.Select(point => point * Width).ToArray())];
    /// <summary>Length of each stroke.</summary>
    private readonly float[] _lengths;
    private readonly float _total;

    /// <summary>How much is signed (0..1).</summary>
    public float Progress { get; private set; }
    public bool Complete => Progress >= 1f;

    public SignatureView() {
        CustomMinimumSize = new Vector2(Width, Width * Strokes.Max(stroke => stroke.Max(point => point.Y)));
        _lengths = [.. _strokes.Select(stroke => stroke.Zip(stroke.Skip(1), (a, b) => a.DistanceTo(b)).Sum())];
        _total = _lengths.Sum();
    }

    /// <summary>Back to an empty line, for the next time the card is read.</summary>
    public void Reset() {
        Progress = 0f;
        QueueRedraw();
    }

    /// <summary>Writes on while <paramref name="held"/>, unwrites otherwise. Once complete it stays so.</summary>
    public void Advance(float delta, bool held) {
        if (Complete) {
            return;
        }
        var progress = Mathf.Clamp(Progress + (held ? delta / SignSeconds : -delta / RollbackSeconds), 0f, 1f);
        if (progress != Progress) {
            Progress = progress;
            QueueRedraw();
        }
    }

    public override void _Draw() {
        DrawLine(new Vector2(0f, Width * LineAt), new Vector2(Width, Width * LineAt), Line, 1.5f);
        var left = Progress * _total;
        for (var s = 0; s < _strokes.Length && left > 0f; s++) {
            var drawn = Partial(_strokes[s], left);
            if (drawn.Length > 1) {
                DrawPolyline(drawn, BallpointBlue, 2f, true);
            }
            left -= _lengths[s];
            if (left < 0f && !Complete) {
                DrawPen(drawn[^1]);
            }
        }
    }

    /// <summary>The first <paramref name="length"/> of <paramref name="stroke"/>.</summary>
    private static Vector2[] Partial(Vector2[] stroke, float length) {
        var drawn = new List<Vector2> { stroke[0] };
        for (var i = 1; i < stroke.Length && length > 0f; i++) {
            var step = stroke[i].DistanceTo(stroke[i - 1]);
            drawn.Add(stroke[i - 1].Lerp(stroke[i], Mathf.Min(length / step, 1f)));
            length -= step;
        }
        return [.. drawn];
    }

    /// <summary>A ballpoint pen with its tip at <paramref name="tip"/>: a cone to the barrel, then the barrel.</summary>
    private void DrawPen(Vector2 tip) {
        var across = new Vector2(-PenLean.Y, PenLean.X);
        var cone = tip + (PenLean * 16f);
        var end = tip + (PenLean * 120f);
        DrawColoredPolygon([tip, cone + (across * 4.5f), cone - (across * 4.5f)], PenBody);
        DrawColoredPolygon([cone + (across * 5f), end + (across * 5f), end - (across * 5f), cone - (across * 5f)], PenBody);
    }
}
