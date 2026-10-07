namespace Scalpel.Tests.Support;

/// <summary>Closing wounds: running sutures, staples, tape and internal stitches.</summary>
public partial class SurgeryDriver
{
    private static readonly string[] ThreadStates = ["loose", "closed", "too tight"];

    /// <summary>
    /// Sews every open skin wound shut: the muscle first where a wound goes through it, then the skin. The needle sews
    /// a running thread along each (<see cref="PlayerSews"/>), a stapler staples along it (<see cref="PlayerStaples"/>);
    /// tape (<paramref name="toolId"/>) goes segment by segment along it at medium tension, held on each until it's
    /// closed. Goes round again for anything that didn't close.
    /// </summary>
    public async Task PlayerClosesWounds(string toolId = "needle")
    {
        Note($"closes the wounds with {toolId}");
        var tool = (await PlayerRequestsItem(toolId))!;
        for (var round = 0; round < 4 && Patient.SkinClosure() < 0.98f; round++)
        {
            foreach (var wound in Patient.Wounds.Where(wound => wound.IsSkinCut).ToList())
            {
                switch (tool.Def.Action)
                {
                    case "sew":
                        if (wound.ThroughMuscle && wound.Muscle.Any(m => m < 1f))
                        {
                            await PlayerSews(wound, TissueDepth.Muscle);
                        }
                        if (wound.Bins.Any(b => b < 1f))
                        {
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

    /// <summary>Tape or a closure strip pressed bin by bin along <paramref name="wound"/>: its muscle first where the cut
    /// goes through it, then its skin.</summary>
    private async Task PlayerTapes(Wound wound)
    {
        foreach (var muscle in (bool[])[true, false])
        {
            if (muscle && !wound.ThroughMuscle)
            {
                continue;
            }
            for (var bin = 0; bin < wound.Bins.Length; bin++)
            {
                var index = bin;
                bool Closed() => (muscle ? wound.Muscle : wound.Bins)[index] >= 1f;
                if (Closed())
                {
                    continue;
                }
                var at = SitePoint(wound.BinPosition(bin));
                await WithinReach(at);
                await PlayerReaches(at);
                await SetLevel(2);
                Use();
                await Frames.Until(Closed, 3f);
                if (!Closed())
                {
                    var hand = Me.Hands[Me.Active];
                    var tip = Me.HeldTool(Me.Active) is { } held ? Body.Probe(held.TipPosition()).ToString() : "no tool";
                    Note($"{(muscle ? "muscle" : "skin")} {bin} won't close: lowered {hand.Lowered}, level {hand.Level}, tip {tip}");
                }
                Use(false);
                await Frames.Physics(3);
            }
        }
    }

    /// <summary>
    /// Staples <paramref name="wound"/> shut with the stapler in the active hand: centered on the cut every
    /// <paramref name="spacing"/> meters along it (the first and last a little in from its ends), legs square across it
    /// (as C/V turn a blade along it), clicked at each spot where the cut doesn't already look closed. Where the muscle
    /// under it is still open the staples go into the muscle, so it goes along again for the skin (or for what sprang
    /// open between staples), up to <paramref name="rounds"/> times while it isn't closed. Captures the first staple as key frame first_staple.
    /// Returns how many staples went in.
    /// </summary>
    public async Task<int> PlayerStaples(Wound wound, float spacing = TissueSim.StitchReach, int rounds = 4)
    {
        var stapler = Me.HeldTool(Me.Active)!;
        var step = spacing / ((wound.Points[^1] - wound.Points[0]).Normalized() * Body.SiteSize).Length();
        var placed = 0;
        for (var round = 0; round < rounds && wound.Closure < 0.99f; round++)
        {
            for (var i = 0; i <= Mathf.FloorToInt(wound.LengthUv / step); i++)
            {
                // A few millimeters in from the ends: aimed right at its end the stapler can land just past the cut.
                var along = Mathf.Clamp(i * step, step * 0.6f, wound.LengthUv - (step * 0.6f));
                // Where the cut already looks closed there's nothing to staple.
                if (Body.Tissue.ClosedAt(BesideWound(wound, along, 0f)))
                {
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
                if (stapler.Charges < charges && ++placed == 1)
                {
                    await Capture("first_staple");
                }
            }
            // The edges settle under the staples before the surgeon looks again: between two staples they can
            // spring a little apart.
            await Frames.Seconds(1f);
        }
        Note($"{placed} staples along wound {wound.Id}, closed {wound.Closure:0.00}");
        return placed;
    }

    /// <summary>Sews <paramref name="wound"/> with a running thread through <paramref name="layer"/>, the needle in the
    /// active hand: holes along it (<see cref="PlayerThreads"/>), the wheel until the hand status reads closed
    /// (<see cref="PlayerPullsThread"/>), and the knot (<see cref="PlayerTiesOff"/>).</summary>
    public async Task PlayerSews(Wound wound, TissueDepth layer)
    {
        if (await PlayerThreads(wound, layer))
        {
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
    /// wound's end. The first two clicks capture a key frame (thread_hole_1, thread_hole_2). Returns false when the
    /// layer shows nowhere beside the wound.
    /// </summary>
    public async Task<bool> PlayerThreads(Wound wound, TissueDepth layer, float stepCells = 1.5f)
    {
        // Grid cells along and across the wound, in uv: they're square in meters, not in uv.
        var along = (wound.Points[^1] - wound.Points[0]).Normalized();
        if (along == Vector2.Zero)
        {
            along = Vector2.Right;
        }
        var tissue = Body.Tissue;
        var step = ((Mathf.Abs(along.X) / tissue.ResX) + (Mathf.Abs(along.Y) / tissue.ResY)) * stepCells;
        var cell = (Mathf.Abs(along.Y) / tissue.ResX) + (Mathf.Abs(along.X) / tissue.ResY);
        // A fractional remainder gets one final endpoint, not two overshooting samples clamped to the same end.
        var holes = Mathf.FloorToInt(wound.LengthUv / step) + 2;
        var first = -1f;
        for (var i = 0; i < 16; i++)
        {
            var off = cell * (0.25f + (i * 0.25f));
            if (Patient.SutureLayerAt(BesideWound(wound, 0f, off), wound) == layer)
            {
                // Skin holes well clear of the opening: the needle lands a little off where the hand aims.
                first = off + (layer == TissueDepth.Skin ? cell : 0f);
                break;
            }
        }
        if (first < 0f)
        {
            Note($"no {layer} shows beside wound {wound.Id} to sew");
            return false;
        }
        Note($"threads {layer} along wound {wound.Id}: {holes} holes");
        for (var i = 0; i < holes; i++)
        {
            // The rest a grid cell off at least, so each lands on its own side of the wound. Only the final hole is
            // clamped to the far end: a two-hole stitch runs from one end to the other.
            var alongAt = i == holes - 1 ? wound.LengthUv : Mathf.Min(i * step, wound.LengthUv);
            var off = (i == 0 ? first : Mathf.Max(first, cell)) * (i % 2 == 0 ? 1f : -1f);
            var at = SitePoint(BesideWound(wound, alongAt, off));
            // Re-squared to each puncture: the holder's carry and its tip-pivot working pose reach differently, and
            // standing by it keeps both sides of the bite inside the arm's range.
            await PlayerWalksTo(at);
            await PlayerReaches(at);
            if ((Tip(Me.Hands[Me.Active]) - at).Slide(Vector3.Up).Length() > 0.004f)
            {
                Note("needle cannot reach intended puncture within 4 mm");
                return false;
            }
            Use();
            await Frames.Seconds(0.2f);
            Use(false);
            await Frames.Physics(3);
            if (i < 2)
            {
                await Capture($"thread_hole_{i + 1}");
            }
        }
        return true;
    }

    /// <summary>Turns the wheel on the needle's thread until the hand status reads <paramref name="state"/>
    /// (SewAction.ThreadState()): down tightens, up loosens. Stops early when the thread goes (torn through).</summary>
    public async Task PlayerPullsThread(string state)
    {
        var needle = Me.HeldTool(Me.Active)!;
        var thread = needle.Suture.Thread;
        for (var i = 0; i < 16; i++)
        {
            var now = SewAction.ThreadState(needle);
            if (now == state || (thread != 0 && needle.Suture.Thread == 0))
            {
                break;
            }
            await Notch(Array.IndexOf(ThreadStates, now) > Array.IndexOf(ThreadStates, state));
        }
        Note($"thread {SewAction.ThreadState(needle)}");
    }

    /// <summary>Holds Use tool where the needle is until the thread is tied off, then captures
    /// <paramref name="keyFrame"/>.</summary>
    public async Task PlayerTiesOff(string keyFrame = "tied_off")
    {
        Use();
        await Frames.Seconds(ToolActions.SutureTieHold + 0.2f);
        Use(false);
        await Frames.Physics(3);
        await Capture(keyFrame);
    }

    /// <summary>The point <paramref name="along"/> (uv) from the start of <paramref name="wound"/> on its line, clamped
    /// to its ends, moved <paramref name="off"/> (uv) to its left (negative: right).</summary>
    public static Vector2 BesideWound(Wound wound, float along, float off)
    {
        var points = wound.Points;
        if (points.Count < 2)
        {
            return points[0] + (Vector2.Right.Orthogonal() * off);
        }
        var travelled = 0f;
        for (var i = 1; i < points.Count; i++)
        {
            var segment = points[i - 1].DistanceTo(points[i]);
            if (travelled + segment >= along || i == points.Count - 1)
            {
                var direction = (points[i] - points[i - 1]).Normalized();
                var t = Mathf.Clamp((along - travelled) / Mathf.Max(segment, 0.0001f), 0f, 1f);
                return points[i - 1].Lerp(points[i], t) + (direction.Orthogonal() * off);
            }
            travelled += segment;
        }
        return points[0];
    }

    /// <summary>Sews internal wounds shut from inside the opening.</summary>
    public async Task PlayerClosesInternalWounds()
    {
        Note("closes internal wounds");
        await PlayerRequestsItem("needle");
        foreach (var wound in Patient.Wounds.Where(wound => wound.IsInternal).ToList())
        {
            var spot = Body.UvToWorld(wound.Points[0], wound.DepthM);
            await PlayerWalksTo(spot);
            // Deep in the belly, across it: the needle pointed straight ahead reaches further.
            await PlayerAimsStraight();
            await PlayerReaches(spot);
            Use();
            await Frames.Until(() => wound.Closure >= 0.9f, 20f);
            Use(false);
            Note($"internal wound closed {wound.Closure:0.00}");
        }
        await PlayerPutsDown();
    }
}
