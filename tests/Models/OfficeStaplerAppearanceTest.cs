namespace Scalpel.Tests.Models;

/// <summary>The office stapler's fixed base and forming plate, with its rear-hinged upper lever.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tool_office_stapler"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class OfficeStaplerAppearanceTest {
    private const float QueryScale = 1000f;
    [TestCase]
    public async Task UpperLeverClearsTheFixedRailAndBothGlovesThroughoutItsSqueeze() {
        var studio = new ModelStudio(0.25f);
        try {
            var def = Db.Tool("office_stapler")!;
            for (var index = 0; index < 2; index++) {
                var hand = GripCheck.MakeHand(studio.Root, index);
                var model = GripCheck.Hold(hand, def, Db.GripFit(def, index), studio.Root);
                var trigger = (MeshInstance3D)model.FindChild("Trigger", true, false);
                var rail = (MeshInstance3D)model.FindChild("Rail", true, false);
                var anvil = (MeshInstance3D)model.FindChild("Anvil", true, false);
                var animator = new ToolAnimator();
                animator.Setup(model, def.Action);
                var leverRest = trigger.Transform;
                var anvilRest = anvil.Transform;
                var railRest = rail.Transform;
                var nose = trigger.Mesh.GetFaces().MinBy(vertex => model.ToLocal(trigger.ToGlobal(vertex)).Z);
                var noseAtRest = model.ToLocal(trigger.ToGlobal(nose));
                AssertFloat(ModelStudio.Vertices(model, anvil).Min(vertex => vertex.Z))
                    .OverrideFailureMessage("the forming plate ends at the configured staple plane")
                    .IsEqualApprox(-def.Length, 0.0003f);
                await Frames.NextPhysics();
                AssertInt(GripCheck.Clipped(hand)).OverrideFailureMessage($"the resting office stapler clears glove {index}").IsEqual(0);
                if (index == 0) {
                    hand.Visible = false;
                    await studio.Capture(model, "office_stapler_model", "00_rest_oblique",
                        "the raised upper lever joins the rear hinge above a fixed rail and supported forming plate", -0.065f);
                    await studio.Capture(model, "office_stapler_model", "01_rest_profile",
                        "the upper lever, rear hinge, fixed rail and forming plate are connected above the shaped black base",
                        new Vector3(0f, 0.01f, -0.065f), new Vector3(0.4f, 0.001f, 0.0001f));
                    hand.Visible = true;
                }
                await studio.Capture(model, "office_stapler_model", $"02_held_{index}",
                    $"glove {index} holds the office stapler without clipping; the rail and forming plate remain exposed", -0.065f);
                foreach (var fraction in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f }) {
                    animator.Animate(false, false, 1f);
                    animator.Animate(true, false, fraction / 6f);
                    if (index == 0) {
                        ClearsFixedParts(model, trigger, rail, anvil, fraction, def.Length / 0.14f);
                    }
                    if (fraction > 0f) {
                        hand.Trigger = true;
                        hand.SnapPose(GripCheck.ShoulderOf(hand));
                        GripCheck.RebuildSolid(model);
                        await Frames.NextPhysics();
                        AssertInt(GripCheck.Clipped(hand))
                            .OverrideFailureMessage($"the office stapler at {fraction:P0} squeeze clears glove {index}").IsEqual(0);
                    }
                }
                AssertFloat(noseAtRest.Y - model.ToLocal(trigger.ToGlobal(nose)).Y)
                    .OverrideFailureMessage("the front of the upper lever lowers toward the forming plate when squeezed").IsGreater(0.03f);
                AssertBool(anvil.Transform == anvilRest && rail.Transform == railRest)
                    .OverrideFailureMessage("the forming plate and rail stay fixed while the lever squeezes").IsTrue();
                if (index == 1) {
                    await studio.Capture(model, "office_stapler_model", "03_squeezed",
                        "the upper lever closes toward the fixed rail and forming plate without crossing them or the glove", -0.065f);
                }
                hand.Trigger = false;
                hand.SnapPose(GripCheck.ShoulderOf(hand));
                animator.Animate(false, false, 1f);
                AssertBool(trigger.Transform == leverRest)
                    .OverrideFailureMessage("release restores the exact resting upper-lever pose").IsTrue();
                if (index == 1) {
                    await studio.Capture(model, "office_stapler_model", "04_released",
                        "the upper lever reopens at the rear hinge while the rail, forming plate and fitted glove retain their shape", -0.065f);
                }
                model.QueueFree();
                hand.GetParent().QueueFree();
                await Frames.NextPhysics();
            }
        } finally {
            await studio.Finish();
        }
    }

    private static void ClearsFixedParts(Node3D model, MeshInstance3D trigger, MeshInstance3D rail, MeshInstance3D anvil, float fraction, float scale) {
        var moving = ModelStudio.Vertices(model, trigger).ToArray();
        using var movingTree = new TriangleMesh();
        AssertBool(movingTree.CreateFromFaces(moving.Select(point => point * QueryScale).ToArray())).OverrideFailureMessage("the imported upper lever has a queryable triangle mesh").IsTrue();
        foreach (var (part, xs, from, to, count) in new[] {
            (rail, new[] { -0.0123f, -0.0117f, 0.0117f, 0.0123f }, -0.117f, -0.035f, 35),
            (anvil, new[] { -0.009f, -0.003f, 0.003f, 0.009f }, -0.137f, -0.125f, 10),
        }) {
            var fixedFaces = ModelStudio.Vertices(model, part).ToArray();
            using var fixedTree = new TriangleMesh();
            AssertBool(fixedTree.CreateFromFaces(fixedFaces.Select(point => point * QueryScale).ToArray())).OverrideFailureMessage($"{part.Name} has a queryable triangle mesh").IsTrue();
            var center = (fixedFaces[0] + fixedFaces[1] + fixedFaces[2]) / 3f;
            var normal = (fixedFaces[1] - fixedFaces[0]).Cross(fixedFaces[2] - fixedFaces[0]).Normalized();
            AssertBool(fixedTree.IntersectSegment((center - normal * 0.01f) * QueryScale, (center + normal * 0.01f) * QueryScale).Count > 0
                || fixedTree.IntersectSegment((center + normal * 0.01f) * QueryScale, (center - normal * 0.01f) * QueryScale).Count > 0)
                .OverrideFailureMessage($"the imported {part.Name} query detects a segment crossing one of its actual faces").IsTrue();
            AssertBool(!EdgesCross(moving, fixedTree) && !EdgesCross(fixedFaces, movingTree))
                .OverrideFailureMessage($"at {fraction:P0} squeeze no imported upper-lever edge crosses {part.Name} and no {part.Name} edge crosses the lever").IsTrue();
            var checkedPoints = 0;
            var minimumGap = float.PositiveInfinity;
            foreach (var x in xs) {
                for (var step = 0; step < count; step++) {
                    var z = Mathf.Lerp(from, to, step / (float)(count - 1)) * scale;
                    var lower = Heights(moving, x, z);
                    var upper = Heights(fixedFaces, x, z);
                    if (lower is { } movingRange && upper is { } fixedRange) {
                        checkedPoints++;
                        minimumGap = Mathf.Min(minimumGap, movingRange.Minimum - fixedRange.Maximum);
                    }
                }
            }
            AssertBool(checkedPoints > 0 && minimumGap > 0.0001f)
                .OverrideFailureMessage($"at {fraction:P0} squeeze the imported upper lever stays above {part.Name} ({checkedPoints} points, {minimumGap * 1000f:0.000} mm)")
                .IsTrue();
        }
    }

    private static bool EdgesCross(Vector3[] faces, TriangleMesh target) {
        for (var index = 0; index < faces.Length; index += 3) {
            for (var edge = 0; edge < 3; edge++) {
                var from = faces[index + edge] * QueryScale;
                var to = faces[index + (edge + 1) % 3] * QueryScale;
                if (target.IntersectSegment(from, to).Count > 0 || target.IntersectSegment(to, from).Count > 0) {
                    return true;
                }
            }
        }
        return false;
    }

    // Vertical intersections with the actual imported triangles, including the lower face of the upper lever.
    private static (float Minimum, float Maximum)? Heights(Vector3[] faces, float x, float z) {
        var minimum = float.PositiveInfinity;
        var maximum = float.NegativeInfinity;
        for (var index = 0; index < faces.Length; index += 3) {
            var a = faces[index];
            var u = faces[index + 1] - a;
            var v = faces[index + 2] - a;
            var det = u.X * v.Z - u.Z * v.X;
            if (Mathf.Abs(det) < 1e-12f) {
                continue;
            }
            var qx = x - a.X;
            var qz = z - a.Z;
            var first = (qx * v.Z - qz * v.X) / det;
            var second = (u.X * qz - u.Z * qx) / det;
            if (first < -0.00001f || second < -0.00001f || first + second > 1.00001f) {
                continue;
            }
            var y = a.Y + first * u.Y + second * v.Y;
            minimum = Mathf.Min(minimum, y);
            maximum = Mathf.Max(maximum, y);
        }
        return float.IsPositiveInfinity(minimum) ? null : (minimum, maximum);
    }
}
