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
        add => playerLifetime.AddChanged(value);
        remove => playerLifetime.RemoveChanged(value);
    }

    public DriveQueueStatus Status => playerLifetime.Status;
    public bool IsPaused => playerLifetime.IsPaused;

    public void Start() => playerLifetime.Start(StartRun);

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

    private Task StartRun(PlayerRun playerRun) => Task.Run(() => RunAsync(playerRun));

    public async Task PauseAsync()
    {
        var playerRun = playerLifetime.Pause();
        if (playerRun is not null) await ObserveAsync(playerRun);
        await RefreshAsync();
    }

    public void Resume() => playerLifetime.Resume(StartRun);

    public async Task StopAsync()
    {
        var playerRun = playerLifetime.CancelCurrentRun();
        if (playerRun is not null) await ObserveAsync(playerRun);
        playerLifetime.ResetCompletedCount(playerRun);
    }

    internal Task RunWhileStartAdmissionBlockedAsync(Func<Task> action, Func<Task>? afterAction = null) =>
        playerLifetime.RunWhileStartAdmissionBlockedAsync(action, afterAction, ObserveAsync, StartRun, this);

    /// <summary>Includes final refresh and session release; failure is never converted into successful idle.</summary>
    public Task WhenIdleAsync() => WaitForIdleLoopAsync();

    private async Task WaitForIdleLoopAsync()
    {
        while (true)
        {
            var playerRun = playerLifetime.CurrentRun;
            if (playerRun is null) return;
            await ObserveAsync(playerRun);
            if (playerLifetime.IsStoppedCurrent(playerRun)) return;
        }
    }

    public async Task RefreshAsync()
    {
        var publication = playerLifetime.ReadPublicationSnapshot();
        var driveQueueCounts = await readCounts();
        playerLifetime.PublishRefresh(publication, driveQueueCounts);
    }

    public void Dispose() => playerLifetime.Dispose();

    private async Task ObserveAsync(PlayerRun playerRun)
    {
        try { await playerRun.Task; }
        finally
        {
            if (playerRun.Task.IsFaulted) playerLifetime.MarkFaultObserved(playerRun);
        }
    }

    private async Task RunAsync(PlayerRun playerRun)
    {
        try
        {
            while (true)
            {
                playerLifetime.PrepareRunPass(playerRun);
                var driveQueueCounts = await RunBoundSessionAsync(playerRun);
                playerLifetime.PublishIdle(playerRun, driveQueueCounts);
                if (!playerLifetime.TryContinueRun(playerRun, StartRun)) return;
            }
        }
        catch (Exception exception)
        {
            playerLifetime.PublishFault(playerRun, exception);
            throw;
        }
        finally { playerRun.DisposeCancellationTokenSource(); }
    }

    private async Task<DriveQueueCounts> RunBoundSessionAsync(PlayerRun playerRun)
    {
        using var driveOperationSession = openSession();
        if (driveOperationSession is null) return await readCounts();
        while (!playerRun.IsCancellationRequested)
        {
            var wakeTask = playerLifetime.PrepareBatchWakeSignal(playerRun);
            var driveBatchReport = await driveOperationSession.PlayBatchAsync(playerRun.Token);
            playerLifetime.RecordBatchOutcome(playerRun, driveBatchReport);
            var driveQueueCounts = await readCounts();
            if (driveBatchReport.Played > 0)
            {
                playerLifetime.PublishBatchOutcome(playerRun, driveBatchReport, driveQueueCounts, DriveQueueState.Running);
                continue;
            }
            if (driveQueueCounts.NextDue is long due)
            {
                playerLifetime.PublishBatchOutcome(playerRun, driveBatchReport, driveQueueCounts,
                    DriveQueueState.Waiting, "Waiting to retry");
                await WaitForRetryDueOrSignalAsync(playerRun, due, wakeTask);
                continue;
            }
            if (playerLifetime.ShouldRunAgain(playerRun)) continue;
            return driveQueueCounts;
        }
        return await readCounts();
    }

    private static async Task WaitForRetryDueOrSignalAsync(PlayerRun playerRun, long due, Task wakeTask)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Max(10, due - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await Task.WhenAny(Task.Delay(delay, playerRun.Token), wakeTask);
    }

    // Run state transitions use PlayerLifetime.Gate; each run owns its token, counters, wake signals and faults.
    private sealed class PlayerRun
    {
        private readonly CancellationTokenSource cancellationTokenSource = new();
        private Task task = Task.CompletedTask;
        private TaskCompletionSource taskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool running = true;
        private bool rerun;
        private bool restartAfterCancellation;
        private bool faulted;
        private bool faultObserved;
        private ExceptionDispatchInfo? failure;
        private int completed;

        internal CancellationToken Token => cancellationTokenSource.Token;
        internal Task Task => task;
        internal bool IsCancellationRequested => cancellationTokenSource.IsCancellationRequested;
        internal bool Running => running;
        internal bool Rerun => rerun;
        internal bool RestartAfterCancellation => restartAfterCancellation;
        internal bool Faulted => faulted;
        internal bool FaultObserved => faultObserved;
        internal ExceptionDispatchInfo? Failure => failure;
        internal int Completed => completed;

        internal bool TryGetUnobservedFailure(out ExceptionDispatchInfo observedFailure)
        {
            if (!faultObserved && failure is { } recordedFailure)
            {
                observedFailure = recordedFailure;
                return true;
            }
            observedFailure = null!;
            return false;
        }

        internal void SetTask(Task value) => task = value;

        internal void DisposeCancellationTokenSource() => cancellationTokenSource.Dispose();

        internal void RequestStart()
        {
            if (cancellationTokenSource.IsCancellationRequested)
                restartAfterCancellation = true;
            else
                rerun = true;
            taskCompletionSource.TrySetResult();
        }

        internal void ResetCompleted() => completed = 0;

        internal void AddCompleted(int count) => completed += count;

        internal void Cancel()
        {
            restartAfterCancellation = false;
            cancellationTokenSource.Cancel();
            taskCompletionSource.TrySetResult();
        }

        internal void MarkFaultObserved() => faultObserved = true;

        internal void PreparePass() => rerun = false;

        internal void StopRunning() => running = false;

        internal void RecordFailure(Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
            faulted = true;
            running = false;
        }

        internal Task PrepareWakeSignal()
        {
            rerun = false;
            if (taskCompletionSource.Task.IsCompleted)
                taskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return taskCompletionSource.Task;
        }
    }

    // Owns subscriptions, lifecycle transitions, batch publication, and the current run for one player.
    private sealed class PlayerLifetime
    {
        private readonly object gate = new();
        private PlayerRun? playerRun;
        private bool paused;
        private bool disposed;
        private bool startAdmissionBlocked;
        private bool startRequestedWhileBlocked;
        private long statusPublicationGeneration;
        private long refreshSequence;
        private long publishedRefreshSequence;
        private DriveQueueStatus status = DriveQueueStatus.Empty;
        private Action? changed;

        internal DriveQueueStatus Status { get { lock (gate) return status; } }
        internal bool IsPaused { get { lock (gate) return paused; } }
        internal PlayerRun? CurrentRun { get { lock (gate) return playerRun; } }

        internal void AddChanged(Action? value)
        {
            lock (gate) changed += value;
        }

        internal void RemoveChanged(Action? value)
        {
            lock (gate) changed -= value;
        }

        internal void Start(Func<PlayerRun, Task> startRun)
        {
            lock (gate) StartIfEligible(startRun);
        }

        private void StartIfEligible(Func<PlayerRun, Task> startRun)
        {
            if (disposed || paused) return;
            if (startAdmissionBlocked)
            {
                startRequestedWhileBlocked = true;
                return;
            }
            if (playerRun?.Running == true)
            {
                playerRun.RequestStart();
                return;
            }
            if (playerRun is not null && playerRun.TryGetUnobservedFailure(out var failure))
                failure.Throw();
            StartReplacementRun(startRun);
        }

        private void StartReplacementRun(Func<PlayerRun, Task> startRun)
        {
            var replacementRun = new PlayerRun();
            playerRun = replacementRun;
            replacementRun.SetTask(startRun(replacementRun));
        }

        internal PlayerRun? Pause()
        {
            lock (gate)
            {
                paused = true;
                var currentRun = playerRun;
                if (currentRun?.Running == true) currentRun.Cancel();
                return currentRun;
            }
        }

        internal void Resume(Func<PlayerRun, Task> startRun)
        {
            lock (gate)
            {
                paused = false;
                StartIfEligible(startRun);
            }
        }

        internal async Task RunWhileStartAdmissionBlockedAsync(
            Func<Task> action, Func<Task>? afterAction, Func<PlayerRun, Task> observeRun,
            Func<PlayerRun, Task> startRun, object owner)
        {
            ArgumentNullException.ThrowIfNull(action);
            PlayerRun? currentRun = null;
            var admissionBlocked = false;
            var actionCompleted = false;
            try
            {
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposed, owner);
                    startAdmissionBlocked = true;
                    startRequestedWhileBlocked = false;
                    admissionBlocked = true;
                    currentRun = RetireObservedFaultedRun(playerRun);
                    if (currentRun?.Running == true) currentRun.Cancel();
                }
                if (currentRun is not null) await observeRun(currentRun);
                await action();
                actionCompleted = true;
                if (afterAction is not null) await afterAction();
            }
            finally
            {
                if (admissionBlocked)
                {
                    lock (gate)
                    {
                        startAdmissionBlocked = false;
                        var shouldStart = actionCompleted && startRequestedWhileBlocked && !paused && !disposed;
                        startRequestedWhileBlocked = false;
                        if (shouldStart) StartIfEligible(startRun);
                    }
                }
            }
        }

        private PlayerRun? RetireObservedFaultedRun(PlayerRun? currentRun)
        {
            if (currentRun?.Failure is null || !currentRun.FaultObserved) return currentRun;
            if (ReferenceEquals(currentRun, playerRun)) playerRun = null;
            return null;
        }

        internal PlayerRun? CancelCurrentRun()
        {
            lock (gate)
            {
                var currentRun = playerRun;
                if (currentRun?.Running == true) currentRun.Cancel();
                return currentRun;
            }
        }

        internal void ResetCompletedCount(PlayerRun? currentRun)
        {
            lock (gate)
            {
                if (currentRun is not null) currentRun.ResetCompleted();
            }
        }

        internal bool IsStoppedCurrent(PlayerRun currentRun)
        {
            lock (gate) return !currentRun.Running && ReferenceEquals(currentRun, playerRun);
        }

        internal (PlayerRun? PlayerRun, long PublicationGeneration, long RefreshSequence) ReadPublicationSnapshot()
        {
            lock (gate) return (playerRun, statusPublicationGeneration, ++refreshSequence);
        }

        internal void PublishRefresh(
            (PlayerRun? PlayerRun, long PublicationGeneration, long RefreshSequence) snapshot,
            DriveQueueCounts counts) =>
            PublishStatus(snapshot.PlayerRun, counts, null,
                expectedPublicationGeneration: snapshot.PublicationGeneration,
                refreshSequence: snapshot.RefreshSequence);

        internal void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                if (playerRun is { Running: true } currentRun) currentRun.Cancel();
            }
        }

        internal void MarkFaultObserved(PlayerRun currentRun)
        {
            lock (gate)
            {
                if (currentRun.Task.IsFaulted) currentRun.MarkFaultObserved();
            }
        }

        internal void PrepareRunPass(PlayerRun currentRun)
        {
            lock (gate) currentRun.PreparePass();
        }

        internal void PublishIdle(PlayerRun currentRun, DriveQueueCounts counts)
        {
            DriveQueueState idleState;
            lock (gate) idleState = paused ? DriveQueueState.Paused : DriveQueueState.Idle;
            PublishStatus(currentRun, counts, null, idleState);
        }

        internal bool TryContinueRun(PlayerRun currentRun, Func<PlayerRun, Task> startRun)
        {
            lock (gate)
            {
                if (currentRun.Rerun && !currentRun.IsCancellationRequested) return true;
                currentRun.StopRunning();
                if (currentRun.RestartAfterCancellation && ReferenceEquals(currentRun, playerRun))
                    StartIfEligible(startRun);
                return false;
            }
        }

        internal void PublishFault(PlayerRun currentRun, Exception exception)
        {
            Action? changedCallback = null;
            lock (gate)
            {
                currentRun.RecordFailure(exception);
                if (ReferenceEquals(currentRun, playerRun))
                {
                    status = status with { State = DriveQueueState.Faulted, Completed = currentRun.Completed };
                    statusPublicationGeneration++;
                    changedCallback = changed;
                }
            }
            changedCallback?.Invoke();
        }

        internal Task PrepareBatchWakeSignal(PlayerRun currentRun)
        {
            lock (gate) return currentRun.PrepareWakeSignal();
        }

        internal void RecordBatchOutcome(PlayerRun currentRun, DriveBatchReport report)
        {
            lock (gate) currentRun.AddCompleted(report.Done);
        }

        internal void PublishBatchOutcome(
            PlayerRun currentRun, DriveBatchReport report, DriveQueueCounts counts,
            DriveQueueState state, string? activity = null) =>
            PublishStatus(currentRun, counts, activity ?? report.Activity, state);

        internal bool ShouldRunAgain(PlayerRun currentRun)
        {
            lock (gate) return currentRun.Rerun;
        }

        private void PublishStatus(PlayerRun? currentRun, DriveQueueCounts counts, string? activity,
            DriveQueueState? state = null, long? expectedPublicationGeneration = null, long? refreshSequence = null)
        {
            Action? changedCallback;
            lock (gate)
            {
                if (!ReferenceEquals(currentRun, playerRun)) return;
                if (expectedPublicationGeneration is long expectedGeneration
                    && expectedGeneration != statusPublicationGeneration) return;
                if (refreshSequence is long sequence && sequence <= publishedRefreshSequence) return;
                var effective = state ?? (currentRun?.Faulted == true ? DriveQueueState.Faulted
                    : paused ? DriveQueueState.Paused
                    : currentRun?.Running == true ? DriveQueueState.Running : DriveQueueState.Idle);
                var completed = currentRun?.Completed ?? 0;
                if (counts.Pending == 0 && counts.Failed == 0 && effective == DriveQueueState.Idle) completed = 0;
                status = new(effective, counts.Pending, counts.Failed, completed, activity ?? "");
                if (refreshSequence is long publishedSequence)
                    publishedRefreshSequence = publishedSequence;
                else
                    statusPublicationGeneration++;
                changedCallback = changed;
            }
            changedCallback?.Invoke();
        }
    }
}
