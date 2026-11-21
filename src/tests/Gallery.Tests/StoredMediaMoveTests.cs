using Gallery.Core.Features.Media.Movement.Managers;
using Gallery.Core.Features.Storage.DriveOperations;
using Gallery.Core.Features.Vaults.Security;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Models;
using Gallery.Core.Services;
using static Gallery.Tests.GalleryTests;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class StoredMediaMoveTests
{
    [Fact]
    public async Task Move_WhenQueueHasAnUnobservedPriorFault_CommitsAndRestartsThePlayer()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "first.png", "folder");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        var priorFailure = new IOException("previous queue failure");
        var reads = 0;
        using var player = new DriveOperationPlayer(() => null, () =>
            Interlocked.Increment(ref reads) == 1
                ? Task.FromException<DriveQueueCounts>(priorFailure)
                : Task.FromResult(new DriveQueueCounts(0, 0, null)));
        player.Start();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (player.Status.State != DriveQueueState.Faulted && DateTime.UtcNow < deadline) await Task.Delay(1);
        Assert.Same(priorFailure, Assert.Throws<IOException>(player.Start));
        var mover = new StoredMediaMover(new GalleryLifetime(fixture.Store, fixture.Vault), fixture.Session,
            player.CaptureFailure, player.StartAfterCommitAsync);
        var destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(destination);

        await mover.MoveAsync(record.Id, destination, "renamed");
        await player.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var current = Assert.Single(await fixture.Scope.Catalog.ListAsync());
        Assert.Equal(Path.Combine(destination, "renamed.dpng"), current.StoredPath);
        Assert.True(File.Exists(current.StoredPath));
        Assert.True((await fixture.Store.ReadDriveQueueCountsAsync()).Pending > 0);
        Assert.Equal(DriveQueueState.Idle, player.Status.State);
    }

    [Fact]
    public async Task ConcurrentMoves_WhenReplacementPlayerFaults_NeitherMoveSurfacesTheNewFailure()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "first.png", "folder");
        var second = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng(21, 10)), "second.png", "folder");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        var oldFailure = new IOException("previous failure");
        var newFailure = new InvalidDataException("replacement failure");
        var replacementFaulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        using var player = new DriveOperationPlayer(() => null, () =>
        {
            Exception failure = Interlocked.Increment(ref reads) == 1 ? oldFailure : newFailure;
            return Task.FromException<DriveQueueCounts>(failure);
        });
        player.Changed += () =>
        {
            if (Volatile.Read(ref reads) == 2 && player.Status.State == DriveQueueState.Faulted)
                replacementFaulted.TrySetResult();
        };
        player.Start();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (player.Status.State != DriveQueueState.Faulted && DateTime.UtcNow < deadline) await Task.Delay(1);
        Assert.Same(oldFailure, Assert.Throws<IOException>(player.Start));
        var starts = 0;
        var captures = 0;
        var mover = new StoredMediaMover(new GalleryLifetime(fixture.Store, fixture.Vault), fixture.Session,
            () =>
            {
                Interlocked.Increment(ref captures);
                return player.CaptureFailure();
            },
            async failure =>
            {
                if (Interlocked.Increment(ref starts) == 2)
                    await replacementFaulted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await player.StartAfterCommitAsync(failure);
            });
        var destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(destination);
        await fixture.Store.Gate.WaitAsync();
        Task firstMove;
        Task secondMove;
        try
        {
            firstMove = mover.MoveAsync(first.Id, destination, "first");
            secondMove = mover.MoveAsync(second.Id, destination, "second");
            Assert.Equal(2, captures);
        }
        finally { fixture.Store.Gate.Release(); }

        await Task.WhenAll(firstMove, secondMove).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, starts);
        Assert.Equal(2, (await fixture.Store.ReadDriveQueueCountsAsync()).Pending);
        var moved = await fixture.Scope.Catalog.ListAsync();
        Assert.Contains(moved, record => record.Id == first.Id && record.StoredPath == Path.Combine(destination, "first.dpng"));
        Assert.Contains(moved, record => record.Id == second.Id && record.StoredPath == Path.Combine(destination, "second.dpng"));
        Assert.Same(newFailure, Assert.Throws<InvalidDataException>(player.Start));
        Assert.Same(newFailure, await Assert.ThrowsAsync<InvalidDataException>(player.WhenIdleAsync));
    }

    [Fact]
    public async Task Move_WhenRenamingImage_PreservesEncryption()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "first.png", "folder");
        var destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(destination);
        var before = await File.ReadAllBytesAsync(record.StoredPath);

        // Act
        await fixture.Scope.MediaMaintenance.MoveAsync(record.Id, destination, "renamed");
        await fixture.Scope.DriveOperations.WaitForDriveOperationsAsync();
        var current = Assert.Single(await fixture.Scope.Catalog.ListAsync());
        var movedBytes = await File.ReadAllBytesAsync(current.StoredPath);
        var preview = await fixture.Scope.Media.PreviewAsync(record.Id);

        // Assert
        Assert.False(File.Exists(record.StoredPath));
        Assert.Equal("renamed", current.Name);
        Assert.Equal(before, movedBytes);
        Assert.StartsWith("data:image/png;base64,", preview);
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task Move_WhenOldDeleteFailedAndCiphertextIsRestored_ThenReusesAndSupersedesFailedDelete(
        CatalogBackend backend)
    {
        using var fixture = new MoveTestVault(backend);
        var scope = fixture.Scope;
        scope.DriveRetryDelay = TimeSpan.FromMilliseconds(1);
        await scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        await scope.DriveOperations.PauseDriveOperationsAsync();
        var record = await scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "first.png", "folder");
        var originalCiphertext = await File.ReadAllBytesAsync(record.StoredPath);
        var originalDirectory = Path.GetDirectoryName(record.StoredPath)!;
        var originalName = Path.GetFileNameWithoutExtension(record.StoredPath);
        var destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(destination);
        var movedPath = Path.Combine(destination, "renamed.dpng");

        await scope.MediaMaintenance.MoveAsync(record.Id, destination, "renamed");
        await File.WriteAllBytesAsync(record.StoredPath, [1, 2, 3]);
        scope.DriveOperations.ResumeDriveOperations();
        await scope.DriveOperations.WaitForDriveOperationsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, scope.DriveOperations.DriveQueueStatus.Failed);

        await scope.DriveOperations.PauseDriveOperationsAsync();
        await File.WriteAllBytesAsync(record.StoredPath, originalCiphertext);
        await scope.MediaMaintenance.MoveAsync(record.Id, originalDirectory, originalName);
        await scope.DriveOperations.RefreshDriveQueueStatusAsync();

        var current = Assert.Single(await scope.Catalog.ListAsync());
        Assert.Equal(record.StoredPath, current.StoredPath);
        Assert.Equal(originalCiphertext, await File.ReadAllBytesAsync(record.StoredPath));
        Assert.Equal(0, scope.DriveOperations.DriveQueueStatus.Failed);

        scope.DriveOperations.ResumeDriveOperations();
        await scope.DriveOperations.WaitForDriveOperationsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(File.Exists(movedPath));
        Assert.Equal(originalCiphertext, await File.ReadAllBytesAsync(record.StoredPath));
        await scope.Ingestion.VerifyImportedAsync(record.Id);
    }

    [Fact]
    public async Task Move_WhenRenamingImage_RejectsOverwrite()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "first.png", "folder");
        var destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(destination);
        var collision = Path.Combine(destination, "collision.dpng");
        await File.WriteAllTextAsync(collision, "don't overwrite");

        // Act
        var error = await Assert.ThrowsAsync<IOException>(() =>
            fixture.Scope.MediaMaintenance.MoveAsync(record.Id, destination, "collision"));

        // Assert
        Assert.Contains("collision.dpng", error.Message);
        Assert.Equal("don't overwrite", await File.ReadAllTextAsync(collision));
        Assert.Equal(record.StoredPath, Assert.Single(await fixture.Scope.Catalog.ListAsync()).StoredPath);
        Assert.True(File.Exists(record.StoredPath));
    }

    [Fact]
    public async Task Move_WhenRenamingImage_RejectsTraversal()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(MakePng()), "first.png", "folder");
        var destination = Path.Combine(fixture.Root, "destination");
        Directory.CreateDirectory(destination);

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Scope.MediaMaintenance.MoveAsync(record.Id, destination, "..\\escaped"));

        // Assert
        Assert.Contains("..\\escaped", error.Message);
        Assert.Contains("filename", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(record.StoredPath, Assert.Single(await fixture.Scope.Catalog.ListAsync()).StoredPath);
        Assert.True(File.Exists(record.StoredPath));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "escaped.dpng")));
    }

    private sealed class MoveTestVault : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gallery-move-tests-" + Guid.NewGuid().ToString("N"));
        public GalleryScope Scope { get; }

        public MoveTestVault(CatalogBackend backend) =>
            Scope = GalleryScope.Create(Path.Combine(Root, "vault"),
                new GalleryRuntimeOptions { CatalogBackend = backend, StateDirectory = Path.Combine(Root, "state") },
                new HttpClient());

        public void Dispose()
        {
            Scope.Dispose();
            Scope.DriveOperations.WaitForDriveOperationsAsync().GetAwaiter().GetResult();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
