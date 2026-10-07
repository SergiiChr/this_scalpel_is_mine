namespace Scalpel.Tests.Support;

/// <summary>Read by build.py: each case of the suite runs in a Godot process of its own, so one surgery's engine and
/// rendering state never carries into the next.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class IsolateCasesAttribute : Attribute;
