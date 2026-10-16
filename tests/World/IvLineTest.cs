namespace Scalpel.Tests.World;

/// <summary>
/// The IV tubing as a player meets it: loose tubing hanging from the stand's drip chamber, its bare end hung across from
/// it. Connected at the stand to the catheter in hand, it follows the catheter into the arm. A surgeon pushing into
/// its slack only moves it aside; one who catches it and walks off rips the catheter out: the skin tears and the
/// catheter falls on the tubing's end, to be picked up and put back in. A catheter put in without the tubing sticks, but nothing runs into it. The
/// tubing never goes through the patient, a surgeon or the furniture.
/// </summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("tool_iv_catheter"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class IvLineTest {
    // The frame the surgeon walks the catheter from the tray to the arm also goes over in IvBagTest, without the tubing.
    private static readonly BudgetAllowance BudgetOverride = new("connecting the line and carrying the catheter to the arm take up to 11 ms of the game's own work here, as picking the catheter up did before the line (IvBagTest)", 2.5f);
    private static readonly BudgetAllowance BagBudgetOverride = new("picking the saline bag up takes up to 18 ms of the game's own work with key frames, as in IvBagTest", 4.5f);

    /// <summary>How far (meters) the tubing may go into skin, furniture or a surgeon: where it bends around a curve, the
    /// straight stretch between two of its points cuts the corner by about this much.</summary>
    private const float Graze = 0.003f;
    /// <summary>Walking a moment into the slack (seconds): not far enough to pull it out.</summary>
    private const float Brush = 0.15f;
    /// <summary>Walking on with the tubing caught (seconds): far enough to pull it out.</summary>
    private const float WalkOff = 1.5f;
    /// <summary>How close to the table's middle (meters across it) a surgeon stands clear of its edge.</summary>
    private const float TableClear = 0.58f;

    [TestCase]
    public async Task ConnectedCatheterGoesInAndWalkingOffWithTheLineCaughtRipsItOut() {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        KeyFrames? shots = null;
        try {
            await driver.Start("appendectomy");
            SurgeryState.PatientIsAsleep(driver.Patient);
            var surgery = driver.Surgery;
            var patient = driver.Patient;
            var line = surgery.Room.IvLine;
            if (KeyFrames.Wanted()) {
                shots = new KeyFrames();
                driver.AddChild(shots);
                shots.Begin(surgery, "iv_line");
                driver.OnKeyFrame = (keyFrame, description) => keyFrame switch {
                    "on_catheter" => shots.CaptureAt(keyFrame, description, line.Catheter!.Middle(), 0.3f),
                    "in_arm" => shots.CaptureAt(keyFrame, description, line.Dressing!.GlobalPosition, 0.3f),
                    "torn" => shots.CaptureAt(keyFrame, description, line.Tear!.GlobalPosition, 0.2f),
                    "loose_end" => shots.CaptureAt(keyFrame, description, line.Points[^1], 0.4f),
                    // Square onto the loop, from over the table: the wall is behind the stand.
                    "clipped" => shots.CaptureOblique(keyFrame, description, Center(line),
                        (line.Points[^1] - line.Points[0]).Cross(Vector3.Up), 1.5f),
                    // Level views: one from above at this distance would be over the ceiling.
                    "pushed" => ShowingLegs(driver.Me, () => shots.CaptureFacing(keyFrame, description,
                        driver.Me.GlobalPosition + new Vector3(0f, 0.4f, 0f), Vector3.Right, 1.4f)),
                    _ => shots.CaptureOblique(keyFrame, description, Middle(line), driver.Me.GlobalPosition - Middle(line), 1.6f),
                };
            }
            driver.Budget.Clear();

            var clip = line.ClipAt;
            var lowest = line.Points.Min(point => point.Y);
            AssertBool(line.End == IvLineEnd.Clipped && line.Points[^1].DistanceTo(clip) < 0.001f && lowest < clip.Y - 0.3f)
                .OverrideFailureMessage($"the tubing hangs from the stand in a loop down to {lowest:0.00} m, its bare end "
                    + $"hung on the stand ({line.End}, end {line.Points[^1]}, clip {clip})")
                .IsTrue();
            AssertBool(line.Asleep).OverrideFailureMessage($"hanging still, it isn't simulated (moved {line.Moved * 1000f:0.000} mm): {string.Join(" ", line.Points)}").IsTrue();
            AssertClear(line, surgery, "clipped on the stand");
            await driver.Capture("clipped", "the tubing hanging from the drip chamber in a loose loop, its bare end hung "
                + "on the stand across from it, not through the stand's base or the floor");

            var catheter = (await driver.PlayerRequestsItem("iv_catheter"))!;
            await driver.PlayerConnectsIvLine();
            var back = catheter.GlobalTransform * new Vector3(0f, 0f, catheter.Bounds.End.Z);
            await driver.AssertAndCapture(line.End == IvLineEnd.Catheter && line.Catheter == catheter
                    && line.Points[^1].DistanceTo(back) < 0.01f,
                "connected at the stand: the tubing's end on the back of the catheter in hand, hanging from it to the "
                    + "stand", "on_catheter");

            await driver.PlayerPutsCatheterIn(catheter);
            var dressing = line.Dressing!;
            var site = dressing.GlobalPosition;
            await Frames.Physics(30);
            AssertBool(patient.IvWorking && line.IsAttached
                    && line.Points[^1].DistanceTo(dressing.ToGlobal(IvDressing.Exit)) < 0.001f)
                .OverrideFailureMessage($"in the vein with the tubing on it, the line works and hangs from the dressing "
                    + $"(working {patient.IvWorking}, {line.End})")
                .IsTrue();
            AssertClear(line, surgery, "in the arm");
            await driver.Capture("in_arm", "the catheter taped down under its film, the tubing taped along the arm and "
                + "leaving it, lying over the arm and the table's edge, not through either");
            await driver.Capture("hanging", "the tubing from the arm sagging loose to the stand, not through the "
                + "patient, the table or the surgeon");

            // Stepping into the slack from the table's edge, the tubing ends up behind the legs: walking back away from
            // the table catches it.
            var low = line.Points.Where(point => point.Y < 1f && point.Z > TableClear).ToList();
            AssertBool(low.Count > 0)
                .OverrideFailureMessage($"the tubing hangs out beside the table: {string.Join(" ", line.Points)}").IsTrue();
            var spot = new Vector3(low.MinBy(point => point.Z).X, 0f, TableClear);
            SurgeryState.SurgeonStandsAt(driver.Me, spot, 0f);
            await Frames.Physics(10);
            await driver.PlayerHoldsWalkKey(InputActions.MoveBack, Brush, () => { });
            await Frames.Physics(30);
            AssertBool(patient.IvWorking && line.Stretch < IvLine.YankStretch)
                .OverrideFailureMessage($"pushed into the slack, the tubing gives and the catheter stays in (stretched "
                    + $"{line.Stretch:0.00} times)")
                .IsTrue();
            AssertClear(line, surgery, "pushed by the legs");
            await driver.Capture("pushed", "the slack pushed aside by the surgeon's legs, bent around them, not through "
                + "them; the catheter still in");

            var clipping = "";
            var stretched = 1f;
            await driver.PlayerHoldsWalkKey(InputActions.MoveBack, WalkOff, () => {
                if (clipping.Length == 0 && patient.IvSet) {
                    clipping = Clipping(line, surgery);
                    stretched = Mathf.Max(stretched, line.Stretch);
                }
            });
            AssertString(clipping).OverrideFailureMessage($"caught on the legs, the tubing never goes through them: {clipping}")
                .IsEmpty();
            AssertBool(!patient.IvSet && patient.Flags.ContainsKey("iv_pulled"))
                .OverrideFailureMessage($"walking off with the tubing caught on the legs rips the catheter out (stretched "
                    + $"{stretched:0.00} times, {driver.Me.GlobalPosition})")
                .IsTrue();
            await Frames.Seconds(2f);
            var ripped = line.Catheter;
            AssertBool(line.End == IvLineEnd.Catheter && ripped is { State: ToolState.Free, Charges: 1 }
                    && line.Points[^1].DistanceTo(ripped.GlobalTransform * new Vector3(0f, 0f, ripped.Bounds.End.Z)) < 0.01f)
                .OverrideFailureMessage($"the catheter came out still on the tubing's end, a catheter to pick up "
                    + $"({line.End}, {ripped?.State}, {ripped?.Charges} charges)")
                .IsTrue();
            AssertClear(line, surgery, "ripped out");
            var tear = line.Tear;
            await driver.AssertAndCapture(tear is not null && tear.GetParent() is Forearm && line.Dressing is null
                    && tear.GlobalPosition.DistanceTo(site) < 0.001f,
                "a ragged tear in the skin where the catheter was, the dressing gone with it", "torn");
            await driver.Capture("loose_end", "the catheter lying where it fell with the tubing on its back, not "
                + "through the floor, the table or the patient");
            driver.Budget.Check(localOverride: BudgetOverride);
        } finally {
            shots?.End();
            await driver.Stop();
            RenderingServer.RenderLoopEnabled = true;
        }
    }

    /// <summary>The scenario's own line, in before the surgery started, ripped out: its catheter is picked up off the
    /// floor beside the tray's spare one, put back in the vein and the line works again.</summary>
    [TestCase]
    public async Task RippedOutPreopCatheterIsPickedUpAndGoesBackIn() {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        try {
            await driver.Start("bullet_muscle");
            SurgeryState.PatientIsAsleep(driver.Patient);
            var patient = driver.Patient;
            var line = driver.Surgery.Room.IvLine;
            AssertBool(patient.IvWorking).OverrideFailureMessage("the scenario starts with a working line").IsTrue();
            SurgeryState.IvLineIsRippedOut(patient, driver.Me);
            var ripped = line.Catheter;
            AssertBool(!patient.IvSet && line.End == IvLineEnd.Catheter && ripped is { State: ToolState.Free })
                .OverrideFailureMessage($"ripped out, the catheter is on the tubing's end, free to pick up ({line.End}, "
                    + $"{ripped?.State})")
                .IsTrue();

            AssertObject(await driver.PlayerPicksUp(ripped!))
                .OverrideFailureMessage($"the player picks the ripped-out catheter up\n{driver.Recent()}").IsSame(ripped!);
            AssertBool(line.Catheter == ripped).OverrideFailureMessage($"the tubing stays on it in hand ({line.End})")
                .IsTrue();
            await driver.PlayerPutsCatheterIn(ripped!);
            await Frames.Physics(30);
            AssertBool(patient.IvWorking && line.IsAttached
                    && line.Points[^1].DistanceTo(line.Dressing!.ToGlobal(IvDressing.Exit)) < 0.001f)
                .OverrideFailureMessage($"put back into the vein, the line works again and hangs from the new dressing "
                    + $"(working {patient.IvWorking}, {line.End})")
                .IsTrue();
        } finally {
            await driver.Stop();
            RenderingServer.RenderLoopEnabled = true;
        }
    }

    [TestCase]
    public async Task CatheterWithoutTheLineGoesInButNothingRunsIntoItUntilOneOnTheLineReplacesIt() {
        FrameBudget.RenderingOff();
        var driver = SurgeryDriver.Create();
        KeyFrames? shots = null;
        try {
            await driver.Start("appendectomy");
            SurgeryState.PatientIsAsleep(driver.Patient);
            var surgery = driver.Surgery;
            var patient = driver.Patient;
            var line = surgery.Room.IvLine;
            if (KeyFrames.Wanted()) {
                shots = new KeyFrames();
                driver.AddChild(shots);
                shots.Begin(surgery, "iv_line_unconnected");
                driver.OnKeyFrame = (keyFrame, description) =>
                    shots.CaptureAt(keyFrame, description, line.Dressing!.GlobalPosition, 0.25f);
            }
            var toasts = new List<string>();
            surgery.Hud.Toasted += toasts.Add;
            driver.Budget.Clear();

            var catheter = (await driver.PlayerRequestsItem("iv_catheter"))!;
            await driver.PlayerPutsCatheterIn(catheter);
            await driver.AssertAndCapture(patient.IvSet && patient.IvInVein && !patient.IvConnected
                    && line.End == IvLineEnd.Clipped && line.Dressing is not null,
                "the catheter taped into the vein without tubing: no tube taped along the arm, the tubing still hung "
                    + "on the stand", "unconnected");
            if (driver.FreeTools("saline_bag").Count == 0) {
                SurgeryState.ToolIsOnTray(surgery, "saline_bag");
                await Frames.Seconds(1f);
            }
            var bag = (await driver.PlayerRequestsItem("saline_bag"))!;
            await driver.PlayerInteracts("Swap IV bag");
            AssertBool(!patient.IvWorking && bag.State == ToolState.Held
                    && toasts.Contains("Nothing goes in. The catheter in the arm isn't on the IV line."))
                .OverrideFailureMessage($"a bag hung on a line that isn't on the catheter gives nothing, and says why "
                    + $"({bag.State}; {string.Join(", ", toasts)})")
                .IsTrue();
            await driver.PlayerPutsDown();
            await driver.PlayerSetsIv();
            await Frames.Physics(30);
            AssertBool(patient.IvWorking && line.IsAttached && line.Dressing is not null)
                .OverrideFailureMessage($"a catheter on the line takes the place of the one that isn't, and the line works "
                    + $"(set {patient.IvSet}, in the vein {patient.IvInVein}, connected {patient.IvConnected}, {line.End}). Last steps:\n"
                    + driver.Recent())
                .IsTrue();
            surgery.Hud.Toasted -= toasts.Add;
            driver.Budget.Check(localOverride: BagBudgetOverride);
        } finally {
            shots?.End();
            await driver.Stop();
            RenderingServer.RenderLoopEnabled = true;
        }
    }

    /// <summary>The middle of the tubing, between its ends.</summary>
    private static Vector3 Middle(IvLine line) => (line.Points[0] + line.Points[^1]) * 0.5f;

    /// <summary>The middle of the whole tubing, its loop included: <see cref="Middle"/> is between its ends.</summary>
    private static Vector3 Center(IvLine line) => line.Points.Aggregate(Vector3.Zero, (sum, point) => sum + point) / line.Points.Count;

    /// <summary>Runs <paramref name="capture"/> with the local surgeon's body shown: first person hides it, and the
    /// shot is of the tubing against the legs.</summary>
    private static async Task ShowingLegs(Surgeon surgeon, Func<Task> capture) {
        surgeon.BodyModel.Visible = true;
        try {
            await capture();
        } finally {
            surgeon.BodyModel.Visible = false;
        }
    }

    private static void AssertClear(IvLine line, Surgery surgery, string when) {
        var clipping = Clipping(line, surgery);
        AssertString(clipping).OverrideFailureMessage($"{when}: the tubing goes through {clipping}").IsEmpty();
    }

    /// <summary>Where the tubing goes more than <see cref="Graze"/> into the patient's skin, a solid box (furniture,
    /// walls, the floor) or a surgeon, checked at its points and halfway between them; empty when nowhere.</summary>
    private static string Clipping(IvLine line, Surgery surgery) {
        var boxes = surgery.Room.FindChildren("*", nameof(CollisionShape3D), true, false).Cast<CollisionShape3D>()
            .Where(shape => shape.Shape is BoxShape3D && shape.GetParent() is CollisionObject3D body
                && (body.CollisionLayer & 1) != 0)
            .ToList();
        var points = line.Points;
        for (var i = 1; i < (2 * points.Count) - 2; i++) {
            var point = i % 2 == 0 ? points[i / 2] : (points[i / 2] + points[(i / 2) + 1]) * 0.5f;
            foreach (var shape in boxes) {
                var half = (((BoxShape3D)shape.Shape).Size * 0.5f) - (Vector3.One * Graze);
                var local = shape.GlobalTransform.AffineInverse() * point;
                if (Mathf.Abs(local.X) < half.X && Mathf.Abs(local.Y) < half.Y && Mathf.Abs(local.Z) < half.Z) {
                    return $"{shape.GetParent().Name} at {point}";
                }
            }
            if (UnderSkin(line, point) > Graze) {
                return $"the patient's skin at {point}, {UnderSkin(line, point) * 1000f:0.0} mm deep";
            }
            foreach (var surgeon in surgery.Surgeons.Values) {
                var feet = surgeon.GlobalPosition;
                var axis = Geometry3D.GetClosestPointToSegment(point, feet + (Vector3.Up * IvLine.BodyRadius),
                    feet + (Vector3.Up * (IvLine.BodyHeight - IvLine.BodyRadius)));
                if (point.DistanceTo(axis) < IvLine.BodyRadius - Graze) {
                    return $"{surgeon.DisplayName} at {point}";
                }
            }
        }
        return "";
    }

    /// <summary>How deep under the patient's skin <paramref name="point"/> is, 0 when outside: from inside a limb the
    /// skin met up and down faces away from the point.</summary>
    private static float UnderSkin(Node3D world, Vector3 point) {
        var up = Rays.Cast(world, point, point + Vector3.Up, PatientBody.SurfaceLayer);
        var down = Rays.Cast(world, point, point + Vector3.Down, PatientBody.SurfaceLayer);
        if (up is not { } above || down is not { } below || above.Normal.Y < 0f || below.Normal.Y > 0f) {
            return 0f;
        }
        return Mathf.Min(above.Position.Y - point.Y, point.Y - below.Position.Y);
    }
}
