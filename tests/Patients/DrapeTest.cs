namespace Scalpel.Tests.Patients;

/// <summary>
/// The surgical drape covers the body around the site on the front, the chest, a thigh and the back: no skin or gown
/// of the trunk and legs pokes through the sheet outside its opening, its long sides hang down to the table past the
/// legs, and it lies over no arm, which may lift under it. Key frames: on the belly, the sheet from the surgeon's side
/// and the other; SurgeonArmsTest shows what the surgeon sees.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class DrapeTest {
    /// <summary>How far apart (meters) the body is probed under the sheet.</summary>
    private const float Step = 0.01f;
    /// <summary>Least room (meters) between the skin and the sheet over it: most of Drape.Offset.</summary>
    private const float Clearance = 0.005f;
    /// <summary>Along the legs, past the hands (body x): nothing beside the body holds the sheet's sides in.</summary>
    private static readonly float[] Legs = [-1f, -0.7f];

    [TestCase("appendectomy")]
    [TestCase("heart_attack")]
    [TestCase("bullet_muscle")]
    [TestCase("knife_back")]
    public async Task DrapeCoversTheBodyAndHangsToTheTable(string scenario) {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        await driver.Start(scenario);
        var body = driver.Body;
        var drape = body.Drape!;
        var root = body.Root;
        var up = drape.Up;
        var trunkFaces = new List<Vector3>();
        var armFaces = new List<Vector3>();
        foreach (var name in (string[])["Body", "Gown"]) {
            var mesh = (MeshInstance3D)root.FindChild(name, true, false);
            Drape.Split(mesh, root.GlobalTransform.AffineInverse() * mesh.GlobalTransform, trunkFaces, armFaces);
        }
        var trunk = new TriangleMesh();
        trunk.CreateFromFaces([.. trunkFaces]);
        // The sheet as it was laid, before breathing lifts it with the trunk.
        var cloth = drape.Mesh.GetFaces();
        var sheet = new TriangleMesh();
        sheet.CreateFromFaces(cloth);
        float? HeightAt(TriangleMesh mesh, float x, float z) {
            var hit = mesh.IntersectRay(new Vector3(x, up * 0.5f, z), Vector3.Down * up);
            return hit.Count > 0 ? up * hit["position"].AsVector3().Y : null;
        }

        var through = new List<string>();
        // Probes between the sheet's grid lines, never on an edge of the opening's thin border strips.
        for (var x = Drape.FromX + Step / 2f; x < Drape.ToX; x += Step) {
            for (var z = -Drape.MinHalfWidth + Step / 2f; z < Drape.MinHalfWidth; z += Step) {
                if (HeightAt(trunk, x, z) is not { } skin) {
                    continue;
                }
                var uv = body.WorldToUv(root.ToGlobal(new Vector3(x, up * skin, z)));
                var open = uv.X > Drape.Frame && uv.X < 1f - Drape.Frame && uv.Y > Drape.Frame && uv.Y < 1f - Drape.Frame;
                if (!open && (HeightAt(sheet, x, z) is not { } over || over - skin < Clearance)) {
                    through.Add($"({x:0.00}, {z:0.00})");
                }
            }
        }
        AssertInt(through.Count).OverrideFailureMessage(
                $"{scenario}: the sheet lies at least {Clearance * 1000f:0} mm over the trunk and legs outside its opening "
                + $"({through.Count} spots through it, body x and z: {string.Join(", ", through.Take(8))})")
            .IsEqual(0);

        foreach (var x in Legs) {
            var section = cloth.Where(p => Mathf.Abs(p.X - x) < Drape.Cell / 2f).ToArray();
            foreach (var sign in (float[])[-1f, 1f]) {
                var side = section.Where(p => p.Z * sign > 0f).ToArray();
                var reach = side.Max(p => p.Z * sign);
                var lowest = side.Min(p => up * p.Y);
                AssertBool(reach > Drape.MaxHalfWidth - 0.001f && lowest < -PatientBody.HalfHeight + Drape.Offset)
                    .OverrideFailureMessage($"{scenario}: at body x {x:0.0} the sheet reaches {Drape.MaxHalfWidth:0.00} m "
                        + $"to the side and hangs down to the table (reaches {reach:0.000} m, its lowest {lowest:0.000} m, "
                        + $"the table {-PatientBody.HalfHeight:0.000} m)")
                    .IsTrue();
            }
        }

        // Inside the sheet's least width the arm's skin is the armpit's, where the sheet always lay.
        var underSheet = armFaces.Where(p => Mathf.Abs(p.Z) > Drape.MinHalfWidth && HeightAt(sheet, p.X, p.Z) is not null).ToArray();
        AssertInt(underSheet.Length).OverrideFailureMessage(
                $"{scenario}: the sheet lies over no arm past its least width ({underSheet.Length} arm points under it, first at "
                + $"{(underSheet.Length > 0 ? underSheet[0] : Vector3.Zero)})")
            .IsEqual(0);

        if (scenario == "appendectomy" && KeyFrames.Wanted()) {
            var shots = new KeyFrames();
            driver.AddChild(shots);
            shots.Begin(driver.Surgery, "drape");
            var near = (driver.Me.GlobalPosition - root.GlobalPosition).Slide(Vector3.Up).Normalized();
            var hips = root.ToGlobal(new Vector3(-0.15f, 0f, 0f));
            await shots.CaptureOblique("near_side", "the sheet from the surgeon's side, over the belly and the hips: it "
                + "falls smoothly off the flank and hangs straight down to the table beside the hand, no gown or skin "
                + "showing under its side, no jagged edge", hips, near, 0.8f);
            await shots.CaptureOblique("far_side", "the sheet from across the table: the opening over the belly, the "
                + "sheet falling down the far flank to the table on both sides of it, its side straight past the arm",
                hips, -near, 0.8f);
            shots.End();
        }
        await driver.Stop();
    }
}
