using System.Runtime.ExceptionServices;
using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers.Models;

namespace Gallery.Core.Services.Processors;

internal sealed record DriveBatchReport(int Played, int Done, string Activity);

/// <summary>A key-holding binding of the queue to one gallery store; disposed when the player stops.</summary>
internal interface IDriveOperationSession : IDisposable
{
    Task<DriveBatchReport> PlayBatchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Owns one queue-player lifetime. Its readonly lifetime component serializes commands under a gate;
/// each start creates a distinct run owning its cancellation, wake signal and completion count. Queue
/// work remains durable in the catalog. WhenIdleAsync and StopAsync surface run faults; an unobserved
/// fault must be observed before Start can replace the run. An opened session owns one store/key binding
/// until that run releases it, including when the gallery locks.
/// </summary>
internal sealed class DriveOperationPlayer : IDisposable
{
    private readonly Func<IDriveOperationSession?> openSession;
    private readonly Func<Task<DriveQueueCounts>> readCounts;
    private readonly PlayerLifetime lifetime = new();

    internal DriveOperationPlayer(Func<IDriveOperationSession?> openSession, Func<Task<DriveQueueCounts>> readCounts)
    {
        this.openSession = openSession;
        this.readCounts = readCounts;
    }

    public event Action? Changed
    {
        add { lock (lifetime.Gate) lifetime.Changed += value; }
        remove { lock (lifetime.Gate) lifetime.Changed -= value; }
    }

    public DriveQueueStatus Status { get { lock (lifetime.Gate) return lifetime.driveQueueStatus; } }
    public bool IsPaused { get { lock (lifetime.Gate) return lifetime.Paused; } }

    public void Start() => StartLoopIfEligible();

    private void StartLoopIfEligible()
    {
        lock (lifetime.Gate)
        {
            if (lifetime.Disposed || lifetime.Paused) return;
            var previous = lifetime.Run;
            if (previous?.Running == true)
            {
                previous.Rerun = true;
                previous.taskCompletionSource.TrySetResult();
                return;
            }
            if (previous?.Failure is { } failure && !previous.FaultObserved)
                failure.Throw();
            var run = new PlayerRun();
            lifetime.Run = run;
            run.task = Task.Run(() => RunAsync(run));
        }
    }

    public async Task PauseAsync()
    {
        lock (lifetime.Gate) lifetime.Paused = true;
        await CancelLoopAsync();
        await RefreshAsync();
    }

    public void Resume()
    {
        lock (lifetime.Gate) lifetime.Paused = false;
        Start();
    }

    public async Task StopAsync()
    {
        await CancelLoopAsync();
        lock (lifetime.Gate)
        {
            if (lifetime.Run is { } run) run.Completed = 0;
        }
    }

    /// <summary>Includes final refresh and session release; failure is never converted into successful idle.</summary>
    public Task WhenIdleAsync() => WaitForIdleLoopAsync();

    private async Task WaitForIdleLoopAsync()
    {
        while (true)
        {
            PlayerRun? run;
            lock (lifetime.Gate) run = lifetime.Run;
            if (run is null) return;
            await ObserveAsync(run);
            lock (lifetime.Gate)
            {
                if (!run.Running && ReferenceEquals(run, lifetime.Run)) return;
            }
        }
    }

    public async Task RefreshAsync()
    {
        var driveQueueCounts = await readCounts();
        PlayerRun? run;
        lock (lifetime.Gate) run = lifetime.Run;
        Publish(run, driveQueueCounts, null);
    }

    public void Dispose()
    {
        lock (lifetime.Gate)
        {
            lifetime.Disposed = true;
            if (lifetime.Run is { Running: true } run)
            {
                run.cancellationTokenSource.Cancel();
                run.taskCompletionSource.TrySetResult();
            }
        }
    }

    private async Task CancelLoopAsync()
    {
        PlayerRun? run;
        lock (lifetime.Gate)
        {
            run = lifetime.Run;
            if (run?.Running == true)
            {
                run.cancellationTokenSource.Cancel();
                run.taskCompletionSource.TrySetResult();
            }
        }
        if (run is not null) await ObserveAsync(run);
    }

    private async Task ObserveAsync(PlayerRun run)
    {
        try { await run.task; }
        finally
        {
            if (run.task.IsFaulted)
            {
                lock (lifetime.Gate) run.FaultObserved = true;
            }
        }
    }

    private async Task RunAsync(PlayerRun run)
    {
        try
        {
            while (true)
            {
                lock (lifetime.Gate) run.Rerun = false;
                var counts = await RunBoundSessionAsync(run);
                DriveQueueState idleState;
                lock (lifetime.Gate) idleState = lifetime.Paused ? DriveQueueState.Paused : DriveQueueState.Idle;
                Publish(run, counts, null, idleState);
                lock (lifetime.Gate)
                {
                    if (run.Rerun && !run.cancellationTokenSource.IsCancellationRequested) continue;
                    run.Running = false;
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            // Publish the failed run's state and preserve the original exception for every observing boundary.
            lock (lifetime.Gate)
            {
                run.Failure = ExceptionDispatchInfo.Capture(exception);
                run.Faulted = true;
                run.Running = false;
                if (ReferenceEquals(run, lifetime.Run))
                    lifetime.driveQueueStatus = lifetime.driveQueueStatus with { State = DriveQueueState.Faulted, Completed = run.Completed };
            }
            throw;
        }
        finally { run.cancellationTokenSource.Dispose(); }
    }

    private async Task<DriveQueueCounts> RunBoundSessionAsync(PlayerRun run)
    {
        using var driveOperationSession = openSession();
        if (driveOperationSession is null) return await readCounts();
        while (!run.cancellationTokenSource.IsCancellationRequested)
        {
            lock (lifetime.Gate)
            {
                run.Rerun = false;
                if (run.taskCompletionSource.Task.IsCompleted)
                    run.taskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            var driveBatchReport = await driveOperationSession.PlayBatchAsync(run.cancellationTokenSource.Token);
            lock (lifetime.Gate) run.Completed += driveBatchReport.Done;
            var driveQueueCounts = await readCounts();
            if (driveBatchReport.Played > 0)
            {
                Publish(run, driveQueueCounts, driveBatchReport.Activity, DriveQueueState.Running);
                continue;
            }
            if (driveQueueCounts.NextDue is long due)
            {
                Publish(run, driveQueueCounts, "Waiting to retry", DriveQueueState.Waiting);
                var delay = TimeSpan.FromMilliseconds(Math.Max(10, due - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                Task wakeTask;
                lock (lifetime.Gate) wakeTask = run.taskCompletionSource.Task;
                await Task.WhenAny(Task.Delay(delay, run.cancellationTokenSource.Token), wakeTask);
                continue;
            }
            lock (lifetime.Gate)
            {
                if (run.Rerun) continue;
            }
            return driveQueueCounts;
        }
        return await readCounts();
    }

    private void Publish(PlayerRun? run, DriveQueueCounts driveQueueCounts, string? activity, DriveQueueState? state = null)
    {
        Action? changed;
        lock (lifetime.Gate)
        {
            if (!ReferenceEquals(run, lifetime.Run)) return;
            var effective = state ?? (run?.Faulted == true ? DriveQueueState.Faulted
                : lifetime.Paused ? DriveQueueState.Paused
                : run?.Running == true ? DriveQueueState.Running : DriveQueueState.Idle);
            var completed = run?.Completed ?? 0;
            if (driveQueueCounts.Pending == 0 && driveQueueCounts.Failed == 0 && effective == DriveQueueState.Idle) completed = 0;
            lifetime.driveQueueStatus = new(effective, driveQueueCounts.Pending, driveQueueCounts.Failed, completed, activity ?? "");
            changed = lifetime.Changed;
        }
        changed?.Invoke();
    }

    // Private resource state: only this player accesses these members, always under PlayerLifetime.Gate.
    // A run owns its token until its task finishes. Counters, wake/rerun flags and faults never transfer.
    private sealed class PlayerRun
    {
        internal readonly CancellationTokenSource cancellationTokenSource = new();
        internal Task task = Task.CompletedTask;
        internal TaskCompletionSource taskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Running = true;
        internal bool Rerun;
        internal bool Faulted;
        internal bool FaultObserved;
        internal ExceptionDispatchInfo? Failure;
        internal int Completed;
    }

    // Owns subscriptions and the current run for one player; no mutable state escapes the gate.
    private sealed class PlayerLifetime
    {
        internal readonly object Gate = new();
        internal PlayerRun? Run;
        internal bool Paused;
        internal bool Disposed;
        internal DriveQueueStatus driveQueueStatus = DriveQueueStatus.Empty;
        internal Action? Changed;
    }
}
