using System.Diagnostics;

namespace Gallery.Core.Features.Vaults.Session;

internal static class AutoLockNotificationPublisher
{
    internal static void Publish(Action? handlers)
    {
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception exception)
            {
                try
                {
                    Trace.TraceError("A vault auto-lock notification handler failed with {0}.",
                        exception.GetType().FullName);
                }
                catch { /* A faulty trace listener must not reintroduce a timer callback exception. */ }
            }
        }
    }
}
