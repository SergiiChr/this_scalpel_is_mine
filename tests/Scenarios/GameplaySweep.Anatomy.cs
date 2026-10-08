namespace Scalpel.Tests.Scenarios;

/// <summary>Chest, belly and limbs: what lies under the skin, opened up.</summary>
internal sealed partial class GameplaySweep
{
    /// <summary>What <see cref="FirstInside"/> meets: an organ (by index), a bone or nothing.</summary>
    private const int Nothing = -1;
    private const int ABone = -2;

    /// <summary>Chest and belly: opened wide, the top layer of organs and the ribs show with no skin, fat or muscle over
    /// them. Taking hold of a top organ and moving it aside shows the one under it. The heart beats and the lungs
    /// breathe.</summary>
    private async Task AnatomyChecks()
    {
        var anatomy = Db.Site(Site).GetValueOrDefault("anatomy").AsGodotDictionary() ?? [];
        if (anatomy.ContainsKey("organs"))
        {
            if (!Body.Organs.Any(organ => organ.Layer == 1))
            {
                Fail($"the {Site} has no organs under the top layer");
            }
            var torn = SurgeryState.SiteIsOpenedWide(Patient);
            if (torn > 0)
            {
                Fail($"folding the flaps of the {Site} back tore {torn} springs");
            }
            await Frames.Physics(2);
            // --coverage draws what's still covered: one row per uv.x (feet first), # where soft tissue is in the way.
            if (OS.GetCmdlineUserArgs().Contains("--coverage"))
            {
                for (var j = 0; j < 25; j++)
                {
                    GD.Print("    " + string.Concat(Enumerable.Range(0, 25)
                        .Select(i => SoftTissueOver(Body, new Vector2(j / 24f, i / 24f)) ? '#' : '.')));
                }
            }
            foreach (var organ in Body.Organs.Where(organ => organ.Layer == 0))
            {
                var hidden = OrganFootprint(Body, organ).Where(uv => SoftTissueOver(Body, uv)).ToList();
                if (hidden.Count > 0)
                {
                    Fail($"the open {Site} still hides the {organ.Kind} under soft tissue at {string.Join(", ", hidden)}");
                }
            }
            foreach (var bone in Body.Bones)
            {
                var uv = SiteUv(Body, BoneMiddle(bone));
                if (uv.X is > 0.05f and < 0.95f && SoftTissueOver(Body, uv))
                {
                    Fail($"the open {Site} still hides {bone.Name} under soft tissue at {uv}");
                }
            }
            await MoveTopOrgan();
        }
        if (anatomy.ContainsKey("organs") || anatomy.ContainsKey("bones"))
        {
            BoneChecks();
        }
        OrganMotionChecks();
    }

    private static Vector2 SiteUv(PatientBody body, Vector3 local) =>
        new((local.X / body.SiteSize.X) + 0.5f, (local.Z / body.SiteSize.Y) + 0.5f);

    /// <summary>The middle of a bone (site space): its middle segment.</summary>
    private static Vector3 BoneMiddle(Bone bone) => bone.GetChild<Node3D>(bone.GetChildCount() / 2).Position;

    /// <summary>True where skin, fat or muscle is drawn over the point <paramref name="uv"/>: the body model outside
    /// the simulated region, or a triangle of one of the simulated layers, where the sim has pulled it now.</summary>
    private static bool SoftTissueOver(PatientBody body, Vector2 uv)
    {
        var tissue = body.Tissue;
        var region = tissue.Region();
        var grid = uv * new Vector2(tissue.ResX, tissue.ResY);
        var corners = 0;
        foreach (var corner in (Vector2I[])[new(0, 0), new(1, 0), new(0, 1), new(1, 1)])
        {
            var at = (new Vector2I(Mathf.FloorToInt(grid.X), Mathf.FloorToInt(grid.Y)) + corner)
                .Clamp(Vector2I.Zero, new Vector2I(tissue.ResX, tissue.ResY));
            corners += region[tissue.Index(at.X, at.Y)];
        }
        if (corners < 2)
        {
            return true;
        }
        var point = new Vector2((uv.X - 0.5f) * body.SiteSize.X, (uv.Y - 0.5f) * body.SiteSize.Y);
        for (var layer = 0; layer < PatientBody.LayerDepth.Length; layer++)
        {
            var triangles = tissue.Triangles(PatientBody.LayerDepth[layer]);
            for (var t = 0; t < triangles.Length; t += 3)
            {
                var p = Enumerable.Range(0, 3).Select(k =>
                {
                    var v = body.LayerPoint(layer, triangles[t + k]);
                    return new Vector2(v.X, v.Z);
                }).ToArray();
                if (Geometry2D.PointIsInsideTriangle(point, p[0], p[1], p[2]))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>The organ's collision box: its last child.</summary>
    private static CollisionShape3D OrganShape(Organ organ) => organ.GetChild<CollisionShape3D>(organ.GetChildCount() - 1);

    /// <summary>Points (uv) spread over what an organ covers seen from above: its middle and toward the ends of its
    /// box.</summary>
    private static List<Vector2> OrganFootprint(PatientBody body, Organ organ)
    {
        var shape = OrganShape(organ);
        var size = shape.Shape is BoxShape3D box ? box.Size : Vector3.One * 0.02f;
        Vector3[] offsets = [Vector3.Zero, new(0.35f, 0f, 0f), new(-0.35f, 0f, 0f), new(0f, 0f, 0.35f), new(0f, 0f, -0.35f)];
        return [.. offsets.Select(offset => SiteUv(body, organ.Transform * (shape.Position + (offset * size))))];
    }

    /// <summary>What a ray straight down onto <paramref name="uv"/> meets first inside the body: an organ index,
    /// <see cref="ABone"/> or <see cref="Nothing"/>.</summary>
    private static int FirstInside(PatientBody body, Vector2 uv)
    {
        var from = body.Site.ToGlobal(new Vector3((uv.X - 0.5f) * body.SiteSize.X, 0.1f, (uv.Y - 0.5f) * body.SiteSize.Y));
        var query = PhysicsRayQueryParameters3D.Create(from, from - (body.Site.GlobalBasis.Y * 0.4f), PatientBody.CavityLayer);
        var hit = body.GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0)
        {
            return Nothing;
        }
        var collider = hit["collider"].AsGodotObject();
        return collider is Bone ? ABone : body.Organs.IndexOf((Organ)collider);
    }

    /// <summary>Grips the top organ over a lower one with forceps, moves it aside, and the lower one shows.</summary>
    private async Task MoveTopOrgan()
    {
        const int forceps = 99001;
        foreach (var lower in Body.Organs.Where(organ => organ.Layer == 1))
        {
            foreach (var uv in OrganFootprint(Body, lower))
            {
                var top = FirstInside(Body, uv);
                if (top < 0 || top == Body.Organs.IndexOf(lower))
                {
                    continue;
                }
                var organ = Body.Organs[top];
                var organUv = SiteUv(Body, organ.Position);
                var depth = Body.SurfaceHeight(organUv) - organ.Position.Y;
                var grip = Patient.Grip(forceps, SiteZone.Cavity, organUv, depth);
                if (grip is not OrganHold hold || hold.Organ != top)
                {
                    var at = Body.UvToWorld(organUv, depth);
                    var shape = OrganShape(organ);
                    Fail($"forceps in the open {Site} didn't take hold of the {organ.Kind}: {grip} (at {at}, organ {organ.GlobalPosition}, box {(shape.Shape as BoxShape3D)?.Size} at {shape.GlobalPosition}, organ_at {Body.OrganAt(at, 0.02f)})");
                    return;
                }
                var home = organ.Position;
                var aside = Body.Site.ToGlobal(organ.Position + new Vector3(0f, 0.03f, 0f)
                    + (new Vector3(organ.Position.X, 0f, organ.Position.Z).Normalized() * 0.12f));
                ToolHold? held = hold;
                for (var i = 0; i < 10 && held is not null; i++)
                {
                    held = Patient.UpdateGrip(forceps, held, aside, 1f, 1f / 60f, 0f);
                    await Frames.NextPhysics();
                }
                if (FirstInside(Body, uv) == top)
                {
                    Fail($"moving the {organ.Kind} aside didn't uncover what's under it");
                }
                Patient.ReleaseGrip(forceps, held, false);
                // Put back where it was, out of the way of what's checked next: by now the patient may be past drifting
                // it back (Patient only settles organs while it's alive).
                organ.Position = home;
                organ.LinearVelocity = Vector3.Zero;
                await Frames.Physics(30);
                return;
            }
        }
        Fail($"nothing in the open {Site} lies under the top layer of organs");
    }

    /// <summary>Chest and limbs: a deep cut opens down to the bone. It's right under the muscle, a tool rests on it,
    /// and the blade grates on it.</summary>
    private void BoneChecks()
    {
        if (Body.Bones.Count == 0)
        {
            if (!Patient.Targets.Any(target => target.Kind is "bone" or "sternum"))
            {
                Fail($"no bones under the {Site}");
            }
            return;
        }
        // The bone closest to the middle of the site.
        var bone = Body.Bones.MinBy(candidate => BoneMiddle(candidate).Slide(Vector3.Up).Length())!;
        var middle = BoneMiddle(bone);
        var uv = SiteUv(Body, middle);
        var across = bone.Name.ToString().StartsWith("Rib") ? new Vector2(0f, 0.08f) : new Vector2(0.12f, 0f);
        Patient.Cut(99100, uv - across, uv + across, 1f, 1f, false, 0.1f);
        for (var i = 0; i < 60; i++)
        {
            Body.Tissue.Substep();
        }
        var first = FirstInside(Body, uv);
        if (first != ABone)
        {
            var what = first >= 0 ? Body.Organs[first] : null;
            Fail($"the {bone.Name} under a deep cut at {uv} isn't the first thing inside: {what?.Name ?? "nothing"} at {what?.Position ?? Vector3.Zero}, bone at {middle}");
        }
        var top = PatientBody.SkinThickness + Body.FatThickness + PatientBody.MuscleThickness;
        var depth = Body.SurfaceHeight(uv) - middle.Y;
        if (depth < top || depth > Body.CavityDepth())
        {
            Fail($"the {bone.Name} sits {depth:0.000} m under the skin, not under the muscle inside the cavity");
        }
        var scraped = Patient.Flags.GetValueOrDefault("bone_scraped");
        // Down to just over the bone's top, where a blade at full effort stops in an opening.
        var thickness = ((CapsuleShape3D)bone.GetChild<CollisionShape3D>(1).Shape).Radius;
        Patient.CutCavity(uv, depth - thickness - 0.002f, 1f, false, 0.5f);
        if (Patient.Flags.GetValueOrDefault("bone_scraped") <= scraped)
        {
            Fail($"cutting down on the {bone.Name} didn't reach the bone");
        }
    }

    /// <summary>The heart beats with the pulse, the lungs swell with each breath, and a heart in asystole lies still.
    /// </summary>
    private void OrganMotionChecks()
    {
        var vitals = Patient.Vitals;
        foreach (var motion in (string[])["beat", "breath"])
        {
            var index = Body.Organs.FindIndex(organ => organ.Motion == motion);
            if (index < 0)
            {
                continue;
            }
            var sizes = new List<float>();
            for (var i = 0; i < 90; i++)
            {
                Body.Animator.Animate(vitals, true, 1f / 30f);
                sizes.Add(Body.OrganMotion(index));
            }
            if (sizes.Max() - sizes.Min() < 0.04f)
            {
                Fail($"the {Body.Organs[index].Kind} doesn't move with the {motion} ({sizes.Max() - sizes.Min():0.000})");
            }
            if (motion == "beat")
            {
                var rhythm = vitals.Rhythm;
                vitals.Rhythm = Rhythm.Asystole;
                Body.Animator.Animate(vitals, true, 0.3f);
                if (!Mathf.IsEqualApprox(Body.OrganMotion(index), 1f))
                {
                    Fail("the heart still beats in asystole");
                }
                vitals.Rhythm = rhythm;
            }
        }
    }
}
