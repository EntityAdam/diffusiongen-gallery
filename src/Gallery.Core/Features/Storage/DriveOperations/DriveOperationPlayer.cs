using System.Runtime.ExceptionServices;
using Gallery.Core.Models;

namespace Gallery.Core.Features.Storage.DriveOperations;

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

    public async Task StartAfterObservingPreviousFailureAsync()
    {
        try { Start(); }
        catch (Exception startFailure)
        {
            try { await WhenIdleAsync(); }
            catch (Exception observedFailure) when (ReferenceEquals(startFailure, observedFailure)) { }
            Start();
        }
    }

    private void StartLoopIfEligible()
    {
        lock (playerLifetime.Gate)
        {
            if (playerLifetime.Disposed || playerLifetime.Paused) return;
            if (playerLifetime.StartAdmissionBlocked)
            {
                playerLifetime.StartRequestedWhileBlocked = true;
                return;
            }
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
            if (playerRun is not null && TryGetUnobservedFailure(playerRun, out var failure))
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
        PlayerRun? playerRun;
        lock (playerLifetime.Gate)
        {
            playerLifetime.Paused = true;
            playerRun = playerLifetime.PlayerRun;
            if (playerRun?.Running == true) CancelRun(playerRun);
        }
        if (playerRun is not null) await ObserveAsync(playerRun);
        await RefreshAsync();
    }

    public void Resume()
    {
        lock (playerLifetime.Gate)
        {
            playerLifetime.Paused = false;
            StartLoopIfEligible();
        }
    }

    public async Task StopAsync()
    {
        var playerRun = await CancelLoopAsync();
        ResetCompletedCount(playerRun);
    }

    internal async Task RunWhileStartAdmissionBlockedAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        PlayerRun? playerRun = null;
        var startAdmissionBlocked = false;
        var actionCompleted = false;
        try
        {
            lock (playerLifetime.Gate)
            {
                ObjectDisposedException.ThrowIf(playerLifetime.Disposed, this);
                playerLifetime.StartAdmissionBlocked = true;
                playerLifetime.StartRequestedWhileBlocked = false;
                startAdmissionBlocked = true;
                playerRun = RetireObservedFaultedRun(playerLifetime.PlayerRun);
                if (playerRun?.Running == true) CancelRun(playerRun);
            }
            if (playerRun is not null) await ObserveAsync(playerRun);
            await action();
            actionCompleted = true;
        }
        finally
        {
            if (startAdmissionBlocked)
            {
                lock (playerLifetime.Gate)
                {
                    playerLifetime.StartAdmissionBlocked = false;
                    var shouldStart = actionCompleted && playerLifetime.StartRequestedWhileBlocked
                        && !playerLifetime.Paused && !playerLifetime.Disposed;
                    playerLifetime.StartRequestedWhileBlocked = false;
                    if (shouldStart)
                        StartLoopIfEligible();
                }
            }
        }
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
        var (playerRun, publicationGeneration) = ReadPublicationSnapshot();
        var driveQueueCounts = await readCounts();
        Publish(playerRun, driveQueueCounts, null, expectedPublicationGeneration: publicationGeneration);
    }

    public void Dispose() => CancelForDisposal();

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

    private (PlayerRun? PlayerRun, long PublicationGeneration) ReadPublicationSnapshot()
    {
        lock (playerLifetime.Gate)
            return (playerLifetime.PlayerRun, playerLifetime.StatusPublicationGeneration);
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

    private PlayerRun? RetireObservedFaultedRun(PlayerRun? playerRun)
    {
        if (playerRun?.Failure is null || !playerRun.FaultObserved) return playerRun;
        if (ReferenceEquals(playerRun, playerLifetime.PlayerRun)) playerLifetime.PlayerRun = null;
        return null;
    }

    private static bool TryGetUnobservedFailure(PlayerRun playerRun, out ExceptionDispatchInfo failure)
    {
        if (!playerRun.FaultObserved && playerRun.Failure is { } recordedFailure)
        {
            failure = recordedFailure;
            return true;
        }
        failure = null!;
        return false;
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
                PrepareRunPass(playerRun);
                var driveQueueCounts = await RunBoundSessionAsync(playerRun);
                DriveQueueState idleState;
                lock (playerLifetime.Gate) idleState = playerLifetime.Paused ? DriveQueueState.Paused : DriveQueueState.Idle;
                Publish(playerRun, driveQueueCounts, null, idleState);
                if (!TryContinueRun(playerRun)) return;
            }
        }
        catch (Exception exception)
        {
            PublishFault(playerRun, exception);
            throw;
        }
        finally { playerRun.cancellationTokenSource.Dispose(); }
    }

    private void PrepareRunPass(PlayerRun playerRun)
    {
        lock (playerLifetime.Gate) playerRun.Rerun = false;
    }

    private bool TryContinueRun(PlayerRun playerRun)
    {
        lock (playerLifetime.Gate)
        {
            if (playerRun.Rerun && !playerRun.cancellationTokenSource.IsCancellationRequested) return true;
            playerRun.Running = false;
            if (playerRun.RestartAfterCancellation && ReferenceEquals(playerRun, playerLifetime.PlayerRun))
                StartLoopIfEligible();
            return false;
        }
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
                playerLifetime.StatusPublicationGeneration++;
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
            var wakeTask = PrepareBatchWakeSignal(playerRun);
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
                await WaitForRetryDueOrSignalAsync(playerRun, due, wakeTask);
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

    private Task PrepareBatchWakeSignal(PlayerRun playerRun)
    {
        lock (playerLifetime.Gate)
        {
            playerRun.Rerun = false;
            if (playerRun.taskCompletionSource.Task.IsCompleted)
                playerRun.taskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return playerRun.taskCompletionSource.Task;
        }
    }

    private static async Task WaitForRetryDueOrSignalAsync(PlayerRun playerRun, long due, Task wakeTask)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Max(10, due - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await Task.WhenAny(Task.Delay(delay, playerRun.cancellationTokenSource.Token), wakeTask);
    }

    private void Publish(PlayerRun? playerRun, DriveQueueCounts driveQueueCounts, string? activity,
        DriveQueueState? state = null, long? expectedPublicationGeneration = null)
    {
        Action? changed;
        lock (playerLifetime.Gate)
        {
            if (!ReferenceEquals(playerRun, playerLifetime.PlayerRun)) return;
            if (expectedPublicationGeneration is long expectedGeneration
                && expectedGeneration != playerLifetime.StatusPublicationGeneration) return;
            var effective = state ?? (playerRun?.Faulted == true ? DriveQueueState.Faulted
                : playerLifetime.Paused ? DriveQueueState.Paused
                : playerRun?.Running == true ? DriveQueueState.Running : DriveQueueState.Idle);
            var completed = playerRun?.Completed ?? 0;
            if (ShouldResetCompletedCount(driveQueueCounts, effective)) completed = 0;
            playerLifetime.driveQueueStatus = new(effective, driveQueueCounts.Pending, driveQueueCounts.Failed, completed, activity ?? "");
            playerLifetime.StatusPublicationGeneration++;
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
        internal bool StartAdmissionBlocked;
        internal bool StartRequestedWhileBlocked;
        internal long StatusPublicationGeneration;
        internal DriveQueueStatus driveQueueStatus = DriveQueueStatus.Empty;
        internal Action? Changed;
    }
}
