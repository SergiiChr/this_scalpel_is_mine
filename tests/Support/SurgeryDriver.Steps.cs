namespace Scalpel.Tests.Support;

/// <summary>The objective steps: incisions, taking things out, bleeding, the heart and the rest.</summary>
public partial class SurgeryDriver
{
    /// <summary>Strokes <paramref name="length"/> meters long through <paramref name="center"/> along the site's long
    /// side, cut to fit on the site, then parallel ones beside it. Steps that need a total length take strokes until
    /// they have it.</summary>
    private List<(Vector2 From, Vector2 To)> IncisionStrokes(Vector2 center, float length)
    {
        var strokes = new List<(Vector2, Vector2)>();
        var along = Body.SiteSize.X >= Body.SiteSize.Y ? new Vector2(1f, 0f) : new Vector2(0f, 1f);
        var across = new Vector2(along.Y, along.X);
        var span = Mathf.Min(length / Body.UvToMeters(1f), 0.84f);
        for (var row = 0; row < 7; row++)
        {
            var shift = across * 0.1f * ((row + 1) / 2) * (row % 2 == 1 ? 1 : -1);
            var middle = (center + shift).Clamp(
                new Vector2(0.08f, 0.08f) + (along * span * 0.5f), new Vector2(0.92f, 0.92f) - (along * span * 0.5f));
            if (middle.X < 0.05f || middle.Y < 0.05f || middle.X > 0.95f || middle.Y > 0.95f)
            {
                continue;
            }
            strokes.Add((middle - (along * span * 0.5f), middle + (along * span * 0.5f)));
        }
        return strokes;
    }

    /// <summary>Marks the incision line with the marker.</summary>
    public async Task PlayerMarksLine(float length)
    {
        Note($"marks {length * 100f:0} cm");
        await PlayerRequestsItem("marker");
        foreach (var (from, to) in IncisionStrokes(WorkUv(), length + 0.01f))
        {
            if (Body.UvToMeters(Patient.MarkedUv) >= length + 0.005f)
            {
                break;
            }
            await PlayerWalksTo(SitePoint((from + to) * 0.5f));
            await PlayerWorksAlong([SitePoint(from), SitePoint(to)], 0, 0.05f);
        }
        Note($"marked {Body.UvToMeters(Patient.MarkedUv) * 100f:0.0} cm");
        await PlayerPutsDown();
    }

    /// <summary>Cuts <paramref name="length"/> meters at full depth with the scalpel, the blade's edge turned along the
    /// line, slowly enough for a clean cut, through where the work is.</summary>
    public async Task PlayerIncises(float length)
    {
        Note($"incises {length * 100f:0} cm");
        foreach (var (from, to) in IncisionStrokes(WorkUv(), length + 0.01f))
        {
            if (Patient.SurgeonCutLengthM(0.7f) >= length + 0.005f)
            {
                break;
            }
            await PlayerCutsSkin(from, to, 3);
        }
        Note($"cut {Patient.SurgeonCutLengthM(0.7f) * 100f:0.0} cm deep enough");
        await PlayerPutsDown();
        await Capture("incised", $"a clean {length * 100f:0} cm incision through the skin, the scalpel put down");
    }

    /// <summary>One stroke of a blade (the scalpel unless <paramref name="toolId"/> says) from <paramref name="from"/>
    /// to <paramref name="to"/> (site uv) at depth <paramref name="level"/> (1 through the skin, 3 through the muscle):
    /// the surgeon stands by the line, turns the edge along it, presses in and draws it slowly enough for a clean cut.
    /// The blade stays in hand.</summary>
    public async Task PlayerCutsSkin(Vector2 from, Vector2 to, int level, string toolId = "scalpel")
    {
        await PlayerRequestsItem(toolId);
        var start = SitePoint(from);
        var finish = SitePoint(to);
        await PlayerWalksTo((start + finish) * 0.5f);
        await PlayerTurnsBlade(finish - start);
        await PlayerWorksAlong([start, finish], level);
    }

    /// <summary>Sets the Gelpi retractor into the cut from <paramref name="from"/> to <paramref name="to"/> (site uv) at
    /// its middle: the surgeon stands by it, rolls it so its tips are square to the cut and presses Use tool there. It
    /// stays in hand, set or not.</summary>
    public async Task<SurgicalTool> PlayerSetsGelpi(Vector2 from, Vector2 to)
    {
        var gelpi = (await PlayerRequestsItem("gelpi"))!;
        var middle = SitePoint((from + to) * 0.5f);
        await PlayerWalksTo(middle);
        await PlayerTurnsBlade(SitePoint(to) - SitePoint(from));
        await PlayerReaches(middle);
        Use();
        await Frames.Physics(10);
        Use(false);
        // Set, it goes down into the cut.
        await Frames.Seconds(SurgicalTool.DigTime + 0.1f);
        Note($"gelpi {(gelpi.InWound ? "set" : "not set")}");
        return gelpi;
    }

    /// <summary>Turns the wheel on the active hand's Gelpi retractor until its tips are <paramref name="spread"/>
    /// meters apart, then lets the skin settle.</summary>
    public async Task PlayerOpensGelpi(float spread)
    {
        var gelpi = Me.HeldTool(Me.Active)!;
        for (var i = 0; i < 30 && Mathf.Abs(gelpi.Spread - spread) >= ToolActions.SpreadStep * 0.5f; i++)
        {
            await Notch(spread > gelpi.Spread);
        }
        await Frames.Seconds(1f);
        Note($"gelpi open {gelpi.Spread * 100f:0.0} cm");
    }

    /// <summary>Takes out every target of <paramref name="kind"/> the way its scenario says: lifted out with forceps
    /// (slowly while it's still attached), drained with suction, sawn or knocked loose.</summary>
    public async Task PlayerExtracts(string kind)
    {
        foreach (var target in Patient.Targets.Where(target => target.Kind == kind && !target.Extracted).ToList())
        {
            Note($"extracts {kind} ({target.RemoveWith})");
            var at = SitePoint(target.Uv);
            switch (target.RemoveWith)
            {
                case "suction":
                    await PlayerRequestsItem("suction");
                    await PlayerWalksTo(at);
                    await PlayerWorksAt(at, 3, 0.5f);
                    await HoldUseUntil(() => target.Extracted, 3);
                    break;
                case "saw":
                    await PlayerRequestsItem("bone_saw");
                    await PlayerWalksTo(at);
                    await PlayerReaches(at);
                    await HoldUseUntil(() => target.Extracted, 3);
                    break;
                case "smash":
                    await PlayerRequestsItem("mallet");
                    await PlayerWalksTo(at);
                    for (var hit = 0; hit < 20 && !target.Extracted; hit++)
                    {
                        await PlayerWorksAt(at, 0, 0.3f);
                        await Frames.Physics(10);
                    }
                    break;
                default:
                    await LiftOut(target);
                    break;
            }
            Note($"{kind} out: {target.Extracted}");
            await PlayerPutsDown();
        }
        await Capture("extracted_" + kind, $"every {kind} is out of the opening and the tool put down");
    }

    /// <summary>Holds Use tool at effort <paramref name="level"/> until <paramref name="done"/>, a minute at most.
    /// </summary>
    private async Task HoldUseUntil(Func<bool> done, int level)
    {
        await SetLevel(level);
        Use();
        await Frames.Until(done, 60f);
        Use(false);
    }

    /// <summary>Forceps onto the target and out over the floor beside the table. Still attached, it comes loose with a
    /// slow pull. Under an organ, its attachment is cut through first, the organ is held aside with forceps in the
    /// other hand while the target comes out, and let go again quickly (held aside too long it bruises).</summary>
    private async Task LiftOut(CavityTarget target)
    {
        var aside = new List<SurgicalTool>();
        if (Patient.Covered(target))
        {
            if (target.Anchor > 0f)
            {
                await CutFree(target);
            }
            // Forceps in both hands first: fetching the second while the first holds the organ aside would drag it.
            await SwitchTo(Right);
            await PlayerRequestsItem("forceps");
            aside = await HoldAside(target);
        }
        await SwitchTo(Right);
        var forceps = (await PlayerRequestsItem("forceps"))!;
        var spot = Body.UvToWorld(target.Uv, target.Depth);
        await PlayerWalksTo(spot);
        await PlayerReaches(spot);
        Use();
        await Frames.Physics(5);
        if (forceps.Hold is not TargetHold)
        {
            Note($"forceps didn't take hold of {target.Kind}: {forceps.Hold}");
            Use(false);
            await LetGo(aside);
            return;
        }
        // A steady pull, a couple of centimeters up, held until it lets go.
        PlayerInput.Action(InputActions.Lift);
        await Frames.Until(() => Body.Site.ToLocal(forceps.TipPosition()).Y > -target.Depth + 0.025f, 3f);
        PlayerInput.Action(InputActions.Lift, false);
        await Frames.Until(() => target.Anchor <= 0f, 30f);
        PlayerInput.Action(InputActions.Lift);
        await Frames.Until(() => target.Extracted, 5f);
        await Frames.Seconds(0.5f);
        PlayerInput.Action(InputActions.Lift, false);
        await LetGo(aside);
        await SwitchTo(Right);
        Use(false);
        await Frames.Physics(10);
    }

    /// <summary>The scalpel worked round a target from inside the opening until whatever holds it is cut through.
    /// </summary>
    private async Task CutFree(CavityTarget target)
    {
        Note($"cuts {target.Kind} free");
        await PlayerRequestsItem("scalpel");
        var spot = Body.UvToWorld(target.Uv, target.Depth);
        await PlayerWalksTo(spot);
        await PlayerReaches(spot);
        await SetLevel(3);
        Use();
        await Frames.Until(() => target.Anchor <= 0f, 20f);
        Use(false);
        Note($"{target.Kind} attached {target.Anchor:0.00}");
        await PlayerPutsDown();
    }

    /// <summary>Forceps in the left hand take hold of the organ over the target and draw it aside, away from the
    /// target. Returns the forceps holding it.</summary>
    private async Task<List<SurgicalTool>> HoldAside(CavityTarget target)
    {
        var holding = new List<SurgicalTool>();
        await SwitchTo(Left);
        for (var i = 0; i < Body.Organs.Count && holding.Count == 0; i++)
        {
            var organ = Body.Organs[i];
            var organUv = Body.LocalToUv(organ.Position);
            if (organUv.DistanceTo(target.Uv) >= 0.07f)
            {
                continue;
            }
            var away = organUv.DistanceTo(target.Uv) > 0.01f ? (organUv - target.Uv).Normalized() : new Vector2(1f, 0f);
            if (OrganGripPoint(i, away) is not { } gripAt)
            {
                Note($"no place to take hold of the {organ.Kind} clear of bleeders");
                continue;
            }
            var forceps = (await PlayerRequestsItem("forceps"))!;
            await PlayerWalksTo(gripAt);
            await PlayerReaches(gripAt);
            Use();
            await Frames.Physics(5);
            if (forceps.Hold is not OrganHold)
            {
                Note($"forceps didn't take hold of the {organ.Kind}: {forceps.Hold}");
                Use(false);
                continue;
            }
            await PlayerSweepsTo(SitePoint(organUv + (away * 0.12f)), 0.1f);
            Note($"holds the {organ.Kind} aside, target covered: {Patient.Covered(target)}");
            holding.Add(forceps);
        }
        return holding;
    }

    /// <summary>A point on top of organ <paramref name="index"/> to take hold of it, toward <paramref name="away"/>
    /// (site uv) and clear of internal wounds (forceps there would clamp the bleeder instead). Null if there's none.
    /// </summary>
    private Vector3? OrganGripPoint(int index, Vector2 away)
    {
        var organ = Body.Organs[index];
        for (var step = 0; step < 6; step++)
        {
            var local = organ.Position + (new Vector3(away.X, 0f, away.Y) * step * 0.008f);
            var uv = Body.LocalToUv(local);
            if (Patient.Wounds.Any(wound => wound.IsInternal && wound.Points[0].DistanceTo(uv) < 0.055f))
            {
                continue;
            }
            var top = Body.Site.ToGlobal(local with { Y = organ.Position.Y });
            if (Body.OrganAt(top, 0.02f) == index)
            {
                return top;
            }
        }
        return null;
    }

    /// <summary>The left hand lets go of what its forceps hold and puts them down.</summary>
    private async Task LetGo(List<SurgicalTool> holding)
    {
        if (holding.Count == 0)
        {
            return;
        }
        await SwitchTo(Left);
        Use(false);
        await Frames.Physics(3);
        Use();
        await Frames.Physics(3);
        Use(false);
        await PlayerPutsDown();
    }

    /// <summary>
    /// Seals what still bleeds until the patient loses less than <paramref name="maxRate"/> ml/s, the way a surgeon
    /// escalates: the cautery on internal bleeders and along the skin beside open wounds (over the opening it would
    /// reach into the cavity), a hemostat locked onto each bleeder that's left, tranexamic acid, then gauze pressed
    /// along the wounds.
    /// </summary>
    public async Task PlayerStopsBleeding(float maxRate)
    {
        Note($"stops the bleeding: {Bleeders()}");
        bool Settled() => Patient.LastingBleedRate <= maxRate;
        if (Settled())
        {
            return;
        }
        if (Available("cautery"))
        {
            await PlayerRequestsItem("cautery");
            foreach (var wound in Bleeding())
            {
                if (wound.IsInternal)
                {
                    var spot = Body.UvToWorld(wound.Points[0], wound.DepthM);
                    await PlayerWalksTo(spot);
                    await PlayerWorksAt(spot, 3, 3f);
                }
                else
                {
                    await PlayerWalksTo(SitePoint(wound.Midpoint));
                    await PlayerWorksAlong(Beside(wound), 2, 0.01f);
                }
            }
            await PlayerPutsDown();
        }
        if (!await Frames.Until(Settled, 3f))
        {
            foreach (var wound in Bleeding())
            {
                await PlayerClampsBleeder(wound);
            }
        }
        if (!await Frames.Until(Settled, 3f) && Available("vial_txa"))
        {
            await PlayerGivesDrug("vial_txa", DoseMl("vial_txa"), IvOrVein);
            await Frames.Seconds(15f);
        }
        // Gauze stops only small cuts for good: pressed on each one, without a break, for long enough.
        var small = Bleeding().Where(wound => wound.IsSmall(Body.UvToMeters(1f))).ToList();
        if (!await Frames.Until(Settled, 3f) && small.Count > 0 && Available("gauze"))
        {
            await PlayerRequestsItem("gauze");
            foreach (var wound in small)
            {
                await PlayerWalksTo(SitePoint(wound.Midpoint));
                await PlayerWorksAt(SitePoint(wound.Midpoint), 3, Wound.SmallCutPress + 1f);
            }
            await PlayerPutsDown();
        }
        await Frames.Until(Settled, 3f);
        Note($"bleeding: {Bleeders()}");
    }

    /// <summary>The tool is there to take, or the nurse can bring it.</summary>
    private bool Available(string id) => Surgery.Scenario.Nurse || FreeTools(id).Count > 0;

    /// <summary>Wounds still bleeding noticeably once gauze pressure wears off, the worst first.</summary>
    private List<Wound> Bleeding() =>
    [
        .. Patient.Wounds
            .Where(wound => wound.BleedRate(Body.UvToMeters(1f), 1f, withGauze: false) >= 0.05f)
            .OrderByDescending(wound => wound.BleedRate(1f, 1f, withGauze: false)),
    ];

    /// <summary>Every wound still bleeding: kind, ml/s, and what holds it back.</summary>
    public string Bleeders() => string.Join(", ", Patient.Wounds
        .Select(wound => (Wound: wound, Rate: wound.BleedRate(Body.UvToMeters(1f), 1f)))
        .Where(entry => entry.Rate >= 0.05f)
        .Select(entry =>
        {
            var w = entry.Wound;
            return $"{w.Kind} {entry.Rate:0.00} ml/s ({Body.UvToMeters(w.LengthUv) * 100f:0.0} cm, open {w.Opened:0.00}, "
                + $"sealed {w.Cauterized:0.00}, held {w.Held:0.00}, pressed {w.Pressed:0.00}, clamped {w.Clamped:0.00}, closed {w.Closure:0.00})";
        }));

    /// <summary>Points along a skin wound on whole skin just beside it, where a tool touches the wound's edge.</summary>
    private List<Vector3> Beside(Wound wound)
    {
        var line = new List<Vector3>();
        for (var i = 0; i < wound.Bins.Length; i++)
        {
            var at = wound.BinPosition(i);
            var ahead = wound.BinPosition(Math.Min(i + 1, wound.Bins.Length - 1)) - wound.BinPosition(Math.Max(i - 1, 0));
            var across = ahead.Length() > 0f ? new Vector2(-ahead.Y, ahead.X).Normalized() : new Vector2(0f, 1f);
            var beside = Enumerable.Range(2, 12)
                .SelectMany(step => ((float[])[1f, -1f]).Select(side => at + (across * side * step * 0.005f)))
                .FirstOrDefault(uv => !Body.IsOpen(uv) && uv.Clamp(Vector2.Zero, Vector2.One) == uv, -Vector2.One);
            if (beside != -Vector2.One)
            {
                line.Add(SitePoint(beside));
            }
        }
        return line;
    }

    /// <summary>A tourniquet pressed onto the limb above the site.</summary>
    public async Task PlayerAppliesTourniquet()
    {
        Note("applies a tourniquet");
        await PlayerRequestsItem("tourniquet");
        foreach (var v in (float[])[-0.4f, -0.2f, 0f, 1.2f])
        {
            if (Patient.TourniquetOn)
            {
                break;
            }
            var spot = SitePoint(new Vector2(0.5f, v));
            await PlayerWalksTo(spot);
            await PlayerWorksAt(spot, 0, 0.5f);
        }
        Note($"tourniquet on: {Patient.TourniquetOn}");
        await PlayerPutsDown();
    }

    /// <summary>A hemostat locked onto a bleeding wound (the worst one when none is given) and left on it: on an
    /// internal bleeder from inside the opening, on a skin wound at its opened edge. Gripped away from anything to
    /// take out, which a clamp would take hold of instead.</summary>
    public async Task PlayerClampsBleeder(Wound? wound = null)
    {
        wound ??= Bleeding().FirstOrDefault();
        if (wound is null)
        {
            return;
        }
        Note($"clamps a bleeding {wound.Kind.ToString().ToLowerInvariant()}");
        var spots = wound.IsInternal
            ? Enumerable.Range(0, 8).Select(turn =>
                Body.UvToWorld(wound.Points[0] + new Vector2(0.035f, 0f).Rotated(turn * Mathf.Pi / 4f), wound.DepthM))
            : wound.BinPositions().Select(uv => SitePoint(uv));
        bool Clear(Vector3 spot)
        {
            var uv = Body.WorldToUv(spot);
            return Patient.Targets.All(target => target.Extracted || target.Uv.DistanceTo(uv) >= 0.06f);
        }
        if (await PlayerRequestsItem("hemostat") is not { } hemostat)
        {
            return;
        }
        foreach (var spot in spots.Where(Clear).ToList())
        {
            await PlayerWalksTo(spot);
            await PlayerReaches(spot);
            Use();
            await Frames.Physics(5);
            Use(false);
            if (wound.Clamped >= 0.8f)
            {
                break;
            }
            if (hemostat.Hold is not null)
            {
                Use();
                await Frames.Physics(3);
                Use(false);
                await Frames.Physics(3);
            }
        }
        if (wound.Clamped >= 0.8f)
        {
            // Self-retaining: let go of the handle and it stays locked on.
            Tap(InputActions.Grab);
            await Frames.Physics(10);
        }
        else
        {
            await PlayerPutsDown();
        }
        Note($"clamped: {wound.Clamped:0.00}");
    }

    /// <summary>Hangs blood bags on the IV stand until the patient has <paramref name="minMl"/> back.</summary>
    public async Task PlayerTransfuses(float minMl)
    {
        Note("transfuses");
        var vitals = Patient.Vitals;
        var needed = minMl * vitals.MaxBloodMl / Vitals.NormalBloodMl;
        for (var bag = 0; bag < 4 && !(Patient.TransfusedMl > 0f && vitals.BloodMl >= needed); bag++)
        {
            await PlayerRequestsItem("blood_o_neg");
            await PlayerInteracts("Swap IV bag");
            await Frames.Until(() => vitals.BloodMl >= needed, 70f);
        }
        Note($"blood {vitals.BloodMl:0} ml of {vitals.MaxBloodMl:0}, {Patient.TransfusedMl:0} ml transfused");
    }

    /// <summary>Turns the patient with the table's handle and the quick time keys, until they lie as
    /// <paramref name="pose"/> says.</summary>
    public async Task PlayerTurnsPatient(PatientPose pose)
    {
        Note("turns the patient");
        for (var attempt = 0; attempt < 4 && Body.Pose != pose; attempt++)
        {
            await PlayerInteracts("Turn the patient");
            for (var key = 0; key < 8; key++)
            {
                await Frames.Physics(20);
                if (Surgery.Hud.FindChildren("*", "", true, false).OfType<QteView>().LastOrDefault() is not { } qte)
                {
                    break;
                }
                // The quick-time event listens to the keys themselves.
                PlayerInput.Key(qte.ShownKey);
            }
            await Frames.Seconds(1f);
        }
        Note($"pose {Body.Pose}");
    }

    /// <summary>Holds every bone fragment back in its place with forceps, one in each hand, for
    /// <paramref name="time"/> seconds. Both forceps are in hand before either fragment is taken hold of: fetching one
    /// later would drag the other along.</summary>
    public async Task PlayerAlignsFragments(float time)
    {
        Note("aligns the fragments");
        var fragments = Patient.Targets.Where(target => target.IsFragment).Take(2).ToList();
        int[] hands = [Right, Left];
        for (var i = 0; i < fragments.Count; i++)
        {
            await SwitchTo(hands[i]);
            await PlayerRequestsItem("forceps");
        }
        for (var i = 0; i < fragments.Count; i++)
        {
            var target = fragments[i];
            await SwitchTo(hands[i]);
            if (Me.HeldTool(Me.Active) is not { } forceps)
            {
                continue;
            }
            var spot = Body.UvToWorld(target.Uv, target.Depth);
            await PlayerWalksTo(spot);
            await PlayerReaches(spot);
            Use();
            await Frames.Physics(5);
            if (forceps.Hold is not TargetHold)
            {
                Note($"forceps didn't take hold of a fragment: {forceps.Hold}");
                continue;
            }
            await PlayerSweepsTo(Body.UvToWorld(target.RestUv, target.Depth), 0.01f);
            Note($"fragment {i} off by {target.Uv.DistanceTo(target.RestUv):0.000}");
        }
        await Frames.Seconds(time + 1f);
        foreach (var hand in (int[])[Left, Right])
        {
            await SwitchTo(hand);
            await PlayerPutsDown();
        }
    }

    /// <summary>Cuts the dead skin off the burns with the scalpel, row by row over each burn.</summary>
    public async Task PlayerDebrides(float amount)
    {
        Note("debrides");
        await PlayerRequestsItem("scalpel");
        await OverBurns(() => Patient.GridFraction(Patient.BurnGrid.Debrided) >= amount, 1);
        Note($"debrided {Patient.GridFraction(Patient.BurnGrid.Debrided):0.00}");
        await PlayerPutsDown();
    }

    /// <summary>Skin grafts pressed onto every cleaned burn cell.</summary>
    public async Task PlayerGrafts(float amount)
    {
        Note("grafts");
        for (var sheet = 0; sheet < 10 && Patient.GridFraction(Patient.BurnGrid.Grafted) < amount; sheet++)
        {
            if (await PlayerRequestsItem("skin_graft") is not { } pad)
            {
                break;
            }
            for (var cell = 0; cell < Patient.Grid * Patient.Grid && pad.Charges != 0 && pad.State == ToolState.Held; cell++)
            {
                if (Patient.NeedsGraft(cell))
                {
                    var uv = (new Vector2(cell % Patient.Grid, cell / Patient.Grid) + new Vector2(0.5f, 0.5f)) / Patient.Grid;
                    await PlayerWalksTo(SitePoint(uv));
                    await PlayerWorksAt(SitePoint(uv), 0, 0.2f);
                }
            }
        }
        Note($"grafted {Patient.GridFraction(Patient.BurnGrid.Grafted):0.00}");
    }

    /// <summary>Works the held tool over every burn cell, row by row, until <paramref name="done"/>.</summary>
    private async Task OverBurns(Func<bool> done, int level)
    {
        for (var y = 0; y < Patient.Grid; y++)
        {
            var row = Enumerable.Range(0, Patient.Grid)
                .Where(x => Patient.IsBurnt((y * Patient.Grid) + x))
                .Select(x => SitePoint((new Vector2(x, y) + new Vector2(0.5f, 0.5f)) / Patient.Grid))
                .ToList();
            if (row.Count < 2)
            {
                continue;
            }
            await PlayerWalksTo(row[row.Count / 2]);
            await PlayerTurnsBlade(row[^1] - row[0]);
            await PlayerWorksAlong(row, level, 0.03f);
            if (done())
            {
                return;
            }
        }
    }

    /// <summary>The defibrillator's paddles on the chest, charged and let go, until the heart is back.</summary>
    public async Task PlayerDefibrillates()
    {
        Note("defibrillates");
        var vitals = Patient.Vitals;
        await Frames.Until(() => vitals.IsArrested, 120f);
        await PlayerRequestsItem("defibrillator");
        var chest = SitePoint(new Vector2(0.5f, 0.5f));
        for (var shock = 0; shock < 6 && !(Patient.Flags.ContainsKey("revived") && !vitals.IsArrested); shock++)
        {
            await PlayerWalksTo(chest);
            await PlayerReaches(chest);
            Use();
            await Frames.Seconds(ToolActions.DefibChargeTime + 0.3f);
            Use(false);
            await Frames.Seconds(3f);
            if (vitals.IsArrested && shock == 1)
            {
                await PlayerPutsDown();
                await PlayerGivesDrug("vial_adrenaline", DoseMl("vial_adrenaline"), IvOrVein);
                await PlayerRequestsItem("defibrillator");
            }
        }
        Note($"revived: {Patient.Flags.ContainsKey("revived")}, rhythm {vitals.Rhythm}");
        await PlayerPutsDown();
    }

    /// <summary>Gets the patient stable (heart going, oxygen 94+, pressure 90+): blood, or fluid without it, down the IV
    /// line while they're short of blood (setting a line first), the defibrillator if the heart stops.</summary>
    public async Task PlayerStabilizes()
    {
        var vitals = Patient.Vitals;
        bool Stable() => !vitals.IsArrested && vitals.Spo2 >= 94f && vitals.Systolic >= 90f;
        for (var round = 0; round < 6 && !Stable() && !Surgery.Finished; round++)
        {
            Note($"stabilizes: spo2 {vitals.Spo2:0}, systolic {vitals.Systolic:0}, blood {vitals.BloodRatio * 100f:0}%");
            if (vitals.IsArrested)
            {
                await PlayerDefibrillates();
            }
            else if (vitals.BloodRatio < 0.95f)
            {
                if (!Patient.IvWorking)
                {
                    await PlayerSetsIv();
                }
                var bag = Surgery.Scenario.StartingTools.Contains("blood_o_neg") || Surgery.Scenario.Nurse
                    ? "blood_o_neg"
                    : "saline_bag";
                await PlayerRequestsItem(bag);
                await PlayerInteracts("Swap IV bag");
            }
            await Frames.Until(Stable, 30f);
        }
    }

    /// <summary>Completes one objective step of the scenario like a player would.</summary>
    public async Task PlayerCompletes(ObjectiveStep step)
    {
        if (Surgery.Finished)
        {
            return;
        }
        var p = step.Parameters;
        var task = step.Type switch
        {
            "sanitize" => PlayerSanitizesSite(p.Float("amount", 0.5f)),
            "iv" => PlayerSetsIv(),
            "anesthesia" => PlayerAnesthetizes(),
            "local_block" => PlayerNumbsSite(),
            "mark" => PlayerMarksLine(p.Float("length", 0.1f)),
            "incise" => PlayerIncises(p.Float("length", 0.1f)),
            "extract" => PlayerExtracts(p.String("target")),
            "close" => PlayerClosesWounds(),
            "close_internal" => PlayerClosesInternalWounds(),
            "stop_bleeding" => PlayerStopsBleeding(p.Float("max_ml_s", 0.3f)),
            "inject" => PlayerInjects(step),
            "defib" => PlayerDefibrillates(),
            "tourniquet" => PlayerAppliesTourniquet(),
            "clamp" => PlayerClampsBleeder(),
            "transfuse" => PlayerTransfuses(p.Float("min_ml", 4000f)),
            "flip" => PlayerTurnsPatient((PatientPose)p.Int("orientation", (int)PatientPose.FaceDown)),
            "align" => PlayerAlignsFragments(p.Float("seconds", 6f)),
            "debride" => PlayerDebrides(p.Float("amount", 0.5f)),
            "graft" => PlayerGrafts(p.Float("amount", 0.7f)),
            "comfort" => PlayerInteracts("Talk to the patient"),
            "stabilize" => PlayerStabilizes(),
            // Waiting it out: calm, listen and wait hold for a while on their own.
            _ => Task.CompletedTask,
        };
        await task;
    }
}
