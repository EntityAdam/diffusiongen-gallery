using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Services.Ports.Models;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Processors.Codecs;
using System.Security.Cryptography;
using Gallery.Web.Components.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gallery.Tests;

public sealed class WorkspaceCatalogTests
{
    [Fact]
    public async Task InitialPageLoadsInBackgroundAndPublishesTheFullCatalog()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var records = await SeedAsync(vault);
        using var catalog = new WorkspaceCatalog(vault.Service, NullLogger.Instance, action => action());
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.Changed += () =>
        {
            if (!catalog.IsPartial) completed.TrySetResult();
            return Task.CompletedTask;
        };

        await catalog.OpenAsync(1, false);
        Assert.Single(catalog.Images);
        Assert.True(catalog.IsPartial);
        Assert.True(catalog.IsLoading);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(catalog.IsLoading);
        Assert.Equal(records.Select(image => image.Id).Order(), catalog.ImagesById.Keys.Order());
    }

    [Fact]
    public async Task DisposedCatalogRejectsAnAlreadyQueuedPublication()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var records = await SeedAsync(vault);
        var queued = new TaskCompletionSource<Func<Task>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var catalog = new WorkspaceCatalog(vault.Service, NullLogger.Instance, action =>
        {
            queued.TrySetResult(action);
            return release.Task;
        });
        await catalog.OpenAsync(1, false);
        var loading = ComponentTestAccess.Get<Task>(catalog, "load");
        var publish = await queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            catalog.Dispose();
            vault.Service.LockVault();
            await publish();
            catalog.Update(records[0]);
            Assert.Empty(catalog.Images);
            Assert.Empty(catalog.ImagesById);
            Assert.Empty(catalog.AllTags);
            Assert.Empty(catalog.Collections.Collections);
            Assert.False(catalog.IsLoading);
        }
        finally { release.TrySetResult(); }
        await loading.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task<IReadOnlyList<ImageRecord>> SeedAsync(TestVault vault)
    {
        // Small catalogs are intentionally returned whole; exceed the encrypted
        // index capacity to exercise the real initial-page/background-load path.
        var records = Enumerable.Range(0, 1025).Select(i => new ImageRecord
        {
            Name = $"{i}.png", ImportedAt = DateTimeOffset.UnixEpoch.AddSeconds(i),
            StoredPath = Path.Combine(vault.Root, "images", $"{i}.dpng"),
            Sha256 = Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(i))),
            MetadataVersion = ComfyMetadata.Version
        }).ToList();
        using var key = vault.Session.AcquireUnlockedSession().Borrow();
        var thumbnail = GalleryTests.MakePng(1, 1);
        await vault.Store.CommitImportsAsync(records.Select(image => new ImportCatalogEntry(image,
            VaultCrypto.Encrypt(thumbnail, key.Bytes, $"thumbnail:{image.Id}"))).ToArray(), key.Bytes);
        // Warm the encrypted first-page index so the workspace opens a partial page.
        await vault.Service.LoadInitialPageAsync(1);
        return records;
    }
}
