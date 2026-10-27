namespace Scalpel.Tests.Support;

/// <summary>Holding a cut open by hand: a lip taken in forceps or hooked with a retractor and drawn aside with the
/// mouse, and a retractor left hooked.</summary>
public partial class SurgeryDriver {
    /// <summary>The first skin beside the middle of <paramref name="cut"/> on <paramref name="side"/> (1 or -1, across
    /// it), and 2 mm into it so jaws close on skin (site uv).</summary>
    public Vector2 LipOf(Wound cut, int side) {
        var along = (cut.Points[^1] - cut.Points[0]).Normalized();
        var step = new Vector2(-along.Y, along.X) * side * Body.MetersToUv(0.001f);
        var lip = cut.Midpoint;
        for (var i = 0; i < 60 && Body.LayerAt(lip) != "skin"; i++) {
            lip += step;
        }
        return lip + (step * 2f);
    }

    /// <summary>The tool in <paramref name="hand"/> lowered onto <paramref name="lip"/> (site uv) and closed on it, or
    /// hooked into it: a <see cref="SkinHold"/> when it took hold.</summary>
    public async Task<SkinHold?> PlayerTakesHoldOfLip(int hand, Vector2 lip) {
        await SwitchTo(hand);
        var tool = Me.HeldTool(hand)!;
        // Pointing ahead, not across: the hand stays back toward the surgeon, out of the way of the opening.
        await PlayerAimsStraight();
        await PlayerReaches(SitePoint(lip));
        Use();
        await Frames.Physics(10);
        Use(false);
        await Frames.Physics(3);
        Note($"{tool.Def.Id} holds {tool.Hold}");
        return tool.Hold as SkinHold;
    }

    /// <summary>The hand holding a lip drawn <paramref name="meters"/> along <paramref name="away"/> (world, across the
    /// floor) with the hand key and the mouse, a millimeter at a time with a pause after each. Returns how far (meters)
    /// the tool's tip went along <paramref name="away"/> for each millimeter.</summary>
    public async Task<List<float>> PlayerDrawsLip(int hand, Vector3 away, float meters) {
        await SwitchTo(hand);
        var tool = Me.HeldTool(hand)!;
        var key = PlayerInput.HandKey(Me);
        var gave = new List<float>();
        const float Stroke = 0.001f;
        const int StrokeFrames = 15;
        for (var drawn = 0f; drawn < meters - 0.0001f; drawn += Stroke) {
            var before = tool.TipPosition();
            Press(key);
            for (var f = 0; f < StrokeFrames; f++) {
                PlayerInput.Mouse(PlayerInput.HandMotion(Me, away * (Stroke / StrokeFrames)));
                await PlayerInput.Delivered();
            }
            Release(key);
            await Frames.Physics(10);
            gave.Add((tool.TipPosition() - before).Dot(away));
        }
        Note($"{tool.Def.Id} drawn {gave.Sum() * 1000f:0.0} mm for {meters * 1000f:0} mm of mouse");
        return gave;
    }

    /// <summary>A tool left hooked in the patient taken back in <paramref name="hand"/> by its tip, let go of the skin and
    /// put down.</summary>
    public async Task PlayerUnhooks(SurgicalTool tool, int hand) {
        await SwitchTo(hand);
        var at = tool.TipPosition() + (Vector3.Up * 0.03f);
        await PlayerWalksTo(at);
        await PlayerReaches(at);
        Press(InputActions.Grab);
        await Frames.Physics(10);
        Release(InputActions.Grab);
        if (Me.HeldTool(hand) != tool) {
            Note($"didn't take the {tool.Def.Id} back: {tool.State}");
            return;
        }
        await PlayerPutsDown();
    }
}
