namespace Scalpel.Tools;

/// <summary>A syringe: the wheel works the plunger (<see cref="Syringe.Plunge"/>). What it pushed where is told once
/// the needle is somewhere else.</summary>
public sealed class SyringeAction : ToolAction
{
    public override void Apply(ToolStep step)
    {
        var tool = step.Tool;
        if (tool.PushedMl > 0f && Syringe.PushLabel(Syringe.NeedleTarget(tool, step.Patient)) != tool.PushedInto)
        {
            Syringe.ReportPushed(tool);
        }
    }
}
