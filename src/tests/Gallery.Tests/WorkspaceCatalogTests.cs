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
        var records = BrowsingWorkspaceFixture.CreateImages(3);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new BrowsingWorkspaceFixture.BrowsingCatalogSource(vault.Scope.Catalog, records, release.Task);
        using var catalog = new WorkspaceCatalog(vault.Scope.Vault, source, vault.Scope.Collections, vault.Scope.Tags, NullLogger.Instance, action => action());
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.Changed += () =>
        {
            if (!catalog.IsPartial) completed.TrySetResult();
            return Task.CompletedTask;
        };

        await catalog.OpenAsync(1, false);
        try
        {
            Assert.Equal(records[0], Assert.Single(catalog.Images));
            Assert.True(catalog.IsPartial);
            Assert.True(catalog.IsLoading);
        }
        finally { release.TrySetResult(); }
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(catalog.IsPartial);
        Assert.False(catalog.IsLoading);
        Assert.Equal(records, catalog.Images);
        Assert.Equal(records.Select(image => image.Id).Order(), catalog.ImagesById.Keys.Order());
    }

    [Fact]
    public async Task DisposedCatalogRejectsAnAlreadyQueuedPublication()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var records = BrowsingWorkspaceFixture.CreateImages(3);
        var source = new BrowsingWorkspaceFixture.BrowsingCatalogSource(vault.Scope.Catalog, records, Task.CompletedTask);
        var queued = new TaskCompletionSource<Func<Task>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var catalog = new WorkspaceCatalog(vault.Scope.Vault, source, vault.Scope.Collections, vault.Scope.Tags, NullLogger.Instance, action =>
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
            vault.Scope.Vault.LockVault();
            await publish();
            await catalog.UpdateAsync(records[0]);
            Assert.Empty(catalog.Images);
            Assert.Empty(catalog.ImagesById);
            Assert.Empty(catalog.AllTags);
            Assert.Empty(catalog.Collections.Collections);
            Assert.False(catalog.IsLoading);
        }
        finally { release.TrySetResult(); }
        await loading.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ReloadWaitsForEveryCatalogSubscriber()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        using var catalog = new WorkspaceCatalog(vault.Scope.Vault, vault.Scope.Catalog, vault.Scope.Collections, vault.Scope.Tags, NullLogger.Instance, action => action());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publications = new List<string>();
        catalog.Changed += async () =>
        {
            started.TrySetResult();
            await release.Task;
            publications.Add("first");
        };
        catalog.Changed += () =>
        {
            publications.Add("second");
            return Task.CompletedTask;
        };

        var reload = catalog.ReloadAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(reload.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await reload.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["first", "second"], publications);
    }

}
