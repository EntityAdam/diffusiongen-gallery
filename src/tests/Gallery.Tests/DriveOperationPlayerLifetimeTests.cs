using System.Collections.Concurrent;
using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers.Models;
using Gallery.Core.Services.Processors;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class DriveOperationPlayerLifetimeTests
{
    [Fact]
    public async Task Runs_WhenStartedIndependently_ThenCountersAndTokensBelongToEachRun()
    {
        var tokens = new ConcurrentQueue<CancellationToken>();
        var completions = new ConcurrentQueue<int>();
        var opened = 0;
        using var player = new DriveOperationPlayer(() =>
        {
            var done = Interlocked.Increment(ref opened) == 1 ? 7 : 2;
            var calls = 0;
            return new Session(token =>
            {
                tokens.Enqueue(token);
                return Task.FromResult(++calls == 1 ? new DriveBatchReport(1, done, "processed") : new(0, 0, ""));
            });
        }, () => Task.FromResult(new DriveQueueCounts(0, 0, null)));
        player.Changed += () =>
        {
            if (player.Status.State == DriveQueueState.Running) completions.Enqueue(player.Status.Completed);
        };

        player.Start();
        await player.WhenIdleAsync();
        player.Start();
        await player.WhenIdleAsync();

        Assert.Equal(new[] { 7, 2 }, completions.ToArray());
        var capturedTokens = tokens.ToArray();
        Assert.NotEqual(capturedTokens[0], capturedTokens[^1]);
        Assert.False(capturedTokens[^1].IsCancellationRequested);
        Assert.Equal(DriveQueueState.Idle, player.Status.State);
    }

    [Fact]
    public async Task IdleRefresh_WhenCountsFail_ThenFaultMustBeObservedBeforeAnotherRunCanReplaceIt()
    {
        var expectedFailure = new IOException("Cannot read queue counts");
        var reads = 0;
        using var player = new DriveOperationPlayer(() => null, () =>
            Interlocked.Increment(ref reads) == 1
                ? Task.FromException<DriveQueueCounts>(expectedFailure)
                : Task.FromResult(new DriveQueueCounts(0, 0, null)));

        player.Start();
        await WaitForFaultAsync(player);
        Assert.Same(expectedFailure, Assert.Throws<IOException>(player.Start));
        Assert.Same(expectedFailure, await Assert.ThrowsAsync<IOException>(player.WhenIdleAsync));
        Assert.Same(expectedFailure, await Assert.ThrowsAsync<IOException>(player.StopAsync));
        player.Start();
        await player.WhenIdleAsync();

        Assert.Equal(DriveQueueState.Idle, player.Status.State);
    }

    [Fact]
    public async Task Batch_WhenItFaults_ThenReleasesSessionAndPreservesTheFailureAtBothBoundaries()
    {
        var expectedFailure = new InvalidDataException("Cannot play persisted operation");
        var session = new Session(_ => Task.FromException<DriveBatchReport>(expectedFailure));
        using var player = new DriveOperationPlayer(() => session, () => Task.FromResult(new DriveQueueCounts(1, 0, null)));

        player.Start();
        var observedFailure = await Assert.ThrowsAsync<InvalidDataException>(player.WhenIdleAsync);

        Assert.Same(expectedFailure, observedFailure);
        Assert.True(session.IsDisposed);
        Assert.Equal(DriveQueueState.Faulted, player.Status.State);
        Assert.Same(expectedFailure, await Assert.ThrowsAsync<InvalidDataException>(player.StopAsync));
    }

    [Fact]
    public async Task Resume_WhenPreviousRunWasPaused_ThenUsesFreshUncancelledRunState()
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new ConcurrentQueue<CancellationToken>();
        var sessions = 0;
        using var player = new DriveOperationPlayer(() =>
        {
            var firstRun = Interlocked.Increment(ref sessions) == 1;
            return new Session(async token =>
            {
                tokens.Enqueue(token);
                if (firstRun)
                {
                    entered.TrySetResult(token);
                    await release.Task;
                }
                return new(0, 0, "");
            });
        }, () => Task.FromResult(new DriveQueueCounts(0, 0, null)));

        player.Start();
        var originalToken = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var paused = player.PauseAsync();
        release.SetResult();
        await paused;
        player.Resume();
        await player.WhenIdleAsync();

        Assert.True(originalToken.IsCancellationRequested);
        Assert.False(tokens.Last().IsCancellationRequested);
        Assert.NotEqual(originalToken, tokens.Last());
        Assert.Equal(2, sessions);
        Assert.Equal(DriveQueueState.Idle, player.Status.State);
    }

    private static async Task WaitForFaultAsync(DriveOperationPlayer player)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (player.Status.State != DriveQueueState.Faulted && DateTime.UtcNow < deadline)
            await Task.Delay(1);
        Assert.Equal(DriveQueueState.Faulted, player.Status.State);
    }

    // One test-owned session serially invokes its scripted batch behavior and exposes only disposal evidence.
    private sealed class Session(Func<CancellationToken, Task<DriveBatchReport>> play) : IDriveOperationSession
    {
        internal bool IsDisposed { get; private set; }
        public Task<DriveBatchReport> PlayBatchAsync(CancellationToken cancellationToken) => play(cancellationToken);
        public void Dispose() => IsDisposed = true;
    }
}
