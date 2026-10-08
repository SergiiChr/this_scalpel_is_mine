namespace Scalpel.Tests.Models;

/// <summary>Model contract checks: every model the game loads exists, and the rigged ones have the bones and parts the
/// animation code drives.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
public class ModelsTest
{
    private static readonly string[] PatientBones =
    [
        "Torso", "Chest", "Neck", "Head", "Jaw",
        "UpperArmL", "ForearmL", "HandL", "UpperArmR", "ForearmR", "HandR",
        "ThighL", "ShinL", "FootL", "ThighR", "ShinR", "FootR",
    ];
    private static readonly string[] PatientParts = ["EyeL", "EyeR", "Lids"];
    private static readonly string[] Organs = ["bowel", "lobe", "sac"];
    /// <summary>Most triangles any one model of a category may have. The generators aim under these
    /// (tools/blender/__main__.py BUDGETS, tools/blender/patient.py BUDGETS).</summary>
    private static readonly Dictionary<string, int> Budgets = new()
    {
        ["patient"] = 52000, ["surgeon"] = 16000, ["organs"] = 7000, ["targets"] = 5000, ["tools"] = 6000,
        ["props"] = 15000,
    };
    /// <summary>Hand positions from the shoulder for <see cref="CuffFit"/>, right hand (the left one mirrors x):
    /// working spots in front, out to the side and low, the arm stretched out past its reach and folded up to the
    /// shoulder. </summary>
    private static readonly Vector3[] CuffSpots =
    [
        new(-0.02f, -0.35f, -0.34f), new(0.2f, -0.45f, -0.22f), new(-0.14f, -0.5f, -0.42f), new(0.06f, -0.2f, -0.22f),
        new(0.2f, -0.2f, -0.8f), new(-0.02f, -0.03f, -0.04f),
    ];
    /// <summary>Where the glove's cuff starts, behind the wrist (meters, glove model space along -X).</summary>
    private const float CuffFrom = 0.035f;
    /// <summary>How far the cuff may stand off the sleeve before it reads as sticking out (meters).</summary>
    private const float CuffStandoff = 0.008f;
    /// <summary>The end of the sleeve this far into the cuff must be inside the glove (meters from the sleeve's end).
    /// </summary>
    private const float CuffInside = 0.03f;

    /// <summary>Every grip a hand takes, and the empty hand ("").</summary>
    private static IEnumerable<string> GripsAndEmpty => GripStyle.All.Keys.Prepend("");

    [TestCase]
    public async Task ModelRigGeometryAndBudgetContracts()
    {
        var holder = new Node3D();
        Frames.Root.AddChild(holder);
        Rig("patient", "body", PatientBones, PatientParts, holder);
        var gloveBones = SurgeonHand.Fingers
            .SelectMany(finger => Enumerable.Range(1, 3).Select(joint => $"{finger}{joint}"))
            .Prepend("Hand");
        Rig("surgeon", "glove", gloveBones, [], holder);
        foreach (var organ in Organs)
        {
            Exists("organs", organ);
        }
        // Every organ any site's anatomy puts in the body needs a model.
        foreach (var site in Db.PatientSites.Values.Where(site => site.VariantType == Variant.Type.Dictionary))
        {
            foreach (var organ in SiteDef.FromVariant(site.AsGodotDictionary()).Organs)
            {
                Exists("organs", organ.Model);
            }
        }
        // Every target kind any scenario uses needs a model.
        foreach (var kind in Db.Scenarios.Concat(Db.DisabledScenarios).SelectMany(scenario => scenario.Targets)
            .Select(target => target.Kind).Distinct())
        {
            Exists("targets", kind);
        }
        HandPoseLimits(holder);
        BladeTips(holder);
        BladeAnimation(holder);
        NeedleHolder(holder);
        ContactAudio();
        IvLineClearance(holder);
        CheckBudgets(holder);
        for (var handIndex = 0; handIndex < 2; handIndex++)
        {
            await GripClearance(holder, handIndex);
            HandTurn(holder, handIndex);
        }
        ArmLimits(holder);
        CuffFit(holder);
        // Let the freed contact loops and test hands go before returning.
        holder.QueueFree();
        await Frames.NextProcess();
    }

    private static string HandName(int index) => index == 0 ? "left" : "right";

    private static string Holding(string grip) => grip.Length > 0 ? "holding by " + grip : "empty";

    /// <summary>The first tool held with <paramref name="grip"/>.</summary>
    private static ToolDef FirstWithGrip(string grip) => Db.Tools.Values.First(def => def.Grip == grip);

    /// <summary>Wherever the hand works (in front, out to the side, low, near), a held tool keeps the hand turned in:
    /// the back of the hand up, or for a fist round a handle facing out to the hand's own side, never palm
    /// up.</summary>
    private static void HandTurn(Node3D holder, int handIndex)
    {
        var hand = GripCheck.MakeHand(holder, handIndex);
        var side = handIndex == 0 ? -1f : 1f;
        var outward = new Vector3(side, 0f, 0f);
        foreach (var grip in GripStyle.All.Keys)
        {
            var def = FirstWithGrip(grip);
            hand.Holding = true;
            hand.Grip = grip;
            hand.Fit = Db.GripFit(def, handIndex);
            foreach (var at in (Vector3[])[new(0.17f, 1.05f, -0.42f), new(0.4f, 0.95f, -0.3f), new(0.05f, 0.9f, -0.5f),
                new(0.25f, 1.2f, -0.3f)])
            {
                hand.Target = new Vector3(at.X * side, at.Y, at.Z);
                if (grip == "syringe")
                {
                    // A syringe is held its own way (Surgeon.FaceSyringe()): pointing in, its scale to the eyes.
                    hand.Tilt = Surgeon.SyringeTilt;
                    hand.Turn = Surgeon.SyringeTurn * side;
                    hand.GlobalPosition = hand.Target;
                    hand.Twist = hand.TwistFacing(new Vector3(0f, Surgeon.EyeHeight, 0f) - hand.Target);
                }
                else
                {
                    hand.Tilt = SurgeonHand.RestTilt;
                    hand.Turn = 0f;
                    hand.Twist = 0f;
                }
                hand.SnapPose(GripCheck.ShoulderOf(hand));
                var back = hand.Glove.GlobalBasis.Y.Normalized();
                var facing = grip == "fist" ? back.Dot(outward) : back.Dot(Vector3.Up);
                AssertFloat(facing)
                    .OverrideFailureMessage(
                        $"the {HandName(handIndex)} hand holding a {def.Id} at {hand.Target} is twisted (back of the "
                            + $"hand {back})")
                    .IsGreaterEqual(0.3f);
            }
        }
        hand.GetParent().QueueFree();
    }

    /// <summary>At the arm's limits (stretched out, folded up to the shoulder, reaching straight along the elbow's
    /// bend) both hands stay finite, and the left one is the right one mirrored.</summary>
    private static void ArmLimits(Node3D holder)
    {
        SurgeonHand[] hands = [GripCheck.MakeHand(holder, 0), GripCheck.MakeHand(holder, 1)];
        var reach = SurgeonHand.UpperArm + SurgeonHand.Forearm;
        var bend = new Vector3(0.6f, -1f, 0f).Normalized();
        var limits = new Dictionary<string, Vector3>
        {
            ["stretched"] = new Vector3(0.3f, -0.25f, -1f).Normalized() * reach * 1.2f,
            ["folded"] = new(-0.02f, -0.03f, -0.04f),
            ["along the bend"] = bend * reach * 0.6f,
        };
        foreach (var grip in GripsAndEmpty)
        {
            foreach (var (limit, offset) in limits)
            {
                var gloves = new List<Transform3D>();
                foreach (var hand in hands)
                {
                    var side = hand.Index == 0 ? -1f : 1f;
                    var shoulder = GripCheck.ShoulderOf(hand);
                    hand.Holding = grip.Length > 0;
                    hand.Grip = grip.Length > 0 ? grip : "pencil";
                    hand.Fit = GripFit.None;
                    hand.Target = shoulder + new Vector3(offset.X * side, offset.Y, offset.Z);
                    hand.SnapPose(shoulder);
                    Transform3D[] parts =
                        [hand.Glove.GlobalTransform, hand.ForeSleeve.GlobalTransform, hand.UpperSleeve.GlobalTransform];
                    AssertBool(parts.All(part => part.IsFinite()))
                        .OverrideFailureMessage($"the {HandName(hand.Index)} hand {Holding(grip)}, arm {limit}, "
                            + $"isn't finite")
                        .IsTrue();
                    gloves.Add(hand.Glove.GlobalTransform);
                }
                // Mirrored across the body's middle, the left glove lands exactly on the right one.
                var left = new Transform3D(Basis.FromScale(new Vector3(-1f, 1f, 1f)), Vector3.Zero) * gloves[0];
                AssertBool(left.Origin.DistanceTo(gloves[1].Origin) <= 0.002f
                        && left.Basis.IsEqualApprox(gloves[1].Basis))
                    .OverrideFailureMessage(
                        $"the hands {Holding(grip)}, arm {limit}, don't mirror each other (left {gloves[0]}, right "
                            + $"{gloves[1]})")
                    .IsTrue();
            }
        }
        foreach (var hand in hands)
        {
            hand.GetParent().QueueFree();
        }
    }

    /// <summary>The glove's cuff follows the forearm and wraps the sleeve: the end of the sleeve that reaches into the
    /// cuff stays inside the glove, and the cuff hugs the sleeve instead of standing off it. Both hands, empty and in
    /// every grip, wherever the hand works and at the arm's limits.</summary>
    private static void CuffFit(Node3D holder)
    {
        for (var handIndex = 0; handIndex < 2; handIndex++)
        {
            var hand = GripCheck.MakeHand(holder, handIndex);
            var side = handIndex == 0 ? -1f : 1f;
            var shoulder = GripCheck.ShoulderOf(hand);
            foreach (var grip in GripsAndEmpty)
            {
                hand.Holding = grip.Length > 0;
                hand.Grip = grip.Length > 0 ? grip : "pencil";
                hand.Fit = grip.Length > 0 ? Db.GripFit(FirstWithGrip(grip), handIndex) : GripFit.None;
                foreach (var at in CuffSpots)
                {
                    hand.Target = shoulder + new Vector3(at.X * side, at.Y, at.Z);
                    hand.SnapPose(shoulder);
                    CheckCuff(hand, $"the {HandName(handIndex)} hand {Holding(grip)} at {at}");
                }
            }
            hand.GetParent().QueueFree();
        }
    }

    private static void CheckCuff(SurgeonHand hand, string what)
    {
        // The forearm model runs along its Y from -0.5 (elbow) to 0.5 (the end in the cuff).
        var fore = hand.ForeSleeve.GlobalTransform;
        var end = fore * new Vector3(0f, 0.5f, 0f);
        var back = ((fore * new Vector3(0f, -0.5f, 0f)) - end).Normalized();
        var sleeve = Meshes(hand.ForeSleeve).Where(mesh => mesh.Visible).SelectMany(mesh => PosedFaces(mesh)).ToList();
        // Only the glove's cuff: the hand itself can bend back beside the sleeve without being part of it.
        var glove = Meshes(hand.Glove).SelectMany(mesh => PosedFaces(mesh, -CuffFrom)).ToList();
        // Under the glove means a ray from the forearm's axis out through a point of the sleeve meets the glove no
        // nearer than the sleeve's surface there. Glove triangles by 5 mm slice along the forearm, so each ray tries
        // only a few.
        var slices = new Dictionary<int, List<int>>();
        for (var t = 0; t < glove.Count; t += 3)
        {
            var low = float.PositiveInfinity;
            var high = float.NegativeInfinity;
            for (var n = 0; n < 3; n++)
            {
                var along = (glove[t + n] - end).Dot(back);
                low = Mathf.Min(low, along);
                high = Mathf.Max(high, along);
            }
            for (var slice = Mathf.FloorToInt(low / 0.005f); slice <= Mathf.FloorToInt(high / 0.005f); slice++)
            {
                if (!slices.TryGetValue(slice, out var list))
                {
                    slices[slice] = list = [];
                }
                list.Add(t);
            }
        }
        var poking = 0;
        foreach (var p in sleeve)
        {
            // The very end of the sleeve is its cap, deep inside the glove.
            var along = (p - end).Dot(back);
            if (along < 0.003f || along >= CuffInside)
            {
                continue;
            }
            var axis = end + (back * along);
            var outward = (p - axis).Normalized();
            var radius = Radius(p, end, back);
            var covered = slices.GetValueOrDefault(Mathf.FloorToInt(along / 0.005f), []).Any(t =>
                Geometry3D.RayIntersectsTriangle(axis, outward, glove[t], glove[t + 1], glove[t + 2]) is var hit
                && hit.VariantType != Variant.Type.Nil && axis.DistanceTo(hit.AsVector3()) >= radius - 0.0005f);
            if (!covered)
            {
                poking++;
            }
        }
        AssertInt(poking).OverrideFailureMessage($"the sleeve pokes through the glove's cuff, {what} ({poking} points)")
            .IsEqual(0);
        // The cuff proper (well behind the wrist, still over the sleeve) keeps close to the sleeve all round.
        var rings = SleeveRings(sleeve, end, back);
        // Past the sleeve's end there's no sleeve to hug.
        var standing = glove.Count(p => (p - end).Dot(back) is var along && along > 0.004f
            && Radius(p, end, back) > SleeveRadius(rings, along) + CuffStandoff);
        AssertInt(standing)
            .OverrideFailureMessage($"the glove's cuff sticks out from the sleeve, {what} ({standing} points)")
            .IsEqual(0);
    }

    private static IEnumerable<MeshInstance3D> Meshes(Node root) =>
        root.FindChildren("*", nameof(MeshInstance3D), true, false).Cast<MeshInstance3D>();

    /// <summary>The sleeve's rings: millimeters along it from its end -> its radius there. Its vertices all lie on a
    /// few rings.</summary>
    private static Dictionary<int, float> SleeveRings(List<Vector3> sleeve, Vector3 end, Vector3 back)
    {
        var rings = new Dictionary<int, float>();
        foreach (var q in sleeve)
        {
            var mm = Mathf.RoundToInt((q - end).Dot(back) * 1000f);
            rings[mm] = Mathf.Max(rings.GetValueOrDefault(mm), Radius(q, end, back));
        }
        return rings;
    }

    /// <summary>The sleeve's radius a distance along it: the wider of the rings on either side.</summary>
    private static float SleeveRadius(Dictionary<int, float> rings, float along)
    {
        var mm = along * 1000f;
        int? below = null;
        int? above = null;
        foreach (var at in rings.Keys)
        {
            if (at <= mm && (below is null || at > below))
            {
                below = at;
            }
            if (at >= mm && (above is null || at < above))
            {
                above = at;
            }
        }
        return Mathf.Max(below is { } low ? rings[low] : 0f, above is { } high ? rings[high] : 0f);
    }

    private static float Radius(Vector3 p, Vector3 end, Vector3 back)
    {
        var d = p - end;
        return (d - (back * d.Dot(back))).Length();
    }

    /// <summary> A mesh's triangles where it's drawn now (world space), skinned by its skeleton's current pose if it
    /// has one. Headless there's no renderer to bake a skinned pose, so this does the skinning itself. <paramref
    /// name="behind"/>: only triangles whose rest pose lies wholly behind this model-space x (the glove's cuff: behind
    /// the wrist). </summary>
    private static List<Vector3> PosedFaces(MeshInstance3D mesh, float behind = float.PositiveInfinity)
    {
        var skeleton = mesh.GetNodeOrNull<Skeleton3D>(mesh.Skeleton);
        if (mesh.Skin is null || skeleton is null)
        {
            // Riding a bone (the glove's rim on its cuff): where the bone is now, before the attachment catches up.
            var xform = mesh.GlobalTransform;
            if (mesh.GetParent() is BoneAttachment3D attachment)
            {
                var rig = attachment.GetParent<Skeleton3D>();
                xform = rig.GlobalTransform * rig.GetBoneGlobalPose(attachment.BoneIdx) * mesh.Transform;
            }
            return [.. mesh.Mesh.GetFaces().Select(vertex => xform * vertex)];
        }
        var skin = mesh.Skin;
        var binds = new Transform3D[skin.GetBindCount()];
        for (var b = 0; b < binds.Length; b++)
        {
            var bone = skin.GetBindBone(b);
            if (bone < 0)
            {
                bone = skeleton.FindBone(skin.GetBindName(b));
            }
            binds[b] = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone) * skin.GetBindPose(b);
        }
        var faces = new List<Vector3>();
        for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
        {
            var arrays = mesh.Mesh.SurfaceGetArrays(surface);
            var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
            var weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();
            var per = bones.Length / vertices.Length;
            var posed = new Vector3[vertices.Length];
            for (var v = 0; v < vertices.Length; v++)
            {
                for (var n = 0; n < per; n++)
                {
                    posed[v] += binds[bones[(v * per) + n]] * vertices[v] * weights[(v * per) + n];
                }
            }
            var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            for (var t = 0; t < indices.Length; t += 3)
            {
                if (vertices[indices[t]].X < behind && vertices[indices[t + 1]].X < behind
                    && vertices[indices[t + 2]].X < behind)
                {
                    faces.AddRange([posed[indices[t]], posed[indices[t + 1]], posed[indices[t + 2]]]);
                }
            }
        }
        return faces;
    }

    /// <summary>Every tool held in a glove the way the game holds it, fitted by data/grips.json: nothing of the tool is
    /// inside the glove's fingers or palm. A tool may pass between the fingers or rest against them, not through them.
    /// </summary>
    private static async Task GripClearance(Node3D holder, int handIndex)
    {
        var hand = GripCheck.MakeHand(holder, handIndex);
        var seen = new HashSet<string>();
        foreach (var def in Db.Tools.Values)
        {
            var modelId = def.ModelName;
            if (!seen.Add(modelId))
            {
                continue;
            }
            var tool = GripCheck.Hold(hand, def, Db.GripFit(def, handIndex), holder);
            await Frames.NextPhysics();
            var clipped = GripCheck.Clipped(hand);
            if (clipped > 0)
            {
                var parts = SurgeonHand.Fingers.Prepend("Palm")
                    .Select(part => (part, count: GripCheck.Clipped(hand, part)))
                    .Where(entry => entry.count > 0)
                    .Select(entry => $"{entry.part} {entry.count}");
                AssertInt(clipped).OverrideFailureMessage(
                        $"the {modelId} goes through the {HandName(handIndex)} glove holding it ({clipped} points: "
                            + $"{string.Join(", ", parts)})")
                    .IsEqual(0);
            }
            tool.QueueFree();
            await Frames.NextPhysics();
        }
        hand.GetParent().QueueFree();
    }

    private static bool Exists(string category, string modelName)
    {
        var path = $"{ModelSlot.Root}/{category}/{modelName}.glb";
        var exists = ResourceLoader.Exists(path);
        AssertBool(exists).OverrideFailureMessage($"missing model {path}").IsTrue();
        return exists;
    }

    private static void Rig(string category, string modelName, IEnumerable<string> bones, string[] parts, Node3D holder)
    {
        if (!Exists(category, modelName))
        {
            return;
        }
        var model = ModelSlot.Instantiate(category, modelName, holder);
        var rig = BoneRig.Find(model);
        AssertObject(rig).OverrideFailureMessage($"{category}/{modelName} has no skeleton").IsNotNull();
        foreach (var bone in bones)
        {
            AssertBool(rig!.Has(bone)).OverrideFailureMessage($"{category}/{modelName} has no bone {bone}").IsTrue();
        }
        foreach (var part in parts)
        {
            AssertObject(model.FindChild(part, true, false))
                .OverrideFailureMessage($"{category}/{modelName} has no part {part}")
                .IsNotNull();
        }
    }

    private static void CheckBudgets(Node3D holder)
    {
        foreach (var (category, budget) in Budgets)
        {
            var dir = $"{ModelSlot.Root}/{category}";
            var models = DirAccess.GetFilesAt(dir).Where(file => file.EndsWith(".glb", StringComparison.Ordinal));
            foreach (var file in models)
            {
                var model = ModelSlot.Instantiate(category, file.GetBaseName(), holder);
                var count = Triangles(model);
                AssertInt(count).OverrideFailureMessage($"{category}/{file} has {count} triangles, budget {budget}")
                    .IsLessEqual(budget);
                model.QueueFree();
            }
        }
    }

    private static void HandPoseLimits(Node3D holder)
    {
        var scrubs = Materials.ToonUnique(Materials.Scrubs[0], 0.08f, false, 0.9f);
        for (var side = 0; side < 2; side++)
        {
            var hand = new SurgeonHand();
            holder.AddChild(hand);
            hand.Build(side, scrubs);
            var shoulder = new Vector3(side == 0 ? -0.2f : 0.2f, 1.4f, 0f);
            Vector3[] targets = [shoulder, shoulder + (Vector3.Down * 0.68f), shoulder + (Vector3.Up * 0.25f)];
            foreach (var target in targets)
            {
                hand.Target = target;
                foreach (var grip in GripStyle.All.Keys)
                {
                    hand.Grip = grip;
                    hand.Holding = true;
                    hand.UpdatePose(shoulder, 1f / 30f);
                    foreach (var part in (Node3D[])[hand.UpperSleeve, hand.ForeSleeve, hand.Glove])
                    {
                        var basis = part.GlobalBasis;
                        AssertBool(part.GlobalPosition.IsFinite() && basis.X.IsFinite() && basis.Y.IsFinite()
                                && basis.Z.IsFinite() && Mathf.Abs(basis.Determinant()) > 0.00001f)
                            .OverrideFailureMessage($"finite mirrored {grip} pose at limit").IsTrue();
                    }
                }
            }
            hand.QueueFree();
        }
    }

    /// <summary>Cutting blades stay rigid; saw blades oscillate in use and return to rest on release.</summary>
    private static void BladeAnimation(Node3D holder)
    {
        foreach (var id in (string[])["scalpel", "switchblade", "bone_saw", "heavy_saw"])
        {
            var tool = new SurgicalTool();
            holder.AddChild(tool);
            tool.Setup(9000, Db.Tool(id)!);
            tool.Freeze = true;
            tool.SetProcess(false);
            var blade = tool.FindChild("Blade", true, false) as Node3D;
            AssertObject(blade).OverrideFailureMessage($"{id} has a blade").IsNotNull();
            var rest = blade!.Transform;
            var moved = 0f;
            var orientationStable = true;
            for (var frame = 0; frame < 120; frame++)
            {
                tool.Animator.Animate(true, false, 1f / 60f);
                moved = Mathf.Max(moved, blade.Position.DistanceTo(rest.Origin));
                orientationStable = orientationStable && blade.Basis.IsEqualApprox(rest.Basis);
            }
            AssertBool(orientationStable).OverrideFailureMessage($"{id} blade keeps its orientation throughout use")
                .IsTrue();
            if (tool.Def.Action == "saw")
            {
                AssertFloat(moved).OverrideFailureMessage($"{id} blade still oscillates in use").IsGreater(0.003f);
            }
            else
            {
                AssertFloat(moved).OverrideFailureMessage($"{id} blade stays fixed to the handle throughout use")
                    .IsLess(0.000001f);
            }
            tool.Animator.Animate(false, false, 1f / 60f);
            AssertBool(blade.Transform.IsEqualApprox(rest)).OverrideFailureMessage($"{id} blade rests on release")
                .IsTrue();
            tool.QueueFree();
        }
    }

    private static void BladeTips(Node3D holder)
    {
        foreach (var name in (string[])["scalpel", "switchblade"])
        {
            var tool = ModelSlot.Instantiate("tools", name, holder);
            var blade = tool.FindChild("Blade", true, false) as MeshInstance3D;
            var handle = tool.FindChild("Handle", true, false) as MeshInstance3D;
            AssertBool(blade is not null && handle is not null)
                .OverrideFailureMessage($"{name} has separate blade and handle")
                .IsTrue();
            var bladeBox = blade!.Mesh.GetAabb();
            var handleBox = handle!.Mesh.GetAabb();
            AssertBool(bladeBox.Position.Z < handleBox.Position.Z && bladeBox.End.Z <= handleBox.Position.Z + 0.001f
                    && handleBox.End.Z > 0.04f)
                .OverrideFailureMessage($"{name} blade points toward -Z working tip").IsTrue();
            tool.QueueFree();
        }
    }

    /// <summary>The curved needle's sharp end is the tool aim point and its shaft physically crosses the closed holder
    /// jaws. Thread is drawn only after it is anchored into tissue, never baked into the idle tool model.</summary>
    private static void NeedleHolder(Node3D holder)
    {
        var model = ModelSlot.Instantiate("tools", "needle", holder);
        var needle = model.FindChild("SutureNeedle", true, false) as MeshInstance3D;
        var jawA = model.FindChild("JawA", true, false) as MeshInstance3D;
        var jawB = model.FindChild("JawB", true, false) as MeshInstance3D;
        AssertBool(needle is not null && jawA is not null && jawB is not null)
            .OverrideFailureMessage("the needle holder has a curved needle and two jaws").IsTrue();
        AssertObject(model.FindChild("Thread", true, false))
            .OverrideFailureMessage("the idle needle has no placeholder thread")
            .IsNull();
        var def = Db.Tools["needle"];
        var tip = model.GlobalTransform * new Vector3(0f, 0f, -def.Length);
        var clamp = model.GlobalTransform * new Vector3(0f, 0f, -def.Length + 0.006f);
        AssertFloat(needle!.GlobalPosition.DistanceTo(tip))
            .OverrideFailureMessage("the curved needle's sharp end is the gameplay tip")
            .IsLess(0.0001f);
        AssertFloat(MeshDistance(needle, clamp))
            .OverrideFailureMessage("the curved needle crosses the holder at its clamp point")
            .IsLess(0.0011f);
        AssertBool(MeshDistance(jawA!, clamp) < 0.0025f && MeshDistance(jawB!, clamp) < 0.0025f)
            .OverrideFailureMessage("both holder jaws meet the needle").IsTrue();
        model.QueueFree();
    }

    private static float MeshDistance(MeshInstance3D mesh, Vector3 point) =>
        PosedFaces(mesh).Min(vertex => vertex.DistanceTo(point));

    private static void ContactAudio()
    {
        Sfx.Contact(9101, "contact_cut", Vector3.Zero, 0.5f);
        var player = Sfx.ContactPlayer(9101);
        AssertObject(player).OverrideFailureMessage("contact audio starts a blade loop").IsNotNull();
        Sfx.Contact(9101, "contact_cut", Vector3.One, 1f);
        AssertBool(Sfx.ContactPlayer(9101) == player && player!.Position == Vector3.One)
            .OverrideFailureMessage("contact audio reuses and moves its loop").IsTrue();
        Sfx.Contact(9102, "contact_cut", Vector3.Zero, 0.5f);
        Sfx.Contact(9103, "contact_suction", Vector3.Zero, 0.5f);
        Sfx.Contact(9104, "contact_swab", Vector3.Zero, 0.5f);
        AssertBool(Sfx.ContactCount == Sfx.ContactLimit && Sfx.ContactPlayer(9104) is null)
            .OverrideFailureMessage("quiet swab does not evict a louder loop").IsTrue();
        Sfx.ExpireContacts();
        Frames.Root.GetNode<Sfx>("Sfx")._Process(1.0);
        AssertInt(Sfx.ContactCount).OverrideFailureMessage("stale contact loops fade away").IsEqual(0);
    }

    private static void IvLineClearance(Node3D holder)
    {
        var line = new IvLine();
        holder.AddChild(line);
        var stand = new Node3D();
        var patient = new Node3D();
        holder.AddChild(stand);
        holder.AddChild(patient);
        patient.Position = patient.Position with { X = 1f };
        var site = new Transform3D(Basis.Identity, new Vector3(0f, 1.4f, 0f));
        line.Attach(stand, new Vector3(0f, 1.6f, 0f), patient, site, 0.035f);
        var lowest = line.Points.Min(point => point.Y);
        AssertBool(line.Points.Count == IvLine.Samples && lowest < IvLine.TripHeight - 0.04f)
            .OverrideFailureMessage("IV attachment immediately builds tubing at walking trip height").IsTrue();
        line.QueueFree();
        stand.QueueFree();
        patient.QueueFree();
    }

    private static int Triangles(Node root)
    {
        var total = 0;
        foreach (var mesh in Meshes(root).Select(node => node.Mesh))
        {
            for (var i = 0; i < mesh.GetSurfaceCount(); i++)
            {
                var arrays = mesh.SurfaceGetArrays(i);
                var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                var corners = indices.Length > 0
                    ? indices.Length
                    : arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
                total += corners / 3;
            }
        }
        return total;
    }
}
