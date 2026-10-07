namespace Scalpel.Patients;

/// <summary>One painting operation of a disk: which channel, how strongly, how it combines with what's there.
/// </summary>
public readonly record struct PaintOp(int Channel, float Value, WoundMap.Mode Mode);

/// <summary>
/// CPU painted damage, fluid and closed-seam textures behind the skin shader. Every peer has its own copy.
/// The host decides what gets painted and broadcasts paint ops, so all copies stay identical.
/// Wounds and Fluids feed wound.gdshaderinc; the red channel of Seams is the simulated skin's closed-incision mask.
/// </summary>
public sealed class WoundMap
{
    public enum Layer { Wounds, Fluids, Seams }

    public enum Mode { Max, Add, Sub, Min }

    /// <summary>Texture sizes range from MinSize to MaxSize, picked so a texel covers about Texel meters on every
    /// site: a pad or blood pool then costs the same to paint on a small face as on a big belly.</summary>
    public const int MinSize = 128;
    public const int MaxSize = 512;
    public const float Texel = 0.0008f;
    // Channels of the Wounds layer.
    public const int Cut = 0;
    public const int Burn = 1;
    public const int Bruise = 2;
    public const int Stitch = 3;
    /// <summary>The Seams layer's channel: closure quality in Stitch must not also mean a tied running suture.</summary>
    public const int ClosedSeam = 0;
    // Channels of the Fluids layer.
    public const int Blood = 0;
    public const int Ink = 1;
    public const int Iodine = 2;
    public const int Grime = 3;
    /// <summary>Cells per side of the coarse grid that remembers what's been painted into.</summary>
    private const int Cells = 16;
    private const int LayerCount = 3;

    /// <summary>The pixels painted into, 4 bytes per texel. Copied into images and textures once per frame by
    /// <see cref="Flush"/>.</summary>
    private readonly byte[][] _data = new byte[LayerCount][];
    private readonly bool[] _dirty = new bool[LayerCount];
    /// <summary>Which cells of a coarse Cells x Cells grid have ever been painted into, per layer and channel.
    /// Subtracting where a channel was never painted changes nothing, so a wipe over clean skin skips it.</summary>
    private readonly bool[][] _painted = new bool[LayerCount][];
    private readonly Image[] _images = new Image[LayerCount];

    public WoundMap(int texels = MaxSize)
    {
        Size = texels;
        Textures = new ImageTexture[LayerCount];
        for (var i = 0; i < LayerCount; i++)
        {
            _images[i] = Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8);
            Textures[i] = ImageTexture.CreateFromImage(_images[i]);
            _data[i] = _images[i].GetData();
            _painted[i] = new bool[Cells * Cells * 4];
        }
    }

    /// <summary>Texels per side of every texture.</summary>
    public int Size { get; }

    public ImageTexture[] Textures { get; }

    public ImageTexture Texture(Layer layer) => Textures[(int)layer];

    /// <summary>The texture size for a site of this size in meters (a power of two).</summary>
    public static int SizeFor(Vector2 siteSize) =>
        Math.Clamp(1 << Mathf.RoundToInt(Mathf.Log(Mathf.Max(siteSize.X, siteSize.Y) / Texel) / Mathf.Log(2f)), MinSize, MaxSize);

    /// <summary>Stamps hard-edged disks along a segment. <paramref name="jitter"/> &gt; 0 makes a ragged, torn line.
    /// </summary>
    public void Stroke(Layer layer, int channel, Vector2 a, Vector2 b, float radius, float value, Mode mode,
        float jitter = 0f, ulong seed = 0)
    {
        var step = Mathf.Max(radius * 0.5f, 0.5f / Size);
        var steps = Math.Max(1, Mathf.CeilToInt(a.DistanceTo(b) / step));
        var rng = new RandomNumberGenerator { Seed = seed };
        for (var i = 0; i <= steps; i++)
        {
            var p = a.Lerp(b, (float)i / steps);
            if (jitter > 0f)
            {
                p += new Vector2(rng.RandfRange(-jitter, jitter), rng.RandfRange(-jitter, jitter));
            }
            Disk(layer, channel, p, radius, value, mode, soft: false);
        }
    }

    public void Disk(Layer layer, int channel, Vector2 center, float radius, float value, Mode mode, bool soft = true) =>
        DiskOps(layer, center, radius, [new PaintOp(channel, value, mode)], soft);

    /// <summary>
    /// Paints several channels of one disk. Each channel gets its own tight loop over the disk's rows, with the mode
    /// picked outside the loop: a wipe paints big disks every frame.
    /// </summary>
    public void DiskOps(Layer layer, Vector2 center, float radius, IEnumerable<PaintOp> ops, bool soft = true)
    {
        var data = _data[(int)layer];
        var painted = _painted[(int)layer];
        var c = center * Size;
        var r = Mathf.Max(radius * Size, 0.75f);
        var lastCell = new Vector2I(Cells - 1, Cells - 1);
        var cellFrom = ((Vector2I)((center - Vector2.One * radius) * Cells)).Clamp(Vector2I.Zero, lastCell);
        var cellTo = ((Vector2I)((center + Vector2.One * radius) * Cells)).Clamp(Vector2I.Zero, lastCell);
        foreach (var (channel, value, mode) in ops)
        {
            var k = value * 255f;
            var hard = Mathf.RoundToInt(k);
            var any = false;
            for (var cy = cellFrom.Y; cy <= cellTo.Y; cy++)
            {
                for (var cx = cellFrom.X; cx <= cellTo.X; cx++)
                {
                    var cell = (cy * Cells + cx) * 4 + channel;
                    any = any || painted[cell];
                    if (mode is Mode.Max or Mode.Add && k > 0f)
                    {
                        painted[cell] = true;
                    }
                }
            }
            if (mode is Mode.Sub or Mode.Min && !any)
            {
                continue;
            }
            for (var y = Math.Max(0, Mathf.FloorToInt(c.Y - r)); y < Math.Min(Size, Mathf.CeilToInt(c.Y + r) + 1); y++)
            {
                var dy = (y + 0.5f - c.Y) / r;
                if (dy * dy > 1f)
                {
                    continue;
                }
                var half = Mathf.Sqrt(1f - dy * dy) * r;
                var row = y * Size * 4 + channel;
                var from = Math.Max(0, Mathf.CeilToInt(c.X - half - 0.5f));
                var to = Math.Min(Size, Mathf.FloorToInt(c.X + half - 0.5f) + 1);
                var edge = 1f - dy * dy;
                for (var x = from; x < to; x++)
                {
                    var i = row + x * 4;
                    var amount = hard;
                    if (soft && mode != Mode.Min)
                    {
                        var dx = (x + 0.5f - c.X) / r;
                        amount = Mathf.RoundToInt(k * (edge - dx * dx));
                    }
                    data[i] = mode switch
                    {
                        Mode.Max => (byte)Math.Clamp(Math.Max(data[i], amount), 0, 255),
                        Mode.Add => (byte)Math.Clamp(data[i] + amount, 0, 255),
                        Mode.Sub => (byte)Math.Clamp(data[i] - amount, 0, 255),
                        _ => (byte)Math.Clamp(Math.Min(data[i], hard), 0, 255),
                    };
                }
            }
        }
        _dirty[(int)layer] = true;
    }

    /// <summary>One channel of the painted map at uv, 0..1, as painted so far (no need to wait for
    /// <see cref="Flush"/>).</summary>
    public float Value(Layer layer, int channel, Vector2 uv)
    {
        var at = (Vector2I)(uv.Clamp(Vector2.Zero, Vector2.One) * (Size - 1)).Floor();
        return _data[(int)layer][(at.Y * Size + at.X) * 4 + channel] / 255f;
    }

    /// <summary>Uploads changed images to the GPU. Call once per frame.</summary>
    public void Flush()
    {
        for (var i = 0; i < LayerCount; i++)
        {
            if (_dirty[i])
            {
                _images[i].SetData(Size, Size, false, Image.Format.Rgba8, _data[i]);
                Textures[i].Update(_images[i]);
                _dirty[i] = false;
            }
        }
    }
}
