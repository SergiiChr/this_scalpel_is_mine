namespace Scalpel.Tests.Patients;

/// <summary>
/// What a scenario puts in a limb is there for the surgeon to find, on an ordinary, an obese and a mirrored patient:
/// every target the scenario lists is built, on the body, inside the limb's cavity (its top under the muscle, its middle
/// not under the floor), with no bone over it seen from above. The limb's bones are the skeleton's X-ray films show,
/// half sunk in muscle, not in a hollow: the floor comes up their sides. A bullet lies along its wound track, no further aside from the entry wound
/// than it lies deep. Key frames: the site opened wide on the ordinary patient, from above and along the limb.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class LimbTargetsTest {
    /// <summary>How far (meters) a measured height may be off: the site's heights are interpolated.</summary>
    private const float Slack = 0.0005f;

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
            if (quirk == "" && KeyFrames.Wanted()) {
                SurgeryState.SiteIsOpenedWide(patient);
                await Frames.Physics(30);
                var shots = new KeyFrames();
                driver.AddChild(shots);
                shots.Begin(driver.Surgery, $"limb_targets/{scenario}");
                await shots.Capture("opened",
                    "the site opened wide: the bones half sunk in red muscle, no hollow round them, and every target lying in the muscle in view",
                    body.Site.GlobalBasis.X);
                shots.End();
            }
            await driver.Stop();
        }
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
        var mesh = bone.GetNode<MeshInstance3D>("Mesh");
        var skeleton = body.Skeleton.SingleOrDefault(part => part.Mesh == mesh.Mesh);
        AssertObject(skeleton).OverrideFailureMessage($"{where}: {bone.Name} is a part of the skeleton X-ray films show")
            .IsNotNull();
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
            AssertFloat(body.CavityFloor(beside)).OverrideFailureMessage($"{what}: muscle comes up its side past its bottom")
                .IsGreater(middle.Y - radius);
        }
    }
}
