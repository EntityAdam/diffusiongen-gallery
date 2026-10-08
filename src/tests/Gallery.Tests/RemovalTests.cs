using Gallery.Models;
using Gallery.Services;

namespace Gallery.Tests;

public sealed class RemovalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingOriginalCanBeRetainedOrDeleted(bool deleteOriginal)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var source = Path.Combine(fixture.Root, "source.png");
        var bytes = GalleryTests.MakePng();
        await File.WriteAllBytesAsync(source, bytes);
        var record = await fixture.Service.ImportAsync(new MemoryStream(bytes), "source.png", fixture.Root, source);
        await fixture.Service.UpdateAsync(record.Id, image => image.MarkedForDeletion = true);
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        Assert.True(Assert.Single(plan).OriginalExists);
        var results = await fixture.Service.RemoveAsync(plan, deleteOriginal ? ExistingOriginalAction.Delete : ExistingOriginalAction.Retain,
            MissingOriginalAction.RestorePng, "");
        Assert.True(Assert.Single(results).Removed);
        Assert.Null(results[0].Error);
        Assert.Equal(!deleteOriginal, File.Exists(source));
        Assert.False(File.Exists(record.StoredPath));
        Assert.Empty(await fixture.Service.ListAsync());
        var fingerprint = VaultCrypto.ContentFingerprint(fixture.Session.Key, record.Sha256);
        Assert.False(await fixture.Store.ContainsFingerprintAsync(fingerprint));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ReadThumbnailAsync(record.Id));
        await fixture.Service.ImportAsync(new MemoryStream(bytes), "reimport.png", "folder");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOriginalCanBeRestoredOrDiscarded(bool restore)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "source.jpg", "browser-folder");
        await fixture.Service.UpdateAsync(record.Id, image => image.MarkedForDeletion = true);
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        Assert.False(Assert.Single(plan).OriginalExists);
        var results = await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain,
            restore ? MissingOriginalAction.RestorePng : MissingOriginalAction.Discard, fixture.Root);
        Assert.True(Assert.Single(results).Removed);
        Assert.Null(results[0].Error);
        if (restore)
        {
            Assert.Equal(Path.Combine(fixture.Root, record.Id + ".png"), results[0].RestoredPath);
            using var image = SixLabors.ImageSharp.Image.Load(results[0].RestoredPath!);
            Assert.Equal(record.Width, image.Width);
        }
        else Assert.Null(results[0].RestoredPath);
        Assert.False(File.Exists(record.StoredPath));
        Assert.Empty(await fixture.Service.ListAsync());
    }

    [Fact]
    public async Task UnmarkedImagesAreRejectedAtPreviewAndExecution()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PrepareRemovalAsync([record.Id]));
        await fixture.Service.UpdateAsync(record.Id, image => image.MarkedForDeletion = true);
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        await fixture.Service.UpdateAsync(record.Id, image => image.MarkedForDeletion = false);
        var result = Assert.Single(await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, ""));
        Assert.False(result.Removed);
        Assert.Contains("no longer marked", result.Error);
        Assert.True(File.Exists(record.StoredPath));
    }

    [Fact]
    public async Task RestoreCollisionAndTamperingRetainGalleryEntry()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Service.UpdateAsync(record.Id, image => image.MarkedForDeletion = true);
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        var target = Path.Combine(fixture.Root, record.Id + ".png");
        await File.WriteAllTextAsync(target, "existing");
        var result = Assert.Single(await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.RestorePng, fixture.Root));
        Assert.False(result.Removed);
        Assert.NotNull(result.Error);
        Assert.Equal("existing", await File.ReadAllTextAsync(target));
        File.Delete(target);
        var ciphertext = await File.ReadAllBytesAsync(record.StoredPath);
        ciphertext[^1] ^= 1;
        await File.WriteAllBytesAsync(record.StoredPath, ciphertext);
        result = Assert.Single(await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.RestorePng, fixture.Root));
        Assert.False(result.Removed);
        Assert.NotNull(result.Error);
        Assert.False(File.Exists(target));
        Assert.Single(await fixture.Service.ListAsync());
    }

    [Fact]
    public async Task ChangedOriginalIsNeverDeletedAndMissingStatusMustBeReviewedAgain()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var source = Path.Combine(fixture.Root, "source.png");
        var bytes = GalleryTests.MakePng();
        await File.WriteAllBytesAsync(source, bytes);
        var record = await fixture.Service.ImportAsync(new MemoryStream(bytes), "source.png", fixture.Root, source);
        await fixture.Service.UpdateAsync(record.Id, image => image.MarkedForDeletion = true);
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        await File.WriteAllTextAsync(source, "replacement");
        var result = Assert.Single(await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Delete, MissingOriginalAction.Discard, ""));
        Assert.False(result.Removed);
        Assert.Contains("changed since import", result.Error);
        Assert.Equal("replacement", await File.ReadAllTextAsync(source));
        File.Delete(source);
        result = Assert.Single(await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, ""));
        Assert.False(result.Removed);
        Assert.Contains("status changed", result.Error);
        Assert.Single(await fixture.Service.ListAsync());
    }

    [Fact]
    public async Task MixedBulkRemovalReportsPerImageAndContinuesAfterFailure()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var records = new List<ImageRecord>();
        for (var index = 0; index < 3; index++)
        {
            var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(30 + index, 20)), "same.png", "folder");
            await fixture.Service.UpdateAsync(record.Id, image => image.MarkedForDeletion = true);
            records.Add(record);
        }
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, records[1].Id + ".png"), "collision");
        var plan = await fixture.Service.PrepareRemovalAsync(records.Select(record => record.Id));
        var results = await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.RestorePng, fixture.Root);
        Assert.Equal(2, results.Count(result => result.Removed));
        Assert.Single(results, result => result.Error is not null);
        Assert.Equal(records[1].Id, Assert.Single(await fixture.Service.ListAsync()).Id);
    }

    [Fact]
    public async Task DatabaseFailurePutsStagedCiphertextBack()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await fixture.Service.UpdateAsync(record.Id, image => image.MarkedForDeletion = true);
        var plan = await fixture.Service.PrepareRemovalAsync([record.Id]);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(fixture.Root, "gallery.db")};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER prevent_delete BEFORE DELETE ON images BEGIN SELECT RAISE(ABORT, 'test failure'); END";
            await command.ExecuteNonQueryAsync();
        }
        var result = Assert.Single(await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Retain, MissingOriginalAction.Discard, ""));
        Assert.False(result.Removed);
        Assert.Contains("test failure", result.Error);
        Assert.True(File.Exists(record.StoredPath));
        Assert.Single(await fixture.Service.ListAsync());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(record.StoredPath)!, "*.removing-*"));
    }

    [Fact]
    public async Task MixedOriginalStatusesApplyTheirOwnPoliciesInOneBulkRequest()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var source = Path.Combine(fixture.Root, "source.png");
        var bytes = GalleryTests.MakePng();
        await File.WriteAllBytesAsync(source, bytes);
        var existing = await fixture.Service.ImportAsync(new MemoryStream(bytes), "source.png", fixture.Root, source);
        var missing = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 20)), "missing.png", "browser");
        foreach (var image in new[] { existing, missing })
            await fixture.Service.UpdateAsync(image.Id, record => record.MarkedForDeletion = true);
        var plan = await fixture.Service.PrepareRemovalAsync([existing.Id, missing.Id]);
        var results = await fixture.Service.RemoveAsync(plan, ExistingOriginalAction.Delete, MissingOriginalAction.RestorePng, fixture.Root);
        Assert.All(results, result => { Assert.True(result.Removed); Assert.Null(result.Error); });
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(Path.Combine(fixture.Root, missing.Id + ".png")));
        Assert.Empty(await fixture.Service.ListAsync());
    }
}
