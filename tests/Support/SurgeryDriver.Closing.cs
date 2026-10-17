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
                foreach (var wound in Patient.Wounds.Where(wound => wound.ThroughMuscle && wound.Muscle.Any(m => m < 1f)).ToList()) {
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
    /// active hand: holes along it (<see cref="PlayerThreads"/>), the wheel until the hand status reads closed
    /// (<see cref="PlayerPullsThread"/>), and the knot (<see cref="PlayerTiesOff"/>).</summary>
    public async Task PlayerSews(Wound wound, TissueDepth layer) {
        if (await PlayerThreads(wound, layer)) {
            await PlayerPullsThread("closed");
            await PlayerTiesOff();
        }
        var bins = layer == TissueDepth.Muscle ? wound.Muscle : wound.Bins;
        Note($"{layer} of wound {wound.Id} closed {bins.DefaultIfEmpty(0f).Min():0.00}");
    }

    /// <summary>
    /// Clicks the needle's thread through <paramref name="wound"/> in <paramref name="layer"/>: the first hole as close
    /// beside the wound as that layer shows (inside the opening for what's under the skin, Patient.SutureLayerAt()),
    /// then one <paramref name="stepCells"/> grid cells further along on the other side each click, the last at the
    /// wound's end. Opt-in progress captures show the first two clicks (thread_hole_1, thread_hole_2), and an opt-in
    /// check that each hole goes in where the aim dot was (<see cref="AssertHoleOnAim"/>). Returns false when the layer
    /// shows nowhere beside the wound.
    /// </summary>
    public async Task<bool> PlayerThreads(Wound wound, TissueDepth layer, float stepCells = 1f, bool captureProgress = false,
        bool checkAim = false) {
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
        var first = -1f;
        var reverse = false;
        // A tapered tip, or a neighbouring repair, may cover the muscle at one end. Start at the
        // accessible end while still sewing the entire incision, rather than starting in the skin above it.
        for (var end = 0; end < 2 && first < 0f; end++) {
            var alongAt = end == 0 ? 0f : wound.LengthUv;
            for (var i = 0; i < 16; i++) {
                var off = cell * (0.25f + (i * 0.25f));
                if (Patient.SutureLayerAt(BesideWound(wound, alongAt, off), wound) == layer) {
                    // Skin holes a cell clear of the opening, not on its drawn-back edge.
                    first = off + (layer == TissueDepth.Skin ? cell : 0f);
                    reverse = end == 1;
                    break;
                }
            }
        }
        if (first < 0f) {
            Note($"no {layer} shows beside wound {wound.Id} to sew");
            return false;
        }
        Note($"threads {layer} along wound {wound.Id}: {holes} holes{(reverse ? " from its far end" : "")}");
        for (var i = 0; i < holes; i++) {
            // The rest a grid cell off at least, so each lands on its own side of the wound. Only the final hole is
            // clamped to the far end: a two-hole stitch runs from one end to the other.
            var alongAt = i == holes - 1 ? wound.LengthUv : Mathf.Min(i * step, wound.LengthUv);
            if (reverse) { alongAt = wound.LengthUv - alongAt; }
            var off = (i == 0 ? first : Mathf.Max(first, cell)) * (i % 2 == 0 ? 1f : -1f);
            var at = SitePoint(BesideWound(wound, alongAt, off));
            // Re-squared to each puncture: the holder's carry and its tip-pivot working pose reach differently, and
            // standing by it keeps both sides of the bite inside the arm's range.
            await PlayerWalksTo(at);
            await PlayerReaches(at);
            if ((Tip(Me.Hands[Me.Active]) - at).Slide(Vector3.Up).Length() > 0.004f) {
                Note($"needle cannot reach intended puncture within 4 mm: aim {BesideWound(wound, alongAt, off)}, "
                    + $"tip {Body.WorldToUv(Tip(Me.Hands[Me.Active]))}, miss "
                    + $"{(Tip(Me.Hands[Me.Active]) - at).Slide(Vector3.Up).Length() * 1000f:0.0} mm, on body {Body.OnBody(Body.WorldToUv(at))}");
                return false;
            }
            // The dot as the player clicks, the needle's tip still held up over the skin.
            var aim = Me.AimPoint();
            Use();
            await Frames.Seconds(0.2f);
            var drawn = checkAim ? LayerDrawn(layer) : [];
            Use(false);
            await Frames.Physics(3);
            if (checkAim) {
                AssertHoleOnAim(aim, drawn, layer, i);
            }
            if (captureProgress && i < 2) {
                await Capture($"thread_hole_{i + 1}", $"the needle through hole {i + 1}, on its own side of the wound, thread trailing from it");
            }
        }
        return true;
    }

    /// <summary>Where each grid point of <paramref name="layer"/> is drawn (site-local).</summary>
    private Vector3[] LayerDrawn(TissueDepth layer) =>
        [.. Enumerable.Range(0, Body.Tissue.Pos.Length).Select(k => Body.LayerPoint((int)layer - 1, k))];

    /// <summary>The aim dot (<paramref name="aim"/>, as the player clicked) lay on what's under the needle, and the
    /// newest hole <paramref name="index"/> went in under it. The tissue of <paramref name="layer"/> the hole holds on to
    /// may have moved since, drawn in by the thread (<paramref name="before"/>: that layer as the click let go): the hole
    /// moves with it.</summary>
    private void AssertHoleOnAim(Vector3 aim, Vector3[] before, TissueDepth layer, int index) {
        AssertFloat(aim.DistanceTo(Me.OnSurface(aim))).OverrideFailureMessage("the aim dot lies on what's under the needle")
            .IsLess(0.001f);
        var thread = Body.Tissue.Thread(Me.HeldTool(Me.Active)!.Suture.Thread)!;
        var k = thread.Anchors[^1];
        var moved = Body.Site.GlobalBasis * (Body.LayerPoint((int)layer - 1, k) - before[k]);
        var hole = Body.Site.ToGlobal(Body.DrawnThread(Me.HeldTool(Me.Active)!.Suture.Thread)!.Holes[^1]);
        var missed = (hole - moved - aim).Slide(Vector3.Up).Length() * 1000f;
        AssertFloat(missed).OverrideFailureMessage($"hole {index + 1} goes in where the aim dot was, not {missed:0.0} mm off")
            .IsLess(0.5f);
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
