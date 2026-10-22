namespace Scalpel.Operation;

/// <summary>An objective step as the HUD shows it: a main task and, for the easiest difficulty, its subtasks.
/// </summary>
public readonly record struct ObjectiveView(string Label, bool Done, bool Optional, bool Spoiled, string[] Subtasks) {
    public Godot.Collections.Array ToVariant() => [Label, Done, Optional, Spoiled, Subtasks];

    public static ObjectiveView FromVariant(Variant data) {
        var view = data.AsGodotArray();
        return new ObjectiveView(view[0].AsString(), view[1].AsBool(), view[2].AsBool(), view[3].AsBool(),
            view[4].AsStringArray());
    }
}

/// <summary>Where one step stands: met, spoiled, and how long its condition has held so far.</summary>
public sealed class StepState {
    /// <summary>A milestone once it's reached, a state step while its condition holds.</summary>
    public bool Done { get; set; }
    /// <summary>Its condition has been met at least once.</summary>
    public bool Reached { get; set; }
    /// <summary>Its condition holds right now (a tourniquet still on, a block not worn off).</summary>
    public bool Holds { get; set; }
    /// <summary>First met before a step it comes after was done: it no longer counts.</summary>
    public bool Spoiled { get; set; }
    public float Timer { get; set; }

    /// <summary>The step's "or" alternative keeps its own timer, so two timed conditions don't reset each other.
    /// </summary>
    public StepState Alternative => field ??= new StepState();

    public bool Counts => Done && !Spoiled;
}

/// <summary>
/// Scenario requirements, host only. Every step is checked every tick, whatever was done before it: a milestone (the
/// bullet out) stays done once reached, a state step (no active bleeding) counts only while it holds.
/// A step first met while a step it comes after (<see cref="ObjectiveStep.After"/>) isn't holding is spoiled for good:
/// done earlier isn't enough when it has worn off or come off since.
/// Nothing here ends the surgery: the outcome is settled when the patient is discharged (<see cref="Settle"/>).
/// Each step type is a small check in <see cref="ObjectiveChecks"/>.
/// </summary>
public partial class Objectives : Node {
    public List<ObjectiveStep> Steps { get; } = [];
    public List<StepState> States { get; } = [];
    /// <summary>Each step's subtasks, worked out for this patient (<see cref="Operation.Subtasks"/>).</summary>
    private readonly List<string[]> _subtasks = [];

    public void Setup(ScenarioDef scenario, SubtaskFacts facts) {
        foreach (var step in scenario.Steps) {
            Steps.Add(step);
            States.Add(new StepState());
            _subtasks.Add([.. Subtasks.For(step, scenario, facts)]);
        }
    }

    /// <summary>Every required step counts: the patient is ready to go.</summary>
    public bool AllDone => Steps.Select((step, i) => step.Optional || States[i].Counts).All(counts => counts);

    public void Tick(float delta, Surgery surgery) {
        for (var i = 0; i < Steps.Count; i++) {
            var step = Steps[i];
            var state = States[i];
            if (state.Spoiled) {
                continue;
            }
            // Milestones are checked on after they're done too: a later step needs to know they still hold.
            var met = ObjectiveChecks.Check(step, state, surgery, delta);
            state.Holds = met;
            if (met && !state.Reached) {
                state.Reached = true;
                if (Missing(step) is { } missing) {
                    state.Spoiled = true;
                    surgery.AnnounceDebug($"Spoiled: {step.Label} (before \"{missing.Label}\")");
                    surgery.Scoring.Note($"Spoiled: {step.Label}, \"{missing.Label}\" wasn't in place", false);
                    continue;
                }
                surgery.Scoring.Note($"Done: {step.Label}", true);
            }
            var done = met || (state.Reached && !ObjectiveChecks.IsState(step.Type));
            if (done != state.Done) {
                state.Done = done;
                surgery.AnnounceDebug($"{(done ? "Done" : "Undone")}: {step.Label}");
            }
        }
    }

    /// <summary>The first step <paramref name="step"/> comes after that doesn't hold now, null when there's none.
    /// Steps come after earlier ones only, and those were checked earlier this tick.</summary>
    private ObjectiveStep? Missing(ObjectiveStep step) {
        for (var i = 0; i < Steps.Count; i++) {
            if (step.After.Contains(Steps[i].Id) && !(States[i].Holds && !States[i].Spoiled)) {
                return Steps[i];
            }
        }
        return null;
    }

    /// <summary>The outcome as the patient leaves: scores every step that counts and every required one that doesn't,
    /// each by its label on the timeline. True when all required ones count.</summary>
    public bool Settle(Scoring scoring) {
        for (var i = 0; i < Steps.Count; i++) {
            if (States[i].Counts) {
                scoring.Add(Steps[i].Optional ? "optional_done" : "objective_done", note: $"Counted: {Steps[i].Label}");
            } else if (!Steps[i].Optional) {
                scoring.Add("objective_missed", note: $"Missed: {Steps[i].Label}");
            }
        }
        return AllDone;
    }

    /// <summary>Compact state for the HUD. A state step shows done only once the required steps before it count: a
    /// wound isn't closed before it's been opened. Subtasks go only with the open steps up to the next required one,
    /// all the HUD lists, which keeps the status small.</summary>
    public List<ObjectiveView> Snapshot() {
        var views = new List<ObjectiveView>(Steps.Count);
        var settled = true;
        var ahead = true;
        for (var i = 0; i < Steps.Count; i++) {
            var (step, state) = (Steps[i], States[i]);
            var done = state.Done && (settled || !ObjectiveChecks.IsState(step.Type));
            string[] subtasks = [];
            if (ahead && !done && !state.Spoiled) {
                ahead = step.Optional;
                // Missing() is what spoils a step: a step it comes after that doesn't hold right now.
                subtasks = [.. _subtasks[i], .. Subtasks.OnlyAfter(step, Steps.Where((_, j) => !States[j].Holds || States[j].Spoiled))];
            }
            settled &= step.Optional || state.Counts;
            views.Add(new ObjectiveView(step.Label, done, step.Optional, state.Spoiled, subtasks));
        }
        return views;
    }
}
