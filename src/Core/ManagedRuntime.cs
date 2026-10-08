using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Scalpel.Core;

/// <summary>
/// Keeps the .NET runtime from stalling frames. Code is compiled the first time it runs, and an engine class sets
/// itself up the first time it's used: the first cut, the first delivery and the like each cost 10-35 ms on the main
/// thread. So at start-up a background thread does it ahead: compiles the game's code and the library code it calls,
/// and sets up the engine classes it uses. Code those call in turn still compiles on first use, a few ms at most.
/// (Godot loads the game assembly from memory, which rules out precompiled ReadyToRun code.) The loading screen waits
/// for it.
/// </summary>
public partial class ManagedRuntime : Node
{
    private static Task _warmUp = Task.CompletedTask;
    private static int _done;
    private static int _total = 1;

    /// <summary>Done when everything is prepared.</summary>
    internal static Task WarmUp => _warmUp;

    /// <summary>How much of the warm-up is done, 0 to 1.</summary>
    internal static float Progress => Math.Min((float)Volatile.Read(ref _done) / Volatile.Read(ref _total), 1f);

    public override void _Ready()
    {
        // Gen2 collections run in the background instead of blocking a frame. Only the size of the young generation
        // decides how long the remaining pauses are, and .NET reads that only from the environment
        // (DOTNET_GCgen0size), never from the game's own runtime config.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        _warmUp = Task.Run(Prepare);
    }

    /// <summary>Collects all garbage while nothing moves yet (a surgery about to start): play starts with an empty
    /// young generation and no Godot wrappers waiting for their finalizers.</summary>
    internal static void CollectNow()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void Prepare()
    {
        var module = typeof(ManagedRuntime).Module;
        List<Action> work =
        [
            .. GameMethods().Select(method => (Action)(() => Compile(method))),
            .. Referenced(module, 0x0A000000).Concat(Referenced(module, 0x2B000000)).OfType<MethodBase>()
                .Select(method => (Action)(() => Compile(method))),
            .. EngineTypes(module).Select(type => (Action)(() => RuntimeHelpers.RunClassConstructor(type.TypeHandle))),
        ];
        Volatile.Write(ref _total, Math.Max(work.Count, 1));
        foreach (var step in work)
        {
            try
            {
                step();
            }
            catch (Exception e) when (e is ArgumentException or TypeLoadException or BadImageFormatException
                or InvalidOperationException or NotSupportedException or TypeInitializationException)
            {
                // Can't be prepared on its own (an unusual signature): it's prepared when it first runs instead.
            }
            Interlocked.Increment(ref _done);
        }
    }

    private static IEnumerable<MethodBase> GameMethods()
    {
        const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic;
        return typeof(ManagedRuntime).Assembly.GetTypes().Where(type => !type.ContainsGenericParameters)
            .SelectMany(type => type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)));
    }

    /// <summary>Every member the game's code refers to in another assembly (<paramref name="table"/>: member
    /// references) or every generic method it calls as instantiated (method specs). Ones that only make sense inside a
    /// generic method of the game's are left out.</summary>
    private static IEnumerable<MemberInfo> Referenced(Module module, int table)
    {
        for (var row = 1; ; row++)
        {
            MemberInfo? member;
            try
            {
                member = module.ResolveMember(table | row);
            }
            catch (ArgumentOutOfRangeException)
            {
                yield break;
            }
            catch (Exception e) when (e is ArgumentException or TypeLoadException or BadImageFormatException
                or MissingMemberException)
            {
                continue;
            }
            yield return member!;
        }
    }

    /// <summary>The engine classes the game refers to, with the name tables nested in them. A server the game calls
    /// statically (PhysicsServer3D) does its work through an instance class (PhysicsServer3DInstance), set up too.
    /// </summary>
    private static List<Type> EngineTypes(Module module)
    {
        var engine = typeof(GodotObject).Assembly;
        var types = new List<Type>();
        for (var row = 1; ; row++)
        {
            try
            {
                var type = module.ResolveType(0x01000000 | row);
                if (type.Assembly != engine || type.ContainsGenericParameters)
                {
                    continue;
                }
                foreach (var used in (Type?[])[type, engine.GetType(type.FullName + "Instance")])
                {
                    if (used is not null)
                    {
                        types.Add(used);
                        types.AddRange(used.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                            .Where(nested => !nested.ContainsGenericParameters));
                    }
                }
            }
            catch (ArgumentOutOfRangeException)
            {
                return types;
            }
            catch (Exception e) when (e is ArgumentException or TypeLoadException)
            {
            }
        }
    }

    private static void Compile(MethodBase method)
    {
        if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() is null)
        {
            return;
        }
        RuntimeHelpers.PrepareMethod(method.MethodHandle,
            method.IsGenericMethod ? [.. method.GetGenericArguments().Select(type => type.TypeHandle)] : null);
    }
}
