using System.Reflection;
using System.Runtime.CompilerServices;

namespace Scalpel.Core;

/// <summary>
/// Keeps the .NET runtime from stalling frames. Methods are compiled the first time they run: the first cut, the first
/// delivery and the like each cost 10-35 ms of compiling on the main thread. So at start-up a background thread
/// compiles the game's code ahead (Godot loads the game assembly from memory, which rules out precompiled
/// ReadyToRun code).
/// </summary>
public partial class ManagedRuntime : Node
{
    public override void _Ready() => Task.Run(CompileAhead);

    private static void CompileAhead()
    {
        const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var type in typeof(ManagedRuntime).Assembly.GetTypes().Where(type => !type.ContainsGenericParameters))
        {
            foreach (var method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
            {
                if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() is null)
                {
                    continue;
                }
                try
                {
                    RuntimeHelpers.PrepareMethod(method.MethodHandle);
                }
                catch (Exception e) when (e is ArgumentException or TypeLoadException or BadImageFormatException
                    or InvalidOperationException or NotSupportedException)
                {
                    // Not compilable on its own (an unusual signature): it's compiled when it first runs instead.
                }
            }
        }
    }
}
