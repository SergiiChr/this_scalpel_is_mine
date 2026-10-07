namespace Scalpel.Tools;

/// <summary>What a syringe's needle is in, the same on every peer. The tip resting just above something counts as in
/// it.</summary>
public abstract record NeedleTarget;

/// <summary>A vial, a dish or the IV drip.</summary>
public sealed record ContainerTarget(SurgicalTool Container) : NeedleTarget;

/// <summary>A forearm vein drawn on the patient.</summary>
public sealed record VeinTarget : NeedleTarget;

/// <summary>The patient's tissue: the deepest layer a cut shows there ("skin", "fat", "muscle" or "cavity").</summary>
public sealed record TissueTarget(string Layer) : NeedleTarget;

/// <summary>A surgeon: a glove (the other one of the hand holding it too) or a partner's body, at a point.</summary>
public sealed record SurgeonTarget(int Peer, string Part, Vector3 At) : NeedleTarget;

/// <summary>Nothing: the air.</summary>
public sealed record AirTarget : NeedleTarget;

/// <summary>Syringes and the IV drip, host only: what the needle is in, the plunger drawing and pushing liquid, and the
/// drip running what was pushed into the bag down the line.</summary>
public static class Syringe
{
    /// <summary>
    /// What a syringe's needle is in. A glove comes before the patient under it, a partner's body after.
    /// </summary>
    public static NeedleTarget NeedleTarget(SurgicalTool tool, Patient patient)
    {
        var surgery = Surgery.Current!;
        var tip = tool.TipPosition();
        if (surgery.Tools.NearestContainer(tip) is { } container)
        {
            return new ContainerTarget(container);
        }
        var holding = tool.State == ToolState.Held && surgery.Surgeons.TryGetValue(tool.Holder, out var holder)
            ? holder.Hands[tool.Slot]
            : null;
        NeedleTarget? inBody = null;
        foreach (var surgeon in surgery.Surgeons.Values)
        {
            if (surgeon.NeedlePart(tip, holding) is not { } hit)
            {
                continue;
            }
            var target = new SurgeonTarget(surgeon.PeerId, hit.Part, hit.At);
            if (hit.Part == "hand")
            {
                return target;
            }
            inBody = target;
        }
        if (patient.Body.VeinAt(tip))
        {
            return new VeinTarget();
        }
        var probe = patient.Body.Probe(tip);
        return probe.Zone switch
        {
            SiteZone.Site => new TissueTarget(patient.Body.LayerAt(probe.Uv)),
            SiteZone.Cavity => new TissueTarget("cavity"),
            SiteZone.Body => new TissueTarget("skin"),
            _ => inBody ?? new AirTarget(),
        };
    }

    /// <summary>
    /// One move of a syringe's plunger, host only: <paramref name="ml"/> &gt; 0 pulls it out, &lt; 0 pushes it in,
    /// whether or not the needle is lowered. Pulled, it draws what the needle is in: a vial's or dish's liquid, blood
    /// from a vein, or air. In skin, fat or muscle nothing comes and the plunger stays put. Pushed, the air at the needle
    /// goes first, then the liquid: into a vial (as much as fits) or the dish, a vein (blood goes back, drugs are given
    /// as IV), the tissue (given as a direct injection), a surgeon (given to them, see Surgery.DoseSurgeon()) or
    /// squirted out.
    /// </summary>
    public static void Plunge(SurgicalTool tool, float ml, Patient patient)
    {
        var tools = Surgery.Current!.Tools;
        var target = NeedleTarget(tool, patient);
        var container = (target as ContainerTarget)?.Container;
        if (ml > 0f)
        {
            Draw(tool, Mathf.Min(ml, tool.Def.Volume - tool.Ml - tool.Air), target, patient);
            return;
        }
        var air = Mathf.Min(-ml, tool.Air);
        var liquid = Mathf.Min(-ml - air, tool.Ml);
        if (container is not null)
        {
            // A vial or bag is sealed: once it's full the plunger won't push more liquid. Past a dish's rim it spills.
            var fits = Mathf.Min(liquid, container.Def.Volume - container.Ml);
            if (container.Def.Action is not ("vial" or "drip"))
            {
                tools.Transfer(tool, null, liquid - fits);
            }
            liquid = fits;
        }
        if (air > 0f)
        {
            tools.AddLiquid(tool, 0f, null, -air);
        }
        if (liquid <= 0f)
        {
            return;
        }
        var into = PushLabel(target);
        if (into != tool.PushedInto)
        {
            ReportPushed(tool);
            tool.PushedInto = into;
        }
        if (into.Length > 0)
        {
            tool.PushedMl += liquid;
            foreach (var drug in tool.Contents.Keys.Where(drug => !tool.PushedDrugs.Contains(drug)).ToList())
            {
                tool.PushedDrugs.Add(drug);
            }
        }
        Push(tool, liquid, target, patient);
    }

    /// <summary>Pulls the plunger <paramref name="amount"/> ml out: draws what the needle is in.</summary>
    private static void Draw(SurgicalTool tool, float amount, NeedleTarget target, Patient patient)
    {
        if (amount <= 0f)
        {
            return;
        }
        var tools = Surgery.Current!.Tools;
        switch (target)
        {
            case ContainerTarget { Container: var container }:
                // An emptied vial gives air.
                var drawn = Mathf.Min(amount, container.Ml);
                if (container.Def.Action == "drip")
                {
                    DrawFromBag(container, tool, drawn);
                }
                else
                {
                    tools.Transfer(container, tool, drawn);
                }
                tools.AddLiquid(tool, 0f, null, amount - drawn);
                break;
            case VeinTarget:
                patient.Vitals.BloodMl -= amount;
                tools.AddLiquid(tool, amount, new Dictionary<string, float> { ["blood"] = amount });
                break;
            case AirTarget:
                tools.AddLiquid(tool, 0f, null, amount);
                break;
        }
    }

    /// <summary>Pushes <paramref name="liquid"/> ml out into what the needle is in.</summary>
    private static void Push(SurgicalTool tool, float liquid, NeedleTarget target, Patient patient)
    {
        var surgery = Surgery.Current!;
        switch (target)
        {
            case ContainerTarget { Container: var container }:
                surgery.Tools.Transfer(tool, container, liquid);
                if (container.Def.Action == "drip")
                {
                    container.Bolus += liquid;
                }
                break;
            case VeinTarget or TissueTarget or SurgeonTarget:
                // Given as it goes in: every push adds to what's already in that body (DrugLevels).
                foreach (var (drug, amount) in surgery.Tools.Transfer(tool, null, liquid))
                {
                    if (drug == "blood")
                    {
                        // Blood pushed back into a vein is the patient's again.
                        if (target is VeinTarget)
                        {
                            patient.Vitals.BloodMl += amount;
                        }
                    }
                    else if (target is SurgeonTarget surgeon)
                    {
                        surgery.DoseSurgeon(surgeon.Peer, drug, amount);
                    }
                    else
                    {
                        patient.Administer(drug, target is VeinTarget ? DrugRoute.Vein : DrugRoute.Direct, amount);
                    }
                }
                surgery.Sound("syringe_inject", tool.TipPosition());
                break;
            default:
                surgery.Tools.Transfer(tool, null, liquid);
                break;
        }
    }

    /// <summary>Draws <paramref name="ml"/> from the IV bag into a syringe by the port at its bottom: what was pushed in
    /// there and hasn't run down the line yet (<see cref="SurgicalTool.Bolus"/>) comes first, its drugs with it, then
    /// the bag's own fluid (and its blood, if it's a blood bag, all through it).</summary>
    public static void DrawFromBag(SurgicalTool bag, SurgicalTool syringe, float ml)
    {
        var fromBolus = Mathf.Min(ml, bag.Bolus);
        var moved = new Dictionary<string, float>();
        foreach (var drug in bag.Contents.Keys.ToList())
        {
            var share = drug == "blood" ? ml / bag.Ml : bag.Bolus > 0f ? fromBolus / bag.Bolus : 0f;
            moved[drug] = bag.Contents[drug] * share;
            bag.Contents[drug] -= moved[drug];
        }
        bag.Bolus -= fromBolus;
        var tools = Surgery.Current!.Tools;
        tools.AddLiquid(bag, -ml);
        tools.AddLiquid(syringe, ml, moved);
    }

    /// <summary>Debug mode: where a syringe's needle pushes liquid, as "Injected 5 ml of Atropine into the vein" names
    /// it. Empty for the air.</summary>
    public static string PushLabel(NeedleTarget target) => target switch
    {
        ContainerTarget { Container.Def: var def } => def.Action switch
        {
            "drip" => "the IV bag",
            "vial" => "the vial",
            _ => "the " + def.Name.ToLowerInvariant(),
        },
        VeinTarget => "the vein",
        TissueTarget tissue => "the " + tissue.Layer,
        SurgeonTarget surgeon =>
            $"{Surgery.Current?.Surgeons.GetValueOrDefault(surgeon.Peer)?.DisplayName ?? "someone"}'s {surgeon.Part}",
        _ => "",
    };

    /// <summary>Debug mode: tells how much of what a syringe pushed where its needle was, once it's somewhere else.
    /// </summary>
    public static void ReportPushed(SurgicalTool tool)
    {
        if (tool.PushedMl <= 0f)
        {
            return;
        }
        var surgery = Surgery.Current!;
        if (tool.PushedInto == "the vein")
        {
            surgery.AnnounceDebug("Hit the vein");
        }
        surgery.AnnounceDebug($"Injected {tool.PushedMl:0.#} ml{DrugList(tool.PushedDrugs)} into {tool.PushedInto}");
        tool.PushedMl = 0f;
        tool.PushedDrugs.Clear();
        tool.PushedInto = "";
    }

    /// <summary>" of Atropine, Saline" for the given drug ids, empty for none.</summary>
    private static string DrugList(IReadOnlyCollection<string> drugs) =>
        drugs.Count > 0 ? " of " + string.Join(", ", drugs.Select(drug => Db.Drug(drug)?.Name ?? drug)) : "";

    /// <summary>
    /// The IV drip runs what was pushed into it (<see cref="SurgicalTool.Bolus"/>) down the line at DripRate from the
    /// moment it's in, if the line is in a vein, each bit given as it reaches the patient (it adds up there, see
    /// DrugLevels). Its own fluid just drips (it does nothing), and so does blood in it. Debug mode tells each ml that
    /// reaches the patient.
    /// </summary>
    public static void Drip(SurgicalTool bag, Patient patient, float dt)
    {
        if (bag.Bolus <= 0f || !patient.IvWorking)
        {
            return;
        }
        // In whole ml steps, so each one is told as it reaches the patient.
        var ml = Mathf.Min(Mathf.Min(ToolActions.DripRate * dt, bag.Bolus), 1f - bag.DrippedMl);
        var share = ml / bag.Bolus;
        var drugs = bag.Contents.Keys.Where(drug => drug != "blood").ToList();
        foreach (var drug in drugs)
        {
            var amount = bag.Contents[drug] * share;
            bag.Contents[drug] -= amount;
            patient.Administer(drug, DrugRoute.Iv, amount);
        }
        bag.Bolus -= ml;
        bag.DrippedMl += ml;
        bag.DrippedTotal += ml;
        var surgery = Surgery.Current!;
        surgery.Tools.AddLiquid(bag, -ml);
        var done = bag.Bolus < 0.0001f;
        if (bag.DrippedMl >= 0.9999f || done)
        {
            surgery.AnnounceDebug($"{bag.DrippedMl:0.#} ml{DrugList(drugs)} reached the patient over IV ({bag.DrippedTotal:0.#} ml total)");
            bag.DrippedMl = 0f;
        }
        if (done)
        {
            bag.Bolus = 0f;
            bag.DrippedTotal = 0f;
            foreach (var drug in drugs)
            {
                bag.Contents.Remove(drug);
            }
        }
    }
}
