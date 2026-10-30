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
    private readonly PlayerLifetime playerLifetime = new();

    internal DriveOperationPlayer(Func<IDriveOperationSession?> openSession, Func<Task<DriveQueueCounts>> readCounts)
    {
        this.openSession = openSession;
        this.readCounts = readCounts;
    }

    public event Action? Changed
    {
        add { lock (playerLifetime.Gate) playerLifetime.Changed += value; }
        remove { lock (playerLifetime.Gate) playerLifetime.Changed -= value; }
    }

    public DriveQueueStatus Status { get { lock (playerLifetime.Gate) return playerLifetime.driveQueueStatus; } }
    public bool IsPaused { get { lock (playerLifetime.Gate) return playerLifetime.Paused; } }

    public void Start() => StartLoopIfEligible();

    private void StartLoopIfEligible()
    {
        lock (playerLifetime.Gate)
        {
            if (playerLifetime.Disposed || playerLifetime.Paused) return;
            var playerRun = playerLifetime.PlayerRun;
            if (playerRun?.Running == true)
            {
                if (playerRun.cancellationTokenSource.IsCancellationRequested)
                    playerRun.RestartAfterCancellation = true;
                else
                    playerRun.Rerun = true;
                playerRun.taskCompletionSource.TrySetResult();
                return;
            }
            if (playerRun?.Failure is { } failure && !playerRun.FaultObserved)
                failure.Throw();
            StartReplacementRun();
        }
    }

    // Caller holds PlayerLifetime.Gate.
    private void StartReplacementRun()
    {
        var playerRun = new PlayerRun();
        playerLifetime.PlayerRun = playerRun;
        playerRun.task = Task.Run(() => RunAsync(playerRun));
    }

    public async Task PauseAsync()
    {
        SetPaused(true);
        await CancelLoopAsync();
        await RefreshAsync();
    }

    public void Resume()
    {
        SetPaused(false);
        Start();
    }

    public async Task StopAsync()
    {
        var playerRun = await CancelLoopAsync();
        ResetCompletedCount(playerRun);
    }

    /// <summary>Includes final refresh and session release; failure is never converted into successful idle.</summary>
    public Task WhenIdleAsync() => WaitForIdleLoopAsync();

    private async Task WaitForIdleLoopAsync()
    {
        while (true)
        {
            var playerRun = ReadCurrentRun();
            if (playerRun is null) return;
            await ObserveAsync(playerRun);
            lock (playerLifetime.Gate)
            {
                if (!playerRun.Running && ReferenceEquals(playerRun, playerLifetime.PlayerRun)) return;
            }
        }
    }

    public async Task RefreshAsync()
    {
        var driveQueueCounts = await readCounts();
        Publish(ReadCurrentRun(), driveQueueCounts, null);
    }

    public void Dispose() => CancelForDisposal();

    private void SetPaused(bool paused)
    {
        lock (playerLifetime.Gate) playerLifetime.Paused = paused;
    }

    private void ResetCompletedCount(PlayerRun? playerRun)
    {
        lock (playerLifetime.Gate)
        {
            if (playerRun is not null) playerRun.Completed = 0;
        }
    }

    private PlayerRun? ReadCurrentRun()
    {
        lock (playerLifetime.Gate) return playerLifetime.PlayerRun;
    }

    private void CancelForDisposal()
    {
        lock (playerLifetime.Gate)
        {
            playerLifetime.Disposed = true;
            if (playerLifetime.PlayerRun is { Running: true } playerRun) CancelRun(playerRun);
        }
    }

    private async Task<PlayerRun?> CancelLoopAsync()
    {
        PlayerRun? playerRun;
        lock (playerLifetime.Gate)
        {
            playerRun = playerLifetime.PlayerRun;
            if (playerRun?.Running == true) CancelRun(playerRun);
        }
        if (playerRun is not null) await ObserveAsync(playerRun);
        return playerRun;
    }

    // Caller holds PlayerLifetime.Gate; a later cancellation supersedes any start that arrived while stopping.
    private static void CancelRun(PlayerRun playerRun)
    {
        playerRun.RestartAfterCancellation = false;
        playerRun.cancellationTokenSource.Cancel();
        playerRun.taskCompletionSource.TrySetResult();
    }

    private async Task ObserveAsync(PlayerRun playerRun)
    {
        try { await playerRun.task; }
        finally
        {
            if (playerRun.task.IsFaulted)
            {
                lock (playerLifetime.Gate) playerRun.FaultObserved = true;
            }
        }
    }

    private async Task RunAsync(PlayerRun playerRun)
    {
        try
        {
            while (true)
            {
                lock (playerLifetime.Gate) playerRun.Rerun = false;
                var driveQueueCounts = await RunBoundSessionAsync(playerRun);
                DriveQueueState idleState;
                lock (playerLifetime.Gate) idleState = playerLifetime.Paused ? DriveQueueState.Paused : DriveQueueState.Idle;
                Publish(playerRun, driveQueueCounts, null, idleState);
                lock (playerLifetime.Gate)
                {
                    if (playerRun.Rerun && !playerRun.cancellationTokenSource.IsCancellationRequested) continue;
                    playerRun.Running = false;
                    if (playerRun.RestartAfterCancellation && ReferenceEquals(playerRun, playerLifetime.PlayerRun))
                        StartLoopIfEligible();
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            PublishFault(playerRun, exception);
            throw;
        }
        finally { playerRun.cancellationTokenSource.Dispose(); }
    }

    private void PublishFault(PlayerRun playerRun, Exception exception)
    {
        Action? changed = null;
        lock (playerLifetime.Gate)
        {
            playerRun.Failure = ExceptionDispatchInfo.Capture(exception);
            playerRun.Faulted = true;
            playerRun.Running = false;
            if (ReferenceEquals(playerRun, playerLifetime.PlayerRun))
            {
                playerLifetime.driveQueueStatus = playerLifetime.driveQueueStatus with { State = DriveQueueState.Faulted, Completed = playerRun.Completed };
                changed = playerLifetime.Changed;
            }
        }
        changed?.Invoke();
    }

    private async Task<DriveQueueCounts> RunBoundSessionAsync(PlayerRun playerRun)
    {
        using var driveOperationSession = openSession();
        if (driveOperationSession is null) return await readCounts();
        while (!playerRun.cancellationTokenSource.IsCancellationRequested)
        {
            lock (playerLifetime.Gate)
            {
                playerRun.Rerun = false;
                if (playerRun.taskCompletionSource.Task.IsCompleted)
                    playerRun.taskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            var driveBatchReport = await driveOperationSession.PlayBatchAsync(playerRun.cancellationTokenSource.Token);
            lock (playerLifetime.Gate) playerRun.Completed += driveBatchReport.Done;
            var driveQueueCounts = await readCounts();
            if (driveBatchReport.Played > 0)
            {
                Publish(playerRun, driveQueueCounts, driveBatchReport.Activity, DriveQueueState.Running);
                continue;
            }
            if (driveQueueCounts.NextDue is long due)
            {
                Publish(playerRun, driveQueueCounts, "Waiting to retry", DriveQueueState.Waiting);
                var delay = TimeSpan.FromMilliseconds(Math.Max(10, due - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                Task wakeTask;
                lock (playerLifetime.Gate) wakeTask = playerRun.taskCompletionSource.Task;
                await Task.WhenAny(Task.Delay(delay, playerRun.cancellationTokenSource.Token), wakeTask);
                continue;
            }
            lock (playerLifetime.Gate)
            {
                if (playerRun.Rerun) continue;
            }
            return driveQueueCounts;
        }
        return await readCounts();
    }

    private void Publish(PlayerRun? playerRun, DriveQueueCounts driveQueueCounts, string? activity, DriveQueueState? state = null)
    {
        Action? changed;
        lock (playerLifetime.Gate)
        {
            if (!ReferenceEquals(playerRun, playerLifetime.PlayerRun)) return;
            var effective = state ?? (playerRun?.Faulted == true ? DriveQueueState.Faulted
                : playerLifetime.Paused ? DriveQueueState.Paused
                : playerRun?.Running == true ? DriveQueueState.Running : DriveQueueState.Idle);
            var completed = playerRun?.Completed ?? 0;
            if (ShouldResetCompletedCount(driveQueueCounts, effective)) completed = 0;
            playerLifetime.driveQueueStatus = new(effective, driveQueueCounts.Pending, driveQueueCounts.Failed, completed, activity ?? "");
            changed = playerLifetime.Changed;
        }
        changed?.Invoke();
    }

    private static bool ShouldResetCompletedCount(DriveQueueCounts driveQueueCounts, DriveQueueState effective) =>
        driveQueueCounts.Pending == 0 && driveQueueCounts.Failed == 0 && effective == DriveQueueState.Idle;

    // Private resource state: only this player accesses these members, always under PlayerLifetime.Gate.
    // A run owns its token until its task finishes. Counters, wake/rerun flags and faults never transfer.
    private sealed class PlayerRun
    {
        internal readonly CancellationTokenSource cancellationTokenSource = new();
        internal Task task = Task.CompletedTask;
        internal TaskCompletionSource taskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Running = true;
        internal bool Rerun;
        internal bool RestartAfterCancellation;
        internal bool Faulted;
        internal bool FaultObserved;
        internal ExceptionDispatchInfo? Failure;
        internal int Completed;
    }

    // Owns subscriptions and the current run for one player; no mutable state escapes the gate.
    private sealed class PlayerLifetime
    {
        internal readonly object Gate = new();
        internal PlayerRun? PlayerRun;
        internal bool Paused;
        internal bool Disposed;
        internal DriveQueueStatus driveQueueStatus = DriveQueueStatus.Empty;
        internal Action? Changed;
    }
}
