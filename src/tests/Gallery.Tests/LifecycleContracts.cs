using Gallery.Core;
using Gallery.Models;
using Gallery.Services;
using Gallery.Services.Sessions;
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
        TestAssertions.Exception<VaultLockedException>(new ExpectedException("the old unlocked generation is explicitly revoked", "revoked", "unlock"), failure);
        Assert.Equal(originalKey, leasedKey);
    }

    [Fact]
    public static void ExportContent_WhenItsOwnerClearsIt_ThenOwnedBufferIsZeroAndPublishedSnapshotIsIndependent()
    {
        // Arrange
        byte[] original = [1, 2, 3];
        var file = new ExportFile("export.png", "image/png", original);
        ImmutableArray<byte> snapshot = file.ContentBytes;

        // Act
        GalleryService.ClearExportContent(file);
        byte[] cleared = file.CopyContentBytes();

        // Assert
        Assert.Equal(new byte[] { 0, 0, 0 }, cleared);
        Assert.Equal(original, snapshot.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, original);
    }
}
