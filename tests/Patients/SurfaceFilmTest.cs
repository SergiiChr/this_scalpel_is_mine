namespace Scalpel.Tests.Patients;

[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("liquids")]
public class SurfaceFilmTest {
    private static readonly SurfaceFilm.Settings Floor = new(0.01f, 0.001f, 0.0005f);

    [TestCase]
    public void APoolOnAFlatSurfaceSpreadsRoundAndKeepsItsVolume() {
        var film = new SurfaceFilm(Plane(0.6f), Floor, Up(6));
        var center = film.Nearest(Vector3.Zero);
        film.Add(center, 5f);
        Run(film, Vector3.Down, 3f);
        AssertFloat(film.HeldMl).OverrideFailureMessage("spreading keeps every milliliter").IsEqualApprox(5f, 0.0001f);
        AssertInt(film.Released.Count).OverrideFailureMessage("nothing drips off a flat floor").IsEqual(0);
        var wet = Enumerable.Range(0, film.Count).Where(k => film.CoverageAt(k) > 0.5f).Select(film.At).ToList();
        var across = wet.Max(p => p.X) - wet.Min(p => p.X);
        var diagonal = wet.Max(p => (p.X + p.Z) * Mathf.Sqrt(0.5f)) - wet.Min(p => (p.X + p.Z) * Mathf.Sqrt(0.5f));
        AssertFloat(across).OverrideFailureMessage($"5 ml spreads into a pool several centimeters across ({across * 100f:0.0} cm)")
            .IsGreater(0.05f);
        AssertFloat(diagonal / across).OverrideFailureMessage($"the pool is round, not the grid's square ({diagonal / across:0.00})")
            .IsBetween(0.85f, 1.15f);
        var edge = wet.Max(p => p.X);
        var outside = Enumerable.Range(0, film.Count)
            .Where(k => Mathf.Abs(film.At(k).Z) < 0.006f && film.At(k).X > edge).MinBy(k => film.At(k).X);
        AssertFloat(film.CoverageAt(outside)).OverrideFailureMessage("the film stops at its edge rather than thinning out over the floor")
            .IsLess(0.5f);
    }

    [TestCase]
    public void LiquidOnASlopeRunsDownhill() {
        var tilt = Basis.FromEuler(new Vector3(0f, 0f, 0.4f));
        var film = new SurfaceFilm(Plane(0.6f).Select(p => tilt * p).ToArray(), Floor, Up(6).Select(n => tilt * n).ToArray());
        var start = film.Nearest(Vector3.Zero);
        film.Add(start, 3f);
        Run(film, Vector3.Down, 2f);
        var mass = Enumerable.Range(0, film.Count).Sum(film.MlAt);
        var center = Enumerable.Range(0, film.Count).Aggregate(Vector3.Zero, (sum, k) => sum + film.At(k) * film.MlAt(k)) / mass;
        AssertFloat(center.Y).OverrideFailureMessage($"the liquid's middle has run down the slope ({center.Y * 100f:0.0} cm)")
            .IsLess(-0.02f);
        AssertFloat(mass + film.Released.Sum(d => d.Ml)).OverrideFailureMessage("running keeps every milliliter")
            .IsEqualApprox(3f, 0.0001f);
    }

    [TestCase]
    public void LiquidOnABoxRunsOverItsEdgeAndDripsFromUnderneath() {
        const float Half = 0.1f;
        var film = new SurfaceFilm(Box(Half), new SurfaceFilm.Settings(0.008f, 0.0003f, 0.0003f), BoxNormals(Half));
        var top = film.Nearest(new Vector3(Half * 0.8f, Half, 0f));
        var dripped = new List<SurfaceFilm.Drip>();
        for (var step = 0; step < 300; step++) {
            if (step < 60) { film.Add(top, 0.5f); }
            film.StepNow(Vector3.Down, 1f / SurfaceFilm.Rate);
            dripped.AddRange(film.Released);
        }
        AssertBool(dripped.Count > 0).OverrideFailureMessage("liquid poured onto the top runs over the edge and drips").IsTrue();
        AssertFloat(dripped.Min(d => d.At.Y)).OverrideFailureMessage("it drips from the underside, not from the top")
            .IsLess(-Half + 0.005f);
        var side = Enumerable.Range(0, film.Count)
            .Where(k => Mathf.Abs(film.NormalAt(k).Y) < 0.2f && film.At(k).X > Half * 0.99f).Max(film.CoverageAt);
        AssertFloat(side).OverrideFailureMessage("it wets the side it ran down on the way").IsGreater(0.05f);
        AssertFloat(film.HeldMl + dripped.Sum(d => d.Ml)).OverrideFailureMessage("what stays and what drips add up to what was poured")
            .IsEqualApprox(30f, 0.0001f);
    }

    [TestCase]
    public void TheWorkerStepsTheSameAsTheMainThread() {
        var worker = new SurfaceFilm(Plane(0.4f), Floor, Up(6));
        var inline = new SurfaceFilm(Plane(0.4f), Floor, Up(6));
        var node = worker.Nearest(new Vector3(0.05f, 0f, -0.03f));
        worker.Add(node, 2f);
        inline.Add(node, 2f);
        var steps = 0;
        for (var frame = 0; frame < 60; frame++) {
            if (worker.Tick(1f / 60f, Vector3.Down)) { steps++; }
        }
        worker.Finish();
        for (var step = 0; step < steps; step++) { inline.StepNow(Vector3.Down, 1f / SurfaceFilm.Rate); }
        var differs = Enumerable.Range(0, worker.Count).Count(k => worker.MlAt(k) != inline.MlAt(k));
        AssertInt(steps).OverrideFailureMessage("a second at 60 fps runs the film's fixed steps").IsEqual((int)SurfaceFilm.Rate);
        AssertInt(differs).OverrideFailureMessage("stepping on the worker gives exactly the main thread's result").IsEqual(0);
        AssertInt(worker.Mesh.GetSurfaceCount()).OverrideFailureMessage("the finished step's film is drawn").IsEqual(1);
    }

    private static void Run(SurfaceFilm film, Vector3 gravity, float seconds) {
        for (var step = 0; step < seconds * SurfaceFilm.Rate; step++) { film.StepNow(gravity, 1f / SurfaceFilm.Rate); }
    }

    /// <summary>A square of <paramref name="size"/> meters in the XZ plane, facing up.</summary>
    private static Vector3[] Plane(float size) {
        var h = size * 0.5f;
        Vector3 a = new(-h, 0f, -h), b = new(h, 0f, -h), c = new(h, 0f, h), d = new(-h, 0f, h);
        return [a, b, c, a, c, d];
    }

    private static Vector3[] Up(int corners) => Enumerable.Repeat(Vector3.Up, corners).ToArray();

    /// <summary>A cube of half size <paramref name="half"/> around the origin.</summary>
    private static Vector3[] Box(float half) {
        var faces = new List<Vector3>();
        foreach (var normal in new[] { Vector3.Up, Vector3.Down, Vector3.Left, Vector3.Right, Vector3.Forward, Vector3.Back }) {
            var u = Mathf.Abs(normal.Y) > 0.5f ? Vector3.Right : Vector3.Up;
            var v = normal.Cross(u);
            Vector3 Corner(float x, float y) => (normal + u * x + v * y) * half;
            faces.AddRange([Corner(-1, -1), Corner(1, -1), Corner(1, 1), Corner(-1, -1), Corner(1, 1), Corner(-1, 1)]);
        }
        return [.. faces];
    }

    private static Vector3[] BoxNormals(float half) => Box(half).Select((p, i) => {
        var start = i / 3 * 3;
        var box = Box(half);
        return ((box[start] + box[start + 1] + box[start + 2]) / 3f).Normalized();
    }).ToArray();
}
