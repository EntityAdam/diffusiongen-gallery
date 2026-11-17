using Gallery.Core.Models;
using Gallery.Web.Components;
using Gallery.Web.Components.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace Gallery.Tests;

public sealed class WorkspaceWorkflowTests
{
    [Fact]
    public async Task PartialBulkEditReloadsCommittedRecordsWithoutReplayingTheEdit()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await vault.Service.UpdateAsync(image.Id, edit => edit with { UserDescription = "current notes" });
        var activity = new GalleryActivity();
        using var catalog = new WorkspaceCatalog(vault.Service, NullLogger.Instance, action => action());
        var reloads = 0;
        catalog.Changed += () => { reloads++; return Task.CompletedTask; };
        var curation = new MediaCurationCommands(vault.Service, catalog, CreateCommands(activity));
        var edits = 0;

        await curation.CurateAsync([image.Id, "missing", image.Id], edit =>
        {
            edits++;
            return edit with { IsFavorite = !edit.IsFavorite };
        }, "Saved");

        Assert.NotEmpty(activity.Error);
        Assert.Empty(activity.Message);
        Assert.Equal(1, reloads);
        Assert.Equal(1, edits);
        var committed = Assert.Single(catalog.Images);
        Assert.True(committed.IsFavorite);
        Assert.Equal("current notes", committed.UserDescription);
    }

    [Fact]
    public async Task CurationTogglesUsePersistedValuesAndPublishTheCommittedRecord()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var stale = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await vault.Service.UpdateAsync(stale.Id, edit => edit with { Rating = 3, IsFavorite = true, IsMarkedForDeletion = true, UserDescription = "keep" });
        var activity = new GalleryActivity();
        using var catalog = new WorkspaceCatalog(vault.Service, NullLogger.Instance, action => action());
        await catalog.ReloadAsync();
        var curation = new MediaCurationCommands(vault.Service, catalog, CreateCommands(activity));

        await curation.ToggleRatingAsync(stale, 3);
        var rated = catalog.ImagesById[stale.Id];
        Assert.Equal(0, rated.Rating);
        Assert.True(rated.IsFavorite);
        await curation.ToggleFavoriteAsync(stale);
        Assert.False(catalog.ImagesById[stale.Id].IsFavorite);
        await curation.ToggleMarkAsync(stale);
        var unmarked = catalog.ImagesById[stale.Id];
        Assert.False(unmarked.IsMarkedForDeletion);
        Assert.Equal("keep", unmarked.UserDescription);
        Assert.Equal(unmarked, Assert.Single(await vault.Service.ListAsync()));
    }

    [Fact]
    public async Task RemovalRetainsPerItemResultsAndRefreshesAfterPartialFailure()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        List<ImageRecord> images = [];
        for (var i = 0; i < 3; i++)
        {
            var image = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(30 + i, 20)), "image.png", "folder");
            images.Add(await vault.Service.UpdateAsync(image.Id, edit => edit with { IsMarkedForDeletion = true }));
        }
        await File.WriteAllTextAsync(Path.Combine(vault.Root, images[1].Id + ".png"), "collision");
        var activity = new GalleryActivity();
        IReadOnlyList<ImageRecord>? refreshed = null;
        using var removal = new RemovalWorkflowState(vault.Service, CreateCommands(activity), NullLogger.Instance,
            async () => { refreshed = await vault.Service.ListAsync(); });
        await removal.PrepareAsync(images.Select(image => image.Id));
        Assert.True(removal.IsOpen);
        await removal.ExecuteAsync((ExistingOriginalAction.Retain, MissingOriginalAction.RestorePng, vault.Root));
        Assert.False(removal.IsOpen);
        Assert.Equal(2, removal.Results.Count(result => result.WasRemoved));
        Assert.Single(removal.Results, result => result.Error is not null);
        Assert.Equal(images[1].Id, Assert.Single(refreshed!).Id);
        Assert.NotEmpty(activity.Error);
    }

    [Fact]
    public async Task RunningLibraryJobBlocksBothRemovalPlanningAndExecution()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await vault.Service.UpdateAsync(image.Id, edit => edit with { IsMarkedForDeletion = true });
        var activity = new GalleryActivity();
        var jobRunning = true;
        var refreshes = 0;
        using var removal = new RemovalWorkflowState(vault.Service, CreateCommands(activity, () =>
        {
            if (jobRunning) throw new InvalidOperationException("Job is running");
        }), NullLogger.Instance, () => { refreshes++; return Task.CompletedTask; });
        await removal.PrepareAsync([image.Id]);
        Assert.False(removal.IsOpen);
        Assert.Equal("Job is running", activity.Error);
        jobRunning = false;
        await removal.PrepareAsync([image.Id]);
        Assert.True(removal.IsOpen);
        jobRunning = true;
        await removal.ExecuteAsync((ExistingOriginalAction.Retain, MissingOriginalAction.Discard, ""));
        Assert.True(removal.IsOpen);
        Assert.Equal(image.Id, Assert.Single(await vault.Service.ListAsync()).Id);
        Assert.Equal(0, refreshes);
    }

    [Fact]
    public async Task VideoUrlCreatedAfterLockIsReleasedWithoutReopeningThePresentation()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        var js = new DelayedMediaJs();
        var activity = new GalleryActivity();
        using var presentation = new MediaPresentationState(vault.Service, js, CreateCommands(activity));
        // The stored bytes suffice here: only the video URL lifecycle is under test.
        var opening = presentation.OpenAsync(image with { MediaType = "video" });
        await js.Creating.Task.WaitAsync(TimeSpan.FromSeconds(5));
        presentation.Dispose();
        vault.Service.LockVault();
        js.Created.SetResult("blob:late-video");
        await opening.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(presentation.IsOpen);
        Assert.Empty(presentation.Preview);
        Assert.Empty(presentation.VideoUrl);
        Assert.Equal("blob:late-video", Assert.Single(js.Released));
    }

    [Fact]
    public async Task LockedGalleryDoesNotRenderRetainedPresentationData()
    {
        using var vault = new TestVault();
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        using var presentation = new MediaPresentationState(vault.Service, null!, CreateCommands(new()));
        var image = new ImageRecord { Id = "retained", Name = "private name" };
        ComponentTestAccess.Set(presentation, "Selected", image);
        ComponentTestAccess.Set(presentation, "Preview", "data:image/png;base64,private");
        ComponentTestAccess.Set(presentation, "ViewerImages", new[] { image });
        using var catalog = new WorkspaceCatalog(vault.Service, NullLogger.Instance, action => action());
        var html = await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await fixture.HtmlRenderer.RenderComponentAsync<MediaPresentationHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(MediaPresentationHost.State)] = presentation,
                [nameof(MediaPresentationHost.Catalog)] = catalog,
                [nameof(MediaPresentationHost.Activity)] = new GalleryActivity()
            }));
            return root.ToHtmlString();
        });
        Assert.Empty(html.Trim());
    }

    [Fact]
    public async Task ImportCompletionRefreshesTheSessionThatIsCurrentlyOpen()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var activity = new GalleryActivity();
        var history = new OrganizationHistory(vault.Service);
        using var retired = new WorkspaceSession(vault.Service, activity, history, null!,
            NullLogger.Instance, action => action(), () => { });
        retired.Dispose();
        using var current = new WorkspaceSession(vault.Service, activity, history, null!,
            NullLogger.Instance, action => action(), () => { });
        var image = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");

        await activity.Ingestion.NotifyCompletedAsync();
        await current.Browser.PauseAsync();
        await vault.Service.WaitForDriveOperationsAsync();

        Assert.Empty(retired.Catalog.Images);
        Assert.Equal(image.Id, Assert.Single(current.Catalog.Images).Id);
    }

    [Fact]
    public async Task DisposingTheSessionDuringRefreshRevokesTheCatalogAndStopsFurtherNotifications()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        using var session = new WorkspaceSession(vault.Service, new(), new(vault.Service), null!,
            NullLogger.Instance, action => action(), () => { });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifiedAfterDisposal = false;
        session.Catalog.Changed += async () =>
        {
            started.TrySetResult();
            await release.Task;
        };
        session.Catalog.Changed += () =>
        {
            notifiedAfterDisposal = true;
            return Task.CompletedTask;
        };

        var reload = session.Catalog.ReloadAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            session.Dispose();
        }
        finally { release.TrySetResult(); }
        await reload.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(session.Catalog.CanRead);
        Assert.False(notifiedAfterDisposal);
    }

    private static WorkspaceCommands CreateCommands(GalleryActivity activity, Action? ensureNoJob = null) => new(activity,
        (status, action) => activity.RunAsync(status, action, NullLogger.Instance), ensureNoJob ?? (() => { }));

    private sealed class DelayedMediaJs : IJSRuntime
    {
        public TaskCompletionSource Creating { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Released { get; } = [];
        public async ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args)
        {
            if (identifier == "galleryMedia.createStreamUrl")
            {
                Creating.SetResult();
                return (T)(object)await Created.Task;
            }
            if (identifier == "galleryViewer.releaseImageUrls") Released.AddRange((string[])args![0]!);
            return default!;
        }
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<T>(identifier, args);
    }
}
