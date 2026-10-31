using System.Diagnostics;

namespace Gallery.Stress;

// Count-only heartbeat: even a slow native open/checkpoint remains visibly in its current phase.
internal sealed class BuildProgress : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task heartbeat;
    private string phase = "Starting";
    private long started = Stopwatch.GetTimestamp();
    private long completed;
    private long total;
    private long failed;

    public BuildProgress() => heartbeat = RunAsync();

    public void Start(string name, long count = 0)
    {
        lock (sync)
        {
            phase = name;
            started = Stopwatch.GetTimestamp();
            completed = 0;
            failed = 0;
            total = count;
            Write();
        }
    }

    public void Report(long count, long? expected = null, long failures = 0)
    {
        lock (sync)
        {
            completed = Math.Max(completed, count);
            failed = Math.Max(failed, failures);
            if (expected is not null) total = expected.Value;
        }
    }

    private void Write()
    {
        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var counts = total > 0 ? $" {completed:N0}/{total:N0}" : "";
        Console.Error.WriteLine($"{phase}:{counts} ({elapsed:N0}s, {completed / Math.Max(1, elapsed):N1} items/s, {failed:N0} failed)");
    }

    private async Task RunAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
                lock (sync) Write();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        await heartbeat;
        stop.Dispose();
    }
}
