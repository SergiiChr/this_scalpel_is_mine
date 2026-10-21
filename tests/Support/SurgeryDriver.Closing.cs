namespace Scalpel.Tests.Support;

/// <summary>Closing wounds: running sutures, staples, tape and internal stitches.</summary>
public partial class SurgeryDriver {
    private static readonly string[] ThreadStates = ["loose", "closed", "too tight"];

    /// <summary>
    /// Sews every open skin wound shut: the muscle first where a wound goes through it, then the skin. The needle sews
    /// a running thread along each (<see cref="PlayerSews"/>), a stapler staples along it (<see cref="PlayerStaples"/>);
    /// other closures (<paramref name="toolId"/>) go segment by segment along it, paper clips pulled until they read
    /// closed, held on each until it's closed. Goes round again for anything that didn't close.
    /// </summary>
    public async Task PlayerClosesWounds(string toolId = "needle") {
        Note($"closes the wounds with {toolId}");
        var tool = (await PlayerRequestsItem(toolId))!;
        for (var round = 0; round < 4 && Patient.SkinClosure() < 0.98f; round++) {
            // Every muscle before any skin: a skin stitch near another cut can reach across it, and over its open muscle
            // it tears through.
            if (tool.Def.Action == "sew") {
                // A stab or bullet hole shows no muscle: the skin stitch across it closes that too.
                foreach (var wound in Patient.Wounds.Where(wound => wound.ThroughMuscle && wound.Muscle.Any(m => m < 1f)
                    && !(wound.IsHole && !Body.IsOpen(wound.Midpoint))).ToList()) {
                    await PlayerSews(wound, TissueDepth.Muscle);
                }
            }
            foreach (var wound in Patient.Wounds.Where(wound => wound.IsSkinCut).ToList()) {
                switch (tool.Def.Action) {
                    case "sew":
                        if (wound.Bins.Any(b => b < 1f)) {
                            await PlayerSews(wound, TissueDepth.Skin);
                        }
                        break;
                    case "staple":
                        await PlayerStaples(wound);
                        break;
                    default:
                        await PlayerTapes(wound);
                        break;
                }
            }
        }
        Note($"skin closure {Patient.SkinClosure():0.00}");
        await PlayerPutsDown();
    }

    /// <summary>A closure strip pressed bin by bin along <paramref name="wound"/>: its muscle first where the cut
    /// goes through it, then its skin. A tensioned closure (paper clips) is pulled until it reads <paramref name="pull"/>
    /// first.</summary>
    public async Task PlayerTapes(Wound wound, string pull = "closed") {
        foreach (var muscle in (bool[])[true, false]) {
            if (muscle && !wound.ThroughMuscle) {
                continue;
            }
            for (var bin = 0; bin < wound.Bins.Length; bin++) {
                var index = bin;
                bool Closed() => (muscle ? wound.Muscle : wound.Bins)[index] >= 1f;
                if (Closed()) {
                    continue;
                }
                var at = SitePoint(wound.BinPosition(bin));
                await WithinReach(at);
                await PlayerReaches(at);
                if (ToolActions.HasTensionWheel(Me.HeldTool(Me.Active)!.Def)) {
                    await PlayerPullsThread(pull);
                }
                Use();
                await Frames.Until(Closed, 3f);
                if (!Closed()) {
                    var hand = Me.Hands[Me.Active];
                    var tip = Me.HeldTool(Me.Active) is { } held ? Body.Probe(held.TipPosition()).ToString() : "no tool";
                    Note($"{(muscle ? "muscle" : "skin")} {bin} won't close: lowered {hand.Lowered}, tip {tip}");
                }
                Use(false);
                await Frames.Physics(3);
            }
        }
    }

    /// <summary>Tapes <paramref name="wound"/> with the roll in the active hand: one strip along it, pressed down
    /// <paramref name="past"/> meters before its start, every <paramref name="spacing"/> meters along it and as far past
    /// its end, where it's cut off (<see cref="PlayerLaysTape"/>).</summary>
    public async Task PlayerTapesAlong(Wound wound, float past = 0.01f, float spacing = 0.03f) {
        var length = wound.LengthUv;
        var stretches = Math.Max(Mathf.CeilToInt(Body.UvToMeters(length) / spacing), 1);
        var points = Enumerable.Range(0, stretches + 1).Select(i => BesideWound(wound, length * i / stretches, 0f)).ToList();
        var along = points[^1] - points[0];
        along = along == Vector2.Zero ? Vector2.Right : along.Normalized();
        points[0] -= along * Body.MetersToUv(past);
        points[^1] += along * Body.MetersToUv(past);
        await PlayerLaysTape(points);
        Note($"taped wound {wound.Id}, closed {wound.Closure:0.00}");
    }

    /// <summary>A strip of tape from the roll in the active hand pressed down at each of <paramref name="points"/> (site
    /// uv) and cut off at the last (<see cref="PlayerSticksTape"/>).</summary>
    public async Task PlayerLaysTape(IReadOnlyList<Vector2> points) {
        for (var i = 0; i < points.Count; i++) {
            await PlayerSticksTape(points[i], i == points.Count - 1);
        }
    }

    /// <summary>Presses the strip on the roll in the active hand down at <paramref name="uv"/> (site uv) with a click,
    /// or with a hold that cuts it off there (<paramref name="cutOff"/>).</summary>
    public async Task PlayerSticksTape(Vector2 uv, bool cutOff = false) => await PlayerSticksTapeAt(SitePoint(uv), cutOff);

    /// <summary>Presses the strip on the roll in the active hand down at <paramref name="at"/> (world, on the patient)
    /// with a click, or with a hold that cuts it off there (<paramref name="cutOff"/>).</summary>
    public async Task PlayerSticksTapeAt(Vector3 at, bool cutOff = false) {
        await WithinReach(at);
        await PlayerReaches(at);
        Use();
        await Frames.Seconds(cutOff ? ToolActions.SutureTieHold + 0.2f : 0.2f);
        Use(false);
        await Frames.Physics(3);
    }

    /// <summary>
    /// Staples <paramref name="wound"/> shut with the stapler in the active hand: centered on the cut every
    /// <paramref name="spacing"/> meters along it (the first and last a little in from its ends), legs square across it
    /// (as C/V turn a blade along it), clicked at each spot where the cut doesn't already look closed. Where the muscle
    /// under it is still open the staples go into the muscle, so it goes along again for the skin, up to <paramref name="rounds"/> times while it isn't closed. Opt-in progress captures show the first staple; callers otherwise capture their unique result.
    /// Returns how many staples went in.
    /// </summary>
    public async Task<int> PlayerStaples(Wound wound, float spacing = TissueSim.StitchReach, int rounds = 4, bool captureProgress = false) {
        var stapler = Me.HeldTool(Me.Active)!;
        var step = spacing / ((wound.Points[^1] - wound.Points[0]).Normalized() * Body.SiteSize).Length();
        var placed = 0;
        for (var round = 0; round < rounds && wound.Closure < 0.99f; round++) {
            for (var i = 0; i <= Mathf.FloorToInt(wound.LengthUv / step); i++) {
                // A few millimeters in from the ends: aimed right at its end the stapler can land just past the cut.
                var along = Mathf.Clamp(i * step, step * 0.6f, wound.LengthUv - (step * 0.6f));
                // Where the cut already looks closed there's nothing to staple.
                if (Body.Tissue.ClosedAt(BesideWound(wound, along, 0f))) {
                    continue;
                }
                var at = SitePoint(BesideWound(wound, along, 0f));
                var cut = SitePoint(BesideWound(wound, along + (step * 0.5f), 0f))
                    - SitePoint(BesideWound(wound, along - (step * 0.5f), 0f));
                await WithinReach(at);
                await PlayerTurnsBlade(cut);
                await PlayerReaches(at);
                var charges = stapler.Charges;
                Use();
                await Frames.Physics(4);
                Use(false);
                await Frames.Physics(3);
                if (stapler.Charges < charges && ++placed == 1 && captureProgress) {
                    await Capture("first_staple", "the first staple sits square across the cut, both legs in, the stapler on it");
                }
            }
        }
        Note($"{placed} staples along wound {wound.Id}, closed {wound.Closure:0.00}");
        return placed;
    }

    /// <summary>Sews <paramref name="wound"/> with a running thread through <paramref name="layer"/>, the needle in the
    /// active hand: holes along it (<see cref="PlayerThreads"/>), spread with a Gelpi where the layer hides, the
    /// wheel until the hand status reads closed (<see cref="PlayerPullsThread"/>), and the knot
    /// (<see cref="PlayerTiesOff"/>).</summary>
    public async Task PlayerSews(Wound wound, TissueDepth layer) {
        var needle = Me.Active;
        var other = needle == Right ? Left : Right;
        SurgicalTool? gelpi = null;
        // Where a layer under the skin hides under the lip, the other hand spreads the cut there with a Gelpi until it
        // shows, moving it along the cut as the needle goes. A stab or bullet hole has nothing to spread, and a hand
        // already holding something (the lip, lifted with forceps) is busy.
        async Task Spread(float along) {
            if (gelpi is not null) {
                await PlayerTakesOutGelpi(gelpi, other, putDown: false);
            }
            await SwitchTo(other);
            var direction = (wound.Points[^1] - wound.Points[0]).Normalized() * Body.MetersToUv(0.01f);
            var at = BesideWound(wound, along, 0f);
            gelpi = await PlayerSetsGelpi(at - direction, at + direction);
            await PlayerOpensGelpi(SpreadToSew);
            Tap(InputActions.Grab);
            await Frames.Seconds(1f);
            await SwitchTo(needle);
        }
        var threaded = await PlayerThreads(wound, layer, expose: layer == TissueDepth.Skin || wound.IsHole || Me.HeldTool(other) is not null ? null : Spread);
        if (gelpi is not null) {
            await PlayerTakesOutGelpi(gelpi, other);
            await SwitchTo(needle);
        }
        if (threaded) {
            await PlayerPullsThread("closed");
            await PlayerTiesOff();
        } else if (Me.HeldTool(Me.Active)!.Suture.Thread != 0) {
            // A thread that can't go on is tied off as it is: the next one may be in another layer.
            await PlayerTiesOff();
        }
        var bins = layer == TissueDepth.Muscle ? wound.Muscle : wound.Bins;
        Note($"{layer} of wound {wound.Id} closed {bins.DefaultIfEmpty(0f).Min():0.00}");
    }

    /// <summary>How far (meters) a Gelpi spreads a cut to lay its muscle bare to sew.</summary>
    private const float SpreadToSew = 0.025f;

    /// <summary>
    /// Clicks the needle's thread through <paramref name="wound"/> in <paramref name="layer"/>, each hole where that layer
    /// shows (<see cref="ShowsBeside"/>): the first as close beside the wound as it does, then one
    /// <paramref name="stepCells"/> grid cells further along on the other side each click, the last at the wound's end.
    /// The muscle shows only where the cut is spread far enough to lay it bare. Opt-in progress captures show the first
    /// two clicks (thread_hole_1, thread_hole_2), and an opt-in check that each hole goes in where the aim dot was
    /// (<see cref="AssertHoleOnAim"/>). Returns false when the layer doesn't show beside the wound where a hole goes.
    /// </summary>
    public async Task<bool> PlayerThreads(Wound wound, TissueDepth layer, float stepCells = 1f, bool captureProgress = false,
        bool checkAim = false, Func<float, Task>? expose = null) {
        // Grid cells along and across the wound, in uv: they're square in meters, not in uv.
        var along = (wound.Points[^1] - wound.Points[0]).Normalized();
        if (along == Vector2.Zero) {
            along = Vector2.Right;
        }
        var tissue = Body.Tissue;
        var step = ((Mathf.Abs(along.X) / tissue.ResX) + (Mathf.Abs(along.Y) / tissue.ResY)) * stepCells;
        var cell = (Mathf.Abs(along.Y) / tissue.ResX) + (Mathf.Abs(along.X) / tissue.ResY);
        // A fractional remainder gets one final endpoint, not two overshooting samples clamped to the same end.
        var holes = Mathf.FloorToInt(wound.LengthUv / step) + 2;
        Note($"threads {layer} along wound {wound.Id}: {holes} holes");
        var side = -1f;
        for (var i = 0; i < holes; i++) {
            // Only the final hole is clamped to the far end: a two-hole stitch runs from one end to the other.
            var alongAt = i == holes - 1 ? wound.LengthUv : Mathf.Min(i * step, wound.LengthUv);
            // Across from the last hole made, so every span crosses the cut.
            side = -side;
            // A cut tapers at its ends, where the layers under the skin show less far along: in from there.
            Vector2? Spot() {
                var found = ShowsBeside(wound, alongAt, side, layer, cell);
                for (var inset = 0.25f; found is null && inset <= 2f; inset += 0.25f) {
                    var inward = alongAt < wound.LengthUv * 0.5f ? alongAt + (inset * step) : alongAt - (inset * step);
                    found = ShowsBeside(wound, inward, side, layer, cell);
                }
                return found;
            }
            var spot = Spot();
            if (spot is not null) {
                // Re-squared to each puncture: the holder's carry and its tip-pivot working pose reach differently, and
                // standing by it keeps both sides of the bite inside the arm's range. Looked at again from there: the
                // thread draws the tissue in as it's pulled through.
                await PlayerWalksTo(SitePoint(spot.Value));
                spot = Spot();
            }
            if (spot is null && expose is not null) {
                await expose(alongAt);
                spot = Spot();
                if (spot is not null) {
                    await PlayerWalksTo(SitePoint(spot.Value));
                    spot = Spot();
                }
            }
            if (spot is null) {
                Note($"no {layer} shows beside wound {wound.Id} for hole {i + 1}");
                // A layer under the skin is cut shorter than the skin over it: its thread runs only where it shows.
                if (layer == TissueDepth.Skin) {
                    return false;
                }
                side = -side;
                continue;
            }
            var at = SitePoint(spot.Value);
            await PlayerReaches(at);
            // As a player does, nudged until the needle is where it was aimed: a layer's wall is a narrow band to touch.
            for (var nudge = 0; nudge < 4 && (Tip(Me.Hands[Me.Active]) - at).Slide(Vector3.Up).Length() > 0.0005f; nudge++) {
                await PlayerReaches(at + (at - Tip(Me.Hands[Me.Active])).Slide(Vector3.Up) * 0.8f);
            }
            if ((Tip(Me.Hands[Me.Active]) - at).Slide(Vector3.Up).Length() > 0.004f) {
                Note($"needle cannot reach intended puncture within 4 mm: aim {spot}, tip {Body.WorldToUv(Tip(Me.Hands[Me.Active]))}, "
                    + $"miss {(Tip(Me.Hands[Me.Active]) - at).Slide(Vector3.Up).Length() * 1000f:0.0} mm, on body {Body.OnBody(spot.Value)}");
                return false;
            }
            // The dot as the player clicks, the needle's tip still held up over the tissue, and what it touches.
            var aim = Me.AimPoint();
            var touched = Body.ExposedUnder(Tip(Me.Hands[Me.Active]))?.Tissue;
            Use();
            await Frames.Seconds(0.2f);
            var drawn = checkAim ? LayerDrawn(layer) : [];
            Use(false);
            await Frames.Physics(3);
            if (checkAim) {
                AssertHoleOnAim(aim, touched, drawn, layer, i);
            }
            if (captureProgress && i < 2) {
                await Capture($"thread_hole_{i + 1}", $"the needle through hole {i + 1}, on its own side of the wound, thread trailing from it");
            }
        }
        var made = Me.HeldTool(Me.Active)!.Suture.Thread is var sewn and not 0 ? Body.Tissue.ThreadUvs(sewn).Count : 0;
        Note($"{made} {layer} holes along wound {wound.Id}");
        return made >= 2;
    }

    /// <summary>Where (site uv, as drawn now) a needle coming down meets <paramref name="layer"/> going out from
    /// <paramref name="wound"/> at <paramref name="along"/> on <paramref name="side"/> (PatientBody.ExposedUnder()): over
    /// the opening it goes down the cut's walls, the skin's, the fat's and the muscle's. Skin holes go a grid cell
    /// (<paramref name="cell"/>) out past the opening on skin, not on its drawn-back edge; a deeper layer in the middle of where
    /// its wall is met. Null when it isn't met within four cells.</summary>
    public Vector2? ShowsBeside(Wound wound, float along, float side, TissueDepth layer, float cell) {
        float? from = null;
        var to = 0f;
        var line = BesideWound(wound, along, 0f);
        var outward = BesideWound(wound, along, side) - line;
        for (var off = 0f; off <= 4f; off += 0.05f) {
            var uv = BesideWound(wound, along, off * cell * side);
            var touched = Touched(uv);
            // Down a wall, on this side's: over a narrow opening the far side's can be nearer.
            var met = touched is { } tissue && tissue.Layer == layer && (Body.LocalToUv(tissue.Contact) - line).Dot(outward) > 0f;
            if (layer == TissueDepth.Skin) {
                // A cell out past the opening on skin met all the way there: at a cut's end, a cell across can be over
                // the opening's end wall.
                from = met ? from ?? off : null;
                if (off >= from + 1f) {
                    return uv;
                }
                continue;
            }
            if (met) {
                from ??= off;
                to = off;
            } else if (from is not null) {
                break;
            }
        }
        // A sliver a needle can't be held on is no band to sew.
        return from is { } start && to - start >= 0.1f ? BesideWound(wound, along, (start + to) * 0.5f * cell * side) : null;
    }

    /// <summary>Where along <paramref name="wound"/> (every grid cell, each side) <paramref name="layer"/> shows still cut
    /// apart, to sew (<see cref="ShowsBeside"/>), as "along/side" pairs, empty when nowhere.</summary>
    public string ShowsAlong(Wound wound, TissueDepth layer) {
        var tissue = Body.Tissue;
        var along = (wound.Points[^1] - wound.Points[0]).Normalized();
        var step = (Mathf.Abs(along.X) / tissue.ResX) + (Mathf.Abs(along.Y) / tissue.ResY);
        var cell = (Mathf.Abs(along.Y) / tissue.ResX) + (Mathf.Abs(along.X) / tissue.ResY);
        var shows = new List<string>();
        for (var at = 0f; at <= wound.LengthUv; at += step) {
            foreach (var side in (float[])[1f, -1f]) {
                // Shown and still apart there: a sewn seam shows too, its edges together.
                if (ShowsBeside(wound, at, side, layer, cell) is { } spot && tissue.GapAt(spot, cell, layer) > TissueSim.OpenGap) {
                    shows.Add($"{at:0.000}/{side:+0;-0}");
                }
            }
        }
        return string.Join(" ", shows);
    }

    /// <summary>What a needle coming straight down at <paramref name="uv"/> (site uv, as drawn now) touches
    /// (PatientBody.ExposedUnder()). Null in an opening through the muscle.</summary>
    internal ExposedTissue? Touched(Vector2 uv) => Body.ExposedUnder(SitePoint(uv))?.Tissue;

    /// <summary>Where each grid point of <paramref name="layer"/> is drawn (site-local).</summary>
    private Vector3[] LayerDrawn(TissueDepth layer) =>
        [.. Enumerable.Range(0, Body.Tissue.Pos.Length).Select(k => Body.LayerPoint((int)layer - 1, k))];

    /// <summary>The aim dot (<paramref name="aim"/>, as the player clicked) lay on <paramref name="layer"/> as it's
    /// drawn, where the needle <paramref name="touched"/> it as the player clicked, and the newest hole <paramref name="index"/> went in
    /// under it. The tissue of <paramref name="layer"/> the hole holds on to may have moved since, drawn in by the thread
    /// (<paramref name="before"/>: that layer as the click let go): the hole moves with it.</summary>
    private void AssertHoleOnAim(Vector3 aim, ExposedTissue? touched, Vector3[] before, TissueDepth layer, int index) {
        AssertBool(touched?.Layer == layer).OverrideFailureMessage($"the needle for hole {index + 1} touches the "
            + $"{touched?.Layer.ToString() ?? "opening"}, not the {layer}").IsTrue();
        var contact = Body.Site.ToGlobal(touched!.Value.Contact);
        AssertFloat(aim.DistanceTo(contact) * 1000f)
            .OverrideFailureMessage($"the aim dot for hole {index + 1} is where the needle touches the {layer}, not {aim.DistanceTo(contact) * 1000f:0.0} mm off").IsLess(1f);
        var thread = Body.Tissue.Thread(Me.HeldTool(Me.Active)!.Suture.Thread)!;
        var k = thread.Anchors[^1];
        var moved = Body.Site.GlobalBasis * (Body.LayerPoint((int)layer - 1, k) - before[k]);
        var hole = Body.Site.ToGlobal(Body.DrawnThread(Me.HeldTool(Me.Active)!.Suture.Thread)!.Holes[^1]);
        var missed = (hole - moved - aim).Slide(Vector3.Up).Length() * 1000f;
        // On a cut's wall the needle bites in from the edge: the hole holds on 0.3 of a grid cell back from the cut's line
        // (Patient.Hole()), and is drawn on the layer's top, back from the wall by as much as it leans.
        var allowed = 0.5f + (layer == TissueDepth.Skin ? 0f : TissueSim.Cell * 0.6f * 1000f);
        AssertFloat(missed).OverrideFailureMessage($"hole {index + 1} goes in where the aim dot was, not {missed:0.0} mm off")
            .IsLess(allowed);
    }

    /// <summary>Turns the wheel on the needle's thread (or a tensioned closure, paper clips) until the hand status reads
    /// <paramref name="state"/> (SewAction.ThreadState()): down tightens, up loosens. Stops early when the thread goes
    /// (torn through).</summary>
    public async Task PlayerPullsThread(string state) {
        var needle = Me.HeldTool(Me.Active)!;
        var thread = needle.Suture.Thread;
        for (var i = 0; i < 16; i++) {
            var now = SewAction.ThreadState(needle);
            if (now == state || (thread != 0 && needle.Suture.Thread == 0)) {
                break;
            }
            await Notch(Array.IndexOf(ThreadStates, now) > Array.IndexOf(ThreadStates, state));
        }
        Note($"thread {SewAction.ThreadState(needle)}");
    }

    /// <summary>Holds Use tool where the needle is until the thread is tied off, then captures
    /// <paramref name="keyFrame"/>, which should show <paramref name="description"/>.</summary>
    public async Task PlayerTiesOff(string keyFrame = "tied_off", string description = TiedOff) {
        Use();
        await Frames.Seconds(ToolActions.SutureTieHold + 0.2f);
        Use(false);
        await Frames.Physics(3);
        await Capture(keyFrame, description);
    }

    /// <summary>What a tied off suture should look like.</summary>
    private const string TiedOff = "the knot is tied: the thread runs across the wound between its holes and draws the edges together";

    /// <summary>The point <paramref name="along"/> (uv) from the start of <paramref name="wound"/> on its line, clamped
    /// to its ends, moved <paramref name="off"/> (uv) to its left (negative: right).</summary>
    public static Vector2 BesideWound(Wound wound, float along, float off) {
        var points = wound.Points;
        if (points.Count < 2) {
            return points[0] + (Vector2.Right.Orthogonal() * off);
        }
        var travelled = 0f;
        for (var i = 1; i < points.Count; i++) {
            var segment = points[i - 1].DistanceTo(points[i]);
            if (travelled + segment >= along || i == points.Count - 1) {
                var direction = (points[i] - points[i - 1]).Normalized();
                var t = Mathf.Clamp((along - travelled) / Mathf.Max(segment, 0.0001f), 0f, 1f);
                return points[i - 1].Lerp(points[i], t) + (direction.Orthogonal() * off);
            }
            travelled += segment;
        }
        return points[0];
    }

    /// <summary>Sews internal wounds shut from inside the opening.</summary>
    public async Task PlayerClosesInternalWounds() {
        Note("closes internal wounds");
        await PlayerRequestsItem("needle");
        foreach (var wound in Patient.Wounds.Where(wound => wound.IsInternal).ToList()) {
            // An incision can lie beside the injury's anatomical point. Enter its actual opening rather than
            // pressing the needle onto the intact skin above the injury.
            var injury = wound.Points[0];
            var entry = Patient.Wounds.Where(cut => cut.IsSkinCut)
                .SelectMany(cut => cut.Points.Append(cut.ClosestPoint(injury)))
                .Prepend(injury).Where(uv => uv.DistanceTo(injury) < 0.05f && Body.IsOpen(uv))
                .OrderBy(uv => uv.DistanceSquaredTo(injury)).Select(uv => (Vector2?)uv).FirstOrDefault();
            if (entry is null) {
                Note($"no opening reaches internal wound {wound.Id}");
                continue;
            }
            var spot = Body.UvToWorld(entry.Value, wound.DepthM);
            await PlayerWalksTo(spot);
            // Deep in the belly, across it: the needle pointed straight ahead reaches further.
            await PlayerAimsStraight();
            await PlayerReaches(spot);
            Use();
            await Frames.Until(() => wound.Closure >= 0.9f, 20f);
            Use(false);
            Note($"internal wound closed {wound.Closure:0.00}");
            if (wound.Closure < 0.9f) {
                Note($"needle at {Body.Probe(Tip(Me.Hands[Me.Active]))}, injury at {wound.Points[0]}, "
                    + $"miss {(Tip(Me.Hands[Me.Active]) - spot).Slide(Vector3.Up).Length() * 1000f:0.0} mm");
            }
        }
        await PlayerPutsDown();
    }
}
