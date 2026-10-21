namespace Scalpel.Tests.Patients;

[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("liquids")]
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
        AssertFloat(film.RemovedMl).OverrideFailureMessage(
            $"ordinary small swab batches remove the dense film's volume (removed {film.RemovedMl:0.000}, held {film.HeldMl:0.000})")
            .IsGreater(0.95f);
        var visible = map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, new Vector2(0.5f, 0.5f));
        AssertFloat(visible).OverrideFailureMessage(
            $"repeated swabbing cleans the visible film too (visible {visible:0.000}, removed {film.RemovedMl:0.000}, held {film.HeldMl:0.000})")
            .IsLess(0.05f);
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
    public void UniformWipeHalvesRealFilmVolumeAndItsOpticalDepthOnce() {
        var film = new SurfaceBlood(new Vector2(0.3f, 0.3f), _ => 0f);
        var map = new WoundMap(WoundMap.MinSize);
        for (var y = 0; y < SurfaceBlood.Size; y++) {
            for (var x = 0; x < SurfaceBlood.Size; x++) {
                film.Add(new Vector2(x + 0.5f, y + 0.5f) / SurfaceBlood.Size, 0.001f);
            }
        }
        film.Paint(map);
        var at = new Vector2(0.5f, 0.5f);
        var before = map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, at);
        var volumeBefore = film.HeldMl;
        var halfDepthCoverage = 1f - Mathf.Sqrt(1f - before);
        map.Disk(WoundMap.Layer.Fluids, WoundMap.Blood, at, 1f, before - halfDepthCoverage,
            WoundMap.Mode.Sub, soft: false);
        film.ReconcileWipes(map);
        AssertFloat(film.HeldMl).OverrideFailureMessage(
            $"a uniform wipe removes half of the real surface-film volume (before {volumeBefore:0.000}, after {film.HeldMl:0.000})")
            .IsEqualApprox(volumeBefore * 0.5f, 0.01f);
        film.Paint(map);
        var after = map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, at);
        AssertFloat(after).OverrideFailureMessage(
            $"half the film volume produces its exponential half-depth coverage once (before {before:0.000}, after {after:0.000}, expected {halfDepthCoverage:0.000})")
            .IsEqualApprox(1f - Mathf.Sqrt(1f - before), 2f / 255f);
    }

    [TestCase]
    public void PartialCoarseWipeKeepsUntouchedTexelsBrightAndFineHoleClear() {
        var film = new SurfaceBlood(new Vector2(0.3f, 0.3f), _ => 0f);
        var map = new WoundMap(WoundMap.MinSize);
        for (var y = 0; y < SurfaceBlood.Size; y++) {
            for (var x = 0; x < SurfaceBlood.Size; x++) {
                film.Add(new Vector2(x + 0.5f, y + 0.5f) / SurfaceBlood.Size, 0.01f);
            }
        }
        film.Paint(map);
        var pixels = map.Snapshot(WoundMap.Layer.Fluids);
        var coarsePixel = (32 * 2) * map.Size + 32 * 2;
        var before = pixels[coarsePixel * 4 + WoundMap.Blood] / 255f;
        var coarseWipe = new Vector2(64.5f, 64.5f) / map.Size;
        map.Disk(WoundMap.Layer.Fluids, WoundMap.Blood, coarseWipe, 0.25f / map.Size, before * 0.5f,
            WoundMap.Mode.Sub, soft: false);
        var fineWipe = new Vector2(65.5f, 64.5f) / map.Size;
        map.Disk(WoundMap.Layer.Fluids, WoundMap.Blood, fineWipe, 0.25f / map.Size, 1f,
            WoundMap.Mode.Sub, soft: false);
        film.ReconcileWipes(map);
        film.Paint(map);
        pixels = map.Snapshot(WoundMap.Layer.Fluids);
        var hole = pixels[(64 * map.Size + 65) * 4 + WoundMap.Blood] / 255f;
        var untouched = pixels[(64 * map.Size + 66) * 4 + WoundMap.Blood] / 255f;
        AssertFloat(hole).OverrideFailureMessage("the fine wiped texel stays clear inside the coarsely reduced cell")
            .IsEqual(0f);
        AssertFloat(untouched).OverrideFailureMessage(
            $"untouched texels retain their prior depth while the fine hole stays clear (before {before:0.000}, after {untouched:0.000})")
            .IsEqualApprox(before, 0.03f);
    }

    [TestCase]
    public void ReconciledFilmReductionIsNotAppliedAgainToTheVisibleBlood() {
        var map = new WoundMap();
        var cover = Enumerable.Repeat(0.8f, SurfaceBlood.Size * SurfaceBlood.Size).ToArray();
        var at = new Vector2(0.5f, 0.5f);
        map.PaintBloodFilm(cover, SurfaceBlood.Size);
        var before = map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, at);
        var afterHalfDepth = 1f - Mathf.Sqrt(1f - before);
        map.Disk(WoundMap.Layer.Fluids, WoundMap.Blood, at, 1f, before - afterHalfDepth, WoundMap.Mode.Sub, soft: false);
        var wiped = map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, at);
        map.AcknowledgeBloodFilmReduction();
        Array.Fill(cover, afterHalfDepth);
        map.PaintBloodFilm(cover, SurfaceBlood.Size);
        var repainted = map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, at);
        AssertFloat(wiped).OverrideFailureMessage(
            $"the wipe removes half the displayed optical depth (before {before:0.000}, wiped {wiped:0.000}, expected {afterHalfDepth:0.000})")
            .IsEqualApprox(afterHalfDepth, 1f / 255f);
        AssertFloat(repainted).OverrideFailureMessage(
            $"reconciled liquid reduction is reflected once in the display (wiped {wiped:0.000}, repainted {repainted:0.000})")
            .IsEqualApprox(wiped, 1f / 255f);
    }

    [TestCase]
    public void AcknowledgedFilmRepaintRespectsTheBloodSupportMask() {
        var map = new WoundMap(WoundMap.MinSize);
        map.SetBloodFilmSupport(uv => uv.X < 0.4f ? -WoundMap.Texel : uv.X < 0.7f ? 0f : WoundMap.Texel);
        var depth = Enumerable.Repeat(2f, SurfaceBlood.Size * SurfaceBlood.Size).ToArray();
        map.PaintBloodFilmDepth(depth, SurfaceBlood.Size);
        byte At(int x, int y) => map.Snapshot(WoundMap.Layer.Fluids)[(y * map.Size + x) * 4 + WoundMap.Blood];
        var unsupportedBefore = At(32, 64);
        var untouchedPartialBefore = At(64, 64);
        var untouchedNeighborBefore = At(72, 64);
        var wipedUv = new Vector2(74.5f, 64.5f) / map.Size;
        var wipedBefore = At(74, 64);
        map.Disk(WoundMap.Layer.Fluids, WoundMap.Blood, wipedUv, 0.25f / map.Size,
            wipedBefore / 510f, WoundMap.Mode.Sub, soft: false);
        var wipedAfter = At(74, 64);

        var reducedCells = new bool[SurfaceBlood.Size * SurfaceBlood.Size];
        for (var y = 31; y <= 32; y++) {
            for (var x = 36; x <= 37; x++) { reducedCells[y * SurfaceBlood.Size + x] = true; }
        }
        map.AcknowledgeBloodFilmReduction(reducedCells, SurfaceBlood.Size);
        for (var cell = 0; cell < depth.Length; cell++) { if (reducedCells[cell]) { depth[cell] = 1f; } }
        map.PaintBloodFilmDepth(depth, SurfaceBlood.Size);

        AssertInt(unsupportedBefore).IsEqual(0);
        AssertInt(At(32, 64)).OverrideFailureMessage("acknowledged depth scaling does not paint outside the support mask")
            .IsEqual(0);
        AssertInt(At(64, 64)).OverrideFailureMessage("an untouched partially supported pixel keeps its previous coverage")
            .IsEqual(untouchedPartialBefore);
        AssertInt(At(72, 64)).OverrideFailureMessage("an untouched partially supported neighbor survives local reconciliation")
            .IsEqual(untouchedNeighborBefore);
        AssertInt(At(74, 64)).OverrideFailureMessage("the wiped partially supported pixel stays at its cleared coverage")
            .IsEqual(wipedAfter);

        Array.Fill(depth, 0.5f);
        map.PaintBloodFilmDepth(depth, SurfaceBlood.Size);
        AssertInt(At(32, 64)).OverrideFailureMessage("later source changes still leave unsupported pixels physically masked")
            .IsEqual(0);
        AssertInt(At(64, 64)).OverrideFailureMessage("later source changes never exceed the partial support mask")
            .IsLessEqual(128);
        AssertInt(At(74, 64)).OverrideFailureMessage("later source changes never exceed support at a wiped partial pixel")
            .IsLessEqual(128);
    }

    [TestCase]
    public void LocalWipeAcknowledgmentDoesNotSuppressDistantNewFilm() {
        var film = new SurfaceBlood(new Vector2(0.3f, 0.3f), _ => 0f);
        var map = new WoundMap(WoundMap.MinSize);
        for (var y = 0; y < SurfaceBlood.Size; y++) {
            for (var x = 0; x < SurfaceBlood.Size; x++) {
                film.Add(new Vector2(x + 0.5f, y + 0.5f) / SurfaceBlood.Size, 0.001f);
            }
        }
        film.Paint(map);
        var distant = new Vector2(0.8f, 0.8f);
        var distantBefore = map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, distant);
        var volumeBefore = film.HeldMl;
        map.Disk(WoundMap.Layer.Fluids, WoundMap.Blood, new Vector2(0.25f, 0.25f), 0.1f, 1f,
            WoundMap.Mode.Sub, soft: false);
        film.ReconcileWipes(map);
        var removed = film.RemovedMl;
        film.Add(distant, 0.02f);
        film.Paint(map);
        var distantAfter = map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, distant);

        AssertFloat(removed).OverrideFailureMessage("the local swab removes its real fluid volume")
            .IsGreater(0f);
        AssertFloat(distantAfter).OverrideFailureMessage(
            $"new fluid outside the wiped cells remains visible (before {distantBefore:0.000}, after {distantAfter:0.000})")
            .IsGreater(distantBefore + 0.1f);
        AssertFloat(film.HeldMl + removed).OverrideFailureMessage(
            $"distant supply is retained while removed volume is accounted (before {volumeBefore:0.000}, held {film.HeldMl:0.000}, removed {removed:0.000})")
            .IsEqualApprox(volumeBefore + 0.02f, 0.001f);
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
            return -Mathf.Log(1f - cover) * SurfaceBlood.CoverDepth;
        }
        AssertFloat(Depth(1f) / Depth(2f)).OverrideFailureMessage("doubling body scale gives four times the area and one quarter of the depth for the same milliliters")
            .IsBetween(3.5f, 4.5f);
    }

    [TestCase(1f, 0f)]
    [TestCase(2f, 0f)]
    [TestCase(1f, 1.7320508f)]
    [TestCase(2f, 1.7320508f)]
    public void PaintedSurfaceFilmIntegratesToItsDose(float scale, float slope) {
        const float DoseMl = 0.02f;
        var siteSize = new Vector2(0.3f, 0.3f);
        var film = new SurfaceBlood(siteSize, uv => uv.X * siteSize.X * slope, scale: scale);
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
            var depth = -Mathf.Log(1f - cover) * SurfaceBlood.CoverDepth;
            volumeM3 += depth * siteSize.X * siteSize.Y * scale * scale * Mathf.Sqrt(1f + slope * slope) / (map.Size * map.Size);
        }
        var visibleDoseMl = (float)(volumeM3 * 1_000_000d);
        AssertFloat(visibleDoseMl)
            .OverrideFailureMessage($"the painted low-dose surface film integrates to its supplied {DoseMl} ml after 8-bit resampling")
            .IsBetween(DoseMl * 0.9f, DoseMl * 1.1f);
    }

    [TestCase]
    public void ClosingASeamDisplacesItsFilmAndAllowsFreshBloodAfterwards() {
        var film = new SurfaceBlood(new Vector2(0.3f, 0.3f), _ => 0f);
        var map = new WoundMap(WoundMap.MinSize);
        var center = new Vector2(0.5f, 0.5f);
        Vector2[] path = [new(0.3f, 0.5f), new(0.7f, 0.5f)];
        const float Dose = 0.02f;
        film.Add(center, Dose);
        film.Paint(map);
        AssertFloat(film.DisplaceStroke(path, 0.026f)).IsEqualApprox(Dose, 0.000001f);
        map.Stroke(WoundMap.Layer.Fluids, WoundMap.Blood, path[0], path[1], 0.026f, 1f, WoundMap.Mode.Sub);
        film.ReconcileWipes(map);
        film.Paint(map);
        AssertFloat(film.RemovedMl).OverrideFailureMessage("closure displaces blood instead of deleting its volume").IsEqual(0f);
        AssertFloat(film.HeldMl + film.RunoffMl).IsEqualApprox(Dose, 0.000001f);
        Advance(film, 1f);
        film.Paint(map);
        AssertFloat(map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, center))
            .OverrideFailureMessage("the displaced film leaves the closure band dry after it settles").IsLess(0.05f);
        AssertFloat(map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, new Vector2(0.5f, 0.55725f)))
            .OverrideFailureMessage("the displaced blood is visible beside the seam").IsGreater(0.1f);
        film.Add(center, Dose);
        film.Paint(map);
        AssertFloat(map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, center))
            .OverrideFailureMessage("a later bleed can wet the closed seam; displacement is not a permanent paint mask").IsGreater(0.3f);
        AssertFloat(film.HeldMl + film.RunoffMl).IsEqualApprox(Dose * 2f, 0.000001f);
    }

    [TestCase]
    public void ChangedSkinGeometryRedirectsTheFilmWithoutAddingLiquid() {
        var rise = 0f;
        var samples = 0;
        var film = new SurfaceBlood(new Vector2(0.3f, 0.3f), uv => { samples++; return rise * (uv.X - 0.5f); });
        const float Dose = 2f;
        film.Add(new Vector2(0.5f, 0.5f), Dose);
        film.Paint(new WoundMap(WoundMap.MinSize));
        rise = 0.06f;
        samples = 0;
        AssertBool(film.BeginGeometryRefit()).IsTrue();
        film.ProcessGeometryRefit(1);
        AssertInt(samples).OverrideFailureMessage("a geometry refresh obeys its per-frame sampling budget").IsEqual(1);
        AssertBool(film.NeedsPaint).OverrideFailureMessage("a partial sample batch does not publish mixed geometry").IsFalse();
        for (var batch = 0; batch < 128; batch++) { film.ProcessGeometryRefit(32); }
        AssertInt(samples).OverrideFailureMessage("only the wet patch and its slope margin are sampled").IsLess(SurfaceBlood.Size * SurfaceBlood.Size);
        AssertFloat(film.HeldMl).OverrideFailureMessage("changing the support geometry preserves the supplied liquid")
            .IsEqualApprox(Dose, 0.000001f);
        for (var frame = 0; frame < 120; frame++) {
            if (frame % 6 == 0) { film.BeginGeometryRefit(); }
            film.ProcessGeometryRefit(32);
            film.Step(Basis.Identity, 1f / 60f);
        }
        var meanX = 0f;
        for (var y = 0; y < SurfaceBlood.Size; y++) {
            for (var x = 0; x < SurfaceBlood.Size; x++) { meanX += film.At(x, y) * (x + 0.5f) / SurfaceBlood.Size; }
        }
        AssertFloat(meanX / film.HeldMl).OverrideFailureMessage($"the existing film flows downhill on the changed skin instead of its old flat support (mean {meanX / film.HeldMl})")
            .IsLess(0.49f);
        AssertFloat(film.HeldMl + film.RemovedMl + film.RunoffMl).IsEqualApprox(Dose, 0.000001f);
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
