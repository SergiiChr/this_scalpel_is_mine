namespace Scalpel.Tests.Patients;

/// <summary>Painting the wound map is cheap; uploading a layer's whole image isn't. Painting every frame uploads at
/// the tissue sim's rate, while the first paint after a quiet spell goes up with its own frame.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
public class WoundMapTest {
    private const float Frame = 1f / 60f;

    [TestCase]
    public void PaintingEveryFrameUploadsAtTheSimRate() {
        var map = new WoundMap(WoundMap.MinSize);
        map.Flush(Frame);
        AssertInt(map.Uploads).OverrideFailureMessage("nothing painted, nothing to upload").IsEqual(0);
        Paint(map);
        map.Flush(Frame);
        AssertInt(map.Uploads).OverrideFailureMessage("the first paint after a quiet spell goes up with its frame").IsEqual(1);
        for (var i = 0; i < 60; i++) {
            Paint(map);
            map.Flush(Frame);
        }
        AssertInt(map.Uploads).OverrideFailureMessage("a second of painting at 60 fps uploads 30 times").IsEqual(31);
        for (var i = 0; i < 30; i++) {
            Paint(map);
            map.Flush(Frame * 1.01f);
        }
        AssertInt(map.Uploads).OverrideFailureMessage("frames a hair short of two per step still upload every other frame")
            .IsEqual(46);
        Paint(map);
        map.Flush(Frame);
        map.Flush(1f);
        AssertInt(map.Uploads).OverrideFailureMessage("a paint left unflushed goes up on the next upload frame").IsEqual(47);
        for (var i = 0; i < 30; i++) {
            Paint(map);
            map.Flush(WoundMap.UploadInterval);
        }
        AssertInt(map.Uploads).OverrideFailureMessage("at the sim rate every frame uploads").IsEqual(77);
    }

    private static void Paint(WoundMap map) =>
        map.Disk(WoundMap.Layer.Fluids, WoundMap.Blood, new Vector2(0.5f, 0.5f), 0.05f, 1f, WoundMap.Mode.Max);
}
