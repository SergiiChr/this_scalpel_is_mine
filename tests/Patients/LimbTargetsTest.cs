namespace Scalpel.Tests.Patients;

/// <summary>
/// What a scenario puts in a limb is there for the surgeon to find, on an ordinary, an obese and a mirrored patient:
/// every target the scenario lists is built, on the body, inside the limb's cavity (its top under the muscle, its middle
/// not under the floor), with no bone over it seen from above. The limb's bones are the skeleton's X-ray films show,
/// half sunk in muscle, not in a hollow: the floor comes up their sides. A bullet lies along its wound track, no further aside from the entry wound
/// than it lies deep. Key frames: on the ordinary patient, a cut through every layer over the first target, from above
/// and along the limb.
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
            if (quirk == "" && KeyFrames.Wanted()) {
                // Cut through every layer along the limb over the first target, as a surgeon goes in for it.
                var target = patient.Targets[0];
                var half = new Vector2(body.MetersToUv(Opening * 0.5f), 0f);
                SurgeryState.SkinIsCut(patient, target.Uv - half, target.Uv + half, 1f);
                await Frames.Seconds(1f);
                AssertBool(body.IsOpen(target.Uv)).OverrideFailureMessage($"{scenario}: the cut opens over the {target.Kind}")
                    .IsTrue();
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
        var walls = mesh.SurfaceGetArrays(mesh.GetSurfaceCount() - 1)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var half = body.SiteSize * 0.5f + (Vector2.One * 0.01f);
        var deepest = 0f;
        for (var v = 0; v + 3 < walls.Length; v += 4) {
            if (Mathf.Abs(walls[v].X) > half.X || Mathf.Abs(walls[v].Z) > half.Y) {
                deepest = Mathf.Max(deepest, Mathf.Max(walls[v].DistanceTo(walls[v + 3]), walls[v + 1].DistanceTo(walls[v + 2])));
            }
        }
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
