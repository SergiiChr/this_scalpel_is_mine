using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace Scalpel.Visuals;

/// <summary>Detach a finished worker before consuming its result, so a fault is observed once and cannot
/// prevent later work from being scheduled. An unfinished worker keeps ownership of its buffers.</summary>
internal static class WorkerTask {
    internal static bool TryTake<T>(ref T? pending, [NotNullWhen(true)] out T? completed) where T : Task {
        if (pending is not { IsCompleted: true }) { completed = null; return false; }
        completed = pending;
        pending = null;
        return true;
    }
}
