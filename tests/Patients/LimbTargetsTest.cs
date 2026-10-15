namespace Scalpel.Tests.Patients;

/// <summary>
/// What a scenario puts in a limb is there for the surgeon to find, on an ordinary, an obese and a mirrored patient:
/// every target the scenario lists is built, on the body, inside the limb's cavity (its top under the muscle, its middle
/// not under the floor), with no bone over it seen from above. The limb's bones are the skeleton's X-ray films show,
/// half sunk in muscle, not in a hollow: the floor comes up their sides. A bullet lies along its wound track, no further aside from the entry wound
/// than it lies deep. On the ordinary patient, a cut through every layer over the first target stays open down its fat
/// and muscle walls, which bulge into it only a little. Key frames: that cut, from above and along the limb.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class LimbTargetsTest {
    /// <summary>How far (meters) a measured height may be off: the site's heights are interpolated.</summary>
    private const float Slack = 0.0005f;
    /// <summary>How long (meters) the cut over a target is in the key frames.</summary>
    private const float Opening = 0.06f;
    /// <summary>How far (meters) the key frames look at the cut from.</summary>
    private const float Close = 0.15f;

    /// <summary>Thicker fat pushes the muscle, the floor and what lies on it down; a mirrored patient has the targets
    /// and wounds on the other side of the bones.</summary>
    private static readonly string[] Patients = ["", "obese", "mirrored"];

    [TestCase("bullet_muscle")]
    [TestCase("ambulance_bullet")]
    [TestCase("open_fracture")]
    [TestCase("gangrene_amputation")]
    [TestCase("leg_extension")]
    public async Task TargetsLieInTheLimbWhereTheyCanBeFound(string scenario) {
        FrameBudget.RenderingOff();
        foreach (var quirk in Patients) {
            var driver = SurgeryDriver.Create();
            await driver.Start(scenario, patientQuirks: quirk == "" ? null : [new QuirkRoll(quirk, "")]);
            var patient = driver.Patient;
            var body = driver.Body;
            var who = quirk == "" ? "an ordinary patient" : $"an {quirk} patient";
            AssertBool(body.IsLimbSite).OverrideFailureMessage($"{scenario} is on a limb").IsTrue();
            var def = patient.Scenario;
            AssertInt(patient.Targets.Count).OverrideFailureMessage($"{scenario} on {who}: every target it lists is built")
                .IsEqual(def.Targets.Count);
            foreach (var kind in def.Steps.Where(step => step.Type == "extract").Select(step => step.Parameters.String("target"))) {
                AssertBool(patient.Targets.Any(target => target.Kind == kind))
                    .OverrideFailureMessage($"{scenario} on {who}: the {kind} it asks to remove is there").IsTrue();
            }
            foreach (var target in patient.Targets) {
                TargetIsInTheCavity(body, target, $"{scenario} on {who}");
                BulletIsAlongItsWound(patient, target, $"{scenario} on {who}");
            }
            foreach (var bone in body.Bones) {
                BoneIsInMuscle(body, bone, $"{scenario} on {who}");
            }
            // A bone to saw takes the place of the one bone it lies on: a shin keeps its fibula.
            var sawn = patient.Targets.Count(target => target.IsBone);
            AssertInt(body.Bones.Count).OverrideFailureMessage(
                    $"{scenario} on {who}: of the {body.MeasuredLimbBones} bones under the site, {sawn} to saw take one's place each")
                .IsEqual(body.MeasuredLimbBones - sawn);
            if (quirk == "") {
                // Cut through every layer along the limb over the first target, as a surgeon goes in for it.
                var target = patient.Targets[0];
                var half = new Vector2(body.MetersToUv(Opening * 0.5f), 0f);
                SurgeryState.SkinIsCut(patient, target.Uv - half, target.Uv + half, 1f);
                await Frames.Seconds(1f);
                AssertBool(body.IsOpen(target.Uv)).OverrideFailureMessage($"{scenario}: the cut opens over the {target.Kind}")
                    .IsTrue();
                foreach (var layer in (int[])[1, 2]) {
                    WallsLeaveTheCutOpen(body, layer, target.Uv - half, target.Uv + half, scenario);
                }
            }
            if (quirk == "" && KeyFrames.Wanted()) {
                var target = patient.Targets[0];
                var shots = new KeyFrames();
                driver.AddChild(shots);
                shots.Begin(driver.Surgery, $"limb_targets/{scenario}");
                await shots.CaptureAt("opened",
                    $"close up, a cut along the limb through every layer: deep red muscle walls, the {target.Kind} down in the cut",
                    body.UvToWorld(target.Uv), Close);
                shots.End();
            }
            await driver.Stop();
        }
    }

    /// <summary>
    /// A limb opened wide, an H of cuts through every layer folded back like a clamshell: the flaps take the skin, the
    /// fat and the fascia over the muscle along (their cut muscle as deep as the fascia, not the limb's muscle down to
    /// its bones), and nothing tears. The bones lie half sunk in the muscle left as the wound's bed.
    /// </summary>
    [TestCase("bullet_muscle")]
    [TestCase("gangrene_amputation")]
    public async Task ALimbOpenedWideFoldsItsFlapsBackOverItsMuscle(string scenario) {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        await driver.Start(scenario);
        var body = driver.Body;
        var torn = SurgeryState.SiteIsOpenedWide(driver.Patient);
        await Frames.Physics(30);
        AssertInt(torn).OverrideFailureMessage($"folding the flaps of the {body.SiteId} back tore {torn} springs").IsEqual(0);
        // The muscle's cut walls on the flaps folded out over the drape, past the site's edge: as deep as the fascia,
        // not as the limb's muscle down to its bones.
        var mesh = body.Layers[2].Mesh;
        using var arrays = mesh.SurfaceGetArrays(mesh.GetSurfaceCount() - 1);
        var walls = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        var uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        var colors = arrays[(int)Mesh.ArrayType.Color].AsColorArray();
        var half = body.SiteSize * 0.5f + (Vector2.One * 0.01f);
        var deepest = 0f;
        // Follow connected edges with the same material coordinate from lip to bottom. This measures the
        // whole rounded column, and also the triangular V tips, without joining unrelated wall vertices.
        var columns = new Dictionary<int, List<int>>();
        for (var t = 0; t < indices.Length; t += 3) {
            for (var e = 0; e < 3; e++) {
                var a = indices[t + e];
                var b = indices[t + (e + 1) % 3];
                if (!uvs[a].IsEqualApprox(uvs[b]) || colors[a].R == colors[b].R) { continue; }
                if (colors[a].R > colors[b].R) { (a, b) = (b, a); }
                if (!columns.TryGetValue(a, out var next)) { columns[a] = next = []; }
                next.Add(b);
            }
        }
        var measured = 0;
        foreach (var top in columns.Keys.Where(v => colors[v].R == 0f)) {
            if (Mathf.Abs(walls[top].X) <= half.X && Mathf.Abs(walls[top].Z) <= half.Y) { continue; }
            var pending = new Stack<int>();
            var seen = new HashSet<int>();
            pending.Push(top);
            while (pending.TryPop(out var v)) {
                if (!seen.Add(v)) { continue; }
                if (colors[v].R == 1f) {
                    deepest = Mathf.Max(deepest, walls[top].DistanceTo(walls[v]));
                    measured++;
                } else if (columns.TryGetValue(v, out var next)) {
                    foreach (var n in next) { pending.Push(n); }
                }
            }
        }
        AssertInt(measured).OverrideFailureMessage("folded flap walls have top-to-bottom edges to measure").IsGreater(0);
        AssertFloat(deepest).OverrideFailureMessage(
                $"the folded flaps' muscle is as deep as the fascia, not the limb's {body.MuscleThickness * 1000f:0} mm ({deepest * 1000f:0.0} mm)")
            .IsLessEqual(PatientBody.DefaultMuscle * 1.5f);
        if (KeyFrames.Wanted()) {
            var shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(driver.Surgery, $"limb_targets/{scenario}_opened_wide");
            await shots.Capture("opened_wide",
                "the limb opened wide: the flaps folded back show thin red muscle under their fat, no shards; the bone lies half sunk in a red muscle bed that rises at the site's ends",
                body.Site.GlobalBasis.X);
            shots.End();
        }
        await driver.Stop();
    }

    /// <summary>How much of a cut's gap halfway down its walls may close, bulging into it from both sides.</summary>
    private const float WallsClose = 0.3f;

    /// <summary>
    /// Along the middle third of a cut from <paramref name="from"/> to <paramref name="to"/> (along the site's x), a
    /// layer's walls bulge into the cut only a little: halfway down, the gap between its two walls stays most of what
    /// walls running straight from its lips to their bottoms would leave, so what lies under it shows from above.
    /// </summary>
    private static void WallsLeaveTheCutOpen(PatientBody body, int layer, Vector2 from, Vector2 to, string where) {
        var name = body.Layers[layer].Name;
        // A limb with next to no fat (a forearm) draws no fat layer at all.
        if (!body.Layers[layer].Visible) {
            return;
        }
        var mesh = body.Layers[layer].Mesh;
        AssertInt(mesh.GetSurfaceCount()).OverrideFailureMessage($"{where}: the cut has {name} walls").IsGreaterEqual(2);
        var walls = mesh.SurfaceGetArrays(mesh.GetSurfaceCount() - 1)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var (start, end) = (body.SitePoint(from, 0f), body.SitePoint(to, 0f));
        var third = Mathf.Abs(end.X - start.X) / 6f;
        var middle = (start + end) * 0.5f;
        // Each wall is drawn in columns down from its lip, PatientBody.WallRows + 1 vertices each, top first.
        var column = PatientBody.WallRows + 1;
        // Per side of the cut (by its lip) and row down its walls, how near the cut's line the wall comes.
        var near = new float[2, column];
        for (var row = 0; row < column; row++) {
            near[0, row] = near[1, row] = float.MaxValue;
        }
        for (var v = 0; v + column <= walls.Length; v += column) {
            if (Mathf.Abs(walls[v].X - middle.X) > third) {
                continue;
            }
            var side = walls[v].Z > middle.Z ? 0 : 1;
            var sign = side == 0 ? 1f : -1f;
            for (var row = 0; row < column; row++) {
                near[side, row] = Mathf.Min(near[side, row], (walls[v + row].Z - middle.Z) * sign);
            }
        }
        AssertBool(near[0, 0] < float.MaxValue && near[1, 0] < float.MaxValue)
            .OverrideFailureMessage($"{where}: {name} walls on both sides of the cut").IsTrue();
        var gaps = Enumerable.Range(0, column).Select(row => near[0, row] + near[1, row]).ToArray();
        // Walls running straight from the lips to their bottoms leave the gap between those two halfway down.
        var (halfway, straight) = (gaps[column / 2], (gaps[0] + gaps[^1]) * 0.5f);
        AssertFloat(halfway).OverrideFailureMessage(
                $"{where}: the {name} walls bulge into the cut only a little, halfway down a gap of {halfway * 1000f:0.0} mm against {straight * 1000f:0.0} mm between straight walls (top to bottom {string.Join(", ", gaps.Select(gap => $"{gap * 1000f:0.0}"))} mm)")
            .IsGreaterEqual(straight * (1f - WallsClose));
    }

    /// <summary>On the body, its top under the muscle, its middle not under the floor, and seen from straight above no
    /// bone or floor over it. A bone to saw is a whole femur whose ends lie past the site: it lies like the limb's own
    /// bone, half sunk in the floor.</summary>
    private static void TargetIsInTheCavity(PatientBody body, CavityTarget target, string where) {
        var uv = target.Uv;
        var what = $"{where}: the {target.Kind} at {uv}";
        AssertBool(uv.X is >= 0f and <= 1f && uv.Y is >= 0f and <= 1f && body.OnBody(uv))
            .OverrideFailureMessage($"{what} lies under the site, on the body").IsTrue();
        var skin = body.SurfaceHeight(uv);
        var floor = body.CavityFloor(uv);
        if (target.IsBone) {
            AssertFloat(target.Position.Y - floor).OverrideFailureMessage($"{what}: its middle lies just over the floor")
                .IsBetween(-Slack, 0.002f + Slack);
        } else {
            var top = target.Position.Y + PatientBody.LocalBounds(target, target).End.Y;
            AssertFloat(skin - top).OverrideFailureMessage($"{what}: its top lies under the muscle ({body.MuscleBottom:0.000} m)")
                .IsGreaterEqual(body.MuscleBottom - Slack);
            AssertFloat(target.Position.Y).OverrideFailureMessage($"{what}: its middle lies over the cavity floor")
                .IsGreaterEqual(floor - Slack);
        }
        var from = body.Site.ToGlobal(body.SitePoint(uv, skin + 0.1f));
        var query = PhysicsRayQueryParameters3D.Create(from, body.Site.ToGlobal(target.Position),
            PatientBody.CavityLayer | CavityTarget.TouchLayer);
        var hit = body.GetWorld3D().DirectSpaceState.IntersectRay(query);
        var over = hit.Count > 0 ? hit["collider"].AsGodotObject() : null;
        AssertBool(over is not Bone && (over as Node)?.Name != "CavityFloor")
            .OverrideFailureMessage($"{what}: nothing hides it from above ({(over as Node)?.Name})").IsTrue();
    }

    /// <summary>A bullet lies within 45° of straight in from its entry wound: no further aside than it lies deep.
    /// </summary>
    private static void BulletIsAlongItsWound(Patient patient, CavityTarget target, string where) {
        var entries = patient.Wounds.Where(wound => wound.Kind == WoundKind.Gunshot).SelectMany(wound => wound.Points).ToList();
        if (target.Kind != "bullet" || entries.Count == 0) {
            return;
        }
        var size = patient.Body.SiteSize;
        var aside = entries.Min(point => ((point - target.Uv) * size).Length());
        AssertFloat(aside).OverrideFailureMessage($"{where}: the bullet lies {aside:0.000} m aside from its entry wound, "
            + $"{target.Depth:0.000} m deep, along the wound track").IsLessEqual(target.Depth);
    }

    /// <summary>The bone lies where the skeleton X-ray films show is, and along it, away from the site's sides where
    /// the floor rises to the skin, the floor lies under its middle and comes up its sides past its bottom: muscle round
    /// it.</summary>
    private static void BoneIsInMuscle(PatientBody body, Bone bone, string where) {
        var skeleton = bone.Part;
        AssertBool(skeleton is not null && body.Skeleton.Contains(skeleton))
            .OverrideFailureMessage($"{where}: {bone.Name} is a part of the skeleton X-ray films show").IsTrue();
        var box = (skeleton!.GlobalTransform * skeleton.GetAabb()).Grow(0.001f);
        foreach (var capsule in bone.GetChildren().OfType<CollisionShape3D>()) {
            var middle = capsule.Position;
            var radius = ((CapsuleShape3D)capsule.Shape).Radius;
            var uv = body.LocalToUv(middle);
            var what = $"{where}: {bone.Name} at {uv}";
            AssertBool(box.HasPoint(body.Site.ToGlobal(middle)))
                .OverrideFailureMessage($"{what} lies in the skeleton's {skeleton.Name}").IsTrue();
            // Where a bone rises into the muscle itself (a forearm toward the wrist), the muscle is round it.
            var underMuscle = body.SurfaceHeight(uv) - (middle.Y + radius) >= body.MuscleBottom;
            if (uv.X is < 0.15f or > 0.85f || uv.Y is < 0.15f or > 0.85f || !underMuscle) {
                continue;
            }
            var beside = uv + new Vector2(0f, body.MetersToUv(radius + 0.002f));
            AssertFloat(body.CavityFloor(uv)).OverrideFailureMessage($"{what}: the floor lies under its middle")
                .IsLess(middle.Y);
            AssertFloat(body.CavityFloor(beside)).OverrideFailureMessage(
                    $"{what}: muscle comes up its side past its bottom (floor {body.SurfaceHeight(beside) - body.CavityFloor(beside):0.0000} m deep beside it, its bottom {body.SurfaceHeight(uv) - middle.Y + radius:0.0000} m)")
                .IsGreater(middle.Y - radius - Slack);
        }
    }
}
