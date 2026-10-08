namespace Scalpel.Operation;

/// <summary>An objective step as the HUD shows it.</summary>
public readonly record struct ObjectiveView(string Label, bool Done, bool Optional, bool Current)
{
    public Godot.Collections.Array ToVariant() => [Label, Done, Optional, Current];

    public static ObjectiveView FromVariant(Variant data)
    {
        var view = data.AsGodotArray();
        return new ObjectiveView(view[0].AsString(), view[1].AsBool(), view[2].AsBool(), view[3].AsBool());
    }
}

/// <summary>Where one step stands: done, and how long its condition has held so far.</summary>
public sealed class StepState
{
    public bool Done { get; set; }
    public float Timer { get; set; }
}

/// <summary>
/// Scenario steps, host only. Required steps complete in order, optional ones any time for bonus points.
/// Each step type is a small check in <see cref="ObjectiveChecks"/>.
/// </summary>
public partial class Objectives : Node
{
    public List<ObjectiveStep> Steps { get; } = [];
    public List<StepState> States { get; } = [];

    public void Setup(ScenarioDef scenario)
    {
        foreach (var step in scenario.Steps)
        {
            Steps.Add(step);
            States.Add(new StepState());
        }
    }

    /// <summary>The first required step not done yet, -1 when they all are.</summary>
    public int CurrentIndex()
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            if (!States[i].Done && !Steps[i].Optional)
            {
                return i;
            }
        }
        return -1;
    }

    public bool AllDone => CurrentIndex() == -1;

    public void Tick(float delta, Surgery surgery)
    {
        var current = CurrentIndex();
        for (var i = 0; i < Steps.Count; i++)
        {
            var optional = Steps[i].Optional;
            if (States[i].Done || (i != current && !optional))
            {
                continue;
            }
            if (ObjectiveChecks.Check(Steps[i], States[i], surgery, delta))
            {
                States[i].Done = true;
                surgery.Scoring.Add(optional ? "optional_done" : "objective_done");
                surgery.AnnounceDebug($"Done: {Steps[i].Label}");
            }
        }
    }

    /// <summary>Compact state for the HUD.</summary>
    public List<ObjectiveView> Snapshot()
    {
        var current = CurrentIndex();
        return [.. Steps.Select((step, i) => new ObjectiveView(step.Label, States[i].Done, step.Optional, i == current))];
    }
}
