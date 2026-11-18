using System.Diagnostics;

namespace Gallery.Core.Features.Vaults;

// Only numeric measurements and fixed operation names belong here. Never attach vault or media data.
internal static class VaultOpenTelemetry
{
    internal const string SourceName = "Gallery.Core.VaultOpen";
    internal static readonly ActivitySource Source = new(SourceName);
    internal static long Start(Activity? activity) => activity?.IsAllDataRequested == true ? Stopwatch.GetTimestamp() : 0;
    internal static double Elapsed(long start) => start == 0 ? 0 : Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    internal static void Record(Activity? activity, string tag, long start) => activity?.SetTag(tag, Elapsed(start));
}
