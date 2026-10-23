namespace Scalpel.Tests.Patients;

[TestSuite, RequireGodotRuntime]
[TestCategory("smoke"), TestCategory("liquids")]
public class BodyBloodLayerTest {
    [TestCase]
    public void CoverageStaysContinuousAcrossAnArbitraryInputSplit() {
        using var layer = new BodyBloodLayer();
        var samples = new List<BodyBloodLayer.Sample>();
        // Two independently supplied halves share a regular wet lattice. The split at x=0 has no physical meaning.
        for (var x = -20; x < 20; x++) {
            for (var z = -5; z <= 5; z++) {
                var at = new Vector3((x + 0.5f) * 0.005f, 0f, z * 0.005f);
                samples.Add(new BodyBloodLayer.Sample(at, Vector3.Up, 0.8f, 0.009f));
            }
        }

        layer.Rebuild([.. samples]);
        var left = layer.CoverageAt(new Vector3(-0.0025f, 0f, 0f));
        var seam = layer.CoverageAt(Vector3.Zero);
        var right = layer.CoverageAt(new Vector3(0.0025f, 0f, 0f));

        AssertFloat(seam).OverrideFailureMessage("coverage remains present at the arbitrary boundary between skin meshes")
            .IsGreater(0.7f);
        AssertFloat(Mathf.Abs(left - right)).OverrideFailureMessage("the arbitrary input split creates no visible coverage step")
            .IsLess(0.08f);
    }

    [TestCase]
    public void RebuildClearsOldCoverageAndPlacementFollowsTheBody() {
        using var layer = new BodyBloodLayer();
        using var material = new ShaderMaterial { Shader = Materials.Skin };
        layer.Bind(material);
        AssertObject(material.GetShaderParameter("body_blood_atlas").AsGodotObject())
            .OverrideFailureMessage("the skin material reads the shared layer texture").IsSame(layer.Texture);
        var local = new Vector3(0.01f, 0f, -0.005f);
        var samples = new[] { new BodyBloodLayer.Sample(local, Vector3.Up, 0.9f, 0.012f) };
        layer.Rebuild(samples);
        var before = layer.CoverageAt(local);
        AssertFloat(before).OverrideFailureMessage("a supplied wet sample appears in the shared layer").IsGreater(0.7f);

        var body = new Transform3D(new Basis(Vector3.Up, 0.7f), new Vector3(0.4f, 0.2f, -0.3f));
        layer.Place(body.AffineInverse());
        var afterMove = layer.CoverageAt(body * local);
        AssertFloat(afterMove).OverrideFailureMessage("moving the body preserves coverage at the same point on its skin")
            .IsEqualApprox(before, 0.02f);

        layer.Rebuild([]);
        AssertFloat(layer.CoverageAt(body * local)).OverrideFailureMessage("rebuilding a dry layer clears the previous wet field")
            .IsEqual(0f);
    }
}
