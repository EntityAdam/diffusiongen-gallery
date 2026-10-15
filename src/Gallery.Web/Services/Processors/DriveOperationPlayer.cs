using Gallery.Web.Models;
using Gallery.Web.Services.DataManagers.Models;

namespace Gallery.Web.Services.Processors;

internal sealed record DriveBatchReport(int Played, int Done, string Activity);

/// <summary>A key-holding binding of the queue to one gallery store; disposed when the player stops.</summary>
internal interface IDriveOperationSession : IDisposable
{
    Task<DriveBatchReport> PlayBatchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Plays queued drive operations in the background. Work lives in the database, so pausing, stopping or
/// a process exit never loses it; the player only decides when the queue is drained. It holds a key lease
/// while running and, like ingestion, keeps running after the gallery locks.
/// </summary>
internal sealed class DriveOperationPlayer(
    Func<IDriveOperationSession?> openSession,
    Func<Task<DriveQueueCounts>> readCounts) : IDisposable
{
    private readonly object syncRoot = new();
    private Task loop = Task.CompletedTask;
    private CancellationTokenSource? cancellation;
    private TaskCompletionSource wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool running;
    private bool rerun;
    private bool paused;
    private bool faulted;
    private bool disposed;
    private int completed;
    private WebDriveQueueStatus status = WebDriveQueueStatus.Empty;

    public event Action? Changed;

    public WebDriveQueueStatus Status { get { lock (syncRoot) return status; } }

    public bool IsPaused { get { lock (syncRoot) return paused; } }

    public void Start()
    {
        lock (syncRoot)
        {
            if (disposed || paused) return;
            if (running)
            {
                rerun = true;
                wake.TrySetResult();
                return;
            }
            running = true;
            rerun = false;
            faulted = false;
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            loop = Task.Run(() => RunAsync(token));
        }
    }

    public async Task PauseAsync()
    {
        lock (syncRoot) paused = true;
        await CancelLoopAsync();
        await RefreshAsync();
    }

    public void Resume()
    {
        lock (syncRoot) paused = false;
        Start();
        _ = RefreshWhenIdleAsync();
    }

    /// <summary>Stops playing without discarding work; the next Start resumes from the queue.</summary>
    public async Task StopAsync()
    {
        await CancelLoopAsync();
        lock (syncRoot) completed = 0;
    }

    /// <summary>Completes once the loop, including its final status refresh and session release, has ended.</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task current;
            lock (syncRoot) current = loop;
            await current;
            lock (syncRoot)
            {
                if (!running && ReferenceEquals(current, loop)) return;
            }
        }
    }

    public async Task RefreshAsync()
    {
        var counts = await readCounts();
        Publish(counts, null);
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            disposed = true;
            cancellation?.Cancel();
        }
    }

    private async Task CancelLoopAsync()
    {
        Task current;
        lock (syncRoot)
        {
            cancellation?.Cancel();
            wake.TrySetResult();
            current = loop;
        }
        await current;
    }

    private async Task RefreshWhenIdleAsync()
    {
        try { await RefreshAsync(); } catch { /* Status refresh is advisory. */ }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var session = openSession();
            if (session is null)
            {
                Finish();
                await RefreshWhenIdleAsync();
                return;
            }
            while (!cancellationToken.IsCancellationRequested)
            {
                lock (syncRoot)
                {
                    rerun = false;
                    if (wake.Task.IsCompleted) wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                var report = await session.PlayBatchAsync(cancellationToken);
                lock (syncRoot) completed += report.Done;
                var counts = await readCounts();
                if (report.Played > 0)
                {
                    Publish(counts, report.Activity, WebDriveQueueState.Running);
                    continue;
                }
                if (counts.NextDue is long due)
                {
                    Publish(counts, "Waiting to retry", WebDriveQueueState.Waiting);
                    var delay = TimeSpan.FromMilliseconds(Math.Max(10, due - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                    Task wakeTask;
                    lock (syncRoot) wakeTask = wake.Task;
                    await Task.WhenAny(Task.Delay(delay, cancellationToken), wakeTask);
                    continue;
                }
                lock (syncRoot)
                {
                    if (rerun) continue;
                    running = false;
                }
                Publish(counts, null);
                return;
            }
            Finish();
            await RefreshWhenIdleAsync();
        }
        catch (Exception)
        {
            // Queued work is durable; a faulted player only stops until the next Start or Retry.
            lock (syncRoot) faulted = true;
            Finish();
            await RefreshWhenIdleAsync();
        }
    }

    private void Finish()
    {
        lock (syncRoot) running = false;
    }

    private void Publish(DriveQueueCounts counts, string? activity, WebDriveQueueState? state = null)
    {
        lock (syncRoot)
        {
            var effective = state ?? (faulted ? WebDriveQueueState.Faulted
                : paused ? WebDriveQueueState.Paused
                : running ? WebDriveQueueState.Running
                : WebDriveQueueState.Idle);
            if (counts.Pending == 0 && counts.Failed == 0 && effective == WebDriveQueueState.Idle) completed = 0;
            status = new WebDriveQueueStatus(effective, counts.Pending, counts.Failed, completed, activity ?? "");
        }
        Changed?.Invoke();
    }
}
