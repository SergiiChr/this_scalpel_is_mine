namespace Scalpel.Tests.Patients;

[TestSuite, RequireGodotRuntime]
[TestCategory("liquid_contours")]
[TestCategory("smoke"), TestCategory("liquids")]
public class LiquidShapeTest {
    [TestCase]
    public void LiquidContoursKeepTheFreeSideWhenTheyMeetAnEdge() {
        var outline = LiquidSurface.Outline(0.1f, 1.3f);
        var clipped = LiquidSurface.Clip(outline, new Rect2(-1f, -1f, 1.025f, 2f));
        AssertFloat(clipped.Max(p => p.X)).IsEqualApprox(0.025f, 0.000001f);
        AssertFloat(clipped.Min(p => p.X)).OverrideFailureMessage("contact does not squeeze the opposite free edge")
            .IsEqualApprox(outline.Min(p => p.X), 0.000001f);
        AssertFloat(LiquidSurface.Area(clipped)).IsLess(LiquidSurface.Area(outline));
        AssertFloat(LiquidSurface.Area(outline)).OverrideFailureMessage("organic variation preserves deposited volume")
            .IsEqualApprox(0.01f, 0.000001f);
        AssertInt(clipped.Count(p => Mathf.Abs(p.X - 0.025f) < 0.000001f)).IsGreaterEqual(2);
    }

    [TestCase]
    public void LiquidGridInterpolatesAnObliqueBoundary() {
        var surface = new LiquidSurface();
        surface.Field(new Rect2(Vector2.Zero, Vector2.One), 8, 8, p => 0.813f - p.X - p.Y, (p, _) => new Vector3(p.X, 0f, p.Y));
        using var mesh = surface.Upload();
        var points = mesh.GetFaces();
        AssertInt(points.Length).IsGreater(0);
        AssertBool(points.All(p => p.X + p.Z <= 0.81301f)).OverrideFailureMessage("no fluid triangle crosses its supporting boundary").IsTrue();
        AssertBool(points.Any(p => Mathf.Abs(p.X + p.Z - 0.813f) < 0.00001f))
            .OverrideFailureMessage("the contour reaches the boundary instead of stopping at stair steps").IsTrue();
    }

    [TestCase]
    public void LiquidRibbonKeepsANarrowBentOpeningConnected() {
        var surface = new LiquidSurface();
        var a = new Vector2(0.011f, 0.007f);
        var corner = new Vector2(0.035f, 0.029f);
        var b = new Vector2(0.017f, 0.054f);
        float Inside(Vector2 p) => 0.0006f - Mathf.Min(p.DistanceTo(Geometry2D.GetClosestPointToSegment(p, a, corner)),
            p.DistanceTo(Geometry2D.GetClosestPointToSegment(p, corner, b)));
        var recordedPath = Enumerable.Range(0, 51).Select(i => a.Lerp(corner, i / 50f))
            .Concat(Enumerable.Range(1, 50).Select(i => corner.Lerp(b, i / 50f))).ToArray();
        surface.Ribbon(recordedPath, 0.005f, Inside, (p, _) => new Vector3(p.X, 0f, p.Y));
        using var mesh = surface.Upload();
        var vertices = mesh.GetFaces();
        AssertInt(vertices.Length).IsGreater(30);
        AssertInt(vertices.Length).OverrideFailureMessage("dense input samples do not produce excessive planar geometry").IsLess(2000);
        bool Covered(Vector2 at) => Enumerable.Range(0, vertices.Length / 3).Any(t => Contains(at,
            new Vector2(vertices[t * 3].X, vertices[t * 3].Z), new Vector2(vertices[t * 3 + 1].X, vertices[t * 3 + 1].Z),
            new Vector2(vertices[t * 3 + 2].X, vertices[t * 3 + 2].Z)));
        foreach (var (from, to) in new[] { (a, corner), (corner, b) }) {
            for (var i = 1; i < 20; i++) {
                AssertBool(Covered(from.Lerp(to, i / 20f)))
                    .OverrideFailureMessage($"the narrow bent pool covers {from.Lerp(to, i / 20f)} on {from} to {to}, step {i}").IsTrue();
            }
        }
        AssertBool(Covered(corner)).OverrideFailureMessage("the original turn remains covered rather than cut off by a chord").IsTrue();
    }

    [TestCase]
    public void LiquidOutletsVaryTheirTimingAndRetainQueuedVolume() {
        var pulse = new LiquidPulse(123);
        for (var i = 0; i < 60; i++) { pulse.Add(0.1f); pulse.Advance(1f / 60f, false); }
        AssertFloat(pulse.Pending).IsEqualApprox(6f, 0.00001f);
        var released = pulse.Advance(1f / 60f, true);
        AssertFloat(released).IsEqualApprox(6f, 0.00001f);
        var intervals = new HashSet<int>();
        var widths = new HashSet<float>();
        var elapsed = 0;
        var extra = 0f;
        for (var i = 0; i < 240; i++) {
            pulse.Add(0.01f);
            elapsed++;
            var amount = pulse.Advance(1f / 60f, true);
            if (amount <= 0f) { continue; }
            extra += amount;
            intervals.Add(elapsed);
            widths.Add(pulse.Width);
            elapsed = 0;
        }
        AssertInt(intervals.Count).OverrideFailureMessage("drops do not detach at metronomic intervals").IsGreater(3);
        AssertInt(widths.Count).OverrideFailureMessage("necks change thickness between releases").IsGreater(3);
        AssertFloat(extra + pulse.Pending).IsEqualApprox(2.4f, 0.00001f);
    }
    // Centreline samples lie on shared triangle edges. Permit floating point round-off at those edges.
    private static bool Contains(Vector2 p, Vector2 a, Vector2 b, Vector2 c) {
        var area = (b - a).Cross(c - a);
        if (Mathf.Abs(area) < 1e-12f) { return false; }
        var ab = (b - a).Cross(p - a);
        var bc = (c - b).Cross(p - b);
        var ca = (a - c).Cross(p - c);
        return area > 0f ? Mathf.Min(ab, Mathf.Min(bc, ca)) >= -1e-10f : Mathf.Max(ab, Mathf.Max(bc, ca)) <= 1e-10f;
    }

}
