namespace Scalpel.Tests.Models;

/// <summary>The IV catheter's assembled hub, wings and needle, with its fitted gloves.</summary>
[TestSuite, RequireGodotRuntime]
[TestCategory("tool_iv_catheter"), TestCategory("visual_confirmation")]
[GodotArgs("--fixed-fps", "60")]
public class IvCatheterAppearanceTest {
    [TestCase]
    public async Task HubWingsAndNeedleClearBothGloves() {
        var studio = new ModelStudio(0.17f);
        try {
            var def = Db.Tool("iv_catheter")!;
            for (var index = 0; index < 2; index++) {
                var hand = GripCheck.MakeHand(studio.Root, index);
                var model = GripCheck.Hold(hand, def, Db.GripFit(def, index), studio.Root);
                await Frames.NextPhysics();
                AssertInt(GripCheck.Clipped(hand))
                    .OverrideFailureMessage($"the IV catheter clears glove {index}").IsEqual(0);
                if (index == 0) {
                    hand.Visible = false;
                    await studio.Capture(model, "iv_catheter_model", "00_rest_oblique",
                        "the IV catheter hub, wings and full needle are visible together", new Vector3(0f, 0f, -0.035f),
                        new Vector3(0.17f, 0.14f, 0.15f));
                    await studio.Capture(model, "iv_catheter_model", "01_hub_detail",
                        "the wings meet the hub and the fine needle extends from it",
                        new Vector3(0f, 0f, -0.03f), new Vector3(0.09f, 0.07f, 0.12f), 0.10f);
                    hand.Visible = true;
                }
                await studio.Capture(model, "iv_catheter_model", $"0{index + 2}_held_{index}",
                    $"glove {index} holds the IV catheter without clipping; its hub and needle remain exposed",
                    new Vector3(0f, 0f, -0.035f),
                    index == 1 ? new Vector3(0.17f, 0.14f, -0.15f) : new Vector3(0.17f, 0.14f, 0.15f));
                model.QueueFree();
                hand.GetParent().QueueFree();
                await Frames.NextPhysics();
            }
        } finally {
            await studio.Finish();
        }
    }
}
