using System.Threading.Tasks;

namespace Scalpel.Visuals;

/// <summary>One continuous blood layer for every piece of the patient's skin. Surface solvers supply samples;
/// neither their grids nor the skin meshes define a rendering boundary. A small volume around the wet skin is
/// stored as an atlas of slices, sampled identically by the body and the simulated skin.</summary>
internal sealed class BodyBloodLayer : IDisposable {
    internal readonly record struct Sample(Vector3 At, Vector3 Normal, float Coverage, float Radius);
    private sealed record Field(Vector3 Origin, float Cell, Vector3I Size, int Columns, int Width, int Height, byte[] Pixels);
    private const float CellSize = 0.0025f;
    private const int MaxAxis = 192;
    private readonly List<ShaderMaterial> _materials = [];
    private readonly Image _image = Image.CreateEmpty(1, 1, false, Image.Format.R8);
    private Task<Field>? _pending;
    private Field? _field;
    private float[] _weighted = [], _weight = [];
    private byte[] _pixels = [];
    private Transform3D _worldToField = Transform3D.Identity;
    internal ImageTexture Texture { get; }
    internal bool Busy => _pending is not null;
    internal bool HasCoverage => _field is { Size.X: > 1 };

    internal BodyBloodLayer() => Texture = ImageTexture.CreateFromImage(_image);

    internal void Bind(ShaderMaterial material) {
        _materials.Add(material);
        material.SetShaderParameter("body_blood_atlas", Texture);
        material.SetMatrix(ShaderParam.BloodInverse, new Projection(_worldToField));
    }

    internal void Place(Transform3D worldToField) {
        if (worldToField == _worldToField) { return; }
        _worldToField = worldToField;
        foreach (var material in _materials) {
            material.SetMatrix(ShaderParam.BloodInverse, new Projection(worldToField));
        }
    }

    internal void Request(Sample[] samples) {
        if (_pending is null) { _pending = Task.Run(() => Raster(samples)); }
    }

    internal void PublishFinished() {
        if (_pending is { IsCompleted: true }) { Finish(); }
    }

    internal void Finish() {
        if (_pending is not { } pending) { return; }
        _pending = null;
        Publish(pending.GetAwaiter().GetResult());
    }

    internal void Rebuild(Sample[] samples) {
        Finish();
        Publish(Raster(samples));
    }

    private Field Raster(Sample[] samples) {
        var low = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var high = -low;
        foreach (var sample in samples) {
            if (sample.Coverage <= 0.002f) { continue; }
            var reach = Vector3.One * (sample.Radius + CellSize * 2f);
            low = low.Min(sample.At - reach);
            high = high.Max(sample.At + reach);
        }
        if (float.IsPositiveInfinity(low.X)) { return new Field(Vector3.Zero, CellSize, Vector3I.Zero, 1, 1, 1, [0]); }
        // Quantized bounds keep creeping pools from resizing their texture on every update. Resolution adapts only
        // for blood spread over a large part of the body; local films retain millimetre-scale detail.
        low = (low / 0.04f).Floor() * 0.04f;
        high = (high / 0.04f).Ceil() * 0.04f;
        var extent = high - low;
        var cell = Mathf.Max(CellSize, Mathf.Max(extent.X, Mathf.Max(extent.Y, extent.Z)) / (MaxAxis - 1));
        var size = (Vector3I)(extent / cell).Ceil() + Vector3I.One;
        var columns = Math.Max(1, Mathf.CeilToInt(Mathf.Sqrt((float)size.Z * size.Y / size.X)));
        var width = size.X * columns;
        var height = size.Y * ((size.Z + columns - 1) / columns);
        var count = width * height;
        if (_pixels.Length != count) {
            _pixels = new byte[count]; _weighted = new float[count]; _weight = new float[count];
        } else {
            Array.Clear(_pixels); Array.Clear(_weighted); Array.Clear(_weight);
        }
        foreach (var sample in samples) {
            var radius = Mathf.Max(sample.Radius, cell * 1.5f);
            var shell = cell * 2f;
            var reach = Mathf.Max(radius, shell);
            var from = ((Vector3I)((sample.At - Vector3.One * reach - low) / cell).Floor()).Clamp(Vector3I.Zero, size - Vector3I.One);
            var to = ((Vector3I)((sample.At + Vector3.One * reach - low) / cell).Ceil()).Clamp(Vector3I.Zero, size - Vector3I.One);
            var normal = sample.Normal.Normalized();
            for (var z = from.Z; z <= to.Z; z++) {
                var tileX = z % columns * size.X;
                var tileY = z / columns * size.Y;
                for (var y = from.Y; y <= to.Y; y++) {
                    var start = (tileY + y) * width + tileX;
                    for (var x = from.X; x <= to.X; x++) {
                        var delta = low + new Vector3(x, y, z) * cell - sample.At;
                        var depth = delta.Dot(normal);
                        var tangent = Mathf.Max(0f, delta.LengthSquared() - depth * depth);
                        var weight = Mathf.Max(0f, 1f - tangent / (radius * radius) - depth * depth / (shell * shell));
                        weight *= weight;
                        _weighted[start + x] += sample.Coverage * weight;
                        _weight[start + x] += weight;
                    }
                }
            }
        }
        for (var i = 0; i < count; i++) {
            if (_weight[i] > 0f) { _pixels[i] = (byte)Mathf.RoundToInt(255f * Mathf.Clamp(_weighted[i] / _weight[i], 0f, 1f)); }
        }
        return new Field(low, cell, size, columns, width, height, _pixels);
    }

    private void Publish(Field field) {
        var resize = _image.GetWidth() != field.Width || _image.GetHeight() != field.Height;
        _image.SetData(field.Width, field.Height, false, Image.Format.R8, field.Pixels);
        if (resize) { Texture.SetImage(_image); } else { Texture.Update(_image); }
        _field = field;
        foreach (var material in _materials) {
            material.SetShaderParameter(ShaderParam.BloodOrigin, field.Origin);
            material.SetShaderParameter(ShaderParam.BloodCell, field.Cell);
            material.SetShaderParameter(ShaderParam.BloodGrid, (Vector3)field.Size);
            material.SetShaderParameter(ShaderParam.BloodColumns, (float)field.Columns);
        }
    }

    /// <summary>The same trilinear sample the skin shader reads; useful for checking contact and continuity.</summary>
    internal float CoverageAt(Vector3 world) {
        if (_field is not { Size.X: > 1 } field) { return 0f; }
        var p = (_worldToField * world - field.Origin) / field.Cell;
        if (p.X < 0f || p.Y < 0f || p.Z < 0f || p.X > field.Size.X - 1 || p.Y > field.Size.Y - 1 || p.Z > field.Size.Z - 1) { return 0f; }
        var a = (Vector3I)p.Floor();
        var f = p - (Vector3)a;
        float Pixel(int x, int y, int z) => _image.GetPixel(z % field.Columns * field.Size.X + x, z / field.Columns * field.Size.Y + y).R;
        float Slice(int z) => Mathf.Lerp(Mathf.Lerp(Pixel(a.X, a.Y, z), Pixel(Math.Min(a.X + 1, field.Size.X - 1), a.Y, z), f.X),
            Mathf.Lerp(Pixel(a.X, Math.Min(a.Y + 1, field.Size.Y - 1), z), Pixel(Math.Min(a.X + 1, field.Size.X - 1), Math.Min(a.Y + 1, field.Size.Y - 1), z), f.X), f.Y);
        return Mathf.Lerp(Slice(a.Z), Slice(Math.Min(a.Z + 1, field.Size.Z - 1)), f.Z);
    }

    public void Dispose() {
        Finish();
        Texture.Dispose();
        _image.Dispose();
    }
}
