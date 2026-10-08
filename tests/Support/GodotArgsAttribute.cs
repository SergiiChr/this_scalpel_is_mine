namespace Scalpel.Tests.Support;

/// <summary>
/// Godot command line options a test suite's process needs, read by build.py: [GodotArgs("--fixed-fps", "60")] runs
/// every frame as one physics step of 1/60 s as fast as the machine goes, for long surgeries.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class GodotArgsAttribute(params string[] args) : Attribute
{
    public IReadOnlyList<string> Args { get; } = args;
}
