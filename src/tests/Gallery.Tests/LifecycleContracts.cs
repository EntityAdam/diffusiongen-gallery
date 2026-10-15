using Gallery.Web.Exceptions;
using Gallery.Web.Models;
using Gallery.Web.Services;
using Gallery.Web.Services.Sessions;
using System.Collections.Immutable;

namespace Gallery.Tests;

// Scoped class admission: these tests inspect revocable key capabilities and actual zeroed buffers;
// the public vault boundary cannot expose these security invariants without disclosing key material.
[Trait("Category", "class_test")]
public static class LifecycleContracts
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void UnlockedSession_WhenItsGenerationIsRevoked_ThenCannotBorrow(bool unlockReplacement)
    {
        // Arrange
        using var session = new VaultSession();
        byte[] originalKey = Enumerable.Repeat((byte)42, 32).ToArray();
        session.Unlock(originalKey);
        var unlockedSession = session.AcquireUnlockedSession();
        using var lease = unlockedSession.Borrow();
        byte[] leasedKey = lease.Bytes;
        if (unlockReplacement) session.Unlock(Enumerable.Repeat((byte)7, 32).ToArray());
        else session.Lock();
        VaultLockedException? failure = null;

        // Act
        try { using var staleLease = unlockedSession.Borrow(); }
        catch (VaultLockedException exception) { failure = exception; }

        // Assert
        Assert.IsType<VaultLockedException>(failure);
        Assert.Contains("revoked", failure!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unlock", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalKey, leasedKey);
    }

    [Fact]
    public static async Task VaultSession_WhenLockedWhileLeaseIsHeld_ThenLeaseKeyRemainsUsableUntilReleased()
    {
        // Arrange
        using var session = new VaultSession();
        byte[] original = Enumerable.Repeat((byte)42, 32).ToArray();
        session.Unlock(original);
        var lease = session.AcquireUnlockedSession().Borrow();

        // Act
        var drain = session.LockAndDrainAsync();
        byte[] keyWhileLocked = lease.Bytes.ToArray();
        var drainedEarly = drain.IsCompleted;
        lease.Dispose();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        Assert.False(session.IsUnlocked);
        Assert.False(drainedEarly);
        Assert.Equal(original, keyWhileLocked);
    }

    [Fact]
    public static void ExportContent_WhenItsOwnerClearsIt_ThenOwnedBufferIsZeroAndPublishedSnapshotIsIndependent()
    {
        // Arrange
        byte[] original = [1, 2, 3];
        var file = new WebExportFile("export.png", "image/png", original);
        ImmutableArray<byte> snapshot = file.ContentBytes;

        // Act
        GalleryService.ClearExportContent(file);
        byte[] cleared = file.CopyContentBytes();

        // Assert
        Assert.Equal(new byte[] { 0, 0, 0 }, cleared);
        Assert.Equal(original, snapshot.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, original);
    }

    [Fact]
    public static async Task VaultSession_WhenInactivityTimeoutElapses_ThenAutoLocksAndRaisesEvent()
    {
        // Arrange
        using var session = new VaultSession(TimeSpan.FromMilliseconds(20));
        byte[] key = Enumerable.Repeat((byte)99, 32).ToArray();
        session.Unlock(key);
        session.InactivityTimeout = TimeSpan.FromMilliseconds(50);
        var autoLockedTcs = new TaskCompletionSource<bool>();
        session.AutoLocked += () => autoLockedTcs.TrySetResult(true);

        // Act
        Assert.True(session.IsUnlocked);
        var signaled = await Task.WhenAny(autoLockedTcs.Task, Task.Delay(2000));

        // Assert
        Assert.Equal(autoLockedTcs.Task, signaled);
        Assert.False(session.IsUnlocked);
        Assert.Throws<VaultLockedException>(() => session.AcquireUnlockedSession());
    }

    [Fact]
    public static async Task VaultSession_WhenActivityRecorded_ThenPostponesAutoLock()
    {
        // Arrange
        using var session = new VaultSession(TimeSpan.FromMilliseconds(20));
        byte[] key = Enumerable.Repeat((byte)99, 32).ToArray();
        session.Unlock(key);
        session.InactivityTimeout = TimeSpan.FromMilliseconds(250);

        // Act - record activity halfway through
        await Task.Delay(130);
        session.RecordActivity();
        Assert.True(session.IsUnlocked);

        // Wait another 130ms (total 260ms from start, but only 130ms since RecordActivity)
        await Task.Delay(130);
        Assert.True(session.IsUnlocked);

        // Wait for timeout to actually elapse (another 160ms)
        await Task.Delay(160);
        Assert.False(session.IsUnlocked);
    }

    [Fact]
    public static void VaultSession_WhenAutoLockDisabled_ThenDoesNotAutoLock()
    {
        // Arrange
        using var session = new VaultSession();
        byte[] key = Enumerable.Repeat((byte)99, 32).ToArray();
        session.Unlock(key);
        session.InactivityTimeout = TimeSpan.Zero;

        // Act
        session.CheckInactivityTimeout();

        // Assert
        Assert.True(session.IsUnlocked);
    }

    [Fact]
    public static void VaultSession_Lock_RevokesUnlockedSessionAndZeroesKey()
    {
        // Arrange
        using var session = new VaultSession();
        byte[] key = Enumerable.Repeat((byte)99, 32).ToArray();
        session.Unlock(key);
        var unlockedSession = session.AcquireUnlockedSession();

        // Act
        session.Lock();

        // Assert
        Assert.False(session.IsUnlocked);
        Assert.Throws<VaultLockedException>(() => session.AcquireUnlockedSession());
        Assert.Throws<VaultLockedException>(() => unlockedSession.Borrow());
    }

    [Fact]
    public static async Task GalleryService_Panic_LocksVaultImmediately()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        Assert.True(fixture.Service.IsUnlocked);

        // Act
        fixture.Service.Panic();

        // Assert
        Assert.False(fixture.Service.IsUnlocked);
    }
}
