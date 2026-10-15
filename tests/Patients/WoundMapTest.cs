namespace Scalpel.Tests.Patients;

/// <summary>Painting the wound map is cheap; uploading a layer's whole image isn't. The body uploads what's painted
/// once per sim step, so a wipe or bleed painting every frame at 60 fps uploads every other frame, and nothing painted
/// in between is lost.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("smoke")]
[GodotArgs("--fixed-fps", "60")]
public class WoundMapTest {
    [TestCase]
    public void FlushPutsThePaintedPixelsOnTheTexture() {
        var map = new WoundMap(WoundMap.MinSize);
        map.Flush();
        AssertInt(map.Uploads).OverrideFailureMessage("nothing painted, nothing to upload").IsEqual(0);
        Paint(map);
        AssertFloat(map.Value(WoundMap.Layer.Fluids, WoundMap.Blood, new Vector2(0.5f, 0.5f)))
            .OverrideFailureMessage("the disk's middle is painted").IsGreater(0.9f);
        AssertBool(map.Uploaded(WoundMap.Layer.Fluids).All(b => b == 0))
            .OverrideFailureMessage("until it's flushed, the texture still holds clean skin").IsTrue();
        map.Flush();
        AssertInt(map.Uploads).OverrideFailureMessage("a painted layer goes up").IsEqual(1);
        AssertTextureHoldsThePaint(map);
    }

    [TestCase]
    public async Task PaintingEveryFrameUploadsOncePerSimStep() {
        var driver = SurgeryDriver.Create();
        await driver.Start("appendectomy");
        var map = driver.Body.WoundMap;
        var before = map.Uploads;
        for (var i = 0; i < 60; i++) {
            Paint(map);
            await Frames.NextProcess();
        }
        AssertInt(map.Uploads - before)
            .OverrideFailureMessage("a second of painting at 60 fps uploads once per sim step, 30 times").IsBetween(29, 31);
        await Frames.Process(2);
        AssertTextureHoldsThePaint(map);
        await driver.Stop();
    }

    private static void Paint(WoundMap map) =>
        map.Disk(WoundMap.Layer.Fluids, WoundMap.Blood, new Vector2(0.5f, 0.5f), 0.05f, 1f, WoundMap.Mode.Max);

    private static void AssertTextureHoldsThePaint(WoundMap map) =>
        AssertBool(map.Uploaded(WoundMap.Layer.Fluids).SequenceEqual(map.Snapshot(WoundMap.Layer.Fluids)))
            .OverrideFailureMessage("the texture holds the pixels as painted").IsTrue();
}
