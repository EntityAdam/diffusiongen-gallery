using System.Threading;

namespace Gallery.Core.Services;

internal static class IngestionProgressCallbackGuard
{
    private static readonly AsyncLocal<GuardScope?> currentScope = new();

    private sealed record GuardScope(string VaultRoot, GuardScope? Parent);

    internal static bool IsActive(string vaultRoot)
    {
        for (var scope = currentScope.Value; scope is not null; scope = scope.Parent)
        {
            if (PathsEqual(scope.VaultRoot, vaultRoot)) return true;
        }
        return false;
    }

    internal static async Task RunAsync(string vaultRoot, Func<Task> callback)
    {
        var scope = Enter(vaultRoot);
        try { await callback(); }
        finally { Exit(scope); }
    }

    internal static void Run(string vaultRoot, Action callback)
    {
        var scope = Enter(vaultRoot);
        try { callback(); }
        finally { Exit(scope); }
    }

    internal static void ThrowIfActive(string operation, string vaultRoot)
    {
        if (IsActive(vaultRoot))
            throw new InvalidOperationException(
                $"Cannot {operation} from an ingestion progress callback. Wait for the import to finish first.");
    }

    private static GuardScope Enter(string vaultRoot)
    {
        var scope = new GuardScope(vaultRoot, currentScope.Value);
        currentScope.Value = scope;
        return scope;
    }

    private static void Exit(GuardScope scope) => currentScope.Value = scope.Parent;

    private static bool PathsEqual(string left, string right) =>
        OperatingSystem.IsWindows()
            ? string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            : string.Equals(left, right, StringComparison.Ordinal);
}
