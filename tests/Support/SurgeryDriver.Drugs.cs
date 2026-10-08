namespace Scalpel.Tests.Support;

/// <summary>Preparing the patient: iodine, needles, drugs and the IV line.</summary>
public partial class SurgeryDriver
{
    /// <summary>
    /// Fills a dish (the iodine dish unless <paramref name="dishId"/> says) from the bottle, takes a cotton pad in
    /// forceps, dips it and wipes the site row by row, dipping again whenever the pad runs dry, until
    /// <paramref name="amount"/> of the site is sanitized.
    /// </summary>
    public async Task PlayerSanitizesSite(float amount, string dishId = "iodine_dish")
    {
        Note("sanitizes the site");
        if (FreeTools(dishId) is not [var dish, ..])
        {
            Note($"no {dishId}");
            return;
        }
        if (dish.Fill < 0.5f)
        {
            await RefillDish(dish);
            Note($"dish filled: {dish.Fill:0.00}");
        }
        var forceps = (await PlayerRequestsItem("forceps"))!;
        var radius = Body.MetersToUv(Db.Tool("cotton_pad")!.Radius);
        var rows = Mathf.CeilToInt(1f / (radius * 1.4f));
        var row = 0;
        for (var attempt = 0; attempt < 12 && Patient.SanitizedFraction() < amount + 0.05f && row < rows; attempt++)
        {
            if (Surgery.Tools.CarriedBy(forceps) is null)
            {
                if (FreeTools("cotton_pad").Count == 0)
                {
                    await PlayerRequestsItem("cotton_pad");
                    await PlayerPutsDown();
                    await PlayerRequestsItem("forceps");
                }
                var pad = FreeTools("cotton_pad")[0];
                await PlayerWalksTo(pad.GlobalPosition);
                await PlayerWorksAt(pad.GlobalPosition, 0, 0.2f);
            }
            if (Surgery.Tools.CarriedBy(forceps) is not { } carried)
            {
                Note("forceps didn't take a pad");
                continue;
            }
            if (dish.Fill <= 0.05f)
            {
                await PlayerPutsDown();
                await RefillDish(dish);
                await PlayerRequestsItem("forceps");
            }
            await PlayerWalksTo(dish.GlobalPosition);
            await PlayerWorksAt(dish.Middle(), 0, 1f);
            Note($"pad dipped: {carried.Fill:0.00}, dish {dish.Fill:0.00}");
            // Rows across the site, as long as the pad holds iodine.
            await PlayerWalksTo(SitePoint(new Vector2(0.5f, 0.5f)));
            for (; carried.Fill > 0.05f && row < rows; row++)
            {
                var v = (row + 0.5f) / rows;
                Vector3[] line = [SitePoint(new Vector2(0.02f, v)), SitePoint(new Vector2(0.98f, v))];
                await PlayerWorksAlong(row % 2 == 1 ? [.. line.Reverse()] : line, 0, 0.06f);
            }
        }
        // Pressed in the air, the forceps let the pad fall.
        if (Surgery.Tools.CarriedBy(forceps) is not null)
        {
            var spot = SurgeryState.FreeTraySpot(Surgery);
            await PlayerWalksTo(spot);
            await PlayerReaches(spot);
            Use();
            await Frames.Physics(3);
            Use(false);
        }
        await PlayerPutsDown();
        Note($"site sanitized: {Patient.SanitizedFraction():0.00}");
    }

    /// <summary>Pours iodine from the bottle into <paramref name="dish"/> and puts the bottle back.</summary>
    private async Task RefillDish(SurgicalTool dish)
    {
        await PlayerRequestsItem("iodine_bottle");
        await PlayerPoursInto(dish, 3f);
        await PlayerPutsDown();
    }

    /// <summary>The dose a player works out from the chart: <paramref name="share"/> of the right dose for this patient,
    /// in ml of <paramref name="vialId"/>.</summary>
    public float DoseMl(string vialId, float share = 1f)
    {
        var vial = Db.Tool(vialId)!;
        return Db.Drug(vial.Drug)!.Dose * Patient.WeightKg * share / vial.Concentration;
    }

    /// <summary>
    /// Draws <paramref name="ml"/> of a vial into a syringe big enough for it and gives it: Drip pushes it into the IV
    /// bag (it runs down the line), Vein straight into the forearm vein, Arm into the forearm muscle beside it, Tissue
    /// into the site's skin at <paramref name="at"/> (both direct injections).
    /// </summary>
    public async Task PlayerGivesDrug(string vialId, float ml, Route route, Vector2? at = null)
    {
        Note($"gives {ml:0.0} ml of {vialId} ({route})");
        if (ml > 50f)
        {
            Note($"{ml:0} ml won't fit a syringe");
            return;
        }
        var size = ml <= 3f ? "syringe_3" : ml <= 10f ? "syringe_10" : "syringe_50";
        var syringe = await PlayerRequestsItem(size);
        if (FreeTools(vialId) is not [var vial, ..])
        {
            if (await PlayerRequestsItem(vialId) is not { } fetched)
            {
                return;
            }
            vial = fetched;
            await PlayerPutsDown(true);
            syringe = await PlayerRequestsItem(size);
        }
        if (syringe is null)
        {
            return;
        }
        // In through the cap at the vial's tip. A player waits to see the needle in before pulling the plunger back:
        // pulled outside the vial, it draws air.
        await NeedleInto(vial.TipPosition(), false);
        if (!await Frames.Until(() => Syringe.NeedleTarget(syringe, Patient) is ContainerTarget { Container: var into }
            && into == vial, 2f))
        {
            Note("the needle didn't go into the vial");
            return;
        }
        var into = Syringe.NeedleTarget(syringe, Patient);
        for (var i = 0; i < Mathf.CeilToInt(ml); i++)
        {
            await Notch(false);
        }
        Note($"drew {syringe.Ml:0.0} ml and {syringe.Air:0.0} ml of air (needle in {into})");
        await NeedleOut();
        var target = route switch
        {
            Route.Drip => Surgery.Tools.DripBag()!.Middle(),
            Route.Vein => VeinPoint(),
            Route.Arm => BesideVein(0.025f),
            _ => SitePoint(at ?? new Vector2(0.5f, 0.5f)),
        };
        await NeedleInto(target, route != Route.Drip);
        if (!await NeedleIsIn(syringe))
        {
            Note($"the needle didn't go in ({route})");
            return;
        }
        into = Syringe.NeedleTarget(syringe, Patient);
        var notches = Mathf.CeilToInt(syringe.Ml + syringe.Air) + 1;
        for (var i = 0; i < notches; i++)
        {
            await Notch(true);
        }
        Note($"pushed into {into}, {syringe.Ml:0.0} ml left");
        await NeedleOut();
        await Frames.Physics(10);
        await PlayerPutsDown();
    }

    /// <summary>Waits until <paramref name="syringe"/>'s needle is in something: the bag, a vein or tissue.</summary>
    private Task<bool> NeedleIsIn(SurgicalTool syringe) =>
        Frames.Until(() => Syringe.NeedleTarget(syringe, Patient) is not AirTarget, 2f);

    /// <summary>Where a drug goes in, see <see cref="PlayerGivesDrug"/>.</summary>
    public enum Route { Drip, Vein, Arm, Tissue }

    /// <summary>The drip when there's a working IV line, the vein otherwise.</summary>
    public Route IvOrVein => Patient.IvWorking ? Route.Drip : Route.Vein;

    /// <summary>Brings the active hand's needle to <paramref name="point"/>. <paramref name="pressed"/>: Use tool
    /// pushes it in there (skin, a vein); otherwise it just rests in a vial or bag.</summary>
    public async Task NeedleInto(Vector3 point, bool pressed)
    {
        var hand = Me.Hands[Me.Active];
        await PlayerWalksTo(point);
        // Pointed straight at it: turned in, a long syringe would lie across whatever is beside a vial.
        await PlayerAimsStraight();
        for (var i = 0; i < 40; i++)
        {
            // Aimed the hand's own way: a vial or the bag it's over snaps the needle in.
            hand.LocalTarget = Me.ToLocal(point - Me.OwnTipOffset(Me.Active) + (Vector3.Up * 0.04f));
            await Frames.NextPhysics();
        }
        if (pressed)
        {
            Use();
            await Frames.Physics(30);
        }
    }

    public async Task NeedleOut()
    {
        Use(false);
        Me.Hands[Me.Active].LocalTarget += new Vector3(0f, 0.15f, 0.1f);
        await Frames.Physics(20);
    }

    /// <summary>A point on the forearm <paramref name="off"/> meters across it from the middle of the vein.</summary>
    private Vector3 BesideVein(float off)
    {
        var vein = Body.Veins[0];
        var line = vein.Line;
        var middle = line.Length / 2;
        var across = vein.GlobalBasis * (line[middle + 1] - line[middle - 1]);
        return VeinPoint() + (across.Cross(Vector3.Up).Normalized() * off);
    }

    /// <summary>The middle of the forearm vein (world space), where an IV catheter or a needle goes in.</summary>
    public Vector3 VeinPoint()
    {
        var vein = Body.Veins[0];
        return vein.ToGlobal(vein.Line[vein.Line.Length / 2]);
    }

    /// <summary>Puts an IV catheter into the forearm vein (the arm away from the site when the site is a forearm).
    /// </summary>
    public async Task PlayerSetsIv()
    {
        Note("sets an IV line");
        // An awake patient in pain jerks their arm: settled first, or the catheter misses the vein, and a missed line
        // can't be put right.
        if (Patient.Vitals.IsAwake && Mathf.Max(Patient.Vitals.Pain, Patient.Vitals.Panic) > 0.4f)
        {
            await PlayerCalmsPatient();
        }
        if (await PlayerRequestsItem("iv_catheter") is not { } catheter)
        {
            return;
        }
        var hand = Me.Hands[Me.Active];
        await PlayerWalksTo(VeinPoint());
        // Following the vein (an awake patient's arm shifts as they flinch and tense up), and pushing in once the tip
        // is over it, still following it while the catheter goes in.
        for (var i = 0; i < 240 && !Patient.IvSet; i++)
        {
            hand.LocalTarget = Me.ToLocal(VeinPoint() - hand.TipOffset(catheter.Def.Length) + (Vector3.Up * 0.04f));
            await Frames.NextPhysics();
            if (i >= 30 && !hand.Trigger && OverVein(catheter.TipPosition()))
            {
                Use();
            }
        }
        Use(false);
        await Frames.Physics(10);
        Note($"IV in: {Patient.IvSet}, in the vein: {Patient.IvInVein}");
        await PlayerPutsDown();
    }

    /// <summary>The vein is right under <paramref name="tip"/>, on the skin below it: what a player sees from above
    /// before pushing a needle in.</summary>
    private bool OverVein(Vector3 tip)
    {
        var skin = Me.SurfaceBelow(tip);
        return skin.Found && Body.VeinAt(tip with { Y = skin.Y });
    }

    /// <summary>A painkilling sedative from the scenario's kit (ketamine, else morphine) into the forearm muscle, then
    /// a moment for it to work.</summary>
    public async Task PlayerCalmsPatient()
    {
        foreach (var vial in (string[])["vial_ketamine", "vial_morphine"])
        {
            if (Surgery.Scenario.StartingTools.Contains(vial) || FreeTools(vial).Count > 0)
            {
                Note($"calms the patient with {vial}");
                await PlayerGivesDrug(vial, DoseMl(vial, 0.5f), Route.Arm);
                await Frames.Until(() => Mathf.Max(Patient.Vitals.Pain, Patient.Vitals.Panic) < 0.3f, 30f);
                return;
            }
        }
    }

    /// <summary>General anesthesia from the scenario's own vial (propofol or ketamine): down the IV line if there's
    /// one, straight into the vein otherwise.</summary>
    public async Task PlayerAnesthetizes()
    {
        var vial = ((string[])["vial_propofol", "vial_ketamine"])
            .FirstOrDefault(id => FreeTools(id).Count > 0 || Surgery.Scenario.StartingTools.Contains(id), "vial_propofol");
        await PlayerGivesDrug(vial, DoseMl(vial), IvOrVein);
        await Frames.Until(() => Patient.Vitals.Anesthesia >= 0.7f, 30f);
        Note($"anesthesia {Patient.Vitals.Anesthesia:0.00}");
    }

    /// <summary>Lidocaine into the skin by the site's wounds (or its middle).</summary>
    public async Task PlayerNumbsSite()
    {
        await PlayerGivesDrug("vial_lidocaine", DoseMl("vial_lidocaine"), Route.Tissue, WorkUv());
        await Frames.Until(() => Patient.Vitals.LocalBlock >= 0.5f, 20f);
        Note($"local block {Patient.Vitals.LocalBlock:0.00}");
    }

    /// <summary>An injection for an "inject" objective: a drug from its vial into the IV line or the vein, or an
    /// antibiotic.</summary>
    public async Task PlayerInjects(ObjectiveStep step)
    {
        var parameters = step.Parameters;
        var vial = parameters.String("flag") == "antibiotic" ? "vial_cefazolin" : "vial_" + parameters.String("drug");
        await PlayerGivesDrug(vial, DoseMl(vial), IvOrVein);
        await Frames.Seconds(2f);
    }

    /// <summary>Where the work is: the first target still in, else the first wound, else the site's middle (uv).
    /// </summary>
    private Vector2 WorkUv() =>
        Patient.Targets.FirstOrDefault(target => !target.Extracted)?.Uv
        ?? Patient.Wounds.FirstOrDefault(wound => !wound.IsInternal)?.Midpoint
        ?? new Vector2(0.5f, 0.5f);
}
