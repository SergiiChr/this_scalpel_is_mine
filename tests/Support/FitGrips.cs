using GodotDictionary = Godot.Collections.Dictionary;

namespace Scalpel.Tests.Support;

/// <summary>
/// Fits every tool model's grip to the glove and writes data/grips.json (see <see cref="GripFit"/>): for each hand,
/// the glove moves off the tool (toward the back of the hand, and sideways) just until the palm clears it, then each
/// finger opens or closes just until it clears too, and finally closes onto the tool so it rests on it
/// (<see cref="Touch"/>). Run it again after changing tool models, the glove or the grips:
/// godot --headless --path . res://tests/Support/FitGrips.tscn
/// </summary>
public partial class FitGrips : Node3D
{
    private const string Out = "res://data/grips.json";
    private const float LiftStep = 0.003f;
    private const float MaxLift = 0.045f;
    private const float MaxShift = 0.03f;
    private const float CurlStep = 0.05f;
    /// <summary>Most extra curl a finger closes to reach the tool (see <see cref="Touch"/>).</summary>
    private const float TouchReach = 0.4f;
    /// <summary>A finger bent further than this folds into the palm.</summary>
    private const float MaxCurl = 1.3f;
    private static readonly int FingerCount = SurgeonHand.Fingers.Count;

    public override void _Ready() => _ = Fit();

    private async Task Fit()
    {
        var fits = new GodotDictionary
        {
            ["_about"] = "Grip fits per tool model and hand, made by tests/Support/FitGrips.tscn. lift, shift: meters the "
                + "glove moves off the tool toward the back of the hand and toward the pinky side. curl: how far each "
                + "finger closes (index, middle, ring, pinky, thumb).",
        };
        for (var index = 0; index < 2; index++)
        {
            var hand = GripCheck.MakeHand(this, index);
            var side = index == 0 ? "left" : "right";
            foreach (var def in Db.Tools.Values.DistinctBy(def => def.ModelName))
            {
                var style = GripStyle.For(def.Grip).Curl;
                var fit = new GripFit(0f, 0f, [.. style]);
                var tool = GripCheck.Hold(hand, def, fit, this);
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                fit = FitLift(hand, fit, "Palm");
                fit = FitFingers(hand, fit);
                // The palm alone clear isn't always enough (the root of the thumb): move the whole glove, then the
                // fingers again.
                if (Try(hand, fit, "") > 0)
                {
                    fit = FitFingers(hand, FitLift(hand, fit, ""));
                }
                for (var finger = 0; finger < FingerCount; finger++)
                {
                    fit = Touch(hand, fit, finger);
                }
                if (fit.Lift != 0f || fit.Shift != 0f || !fit.Curl!.SequenceEqual(style))
                {
                    var entry = fits.TryGetValue(def.ModelName, out var found) ? found.AsGodotDictionary() : [];
                    // Rounded in double: written from float they'd show float noise (0.006 as 0.00600000005).
                    entry[side] = new GodotDictionary
                    {
                        ["lift"] = Math.Round((double)fit.Lift, 3), ["shift"] = Math.Round((double)fit.Shift, 3),
                        ["curl"] = new Godot.Collections.Array(fit.Curl!.Select(curl => (Variant)Math.Round((double)curl, 2))),
                    };
                    fits[def.ModelName] = entry;
                }
                GD.Print($"{def.ModelName} {side}: lift {fit.Lift:0.000} shift {fit.Shift:0.000} curl [{string.Join(", ", fit.Curl!)}], still clipping {Try(hand, fit, "")}");
                tool.QueueFree();
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            }
            hand.GetParent().QueueFree();
        }
        using var file = FileAccess.Open(Out, FileAccess.ModeFlags.Write);
        file.StoreString(Json.Stringify(fits, "\t", false) + "\n");
        GD.Print($"fit_grips: wrote {Out}");
        GetTree().Quit();
    }

    private static GripFit FitFingers(SurgeonHand hand, GripFit fit)
    {
        for (var finger = 0; finger < FingerCount; finger++)
        {
            fit = FitFinger(hand, fit, finger);
        }
        return fit;
    }

    /// <summary>The smallest move of the glove off the tool (lift toward the back of the hand, shift sideways) that
    /// gets <paramref name="part"/> of the glove clear of it, or failing that the one that clips least.</summary>
    private static GripFit FitLift(SurgeonHand hand, GripFit fit, string part)
    {
        var moves = new List<Vector2>();
        for (var lift = 0f; lift <= MaxLift + 0.0001f; lift += LiftStep)
        {
            for (var shift = -MaxShift; shift <= MaxShift + 0.0001f; shift += LiftStep)
            {
                moves.Add(new Vector2(lift, shift));
            }
        }
        var best = int.MaxValue;
        var bestMove = Vector2.Zero;
        foreach (var move in moves.OrderBy(move => move.Length()))
        {
            var clipped = Try(hand, fit with { Lift = move.X, Shift = move.Y }, part);
            if (clipped < best)
            {
                best = clipped;
                bestMove = move;
            }
            if (clipped == 0)
            {
                break;
            }
        }
        return fit with { Lift = bestMove.X, Shift = bestMove.Y };
    }

    /// <summary>The fit with <paramref name="finger"/> curled to <paramref name="curl"/>.</summary>
    private static GripFit Curled(GripFit fit, int finger, float curl)
    {
        float[] curls = [.. fit.Curl!];
        curls[finger] = curl;
        return fit with { Curl = curls };
    }

    /// <summary>The curl nearest the grip's own that keeps one finger clear of the tool: opening it first, then closing
    /// it further (a thumb can wrap past a thick handle), or failing both the one that clips least.</summary>
    private static GripFit FitFinger(SurgeonHand hand, GripFit fit, int finger)
    {
        var start = fit.Curl![finger];
        var candidates = new List<float>();
        for (var i = 0; i < 30; i++)
        {
            foreach (var value in (float[])[start - (CurlStep * i), start + (CurlStep * i)])
            {
                if (value is >= 0f and <= MaxCurl && !candidates.Contains(value))
                {
                    candidates.Add(value);
                }
            }
        }
        var best = int.MaxValue;
        var bestCurl = start;
        foreach (var value in candidates)
        {
            var clipped = Try(hand, Curled(fit, finger, value), SurgeonHand.Fingers[finger]);
            if (clipped < best)
            {
                best = clipped;
                bestCurl = value;
            }
            if (clipped == 0)
            {
                break;
            }
        }
        return Curled(fit, finger, bestCurl);
    }

    /// <summary>Closes a clear finger onto the tool: a step at a time, up to TouchReach more curl, stopping at the last
    /// pose before it would go into the tool, so it rests on it instead of hovering. A finger that meets nothing in
    /// that range (it curls past the tool) keeps its curl.</summary>
    private static GripFit Touch(SurgeonHand hand, GripFit fit, int finger)
    {
        var part = SurgeonHand.Fingers[finger];
        if (Try(hand, fit, part) > 0)
        {
            return fit;
        }
        var start = fit.Curl![finger];
        for (var value = start; value + CurlStep <= Mathf.Min(start + TouchReach, MaxCurl) + 0.0001f; value += CurlStep)
        {
            if (Try(hand, Curled(fit, finger, value + CurlStep), part) > 0)
            {
                return Curled(fit, finger, value);
            }
        }
        return fit;
    }

    private static int Try(SurgeonHand hand, GripFit fit, string part)
    {
        hand.Fit = fit;
        hand.SnapPose(GripCheck.ShoulderOf(hand));
        return GripCheck.Clipped(hand, part);
    }
}
