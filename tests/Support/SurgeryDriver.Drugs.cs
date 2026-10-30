namespace Scalpel.Tests.Support;

/// <summary>Preparing the patient: iodine, needles, drugs and the IV line.</summary>
public partial class SurgeryDriver {
    /// <summary>
    /// Fills a dish (the iodine dish unless <paramref name="dishId"/> says) from the bottle, takes a cotton pad in
    /// forceps, dips it and wipes the site row by row, dipping again whenever the pad runs dry, until
    /// <paramref name="amount"/> of the site is sanitized.
    /// </summary>
    public async Task PlayerSanitizesSite(float amount, string dishId = "iodine_dish") {
        Note("sanitizes the site");
        if (FreeTools(dishId) is not [var dish, ..]) {
            Note($"no {dishId}");
            return;
        }
        if (dish.Fill < 0.5f) {
            await RefillDish(dish);
            Note($"dish filled: {dish.Fill:0.00}");
        }
        var forceps = (await PlayerRequestsItem("forceps"))!;
        var radius = Body.MetersToUv(Db.Tool("cotton_pad")!.Radius);
        var rows = Mathf.CeilToInt(1f / (radius * 1.4f));
        var row = 0;
        for (var attempt = 0; attempt < 12 && Patient.SanitizedFraction() < amount + 0.05f && row < rows; attempt++) {
            if (Surgery.Tools.CarriedBy(forceps) is null) {
                if (FreeTools("cotton_pad").Count == 0) {
                    await PlayerRequestsItem("cotton_pad");
                    await PlayerPutsDown();
                    await PlayerRequestsItem("forceps");
                }
                var pad = FreeTools("cotton_pad")[0];
                await PlayerWalksTo(pad.GlobalPosition);
                await PlayerWorksAt(pad.GlobalPosition, 0, 0.2f);
            }
            if (Surgery.Tools.CarriedBy(forceps) is not { } carried) {
                Note("forceps didn't take a pad");
                continue;
            }
            if (dish.Fill <= 0.05f) {
                await PlayerPutsDown();
                await RefillDish(dish);
                await PlayerRequestsItem("forceps");
            }
            await PlayerWalksTo(dish.GlobalPosition);
            await PlayerWorksAt(dish.Middle(), 0, 1f);
            Note($"pad dipped: {carried.Fill:0.00}, dish {dish.Fill:0.00}");
            // Rows across the skin the drape leaves open, as long as the pad holds iodine. Lowered onto the drape's
            // frame, the pad is held off the skin, in the air, and the forceps let it go.
            await PlayerWalksTo(SitePoint(new Vector2(0.5f, 0.5f)));
            var (from, to) = (Drape.Frame + radius, 1f - Drape.Frame - radius);
            for (; carried.Contents.GetValueOrDefault("iodine") > carried.Def.Volume * 0.05f && row < rows; row++) {
                var v = (row + 0.5f) / rows;
                Vector3[] line = [SitePoint(new Vector2(from, v)), SitePoint(new Vector2(to, v))];
                await PlayerWorksAlong(row % 2 == 1 ? [.. line.Reverse()] : line, 0, 0.06f);
            }
        }
        // Pressed in the air, the forceps let the pad fall.
        if (Surgery.Tools.CarriedBy(forceps) is not null) {
            var spot = SurgeryState.FreeTraySpot(Surgery, Me.GlobalPosition);
            await PlayerWalksTo(spot);
            await PlayerReaches(spot);
            Use();
            await Frames.Physics(3);
            Use(false);
        }
        await PlayerPutsDown();
        Note($"site sanitized: {Patient.SanitizedFraction():0.00}");
    }

    /// <summary>Saline from the IV bag drawn into the 50 ml syringe and squirted into every dirty cut until it's rinsed
    /// clean, the syringe held over the cut so its stream lands in it, and refilled from the bag
    /// whenever it runs dry.</summary>
    public async Task PlayerRinsesCuts() {
        var syringe = (await PlayerRequestsItem("syringe_50"))!;
        var tilt = Me.Hands[Me.Active].Tilt;
        var shown = false;
        foreach (var cut in Patient.Wounds.Where(wound => wound.Dirty).ToList()) {
            var need = Patient.RinseMl(cut);
            for (var pushes = 0; cut.Dirty && pushes < need * 2f;) {
                if (syringe.Ml < 1f) {
                    await DrawFromDrip(syringe);
                }
                await AimsStreamAt(syringe, SitePoint(cut.Midpoint));
                for (; cut.Dirty && syringe.Ml >= 1f && pushes < need * 2f; pushes++) {
                    await Notch(true);
                    if (!shown && pushes == 2) {
                        shown = true;
                        await ShowsStreamInto(cut, syringe);
                    }
                }
                await NeedleOut();
            }
            Note($"rinsed {cut.Rinsed:0} of {need:0} ml through a cut, {(cut.Dirty ? "still dirty" : "clean")}");
        }
        // Held flatter again: the next tool takes the hand's tilt.
        await PlayerTilts(tilt);
        await PlayerPutsDown();
    }

    /// <summary>Holds <paramref name="syringe"/> over <paramref name="spot"/> and moves it until where its stream would land
    /// is right there, as a player does watching it. It's pointed steeply down, so the stream lands under its tip: held
    /// flatter, it squirts far, and the hand, held up over the patient's arm beside the site or reaching past it, would
    /// throw it short or long of the cut.</summary>
    private async Task AimsStreamAt(SurgicalTool syringe, Vector3 spot) {
        var over = spot;
        await NeedleInto(over, false);
        await PlayerTilts(SteepTilt);
        await Steadies(syringe);
        for (var i = 0; i < 4; i++) {
            var miss = (spot - ToolEffects.StreamPath(Surgery, syringe.TipPosition(), syringe.TipDirection())[^1]) with { Y = 0f };
            if (miss.Length() < 0.005f) {
                break;
            }
            over += miss;
            await NeedleInto(over, false);
            await Steadies(syringe);
        }
    }

    /// <summary>Pitches the active hand's tool to <paramref name="tilt"/> (<see cref="SurgeonHand.Tilt"/>) with Aim tool.
    /// </summary>
    private async Task PlayerTilts(float tilt) {
        var hand = Me.Hands[Me.Active];
        await PlayerAims(new Vector2(0f, (hand.Tilt - tilt) / (Surgeon.AimSensitivity * Settings.MouseSensitivity)), 1);
        PlayerLetsGoOfAim();
    }

    /// <summary>Waits until <paramref name="tool"/>'s tip has stopped moving: a hand rises and settles at its own pace.
    /// </summary>
    private static async Task Steadies(SurgicalTool tool) {
        var last = tool.TipPosition();
        await Frames.NextPhysics();
        await Frames.Until(() => {
            var moved = tool.TipPosition().DistanceTo(last);
            last = tool.TipPosition();
            return moved < 0.0003f;
        }, 2f);
    }

    /// <summary>The saline squirting from <paramref name="syringe"/> shows as one stream leaving its needle tip and
    /// landing in <paramref name="cut"/>.</summary>
    private async Task ShowsStreamInto(Wound cut, SurgicalTool syringe) {
        // The stream follows the tip on every drawn frame.
        await Frames.Process(1);
        var streams = Surgery.Effects.Streams();
        var lands = SitePoint(cut.Midpoint);
        static float Across(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();
        var into = streams.Count == 1 && streams[0].Top.DistanceTo(syringe.TipPosition()) < 0.003f
            && Across(streams[0].Bottom, lands) < 0.02f;
        AssertBool(into).OverrideFailureMessage(
            $"one stream leaves the needle tip {syringe.TipPosition()} and lands in the cut at {lands}: {string.Join(", ", streams)}")
            .IsTrue();
        await Capture("rinsing", "the syringe held over the cut, a thin pale stream of saline from its needle tip down into the cut");
    }

    /// <summary>Fills <paramref name="syringe"/> from the IV drip bag on the stand, through its port.</summary>
    private async Task DrawFromDrip(SurgicalTool syringe) {
        var bag = Surgery.Tools.DripBag()!;
        await NeedleInto(bag.Middle(), false);
        if (!await Frames.Until(() => Syringe.NeedleTarget(syringe, Patient) is ContainerTarget { Container: var into }
            && into == bag, 2f)) {
            Note("the needle didn't go into the bag");
        }
        var notches = Mathf.FloorToInt(syringe.Def.Volume - syringe.Ml - syringe.Air);
        for (var i = 0; i < notches; i++) {
            await Notch(false);
        }
        Note($"drew {syringe.Ml:0} ml of saline");
        await NeedleOut();
    }

    /// <summary>Pours iodine from the bottle into <paramref name="dish"/> and puts the bottle back.</summary>
    private async Task RefillDish(SurgicalTool dish) {
        await PlayerRequestsItem("iodine_bottle");
        await PlayerPoursInto(dish, 3f);
        await PlayerPutsDown();
    }

    /// <summary>The dose a player works out from the chart: <paramref name="share"/> of the right dose for this patient,
    /// in ml of <paramref name="vialId"/>.</summary>
    public float DoseMl(string vialId, float share = 1f) {
        var vial = Db.Tool(vialId)!;
        return Db.Drug(vial.Drug)!.Dose * Patient.WeightKg * share / vial.Concentration;
    }

    /// <summary>Called once <see cref="PlayerGivesDrug"/>'s needle is in the vial, before drawing: a test's key frame
    /// of the label beside the aim. Opt-in, so the shared flow doesn't add the same key frame to every suite.</summary>
    public Func<Task>? OnNeedleInVial { get; set; }

    /// <summary>
    /// Draws <paramref name="ml"/> of a vial into a syringe big enough for it and gives it: Drip pushes it into the IV
    /// bag (it runs down the line), Vein straight into the forearm vein, Arm into the forearm muscle beside it, Tissue
    /// into the site's skin at <paramref name="at"/> (both direct injections).
    /// </summary>
    public async Task PlayerGivesDrug(string vialId, float ml, Route route, Vector2? at = null) {
        Note($"gives {ml:0.0} ml of {vialId} ({route})");
        if (ml > 50f) {
            Note($"{ml:0} ml won't fit a syringe");
            return;
        }
        var size = ml <= 3f ? "syringe_3" : ml <= 10f ? "syringe_10" : "syringe_50";
        var syringe = await PlayerRequestsItem(size);
        if (FreeTools(vialId) is not [var vial, ..]) {
            if (await PlayerRequestsItem(vialId) is not { } fetched) {
                return;
            }
            vial = fetched;
            await PlayerPutsDown(true);
            syringe = await PlayerRequestsItem(size);
        }
        if (syringe is null) {
            return;
        }
        // In through the cap at the vial's tip. A player waits to see the needle in before pulling the plunger back:
        // pulled outside the vial, it draws air.
        await NeedleInto(vial.TipPosition(), false);
        if (!await Frames.Until(() => Syringe.NeedleTarget(syringe, Patient) is ContainerTarget { Container: var into }
            && into == vial, 2f)) {
            Note("the needle didn't go into the vial");
            return;
        }
        await Frames.Process(1);
        AssertString(Surgery.Hud.AimName)
            .OverrideFailureMessage($"the needle in the vial's cap, its label \"{vial.Def.Name}\" beside the aim")
            .IsEqual(vial.Def.Name);
        if (OnNeedleInVial is not null) {
            await Unbudgeted(OnNeedleInVial);
        }
        var into = Syringe.NeedleTarget(syringe, Patient);
        for (var i = 0; i < Mathf.CeilToInt(ml); i++) {
            await Notch(false);
        }
        Note($"drew {syringe.Ml:0.0} ml and {syringe.Air:0.0} ml of air (needle in {into})");
        await NeedleOut();
        var holds = route is Route.Vein or Route.Arm && await PlayerHoldsForearm();
        var target = route switch {
            Route.Drip => Surgery.Tools.DripBag()!.Middle(),
            Route.Vein => VeinPoint(),
            // Where the held arm lies: it may still be jerked away while the hand presses it back down.
            Route.Arm => WhereItLies(((Forearm)WorkingVein().GetParent()).BoneName, BesideVein(0.025f)),
            _ => SitePoint(at ?? new Vector2(0.5f, 0.5f)),
        };
        await NeedleInto(target, route != Route.Drip);
        var isIn = await NeedleIsIn(syringe);
        // Another surgeon's hand over the spot takes the needle: the player draws it back and tries again once the
        // hand has moved on, rather than push the drug into a colleague.
        for (var tries = 0; isIn && Syringe.NeedleTarget(syringe, Patient) is SurgeonTarget && tries < 5; tries++) {
            Note($"needle in {Syringe.NeedleTarget(syringe, Patient)}, drawn back");
            await NeedleOut();
            await Frames.Seconds(1f);
            await NeedleInto(target, route != Route.Drip);
            isIn = await NeedleIsIn(syringe);
        }
        if (isIn) {
            into = Syringe.NeedleTarget(syringe, Patient);
            var notches = Mathf.CeilToInt(syringe.Ml + syringe.Air) + 1;
            for (var i = 0; i < notches; i++) {
                await Notch(true);
            }
            Note($"pushed into {into}, {syringe.Ml:0.0} ml left");
        }
        await NeedleOut();
        if (holds) {
            await PlayerLetsGoWithFreeHand();
        }
        if (!isIn) {
            Note($"the needle didn't go in ({route})");
            return;
        }
        await Frames.Physics(10);
        await PlayerPutsDown();
    }

    /// <summary>An awake patient (whose arm shifts as they flinch and tense up, let alone thrash about) has the forearm
    /// held still with the free hand, by the wrist, so a needle or catheter goes in where its vein is. False when the
    /// patient is asleep, another surgeon holds them still already or no hand is free.</summary>
    public async Task<bool> PlayerHoldsForearm() {
        var other = 1 - Me.Active;
        if (!Patient.Vitals.IsAwake || Me.HeldTool(other) is not null || AnotherHoldsPatient) {
            return false;
        }
        // Where the arm lies on the table, not where it has jerked to: the hand presses it back down there.
        var vein = Body.Veins[0];
        var wrist = WhereItLies(((Forearm)vein.GetParent()).BoneName, vein.ToGlobal(vein.Line[^1]));
        await SwitchTo(other);
        await PlayerHoldsPatientAt(wrist);
        await SwitchTo(1 - other);
        return true;
    }

    /// <summary>Where <paramref name="point"/> (world), on the part of the body <paramref name="bone"/> moves, is with
    /// that part lying at rest, not jerked away.</summary>
    private Vector3 WhereItLies(string bone, Vector3 point) {
        var skeleton = Body.Animator.Rig!.Skeleton.GlobalTransform;
        return skeleton * Body.Animator.Rig.Moved(bone).AffineInverse() * skeleton.AffineInverse() * point;
    }

    /// <summary>A patient thrashing about (awake, in pain or panicking) has the limb the site is on held still with the
    /// free hand where it lies, on its top a little way along it from <paramref name="wound"/>, out of the needle's way.
    /// False when the patient lies still, the site isn't on a limb or no hand is free.</summary>
    private async Task<bool> PlayerHoldsThrashingSiteLimb(Wound wound) {
        var other = 1 - Me.Active;
        if (!Thrashes || Me.HeldTool(other) is not null || Body.SitePart.Length == 0) {
            return false;
        }
        var site = SitePoint(wound.Midpoint);
        var bone = Body.RigBoneAt(site);
        var middle = WhereItLies(bone, site);
        if (Body.LimbRingAt(middle) is not { } ring) {
            return false;
        }
        // Past the wound's end and the palm's half width, either way along the limb, as long as it's still that part.
        var away = (Body.UvToMeters(wound.LengthUv) * 0.5f) + HoldBesideWound;
        foreach (var along in (float[])[away, -away]) {
            var beside = middle + (ring.Axis * along);
            if (Body.RigBoneAt(beside) != bone
                || Rays.Cast(Body, beside + (Vector3.Up * 0.2f), beside + (Vector3.Down * 0.2f), PatientBody.SurfaceLayer)
                    is not { } top) {
                continue;
            }
            await SwitchTo(other);
            await PlayerHoldsPatientAt(top.Position);
            await SwitchTo(1 - other);
            return true;
        }
        return false;
    }

    /// <summary>The syringe's pitch squirting into a cut (<see cref="SurgeonHand.TiltRange"/>): nearly straight down.
    /// </summary>
    private const float SteepTilt = -1.4f;

    /// <summary>How far (meters) past a wound's end a hand holding the limb lies: the palm's half width and a little,
    /// so the needle's holes beside the wound stay clear of it.</summary>
    private const float HoldBesideWound = 0.06f;

    /// <summary>The free hand lets go of the patient if it still holds them (walking away lets go on its own).
    /// </summary>
    public async Task PlayerLetsGoWithFreeHand() {
        var other = 1 - Me.Active;
        if (!Me.HoldsPatient(other)) {
            return;
        }
        await SwitchTo(other);
        await PlayerLetsGoOfPatient();
        await SwitchTo(1 - other);
    }

    /// <summary>Another surgeon at the table holds the patient with either hand: in a network game too, where the
    /// hold is theirs and only their hands' pressed bone comes across.</summary>
    public bool AnotherHoldsPatient => Surgery.Surgeons.Values.Any(other => other != Me && other.Hands.Any(hand => hand.PressBone.Length > 0));

    /// <summary>An awake patient in pain or panicking jerks their arms.</summary>
    private bool Thrashes => Patient.Vitals.IsAwake && Mathf.Max(Patient.Vitals.Pain, Patient.Vitals.Panic) > 0.4f;

    /// <summary>Waits until <paramref name="syringe"/>'s needle is in something: the bag, a vein or tissue.</summary>
    private Task<bool> NeedleIsIn(SurgicalTool syringe) =>
        Frames.Until(() => Syringe.NeedleTarget(syringe, Patient) is not AirTarget, 2f);

    /// <summary>Where a drug goes in, see <see cref="PlayerGivesDrug"/>.</summary>
    public enum Route { Drip, Vein, Arm, Tissue }

    /// <summary>The drip when there's a working IV line, the vein otherwise.</summary>
    public Route IvOrVein => Patient.IvWorking ? Route.Drip : Route.Vein;

    /// <summary>Brings the active hand's needle to <paramref name="point"/>. <paramref name="pressed"/>: Use tool
    /// pushes it in there (skin, a vein); otherwise it just rests in a vial or bag.</summary>
    public async Task NeedleInto(Vector3 point, bool pressed) {
        var hand = Me.Hands[Me.Active];
        await PlayerWalksTo(point);
        // Pointed straight at it: turned in, a long syringe would lie across whatever is beside a vial.
        await PlayerAimsStraight();
        for (var i = 0; i < 40; i++) {
            // Aimed the hand's own way: a vial or the bag it's over snaps the needle in.
            hand.LocalTarget = Me.ToLocal(point - Me.OwnTipOffset(Me.Active) + (Vector3.Up * 0.04f));
            await Frames.NextPhysics();
        }
        if (pressed) {
            Use();
            await Frames.Physics(30);
        }
    }

    public async Task NeedleOut() {
        Use(false);
        Me.Hands[Me.Active].LocalTarget += new Vector3(0f, 0.15f, 0.1f);
        await Frames.Physics(20);
    }

    /// <summary>A point on the forearm <paramref name="off"/> meters across it from the middle of the vein.</summary>
    private Vector3 BesideVein(float off) {
        var vein = WorkingVein();
        var line = vein.Line;
        var middle = line.Length / 2;
        var across = vein.GlobalBasis * (line[middle + 1] - line[middle - 1]);
        return VeinPoint() + (across.Cross(Vector3.Up).Normalized() * off);
    }

    /// <summary>The middle of the forearm vein (world space), where an IV catheter or a needle goes in.</summary>
    private Vein WorkingVein() => StandingConstraint is null ? Body.Veins[0]
        : Body.Veins.OrderBy(vein => vein.ToGlobal(vein.Line[vein.Line.Length / 2]).DistanceSquaredTo(Me.GlobalPosition)).First();

    public Vector3 VeinPoint() {
        var vein = WorkingVein();
        return vein.ToGlobal(vein.Line[vein.Line.Length / 2]);
    }

    /// <summary>Connects the IV tubing to a catheter at the stand and puts the catheter into the forearm vein (the arm
    /// away from the site when the site is a forearm).</summary>
    public async Task PlayerSetsIv() {
        Note("sets an IV line");
        // Settled first, or the jerking arm makes the catheter miss the vein, and a missed line can't be put right.
        if (Thrashes) {
            await PlayerCalmsPatient();
        }
        if (await PlayerRequestsItem("iv_catheter") is not { } catheter) {
            return;
        }
        await PlayerConnectsIvLine();
        await PlayerPutsCatheterIn(catheter);
    }

    /// <summary>Walks up to the IV stand, looks at it and presses Interact on "Connect the IV line": the tubing goes
    /// onto the catheter in hand.</summary>
    public Task PlayerConnectsIvLine() => PlayerWorksIvStand("Connect the IV line");

    /// <summary>As <see cref="PlayerConnectsIvLine"/>, on "Disconnect the IV line": the tubing comes off the catheter in
    /// hand and is hung back on the stand.</summary>
    public Task PlayerDisconnectsIvLine() => PlayerWorksIvStand("Disconnect the IV line");

    private async Task PlayerWorksIvStand(string prompt) {
        var stand = Surgery.Room.FindChildren("*", "", true, false).OfType<Interactable>()
            .First(spot => spot.Prompt == prompt);
        await PlayerWalksTo(stand.GlobalPosition, 0.6f);
        var pitch = Me.Pitch;
        await PlayerLooksAt(stand.GlobalPosition);
        await Frames.Physics(2);
        PlayerInput.Tap(InputActions.Interact);
        await PlayerInput.Delivered();
        // Looking back up from the stand, as a player turns back to the patient: later steps work at eye level.
        PlayerInput.Mouse(new Vector2(0f, Me.Pitch - pitch) / (Surgeon.LookSensitivity * Settings.MouseSensitivity));
        await PlayerInput.Delivered();
        await Frames.Physics(5);
        Note($"{prompt}: {Surgery.Room.IvLine.End}");
    }

    /// <summary>How far (meters) the hand comes back toward the surgeon once the catheter is in: clear of the tubing.
    /// </summary>
    private const float HandBack = 0.15f;

    /// <summary>Puts the catheter in hand into the forearm vein, following it over and pushing in once the tip is over
    /// it.</summary>
    public async Task PlayerPutsCatheterIn(SurgicalTool catheter) {
        var hand = Me.Hands[Me.Active];
        await PlayerWalksTo(VeinPoint());
        var holds = await PlayerHoldsForearm();
        // Following the vein (an awake patient's arm shifts as they flinch and tense up), and pushing in once the tip
        // is over it, still following it while the catheter goes in.
        for (var i = 0; i < 240 && Patient.CanSetIv(Surgery.Room.IvLine.Catheter == catheter); i++) {
            hand.LocalTarget = Me.ToLocal(VeinPoint() - hand.TipOffset(catheter.Def.Length) + (Vector3.Up * 0.04f));
            await Frames.NextPhysics();
            if (i >= 30 && !hand.Trigger && OverVein(catheter.TipPosition())) {
                Use();
            }
        }
        Use(false);
        await Frames.Physics(10);
        Note($"IV in: {Patient.IvSet}, in the vein: {Patient.IvInVein}, on the line: {Patient.IvConnected}");
        if (holds) {
            await PlayerLetsGoWithFreeHand();
        }
        // The emptied hand comes back off the arm: left where the catheter went in, the tubing leaving the dressing can
        // come to rest on it, and the surgeon walking off would drag it along and rip the catheter out.
        if (Me.HeldTool(Me.Active) is null) {
            await PlayerReaches(Tip(hand) + ((Me.GlobalPosition - Tip(hand)).Slide(Vector3.Up).Normalized() * HandBack));
        }
        await PlayerPutsDown();
    }

    /// <summary>The vein is right under <paramref name="tip"/>, on the skin below it: what a player sees from above
    /// before pushing a needle in.</summary>
    private bool OverVein(Vector3 tip) {
        var skin = Me.SurfaceBelow(tip);
        return skin.Found && Body.VeinAt(tip with { Y = skin.Y });
    }

    /// <summary>A painkilling sedative from the scenario's kit (ketamine, else morphine) into the forearm muscle, then
    /// a moment for it to work.</summary>
    public async Task PlayerCalmsPatient() {
        foreach (var vial in (string[])["vial_ketamine", "vial_morphine"]) {
            if (Surgery.Scenario.StartingTools.Contains(vial) || FreeTools(vial).Count > 0) {
                Note($"calms the patient with {vial}");
                await PlayerGivesDrug(vial, DoseMl(vial, 0.5f), Route.Arm);
                await Frames.Until(() => Mathf.Max(Patient.Vitals.Pain, Patient.Vitals.Panic) < 0.3f, 30f);
                return;
            }
        }
    }

    /// <summary>A right dose of diazepam into the forearm muscle, then a moment for it to calm the patient below
    /// <paramref name="maxPanic"/>.</summary>
    public async Task PlayerSedates(float maxPanic) {
        await PlayerGivesDrug("vial_diazepam", DoseMl("vial_diazepam"), Route.Arm);
        await Frames.Until(() => Patient.SedativeWorks && Patient.Vitals.Panic < maxPanic, 30f);
        Note($"sedated: panic {Patient.Vitals.Panic:0.00}, awake {Patient.Vitals.IsAwake}");
    }

    /// <summary>General anesthesia from the scenario's own vial (propofol or ketamine): down the IV line if there's
    /// one, straight into the vein otherwise.</summary>
    public async Task PlayerAnesthetizes() {
        var vial = ((string[])["vial_propofol", "vial_ketamine"])
            .FirstOrDefault(id => FreeTools(id).Count > 0 || Surgery.Scenario.StartingTools.Contains(id), "vial_propofol");
        await PlayerGivesDrug(vial, DoseMl(vial), IvOrVein);
        await Frames.Until(() => Patient.Vitals.IsUnder && Patient.AnestheticWorks, 30f);
        Note($"anesthesia {Patient.Vitals.Anesthesia:0.00}");
    }

    /// <summary>Lidocaine into the skin by the site's wounds (or its middle).</summary>
    public async Task PlayerNumbsSite() {
        await PlayerGivesDrug("vial_lidocaine", DoseMl("vial_lidocaine"), Route.Tissue, WorkUv());
        await Frames.Until(() => Patient.Vitals.LocalBlock >= 0.5f, 20f);
        Note($"local block {Patient.Vitals.LocalBlock:0.00}");
    }

    /// <summary>An injection for an "inject" objective: a drug from its vial into the IV line or the vein, or an
    /// antibiotic.</summary>
    public async Task PlayerInjects(ObjectiveStep step) {
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
