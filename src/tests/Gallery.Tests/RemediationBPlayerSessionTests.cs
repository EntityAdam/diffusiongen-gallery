using System.Collections.Concurrent;
using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.DataManagers.Models;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Sessions;
using Gallery.Core.Services.Sessions.Locations;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class RemediationBPlayerSessionTests
{
    [Fact]
    public async Task Resume_WhenPauseIsStillCancellingTheRun_ThenReplacementRunProcessesPendingWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = 0;
        var openSessions = 0;
        var maximumOpenSessions = 0;
        var pending = 1;
        using var player = new DriveOperationPlayer(() =>
        {
            var firstSession = Interlocked.Increment(ref sessions) == 1;
            var open = Interlocked.Increment(ref openSessions);
            InterlockedMax(ref maximumOpenSessions, open);
            return new Session(async _ =>
            {
                if (firstSession)
                {
                    entered.TrySetResult();
                    await release.Task;
                    return new(0, 0, "");
                }
                return Interlocked.Exchange(ref pending, 0) == 1 ? new DriveBatchReport(1, 1, "processed") : new(0, 0, "");
            }, () => Interlocked.Decrement(ref openSessions));
        }, () => Task.FromResult(new DriveQueueCounts(Volatile.Read(ref pending), 0, null)));

        player.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pausing = player.PauseAsync();
        player.Resume();
        release.SetResult();
        await pausing.WaitAsync(TimeSpan.FromSeconds(5));
        await player.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, sessions);
        Assert.Equal(0, Volatile.Read(ref pending));
        Assert.Equal(1, maximumOpenSessions);
        Assert.False(player.IsPaused);
        Assert.Equal(DriveQueueState.Idle, player.Status.State);
    }

    [Fact]
    public async Task Stop_WhenIssuedAfterAStartThatArrivedDuringPause_ThenNoReplacementRunStarts()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessions = 0;
        using var player = new DriveOperationPlayer(() =>
        {
            Interlocked.Increment(ref sessions);
            return new Session(async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return new(0, 0, "");
            }, () => { });
        }, () => Task.FromResult(new DriveQueueCounts(1, 0, null)));

        player.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pausing = player.PauseAsync();
        player.Resume();
        var stopping = player.StopAsync();
        release.SetResult();
        await Task.WhenAll(pausing, stopping).WaitAsync(TimeSpan.FromSeconds(5));
        await player.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, sessions);
    }

    [Fact]
    public async Task Batch_WhenItFaultsAfterProgress_ThenChangedObservesFaultedStatus()
    {
        var expectedFailure = new InvalidDataException("Cannot play persisted operation");
        var calls = 0;
        var observedStates = new ConcurrentQueue<DriveQueueState>();
        using var player = new DriveOperationPlayer(
            () => new Session(_ => ++calls == 1
                ? Task.FromResult(new DriveBatchReport(1, 1, "processed"))
                : Task.FromException<DriveBatchReport>(expectedFailure), () => { }),
            () => Task.FromResult(new DriveQueueCounts(1, 0, null)));
        player.Changed += () => observedStates.Enqueue(player.Status.State);

        player.Start();
        var observedFailure = await Assert.ThrowsAsync<InvalidDataException>(player.WhenIdleAsync);

        Assert.Same(expectedFailure, observedFailure);
        Assert.Contains(DriveQueueState.Running, observedStates);
        Assert.Equal(DriveQueueState.Faulted, observedStates.Last());
    }

    [Fact]
    public async Task EnsureCheckout_WhenDisposalRacesAcquisition_ThenNoCheckoutOutlivesDisposal()
    {
        var root = Path.Combine(Path.GetTempPath(), "gallery-remediation-b-" + Guid.NewGuid().ToString("N"));
        try
        {
            for (var iteration = 0; iteration < 100; iteration++)
            {
                var galleryLifetime = new GalleryLifetime(new GalleryStore(Path.Combine(root, "gallery.db")), new VaultStore(root));
                using var startGate = new Barrier(2);
                var unlocking = Task.Run(() =>
                {
                    startGate.SignalAndWait();
                    try { galleryLifetime.EnsureCheckout(); }
                    catch (ObjectDisposedException) { }
                });
                var disposing = Task.Run(() =>
                {
                    startGate.SignalAndWait();
                    if (galleryLifetime.TryBeginDisposal() && galleryLifetime.Current.Checkout is not null)
                        galleryLifetime.ReleaseCheckout();
                });
                await Task.WhenAll(unlocking, disposing).WaitAsync(TimeSpan.FromSeconds(5));

                using var nextCheckout = VaultCheckout.Acquire(root);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Unlock_WhenKeyIsNull_ThenRejectsAtTheEntryBoundary()
    {
        using var vaultSession = new VaultSession(Timeout.InfiniteTimeSpan);

        Assert.Throws<ArgumentNullException>(() => vaultSession.Unlock(null!));
        Assert.False(vaultSession.IsUnlocked);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current) return;
            current = previous;
        }
    }

    private sealed class Session(Func<CancellationToken, Task<DriveBatchReport>> play, Action onDispose) : IDriveOperationSession
    {
        public Task<DriveBatchReport> PlayBatchAsync(CancellationToken cancellationToken) => play(cancellationToken);
        public void Dispose() => onDispose();
    }
}
