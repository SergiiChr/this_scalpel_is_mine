namespace Scalpel.Tools;

/// <summary>What the hand holding a tool does this frame: lowered onto its spot, Use tool held, the wheel's effort
/// level, how fast the hand moves, whose hand and their quirks.</summary>
public readonly record struct HandInput(bool Lowered, bool Trigger, int Level, float Speed, int Peer, Modifiers Mods);

/// <summary>
/// One frame of a held tool at work: what the hand does and what just changed (pressed, released, a higher level, come
/// down onto something), how hard it works and what its tip touches.
/// </summary>
public sealed class ToolStep(SurgicalTool tool, HandInput hand, Patient patient, float dt)
{
    public SurgicalTool Tool { get; } = tool;
    public HandInput Hand { get; } = hand;
    public Patient Patient { get; } = patient;
    public float Dt { get; } = dt;
    /// <summary>Use tool went down: it counts a frame later, once the tool has come down onto whatever is under it
    /// (hovering, a tool can be just out of touch of a rounded limb).</summary>
    public bool Pressed { get; init; }
    public bool Released { get; init; }
    public bool LevelUp { get; init; }
    /// <summary>Lowered since the last frame too: the hand has come down onto whatever is under it by now.</summary>
    public bool Settled { get; init; }
    public Vector3 Tip { get; init; }
    public SiteProbe Probe { get; init; }

    public ToolDef Def => Tool.Def;
    public bool Lowered => Hand.Lowered;
    public bool Trigger => Hand.Trigger;
    public int Level => Hand.Level;
    /// <summary>Powered and pressed tools work harder at higher levels.</summary>
    public float Effort => Hand.Level / 3f;
    public SiteZone Zone => Probe.Zone;
    public Vector2 Uv => Probe.Uv;
    /// <summary>The tip is on the patient: the site, an opening or another part of the body.</summary>
    public bool Touching => Zone is SiteZone.Site or SiteZone.Cavity or SiteZone.Body;
    /// <summary>The tip is on the surgical site or in an opening.</summary>
    public bool InSite => Zone is SiteZone.Site or SiteZone.Cavity;
    public Surgery Surgery => Surgery.Current!;
    public ToolManager Tools => Surgery.Tools;
}

/// <summary>What one kind of tool does (data/tools.cfg "action"). Tools share actions: a lighter and a cautery pen both
/// cauterize, with different <see cref="ToolDef"/> numbers.</summary>
public abstract class ToolAction
{
    private static readonly Dictionary<string, ToolAction> Actions = new()
    {
        ["cut"] = new CutAction(),
        ["clamp"] = new ClampAction(),
        ["sew"] = new SewAction(),
        ["spread"] = new SpreadAction(),
        ["staple"] = new StapleAction(),
        ["suture"] = new SutureAction(),
        ["cauterize"] = new CauterizeAction(),
        ["mark"] = new MarkAction(),
        ["inject"] = new InjectAction(),
        ["syringe"] = new SyringeAction(),
        ["shock"] = new ShockAction(),
        ["saw"] = new SawAction(),
        ["smash"] = new SmashAction(),
        ["suction"] = new SuctionAction(),
        ["swab"] = new SwabAction(),
        ["pour"] = new PourAction(),
        ["tourniquet"] = new TourniquetAction(),
        ["graft"] = new GraftAction(),
        ["iv_line"] = new IvLineAction(),
    };

    /// <summary>The action of this name, null for tools that do nothing of their own (dishes, vials, bags).</summary>
    public static ToolAction? For(string action) => Actions.GetValueOrDefault(action);

    /// <summary>Works the tool for one frame. Host only.</summary>
    public abstract void Apply(ToolStep step);
}

/// <summary>
/// What tools do to the patient, host only: <see cref="Update"/> runs every physics frame for held tools and
/// <see cref="UpdateStanding"/> for tools left holding on. Each action is a <see cref="ToolAction"/>; what they share
/// (aim geometry, contact, blood, wiping) is here.
/// </summary>
public static class ToolActions
{
    /// <summary>How each action is controlled. Use tool (LMB, held) lowers every tool onto its spot and presses its
    /// trigger. Actions listed here take an effort level from the wheel (0 does nothing, 3 the most), named by the value.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LevelNames = new Dictionary<string, string>
    {
        ["cut"] = "Depth", ["suture"] = "Tension", ["cauterize"] = "Heat", ["saw"] = "Speed", ["suction"] = "Suction",
        ["swab"] = "Pressure", ["inject"] = "Plunger",
    };
    /// <summary>Actions listed here do their thing the moment Use tool is pressed (or while held), named by the value.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> TriggerNames = new Dictionary<string, string>
    {
        ["clamp"] = "Pinch / let go", ["smash"] = "Strike", ["tourniquet"] = "Tighten", ["graft"] = "Place graft",
        ["shock"] = "Charge (hold), let go to shock", ["sew"] = "Stitch", ["spread"] = "Set in / take out", ["staple"] = "Staple",
    };
    /// <summary>Cut depth per level (0 just rests on the skin, 3 deep). 0.7+ goes through the skin.</summary>
    public static readonly float[] DepthByLevel = [0f, 0.3f, 0.6f, 1f];
    public const float DefibChargeTime = 2f;
    /// <summary>How long a cut the point of a blade makes pressed straight in (meters).</summary>
    public const float StabLength = 0.006f;
    /// <summary>A blade only cuts along its edge: a move further off the edge line than this (cosine) just drags it.
    /// </summary>
    public const float AlongBlade = 0.8f;
    /// <summary>Clamps that can pinch a cotton pad.</summary>
    public static readonly IReadOnlySet<string> PadHolders = new HashSet<string> { "forceps", "hemostat" };
    /// <summary>How close to the pad a clamp's tip has to be to pinch it.</summary>
    public const float PadReach = 0.04f;
    /// <summary>Clamps that only hook skin, the edge of a cut: never what lies under it in the opening.</summary>
    public static readonly IReadOnlySet<string> SkinHooks = new HashSet<string> { "retractor" };
    /// <summary>ml of iodine a cotton pad soaks up from a dish.</summary>
    public const float PadMl = 10f;
    /// <summary>A soaked pad runs dry after 1 / PadDrain seconds of wiping.</summary>
    public const float PadDrain = 0.12f;
    /// <summary>How fast drugs pushed into the IV bag run down the line into the patient (ml/s).</summary>
    public const float DripRate = 2f;
    /// <summary>A syringe has its own wheel instead of an effort level: one notch moves the plunger this many ml (see
    /// <see cref="Syringe.Plunge"/>).</summary>
    public const float PlungerStep = 1f;
    /// <summary>A needle's wheel works the free end of its thread: one notch changes its tension (a rest length ratio,
    /// see TissueSim.ThreadClosed) this much, down tightens and up loosens.</summary>
    public const float SutureTensionStep = 0.04f;
    public static readonly Vector2 SutureTensionRange = new(0.52f, 1.56f);
    /// <summary>Use tool held this long (seconds) adds the last hole and ties the thread off.</summary>
    public const float SutureTieHold = 0.65f;
    /// <summary>A spreader's (the Gelpi retractor's) wheel opens and closes it: one notch moves its tips this much
    /// further apart (meters), between closed and fully open (SpreadRange). Closed, its points still sit SpreadRange.X
    /// apart (GELPI_CLOSED in tools/assetgen/instruments.py).</summary>
    public const float SpreadStep = 0.005f;
    public static readonly Vector2 SpreadRange = new(0.012f, 0.08f);
    /// <summary>Steps (meters) a stapler's leg over an opening reaches out in for its edge (StapleLegs()).</summary>
    public const float StapleReachStep = 0.0005f;
    /// <summary>How deep (meters) a spreader's points reach into a cut: they hang GELPI_DROP
    /// (tools/assetgen/instruments.py) under its arms, which stop on the skin.</summary>
    public const float SpreadReach = 0.012f;
    /// <summary>Wipes paint big soft disks: at most this often, or once the tool moved PaintMove (uv) since the last
    /// one.</summary>
    public const float PaintInterval = 1f / 15f;
    public const float PaintMove = 0.02f;

    /// <summary>Where a blade's edge runs on the skin: where the blade plane meets a flat surface, so rotating the tool
    /// turns it.</summary>
    public static Vector3 BladeDirection(SurgicalTool tool)
    {
        var edge = tool.GlobalBasis.X.Cross(Vector3.Up);
        if (edge.Length() < 0.2f)
        {
            edge = -tool.GlobalBasis.Z * new Vector3(1, 0, 1);
        }
        return edge.Normalized();
    }

    /// <summary>Which way a spreader opens, or a stapler's legs lie, across the floor: along the tool's own X axis,
    /// where a spreader's jaws swing apart and a stapler's head is wide. Square to a blade's edge
    /// (<see cref="BladeDirection"/>), so turning the tool turns it the same way.</summary>
    public static Vector3 SideAxis(SurgicalTool tool) => Vector3.Up.Cross(BladeDirection(tool));

    /// <summary>Two points <paramref name="apart"/> meters apart about the tool's tip along <see cref="SideAxis"/>: a
    /// spreader's tips, a stapler's legs. The one toward -X first.</summary>
    public static Vector3[] SidePoints(SurgicalTool tool, float apart)
    {
        var half = SideAxis(tool) * apart * 0.5f;
        return [tool.TipPosition() - half, tool.TipPosition() + half];
    }

    /// <summary>
    /// Where a stapler's legs go in (world): <see cref="ToolDef.StapleSpan"/> apart across it about its tip
    /// (<see cref="SidePoints"/>), each one that would land in an opening moved out up to
    /// <see cref="ToolDef.StapleGive"/> to its edge. One that can't reach an edge stays in the opening, and the staple
    /// won't take.
    /// </summary>
    public static Vector3[] StapleLegs(SurgicalTool tool, PatientBody body)
    {
        var legs = SidePoints(tool, tool.Def.StapleSpan);
        for (var side = 0; side < 2; side++)
        {
            var outward = SideAxis(tool) * (side == 1 ? StapleReachStep : -StapleReachStep);
            var leg = legs[side];
            for (var i = 0; i <= (int)(tool.Def.StapleGive / StapleReachStep); i++)
            {
                if (!body.Tissue.IsOpen(body.WorldToUv(leg), TissueDepth.Skin))
                {
                    legs[side] = leg;
                    break;
                }
                leg += outward;
            }
        }
        return legs;
    }

    /// <summary>Why a staple with its legs at a and b (uv) didn't go in, for the one who pressed.</summary>
    public static string StapleMiss(TissueSim tissue, Vector2 a, Vector2 b)
    {
        if (tissue.IsOpen(a, TissueDepth.Skin) || tissue.IsOpen(b, TissueDepth.Skin))
        {
            return "The edges are too far apart: a leg lands in the opening.";
        }
        return tissue.CutBetween(a, b).X < 0f ? "Put the two rings across the cut, one on each edge." : "It's already closed there.";
    }

    /// <summary>The tool is doing its job right now (for animation and fingers), not only resting on something.
    /// </summary>
    public static bool InUse(string action, bool lowered, bool trigger, int level)
    {
        if (TriggerNames.ContainsKey(action))
        {
            return trigger;
        }
        return lowered && (level > 0 || !LevelNames.ContainsKey(action));
    }

    /// <summary>Works a held tool for one physics frame: works out what the hand just did, what the tip touches, and
    /// hands that to the tool's action.</summary>
    public static void Update(SurgicalTool tool, HandInput hand, Patient patient, float dt)
    {
        var use = tool.Use;
        var tip = tool.TipPosition();
        var step = new ToolStep(tool, hand, patient, dt)
        {
            // Use lowers the tool and presses its trigger at once: the press counts a frame later, once the tool has come
            // down onto whatever is under it (hovering, a tool can be just out of touch of a rounded limb).
            Pressed = hand.Trigger && use.TriggerBefore && !use.PressedBefore,
            Released = !hand.Trigger && use.TriggerBefore,
            LevelUp = hand.Level > use.LevelBefore,
            Settled = hand.Lowered && use.LoweredBefore,
            Tip = tip,
            Probe = patient.Body.Probe(tip),
        };
        use.PressedBefore = hand.Trigger && use.TriggerBefore;
        use.TriggerBefore = hand.Trigger;
        use.LevelBefore = hand.Level;
        if (hand.Lowered && !use.LoweredBefore)
        {
            use.Stroke++;
            use.StabbedLevel = 0;
            use.StapleAim = StapleLegs(tool, patient.Body);
        }
        use.LoweredBefore = hand.Lowered;
        if (hand.Lowered && step.Touching)
        {
            OnContact(step);
            Bloody(step);
        }
        if (hand.Lowered && step.InSite)
        {
            ContactSound(tool, hand.Speed, hand.Level, step.Effort, tip);
        }
        ToolAction.For(tool.Def.Action)?.Apply(step);
    }

    /// <summary>
    /// Standing (self-retaining) clamps keep holding their grip after the hand lets go: where their tip is, or for one
    /// lying on the skin where the hand left the skin it holds (<see cref="SkinHold.Hold"/>, in the site's space, see
    /// ToolManager.LeaveStanding()).
    /// </summary>
    public static void UpdateStanding(SurgicalTool tool, Patient patient, float dt)
    {
        if (tool.Def.Action == "drip")
        {
            Syringe.Drip(tool, patient, dt);
        }
        if (tool.Hold is { } hold)
        {
            var at = hold is SkinHold { Hold: { } held } ? patient.Body.Site.ToGlobal(held) : tool.TipPosition();
            tool.Hold = patient.UpdateGrip(tool.Uid, hold, at, tool.Def.Power, dt, 0f);
        }
    }

    /// <summary>A cotton pad soaks up iodine in any dish, then leaves it on the skin until it runs dry. Iodine only
    /// stays sterile on the way in if a clean pad is held with forceps: a glove on it spoils the site.</summary>
    public static void Wipe(SurgicalTool pad, ToolStep step, float dt, bool gloved)
    {
        var tools = step.Tools;
        // On the patient it always wipes, even with a dish left right beside the site.
        if (!step.InSite)
        {
            var dish = tools.NearestDish(step.Tip);
            var iodine = dish?.Contents.GetValueOrDefault("iodine") ?? 0f;
            var soak = Mathf.Min(Mathf.Min(dt * 2f, 1f - pad.Fill), iodine / PadMl);
            if (soak > 0f && dish is not null)
            {
                tools.SetFill(pad, pad.Fill + soak);
                // What's mixed in with the iodine (blood) comes along in its share.
                tools.Transfer(dish, null, soak * PadMl * dish.Ml / iodine);
            }
            return;
        }
        var soaked = pad.Fill > 0f;
        var wiped = Gather(pad, step.Uv, dt);
        if (wiped > 0f)
        {
            step.Patient.SwabAt(step.Zone, step.Uv, pad.Def, wiped, soaked ? "iodine" : "");
        }
        if (soaked)
        {
            tools.SetFill(pad, pad.Fill - PadDrain * dt);
            if ((gloved || !pad.Sterile) && step.Zone == SiteZone.Site && pad.Use.Reported.Add("dirty"))
            {
                step.Patient.ContaminateSite("");
                step.Surgery.Scoring.Add("dirty_tool");
            }
        }
    }

    /// <summary>Collects wiping time and returns it once it's worth painting (0 until then), so a pad held still or
    /// moved slowly paints a few times a second instead of every physics frame.</summary>
    public static float Gather(SurgicalTool tool, Vector2 uv, float dt)
    {
        var use = tool.Use;
        use.PaintDt += dt;
        if (use.PaintDt < PaintInterval && use.PaintUv.DistanceTo(uv) < PaintMove)
        {
            return 0f;
        }
        var gathered = use.PaintDt;
        use.PaintDt = 0f;
        use.PaintUv = uv;
        return gathered;
    }

    /// <summary>Takes one use of an injection or a catheter, consuming it on its last one.</summary>
    public static void UseCharge(SurgicalTool tool)
    {
        if (tool.Charges <= 0)
        {
            return;
        }
        tool.Charges--;
        if (tool.Charges == 0 && tool.Def.Action is "inject" or "iv_line")
        {
            Surgery.Current!.Tools.Consume(tool);
        }
    }

    /// <summary>Takes one use of a tool with limited uses (tape, thread), never going below none.</summary>
    public static void SpendCharge(SurgicalTool tool)
    {
        if (tool.Charges > 0)
        {
            tool.Charges--;
        }
    }

    /// <summary>A looping bed under a blade, swab, clamp or suction tip working the site, louder the faster it moves.
    /// It plays even when nothing gets cut: the one-shots (cut, sizzle, saw, slurp) mark the actual effect.</summary>
    private static void ContactSound(SurgicalTool tool, float speed, int level, float effort, Vector3 tip)
    {
        (string Id, float Strength)? sound = tool.Def.Action switch
        {
            "cut" when level > 0 && speed > 0.015f => ("contact_cut", Mathf.Clamp(speed * 2.5f, 0.2f, 1f)),
            "swab" when level > 0 && speed > 0.01f => ("contact_swab", Mathf.Clamp(speed * 2f, 0.15f, 0.75f)),
            "clamp" when tool.Hold is not null && speed > 0.01f => ("contact_swab", Mathf.Clamp(speed * 1.5f, 0.15f, 0.6f)),
            "suction" when level > 0 => ("contact_suction", effort),
            _ => null,
        };
        if (sound is { } contact)
        {
            Surgery.Current!.ContactSound(tool.Uid, contact.Id, tip, contact.Strength);
        }
    }

    private static void OnContact(ToolStep step)
    {
        var tool = step.Tool;
        if (step.Zone == SiteZone.Body)
        {
            step.Patient.Touch();
            return;
        }
        if (!tool.Sterile && tool.Def.Action != "swab" && tool.Use.Reported.Add("dirty"))
        {
            step.Patient.ContaminateSite("");
            step.Surgery.Scoring.Add("dirty_tool");
        }
        if (tool.Def.Improvised && tool.Use.Reported.Add("improvised"))
        {
            step.Surgery.Scoring.Add("improvised_tool");
        }
    }

    /// <summary>Working in blood leaves it on the tool: blades, clamps and suction pick it up fast, gauze soaks it up.
    /// A blade comes away bloody from skin it has cut, not from resting on whole skin.</summary>
    private static void Bloody(ToolStep step)
    {
        var body = step.Patient.Body;
        var onSite = step.Zone == SiteZone.Site;
        var wet = onSite ? body.BloodAt(step.Uv) : 0f;
        var cut = step.Def.Action == "cut" && onSite && body.WoundMap.Value(WoundMap.Layer.Wounds, WoundMap.Cut, step.Uv) > 0.1f;
        if (step.Zone == SiteZone.Cavity || cut)
        {
            wet = Mathf.Max(wet, 0.6f);
        }
        if (wet > 0.15f)
        {
            var rate = step.Def.Action == "swab" ? 1.2f : 0.5f;
            step.Tools.AddBlood(step.Tool, wet * rate * step.Dt);
        }
    }
}
