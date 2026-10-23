using System.Threading.Tasks;

namespace Scalpel.Visuals;

/// <summary>One continuous blood layer for every piece of the patient's skin. Surface solvers supply samples;
/// neither their grids nor the skin meshes define a rendering boundary. A small volume around the wet skin is
/// stored as an atlas of slices, sampled identically by the body and the simulated skin a little outside the skin
/// each fragment lies on (<see cref="ReadOutside"/>), so a flap's underside and the wall of a cut stay clean.</summary>
internal sealed class BodyBloodLayer : IDisposable {
    /// <summary>Wet skin at <paramref name="At"/> (field space) facing <paramref name="Normal"/>, covered
    /// <paramref name="Coverage"/> (0..1), blended with its neighbours within <paramref name="Radius"/>.</summary>
    internal readonly record struct Sample(Vector3 At, Vector3 Normal, float Coverage, float Radius);
    private sealed record Field(Vector3 Origin, float Cell, Vector3I Size, int Columns, int Width, int Height);
    /// <summary>The field's sides are rounded up to this many voxels, so a creeping pool seldom resizes its texture.
    /// </summary>
    private const int SizeStep = 16;
    /// <summary>Voxel size (m) while the wet skin fits <see cref="MaxVoxels"/>; coarser over more of the body.</summary>
    internal const float CellSize = 0.0025f;
    private const int MaxAxis = 192;
    /// <summary>The most voxels a field holds: its bytes go to the GPU on every change.</summary>
    private const int MaxVoxels = 2_000_000;
    /// <summary>How far outside the skin, in voxels, the field is read (body_blood.gdshaderinc reads the same).</summary>
    internal const float ReadOutside = 0.75f;
    // A sample's blood in depth, in voxels: whole up to OutFull outside the skin and InFull under it, gone at OutEnd
    // and InEnd. Read ReadOutside out, skin keeps all of it, a Tolerance off either way too, from every voxel the
    // read blends (OutFull is a whole voxel past the read, or skin crossing voxel rows at a slant showed rings); the
    // underside of the thinnest skin (1.5 mm, under a voxel) reads nothing, and the wall of a cut less than half.
    private const float OutFull = 2f, OutEnd = 2.75f, InFull = 0.5f, InEnd = 0.9f;
    /// <summary>How far (voxels) a sample may move, and how much its coverage may change, before the field is built
    /// again: the body breathing against the site's frame, or a pool creeping, isn't worth one.</summary>
    private const float Tolerance = 0.25f, CoverageTolerance = 0.02f;
    private readonly List<ShaderMaterial> _materials = [];
    private readonly Image _image = Image.CreateEmpty(1, 1, false, Image.Format.R8);
    private Task<Field?>? _pending;
    /// <summary>The last field built; read by the worker only to size its tolerance.</summary>
    private volatile Field? _field;
    /// <summary>The samples the last field was built from: the same ones again need no new field.</summary>
    private Sample[] _input = [];
    private int _inputCount;
    private float[] _weighted = [], _weight = [], _depth = [];
    private byte[] _pixels = [];
    private int _textureWidth, _textureHeight;
    private Transform3D _worldToField = Transform3D.Identity;
    internal ImageTexture Texture { get; }
    internal bool Busy => _pending is not null;

    internal BodyBloodLayer() {
        Texture = ImageTexture.CreateFromImage(_image);
        (_textureWidth, _textureHeight) = (1, 1);
    }

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

    /// <summary>Gathers the samples with <paramref name="gather"/> and builds the field from them on a worker, unless
    /// one is at it. Samples within <see cref="Tolerance"/> of the last field's keep it, and send nothing to the GPU.
    /// </summary>
    internal void Request(Func<IReadOnlyList<Sample>> gather) {
        if (_pending is null) { _pending = Task.Run(() => Build(gather)); }
    }

    internal void PublishFinished() {
        if (_pending is { IsCompleted: true }) { Finish(); }
    }

    internal void Finish() {
        if (_pending is not { } pending) { return; }
        _pending = null;
        if (pending.GetAwaiter().GetResult() is { } field) { Publish(field); }
    }

    /// <summary>Gathers the samples and builds and publishes the field now.</summary>
    internal void Rebuild(Func<IReadOnlyList<Sample>> gather) {
        Finish();
        if (Build(gather) is { } field) { Publish(field); }
    }

    internal void Rebuild(IReadOnlyList<Sample> samples) => Rebuild(() => samples);

    private Field? Build(Func<IReadOnlyList<Sample>> gather) => Take(gather()) ? Raster(_input, _inputCount) : null;

    /// <summary>Keeps <paramref name="samples"/> as the next field's input; false when they are the last one's within
    /// tolerance.</summary>
    private bool Take(IReadOnlyList<Sample> samples) {
        var same = samples.Count == _inputCount;
        var cell = _field?.Cell ?? CellSize;
        for (var i = 0; same && i < _inputCount; i++) {
            var (now, was) = (samples[i], _input[i]);
            same = now.At.DistanceSquaredTo(was.At) <= Tolerance * Tolerance * cell * cell
                && Mathf.Abs(now.Coverage - was.Coverage) <= CoverageTolerance && now.Normal.Dot(was.Normal) >= 0.99f
                && now.Radius == was.Radius;
        }
        if (same) { return false; }
        if (_input.Length < samples.Count) { Array.Resize(ref _input, Math.Max(samples.Count, _input.Length * 2)); }
        for (var i = 0; i < samples.Count; i++) { _input[i] = samples[i]; }
        _inputCount = samples.Count;
        return true;
    }

    private Field Raster(Sample[] samples, int count) {
        var low = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        var high = -low;
        for (var s = 0; s < count; s++) {
            var sample = samples[s];
            if (sample.Coverage <= 0.002f) { continue; }
            var reach = Vector3.One * (sample.Radius + CellSize * OutEnd);
            low = low.Min(sample.At - reach);
            high = high.Max(sample.At + reach);
        }
        if (float.IsPositiveInfinity(low.X)) {
            _image.SetData(1, 1, false, Image.Format.R8, [0]);
            return new Field(Vector3.Zero, CellSize, Vector3I.Zero, 1, 1, 1);
        }
        // Quantized bounds keep creeping pools from resizing their texture on every update. Resolution adapts only
        // for blood spread over a large part of the body; local films retain millimetre-scale detail.
        low = (low / 0.04f).Floor() * 0.04f;
        high = (high / 0.04f).Ceil() * 0.04f;
        var extent = high - low;
        var cell = Mathf.Max(CellSize, Mathf.Max(extent.X, Mathf.Max(extent.Y, extent.Z)) / (MaxAxis - 1));
        cell = Mathf.Max(cell, Mathf.Pow(extent.X * extent.Y * extent.Z / MaxVoxels, 1f / 3f));
        var size = ((Vector3I)(extent / cell).Ceil() + Vector3I.One * SizeStep) / SizeStep * SizeStep;
        var columns = Math.Max(1, Mathf.CeilToInt(Mathf.Sqrt((float)size.Z * size.Y / size.X)));
        var width = size.X * columns;
        var height = size.Y * ((size.Z + columns - 1) / columns);
        var voxels = width * height;
        // The image needs its exact size; the sums keep the most they ever needed.
        if (_pixels.Length != voxels) { _pixels = new byte[voxels]; } else { Array.Clear(_pixels); }
        if (_weighted.Length < voxels) { _weighted = new float[voxels]; _weight = new float[voxels]; _depth = new float[voxels]; } else {
            Array.Clear(_weighted, 0, voxels); Array.Clear(_weight, 0, voxels); Array.Clear(_depth, 0, voxels);
        }
        // Plain floats in the voxel loop: it runs millions of times a field.
        var (outSlope, inSlope) = (1f / (OutEnd - OutFull), 1f / (InEnd - InFull));
        for (var s = 0; s < count; s++) {
            var sample = samples[s];
            var radius = Mathf.Max(sample.Radius, cell * 1.5f);
            var reach = Mathf.Max(radius, cell * OutEnd);
            var from = ((Vector3I)((sample.At - Vector3.One * reach - low) / cell).Floor()).Clamp(Vector3I.Zero, size - Vector3I.One);
            var to = ((Vector3I)((sample.At + Vector3.One * reach - low) / cell).Ceil()).Clamp(Vector3I.Zero, size - Vector3I.One);
            var normal = sample.Normal.Normalized();
            var (nx, ny, nz) = (normal.X / cell, normal.Y / cell, normal.Z / cell);
            var (ax, ay, az) = (sample.At.X - low.X, sample.At.Y - low.Y, sample.At.Z - low.Z);
            var (r2, cell2, coverage) = (radius * radius, cell * cell, sample.Coverage);
            for (var z = from.Z; z <= to.Z; z++) {
                var tileX = z % columns * size.X;
                var tileY = z / columns * size.Y;
                var dz = z * cell - az;
                for (var y = from.Y; y <= to.Y; y++) {
                    var start = (tileY + y) * width + tileX;
                    var dy = y * cell - ay;
                    var alongYz = dy * ny + dz * nz;
                    var squareYz = dy * dy + dz * dz;
                    for (var x = from.X; x <= to.X; x++) {
                        var dx = x * cell - ax;
                        var depth = dx * nx + alongYz;
                        if (depth > OutEnd || depth < -InEnd) { continue; }
                        var weight = 1f - (dx * dx + squareYz - depth * depth * cell2) / r2;
                        if (weight <= 0f) { continue; }
                        var held = depth >= 0f ? (OutEnd - depth) * outSlope : (InEnd + depth) * inSlope;
                        if (held <= 0f) { continue; }
                        weight *= weight;
                        _weighted[start + x] += coverage * weight;
                        _weight[start + x] += weight;
                        if (held > _depth[start + x]) { _depth[start + x] = held < 1f ? held : 1f; }
                    }
                }
            }
        }
        for (var i = 0; i < voxels; i++) {
            // Blood blends between the samples round a voxel, whatever their weights; depth alone thins it.
            if (_weight[i] > 0f) { _pixels[i] = (byte)Mathf.RoundToInt(255f * Mathf.Clamp(_weighted[i] / _weight[i] * _depth[i], 0f, 1f)); }
        }
        // Off the main thread: nothing reads the image until the field is published.
        _image.SetData(width, height, false, Image.Format.R8, _pixels);
        return new Field(low, cell, size, columns, width, height);
    }

    private void Publish(Field field) {
        if (field.Width != _textureWidth || field.Height != _textureHeight) {
            Texture.SetImage(_image);
            (_textureWidth, _textureHeight) = (field.Width, field.Height);
        } else {
            Texture.Update(_image);
        }
        _field = field;
        foreach (var material in _materials) {
            material.SetShaderParameter(ShaderParam.BloodOrigin, field.Origin);
            material.SetShaderParameter(ShaderParam.BloodCell, field.Cell);
            material.SetShaderParameter(ShaderParam.BloodGrid, (Vector3)field.Size);
            material.SetShaderParameter(ShaderParam.BloodColumns, (float)field.Columns);
        }
    }

    /// <summary>The blood the shaders show on skin at <paramref name="world"/> facing <paramref name="normal"/>:
    /// the field read <see cref="ReadOutside"/> voxels out of it.</summary>
    internal float CoverageOn(Vector3 world, Vector3 normal) {
        if (_field is not { Size.X: > 1 } field) { return 0f; }
        var at = _worldToField * world + (_worldToField.Basis * normal).Normalized() * (ReadOutside * field.Cell);
        return CoverageAt(_worldToField.AffineInverse() * at);
    }

    /// <summary>The same trilinear sample the skin shader reads at <paramref name="world"/> itself; useful for
    /// checking contact and continuity.</summary>
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
