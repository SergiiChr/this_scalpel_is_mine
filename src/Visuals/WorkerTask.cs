using System.Threading.Tasks;

namespace Scalpel.Visuals;

/// <summary>Detach a finished worker before consuming its result, so a fault is observed once and cannot
/// prevent later work from being scheduled. An unfinished worker keeps ownership of its buffers.</summary>
internal static class WorkerTask {
    /// <summary>Observe a completed non-generic worker task without throwing on the simulation frame.</summary>
    internal static bool TryTakeTaskResult(ref Task? pending, out Exception? failure) {
        if (pending is not { IsCompleted: true } completed) {
            failure = null;
            return false;
        }
        pending = null;
        try {
            completed.GetAwaiter().GetResult();
            failure = null;
        } catch (Exception error) {
            failure = error;
        }
        return true;
    }

    internal static bool TryTakeResult<T>(ref Task<T>? pending, out T? result, out Exception? failure) {
        if (pending is not { IsCompleted: true } completed) {
            result = default;
            failure = null;
            return false;
        }
        pending = null;
        try {
            result = completed.GetAwaiter().GetResult();
            failure = null;
        } catch (Exception error) {
            result = default;
            failure = error;
        }
        return true;
    }
}
