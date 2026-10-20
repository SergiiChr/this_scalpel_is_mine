namespace Scalpel.Tests.Patients;

[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("liquids"), TestCategory("surface_film")]
public class SurfaceBloodTest {
    [TestCase]
    public void SurfaceFilmSpreadsInTwoDimensionsAndKeepsItsVolume() {
        var film = new SurfaceBlood(new Vector2(0.3f, 0.3f), _ => 0f);
        film.Add(new Vector2(0.5f, 0.5f), 2f);
        Advance(film, 2f);
        var wet = Wet(film);
        AssertInt(wet.Max(p => p.X) - wet.Min(p => p.X))
            .OverrideFailureMessage("a flat puddle spreads across the skin, not along a line").IsGreater(6);
        AssertInt(wet.Max(p => p.Y) - wet.Min(p => p.Y))
            .OverrideFailureMessage("the puddle also spreads in the other surface direction").IsGreater(6);
        AssertFloat(film.HeldMl + film.RunoffMl).OverrideFailureMessage("spreading conserves the supplied blood")
            .IsEqualApprox(2f, 0.0001f);
    }

    [TestCase]
    public void SurfaceFilmJoinsNearbyBleedsAndFlowsDownhill() {
        var film = new SurfaceBlood(new Vector2(0.3f, 0.3f), uv => -uv.X * 0.05f);
        film.Add(new Vector2(0.35f, 0.45f), 2f);
        film.Add(new Vector2(0.35f, 0.55f), 2f);
        Advance(film, 2f);
        var wet = Wet(film);
        var moment = wet.Sum(p => (p.X + 0.5f) / SurfaceBlood.Size * film.At(p.X, p.Y));
        var mass = wet.Sum(p => film.At(p.X, p.Y));
        var middle = Mathf.RoundToInt(moment / mass * SurfaceBlood.Size - 0.5f);
        AssertFloat(moment / mass - 0.35f)
            .OverrideFailureMessage("gravity carries the connected film downhill by more than one surface cell")
            .IsGreater(1f / SurfaceBlood.Size);
        AssertFloat(film.At(middle, SurfaceBlood.Size / 2))
            .OverrideFailureMessage("blood spreads into the gap between nearby supplies, joining the streams").IsGreater(0.001f);
        AssertFloat(film.HeldMl + film.RunoffMl).OverrideFailureMessage("joining and transport conserve volume")
            .IsEqualApprox(4f, 0.0001f);
    }

    [TestCase(0f, 0f)]
    [TestCase(1f, 1f)]
    public void SurfaceFilmAccountsForBloodLeavingACorner(float x, float y) {
        var film = new SurfaceBlood(new Vector2(0.3f, 0.3f), _ => 0f);
        film.Add(new Vector2(x, y), 2f);
        Advance(film, 1f);
        AssertFloat(film.RunoffMl).OverrideFailureMessage("the edge releases runoff").IsGreater(0f);
        AssertFloat(film.HeldMl + film.RunoffMl).OverrideFailureMessage("every drop is either on the skin or in runoff")
            .IsEqualApprox(2f, 0.0001f);
    }

    [TestCase]
    public void SurfaceFilmDoesNotRestoreBloodAfterWiping() {
        var film = new SurfaceBlood(new Vector2(0.3f, 0.3f), _ => 0f);
        var map = new WoundMap(WoundMap.MinSize);
        film.Add(new Vector2(0.5f, 0.5f), 1f);
        Advance(film, 1f);
        film.Paint(map);
        map.Disk(WoundMap.Layer.Fluids, WoundMap.Blood, new Vector2(0.5f, 0.5f), 1f, 1f, WoundMap.Mode.Sub, soft: false);
        film.ReconcileWipes(map);
        AssertFloat(film.RemovedMl).OverrideFailureMessage("wiping removes the liquid behind the visible blood").IsGreater(0.95f);
        Advance(film, 1f);
        film.Paint(map);
        AssertFloat(map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, new Vector2(0.5f, 0.5f)))
            .OverrideFailureMessage("the wiped center stays clean when the liquid updates").IsLess(0.05f);
        AssertFloat(film.HeldMl + film.RemovedMl + film.RunoffMl)
            .OverrideFailureMessage("removed blood is accounted for separately from runoff").IsEqualApprox(1f, 0.0001f);
    }

    [TestCase]
    public void SurfaceFilmRespondsToGradualSwabbing() {
        var film = new SurfaceBlood(new Vector2(0.3f, 0.3f), _ => 0f);
        var map = new WoundMap(WoundMap.MinSize);
        film.Add(new Vector2(0.5f, 0.5f), 1f);
        film.Paint(map);
        for (var wipe = 0; wipe < 15; wipe++) {
            map.Disk(WoundMap.Layer.Fluids, WoundMap.Blood, new Vector2(0.5f, 0.5f), 1f, 0.08f, WoundMap.Mode.Sub, soft: false);
            film.ReconcileWipes(map);
            film.Paint(map);
        }
        AssertFloat(film.RemovedMl).OverrideFailureMessage("ordinary small swab batches remove the dense film's volume").IsGreater(0.95f);
        AssertFloat(map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, new Vector2(0.5f, 0.5f)))
            .OverrideFailureMessage("repeated swabbing cleans the visible film too").IsLess(0.05f);
    }

    [TestCase]
    public void SurfaceFilmConsumesEachPartialWipeOnce() {
        var film = new SurfaceBlood(new Vector2(0.3f, 0.3f), _ => 0f);
        var map = new WoundMap();
        film.Add(new Vector2(0.5f, 0.5f), 1f);
        film.Paint(map);
        map.Disk(WoundMap.Layer.Fluids, WoundMap.Blood, new Vector2(0.5f, 0.5f), 1f, 0.2f, WoundMap.Mode.Sub, soft: false);
        film.ReconcileWipes(map);
        var removed = film.RemovedMl;
        film.ReconcileWipes(map);
        AssertFloat(film.RemovedMl).OverrideFailureMessage("transport and paint consume the same partial wipe only once")
            .IsEqual(removed);
    }

    [TestCase]
    public void SurfaceFilmPreservesWipesSmallerThanASolverCell() {
        var map = new WoundMap();
        var cover = Enumerable.Repeat(0.9f, SurfaceBlood.Size * SurfaceBlood.Size).ToArray();
        var at = new Vector2(0.5f, 0.5f);
        map.PaintBloodFilm(cover, SurfaceBlood.Size);
        map.Disk(WoundMap.Layer.Fluids, WoundMap.Blood, at, 0.5f / map.Size, 1f, WoundMap.Mode.Sub, soft: false);
        for (var frame = 0; frame < 8; frame++) {
            map.PaintBloodFilm(cover, SurfaceBlood.Size);
        }
        AssertFloat(map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, at))
            .OverrideFailureMessage("a fine wipe stays clear across later film uploads").IsEqual(0f);
        Array.Fill(cover, 1f);
        map.PaintBloodFilm(cover, SurfaceBlood.Size);
        AssertFloat(map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, at))
            .OverrideFailureMessage("newly arriving liquid can wet the cleared area again").IsBetween(0.08f, 0.15f);
    }

    [TestCase]
    public void DryFilmSleepsAndOppositeSidesKeepTheirOwnRunoff() {
        var film = new SurfaceBlood(new Vector2(0.3f, 0.3f), _ => 0f);
        AssertBool(film.HasFilm).IsFalse();
        film.Add(new Vector2(0.5f, 0.5f), 0f);
        Advance(film, 1f);
        AssertBool(film.HasFilm).OverrideFailureMessage("zero-volume deposits do not wake a dry film").IsFalse();
        film.Add(new Vector2(0f, 0.5f), 2f);
        film.Add(new Vector2(1f, 0.5f), 2f);
        AssertBool(film.HasFilm).IsTrue();
        Advance(film, 1f);
        var positions = new List<Vector2>();
        var drained = 0f;
        while (film.TakeRunoff(out var uv, out var ml)) { positions.Add(uv); drained += ml; }
        AssertBool(positions.Any(p => p.X < 0.25f) && positions.Any(p => p.X > 0.75f))
            .OverrideFailureMessage("simultaneous opposite-side runoff produces drops on both sides").IsTrue();
        AssertInt(positions.Count).OverrideFailureMessage("all runoff transfers in at most one operation per local outlet").IsLessEqual(32);
        AssertFloat(drained + film.QueuedRunoffMl).IsEqualApprox(film.RunoffMl, 0.0001f);
        AssertFloat(film.HeldMl + film.RunoffMl).IsEqualApprox(4f, 0.0001f);
        var map = new WoundMap(WoundMap.MinSize);
        film.Paint(map);
        map.Disk(WoundMap.Layer.Fluids, WoundMap.Blood, new Vector2(0.5f, 0.5f), 1f, 1f, WoundMap.Mode.Sub, soft: false);
        film.ReconcileWipes(map);
        film.Step(Basis.Identity, 1f / 60f);
        film.Paint(map);
        AssertBool(film.NeedsTransport(Basis.Identity))
            .OverrideFailureMessage("a complete wipe leaves only pinned residue below the texture precision, without ongoing transport").IsFalse();
        AssertFloat(map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, new Vector2(0f, 0.5f))).IsEqual(0f);
    }

    [TestCase]
    public void SettledFilmSleepsUntilTheSurfaceOrSupplyChanges() {
        var film = new SurfaceBlood(new Vector2(0.3f, 0.3f), _ => 0f);
        film.Add(new Vector2(0.5f, 0.5f), 0.0001f);
        film.Step(Basis.Identity, 1f / 60f);
        var map = new WoundMap(WoundMap.MinSize);
        film.Paint(map);
        AssertBool(film.HasFilm).IsTrue();
        AssertBool(film.NeedsTransport(Basis.Identity)).OverrideFailureMessage("pinned residue stays visible without continuously running transport").IsFalse();
        AssertBool(film.NeedsPaint).IsFalse();
        var tilted = new Basis(Vector3.Forward, 0.2f);
        AssertBool(film.NeedsTransport(tilted)).OverrideFailureMessage("a changed body slope wakes transport").IsTrue();
        film.Step(tilted, 1f / 60f);
        AssertBool(film.NeedsTransport(tilted)).IsFalse();
        film.Add(new Vector2(0.5f, 0.5f), 1f);
        AssertBool(film.NeedsTransport(tilted)).OverrideFailureMessage("new supply wakes a settled surface").IsTrue();
    }

    [TestCase]
    public void SeparateBleedsOnOneEdgeKeepSeparateReleasePositions() {
        var film = new SurfaceBlood(new Vector2(0.3f, 0.3f), _ => 0f);
        film.Add(new Vector2(0f, 0.15f), 2f);
        film.Add(new Vector2(0f, 0.85f), 2f);
        Advance(film, 1f);
        var positions = new List<Vector2>();
        var drained = 0f;
        while (film.TakeRunoff(out var uv, out var ml)) { positions.Add(uv); drained += ml; }
        AssertBool(positions.Any(p => p.Y < 0.3f) && positions.Any(p => p.Y > 0.7f)).IsTrue();
        AssertBool(positions.All(p => p.Y < 0.3f || p.Y > 0.7f))
            .OverrideFailureMessage("separate outlets do not combine their release point into an unwetted midpoint").IsTrue();
        AssertFloat(drained + film.QueuedRunoffMl).IsEqualApprox(film.RunoffMl, 0.0001f);
    }

    [TestCase]
    public void SupportedFilmHasAContinuousSlopingEdgeWithoutChangingVolume() {
        static float Margin(Vector2 uv) => (uv.Y - 0.25f - uv.X * 0.35f) * 0.05f;
        var film = new SurfaceBlood(new Vector2(0.3f, 0.3f), _ => 0f, uv => Margin(uv) >= 0f);
        var map = new WoundMap(WoundMap.MinSize);
        map.SetBloodFilmSupport(Margin);
        var supplied = 0f;
        for (var y = 0; y < SurfaceBlood.Size; y++) {
            for (var x = 0; x < SurfaceBlood.Size; x++) {
                var uv = new Vector2(x + 0.5f, y + 0.5f) / SurfaceBlood.Size;
                if (Margin(uv) < 0f) { continue; }
                film.Add(uv, 1f);
                supplied++;
            }
        }
        film.Step(Basis.Identity, 1f / 60f);
        film.Paint(map);
        var pixels = map.Snapshot(WoundMap.Layer.Fluids);
        for (var x = 4; x < map.Size - 4; x++) {
            var edge = Enumerable.Range(0, map.Size).First(y => pixels[(y * map.Size + x) * 4 + WoundMap.Blood] >= 128);
            var expected = 0.25f + (x + 0.5f) / map.Size * 0.35f;
            AssertFloat(Mathf.Abs((edge + 0.5f) / map.Size - expected))
                .OverrideFailureMessage("a sloping support boundary stays within one texture pixel, rather than stepping by solver cells")
                .IsLess(1.1f / map.Size);
        }
        AssertFloat(film.HeldMl + film.RunoffMl).IsEqualApprox(supplied, 0.01f);
    }

    [TestCase]
    public void FilmReconstructionClearsOldCoverageWhenResolutionChanges() {
        var map = new WoundMap(WoundMap.MinSize);
        var film = new float[SurfaceBlood.Size * SurfaceBlood.Size];
        film[31 * SurfaceBlood.Size + 62] = 1f;
        map.PaintBloodFilm(film, SurfaceBlood.Size);
        var before = map.Snapshot(WoundMap.Layer.Fluids);
        var peak = Enumerable.Range(0, map.Size * map.Size).MaxBy(i => before[i * 4 + WoundMap.Blood]);
        AssertInt(before[peak * 4 + WoundMap.Blood]).IsGreater(127);
        map.PaintBloodFilm(new float[32 * 32], 32);
        AssertFloat(map.Snapshot(WoundMap.Layer.Fluids)[peak * 4 + WoundMap.Blood] / 255f)
            .OverrideFailureMessage("changing reconstruction resolution clears old liquid, leaving only its thin stain").IsLess(0.12f);
    }

    [TestCase]
    public void SameDoseHasLowerDepthOnALargerBodySurface() {
        float Depth(float scale) {
            var film = new SurfaceBlood(new Vector2(0.3f, 0.3f), _ => 0f, scale: scale);
            var map = new WoundMap(WoundMap.MinSize);
            film.Add(new Vector2(0.5f, 0.5f), 0.005f);
            film.Paint(map);
            var cover = map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, new Vector2(0.5f, 0.5f));
            return -Mathf.Log(1f - cover) * 0.0004f;
        }
        AssertFloat(Depth(1f) / Depth(2f)).OverrideFailureMessage("doubling body scale gives four times the area and one quarter of the depth for the same milliliters")
            .IsBetween(3.5f, 4.5f);
    }

    [TestCase(1f)]
    [TestCase(2f)]
    public void PaintedSurfaceFilmIntegratesToItsDose(float scale) {
        const float DoseMl = 0.02f;
        const float CoverDepth = 0.0004f;
        var siteSize = new Vector2(0.3f, 0.3f);
        var film = new SurfaceBlood(siteSize, _ => 0f, scale: scale);
        var map = new WoundMap(WoundMap.MinSize);
        film.Add(new Vector2(0.5f, 0.5f), DoseMl);
        film.Paint(map);

        // SurfaceBlood encodes depth as 1 - exp(-depth / CoverDepth). Undo that law after
        // resampling and 8-bit storage, then integrate over the physical site area.
        var pixels = map.Snapshot(WoundMap.Layer.Fluids);
        var volumeM3 = 0d;
        for (var pixel = 0; pixel < map.Size * map.Size; pixel++) {
            var cover = pixels[pixel * 4 + WoundMap.Blood] / 255f;
            if (cover <= 0f) { continue; }
            var depth = -Mathf.Log(1f - cover) * CoverDepth;
            volumeM3 += depth * siteSize.X * siteSize.Y * scale * scale / (map.Size * map.Size);
        }
        var visibleDoseMl = (float)(volumeM3 * 1_000_000d);
        AssertFloat(visibleDoseMl)
            .OverrideFailureMessage($"the painted low-dose surface film integrates to its supplied {DoseMl} ml after 8-bit resampling")
            .IsBetween(DoseMl * 0.9f, DoseMl * 1.1f);
    }

    private static void Advance(SurfaceBlood film, float seconds) {
        for (var i = 0; i < seconds * 60f; i++) {
            film.Step(Basis.Identity, 1f / 60f);
        }
    }

    private static List<Vector2I> Wet(SurfaceBlood film) {
        var result = new List<Vector2I>();
        for (var y = 0; y < SurfaceBlood.Size; y++) {
            for (var x = 0; x < SurfaceBlood.Size; x++) {
                if (film.At(x, y) > 0.0001f) { result.Add(new Vector2I(x, y)); }
            }
        }
        return result;
    }
}
