using Gallery.Core.Exceptions;
using Gallery.Core.Services.Sessions;
using Gallery.Core.Services.Sessions.Generations;

namespace Gallery.Tests;

// Direct admission: only the lease owner exposes the buffers needed to verify revocation and zeroing.
[Trait("Category", "class_test")]
public sealed class SessionLeaseOwnershipTests
{
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
        var state = new VaultLeaseState { LastAccessUtc = started, InactivityTimeout = TimeSpan.FromMinutes(1) };
        var generation = new UnlockedVaultSession(Enumerable.Repeat((byte)3, 32).ToArray(), state);

        var beforeLimit = generation.HasExpired(started.AddSeconds(59));
        var atLimit = generation.HasExpired(started.AddMinutes(1));
        var revokedBeforeCommand = generation.IsRevoked;
        generation.Expire();
        var failure = Assert.Throws<VaultLockedException>(() => generation.Borrow());

        Assert.False(beforeLimit);
        Assert.True(atLimit);
        Assert.False(revokedBeforeCommand);
        Assert.True(generation.IsRevoked);
        Assert.Equal("timed_out", failure.ContextualDataByName.ValuesByName["Vault.State"]);
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
}
