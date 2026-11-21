using Gallery.Core.Exceptions;
using Gallery.Core.Features.Vaults.Session;

namespace Gallery.Tests;

// Direct admission: only the lease owner exposes the buffers needed to verify revocation and zeroing.
[Trait("Category", "class_test")]
public sealed class SessionLeaseOwnershipTests
{
    [Fact]
    public void BackgroundKeyBorrow_DoesNotResetInactivityAndStillParticipatesInDrain()
    {
        var lastActivity = DateTimeOffset.UtcNow.AddMinutes(-1);
        var state = new VaultLeaseState { LastAccessUtc = lastActivity, InactivityTimeout = TimeSpan.FromHours(1) };
        var generation = new UnlockedVaultSession(Enumerable.Repeat((byte)7, 32).ToArray(), state);
        using var lease = generation.Borrow(LeaseActivityPolicy.PreserveInactivityDeadline);
        Assert.Equal(lastActivity, state.LastAccessUtc);
        Assert.Equal(1, state.ActiveLeases);
        generation.Revoke();
        Assert.Throws<VaultLockedException>(() => generation.Borrow(LeaseActivityPolicy.PreserveInactivityDeadline));
        Assert.Equal(1, state.ActiveLeases);
        Assert.Equal(Enumerable.Repeat((byte)7, 32), lease.Bytes);
        var buffer = lease.Bytes;
        lease.Dispose();
        Assert.Equal(new byte[32], buffer);
        Assert.Equal(0, state.ActiveLeases);
    }

    [Fact]
    public async Task ReplacedGeneration_DrainsBothGenerationsAndReleasesEachLeaseExactlyOnce()
    {
        using var session = new VaultSession(Timeout.InfiniteTimeSpan);
        byte[] firstKey = Enumerable.Repeat((byte)7, 32).ToArray();
        session.Unlock(firstKey);
        var firstGeneration = session.AcquireUnlockedSession();
        var firstLease = firstGeneration.Borrow();
        var firstBuffer = firstLease.Bytes;
        session.Unlock(Enumerable.Repeat((byte)9, 32).ToArray());
        using var secondLease = session.AcquireUnlockedSession().Borrow();

        var drain = session.LockAndDrainAsync();
        var revoked = Assert.Throws<VaultLockedException>(() => firstGeneration.Borrow());
        var afterLock = firstBuffer.ToArray();
        firstLease.Dispose();
        firstLease.Dispose();
        var drainedBeforeSecondRelease = drain.IsCompleted;
        secondLease.Dispose();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("revoked", revoked.ContextualDataByName.ValuesByName["Vault.State"]);
        Assert.Equal(firstKey, afterLock);
        Assert.Equal(new byte[32], firstBuffer);
        Assert.False(drainedBeforeSecondRelease,
            $"Expected drain completed before second lease release: false. Actual: {drainedBeforeSecondRelease}.");
        Assert.True(drain.IsCompletedSuccessfully,
            $"Expected drain successfully completed after all lease releases: true. Actual: {drain.IsCompletedSuccessfully}.");
    }

    [Fact]
    public void ExpiryQuery_DoesNotRevokeOrClearUntilTheExplicitExpiryCommand()
    {
        var started = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(started);
        var state = new VaultLeaseState(timeProvider) { LastAccessUtc = started, InactivityTimeout = TimeSpan.FromMinutes(1) };
        var generation = new UnlockedVaultSession(Enumerable.Repeat((byte)3, 32).ToArray(), state);

        timeProvider.Advance(TimeSpan.FromSeconds(59));
        var beforeLimit = generation.HasExpired();
        timeProvider.AdjustUtc(TimeSpan.FromHours(-2));
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var atLimit = generation.HasExpired();
        var revokedBeforeCommand = generation.IsRevoked;
        generation.Revoke(timedOut: true);
        var failure = Assert.Throws<VaultLockedException>(() => generation.Borrow());

        Assert.False(beforeLimit);
        Assert.True(atLimit);
        Assert.False(revokedBeforeCommand);
        Assert.True(generation.IsRevoked);
        Assert.Equal("timed_out", failure.ContextualDataByName.ValuesByName["Vault.State"]);
    }

    [Fact]
    public void ExpiredGenerationBorrow_RevokesAndLeavesExistingLeaseUntilDisposed()
    {
        var started = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(started);
        var state = new VaultLeaseState(timeProvider) { LastAccessUtc = started, InactivityTimeout = TimeSpan.FromMinutes(1) };
        var generation = new UnlockedVaultSession(Enumerable.Repeat((byte)3, 32).ToArray(), state);
        using var lease = generation.Borrow();
        var buffer = lease.Bytes;

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var failure = Assert.Throws<VaultLockedException>(() => generation.Borrow());

        Assert.Equal("timed_out", failure.ContextualDataByName.ValuesByName["Vault.State"]);
        Assert.Equal("The gallery locked automatically due to inactivity.", failure.Message);
        Assert.True(generation.IsRevoked);
        Assert.True(generation.HasPendingTimeoutNotification);
        Assert.True(generation.RevocationToken.IsCancellationRequested);
        Assert.Equal(1, state.ActiveLeases);
        Assert.Equal(Enumerable.Repeat((byte)3, 32), buffer);
        lease.Dispose();
        Assert.Equal(new byte[32], buffer);
        Assert.Equal(0, state.ActiveLeases);
    }

    [Fact]
    public void ManualLockBorrow_ThrowsAsRevokedWithoutPublishingTimeout()
    {
        using var session = new VaultSession(Timeout.InfiniteTimeSpan);
        session.Unlock(new byte[32]);
        var notifications = 0;
        session.AutoLocked += () => notifications++;
        session.Lock();

        var failure = Assert.Throws<VaultLockedException>(
            () => session.BorrowUnlockedKey(LeaseActivityPolicy.PreserveInactivityDeadline));

        Assert.Equal("revoked", failure.ContextualDataByName.ValuesByName["Vault.State"]);
        Assert.Equal(0, notifications);
        Assert.True(session.WhenDrainedAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public void BorrowWithoutUnlockedGeneration_ThrowsAsLockedWithoutCreatingALease()
    {
        using var session = new VaultSession(Timeout.InfiniteTimeSpan);

        var failure = Assert.Throws<VaultLockedException>(
            () => session.BorrowUnlockedKey(LeaseActivityPolicy.PreserveInactivityDeadline));

        Assert.Equal("locked", failure.ContextualDataByName.ValuesByName["Vault.State"]);
        Assert.True(session.WhenDrainedAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public void TimedOutSessionBorrow_ThrowsWithoutCreatingALeaseAndPublishesNotification()
    {
        var started = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(started);
        using var session = new VaultSession(Timeout.InfiniteTimeSpan, timeProvider);
        session.InactivityTimeout = TimeSpan.FromMinutes(1);
        session.Unlock(new byte[32]);
        var notifications = 0;
        session.AutoLocked += () => notifications++;

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var failure = Assert.Throws<VaultLockedException>(
            () => session.BorrowUnlockedKey(LeaseActivityPolicy.PreserveInactivityDeadline));

        Assert.Equal("timed_out", failure.ContextualDataByName.ValuesByName["Vault.State"]);
        Assert.Equal(1, notifications);
        Assert.True(session.WhenDrainedAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public void TimeoutTransition_RaisesOneEventAcrossRepeatedQueriesAndKeyReads()
    {
        using var session = new VaultSession(Timeout.InfiniteTimeSpan);
        session.Unlock(new byte[32]);
        var notifications = 0;
        session.AutoLocked += () => notifications++;

        session.InactivityTimeout = TimeSpan.FromTicks(1);
        session.CheckInactivityTimeout();
        var unlocked = session.IsUnlocked;
        Assert.Throws<InvalidOperationException>(() => session.Key);
        session.CheckInactivityTimeout();

        Assert.False(unlocked);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void TimeoutNotification_WhenOneSubscriberThrows_ThenRemainingSubscribersStillRun()
    {
        using var session = new VaultSession(Timeout.InfiniteTimeSpan);
        session.Unlock(new byte[32]);
        var notifications = 0;
        session.AutoLocked += () => throw new InvalidOperationException("subscriber failure");
        session.AutoLocked += () => notifications++;

        session.InactivityTimeout = TimeSpan.FromTicks(1);

        Assert.False(session.IsUnlocked);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public async Task ConcurrentLeaseDisposal_ClearsEveryBufferAndCompletesDrain()
    {
        using var session = new VaultSession(Timeout.InfiniteTimeSpan);
        session.Unlock(Enumerable.Repeat((byte)5, 32).ToArray());
        var generation = session.AcquireUnlockedSession();
        var leases = Enumerable.Range(0, 64).Select(_ => generation.Borrow()).ToArray();
        var buffers = leases.Select(lease => lease.Bytes).ToArray();

        var drain = session.LockAndDrainAsync();
        await Task.WhenAll(leases.Select(lease => Task.Run(() => { lease.Dispose(); lease.Dispose(); })));
        await drain.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(buffers, buffer => Assert.Equal(new byte[32], buffer));
        Assert.True(drain.IsCompletedSuccessfully);
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset utcNow = utcNow;
        private long timestamp;

        public override DateTimeOffset GetUtcNow() => utcNow;
        public override long GetTimestamp() => timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan elapsed)
        {
            utcNow += elapsed;
            timestamp += elapsed.Ticks;
        }

        public void AdjustUtc(TimeSpan adjustment) => utcNow += adjustment;
    }
}
