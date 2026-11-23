using System.Threading;

namespace Gallery.Core.Services;

internal static class IngestionProgressCallbackGuard
{
    private static readonly AsyncLocal<int> callbackDepth = new();

    internal static bool IsActive => callbackDepth.Value > 0;

    internal static async Task RunAsync(Func<Task> callback)
    {
        callbackDepth.Value++;
        try { await callback(); }
        finally { callbackDepth.Value--; }
    }

    internal static void ThrowIfActive(string operation)
    {
        if (IsActive)
            throw new InvalidOperationException(
                $"Cannot {operation} from an ingestion progress callback. Wait for the import to finish first.");
    }
}
