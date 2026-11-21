using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text;
using Gallery.Core.Models;
using Gallery.Web.Components.Features.Browsing;
using Gallery.Web.Components.Features.Collections;
using Gallery.Web.Components.Features.Curation;
using Gallery.Web.Components.Features.Media;
using Gallery.Web.Components.Features.Settings;
using Gallery.Web.Components.Features.Tags;
using Gallery.Web.Components.Features.Vision;
using Gallery.Web.Components.Pages;
using Gallery.Web.Components.Shared;
using Gallery.Web.Components.Workspace;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class UiConsistencyTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task SettingsDeepLink_RequiresUnlockAndThenOpensRequestedTab()
    {
        using var vault = new TestVault();
        await vault.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        vault.Scope.Vault.LockVault();
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (home, root) = await Render<Home>(fixture, new() { [nameof(Home.Route)] = "settings/security" });

        var locked = await Html(fixture, root);
        Assert.Contains("unlock-credential", locked);
        Assert.DoesNotContain("new-confirm", locked);

        await Invoke(fixture, home, "UnlockAsync", (TestVault.Passphrase, false));
        await WaitForWorkspaceAsync(fixture, home);
        Assert.Contains("new-confirm", await Html(fixture, root));
    }

    [Fact]
    public async Task EndpointDeepLinkLoadsSavedSettingsAndCatalogReloadPreservesItsDraft()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await vault.Scope.Vision.SaveSettingsAsync(new VisionSettings { Model = "saved-model" });
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (workspace, _) = await fixture.RenderWorkspaceAsync("settings/endpoints");
        var settings = fixture.LatestComponent<SettingsWorkspace>();
        var endpoints = ComponentTestAccess.Get<LlmEndpointSettings>(settings, "llmEndpointSettings");
        Assert.Equal("saved-model", endpoints.SavedSettings.Model);
        Assert.False(settings.HasUnsavedChanges);
        ComponentTestAccess.Call(endpoints, "SetLaneModel", "unsaved-model");

        await Invoke(fixture, workspace, "Catalog.ReloadAsync");

        Assert.True(settings.HasUnsavedChanges);
        Assert.Same(endpoints, ComponentTestAccess.Get<LlmEndpointSettings>(settings, "llmEndpointSettings"));
        Assert.Equal("unsaved-model", ComponentTestAccess.Get<VisionEndpointLane>(endpoints, "Lane").Model);
        Assert.Equal("saved-model", (await vault.Scope.Vision.LoadSettingsAsync()).Model);
    }

    [Fact]
    public async Task SidebarNavigationUsesTheSameNotesGuardAsClosingDetails()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (workspace, root) = await fixture.RenderWorkspaceAsync();
        await Invoke(fixture, workspace, "Presentation.OpenAsync", image, null!);
        var detail = fixture.LatestComponent<MediaDetailDialog>();
        ComponentTestAccess.Set(detail, "editUserDescription", "unsaved notes");
        await Invoke(fixture, detail, "RequestCloseDetailAsync");
        await Invoke(fixture, workspace, "Navigation.SwitchWorkflowAsync", "settings");
        var guard = ComponentTestAccess.Get<UnsavedChangesGuardState>(workspace, "Session.Guard");
        Assert.True(guard.IsPending);
        Assert.Single(Regex.Matches(await Html(fixture, root), "id=\"unsaved-notes-title\""));
        guard.KeepEditing();

        await Invoke(fixture, workspace, "Navigation.SwitchWorkflowAsync", "settings");
        Assert.Equal("gallery", Field<string>(workspace, "Navigation.Section"));
        await Invoke(fixture, workspace, "Session.Guard.DiscardAsync");
        Assert.Equal("settings", Field<string>(workspace, "Navigation.Section"));
        Assert.Null(ComponentTestAccess.Get<MediaPresentationState>(workspace, "Presentation").Selected);
        Assert.Empty(Assert.Single(await vault.Scope.Catalog.ListAsync()).UserDescription);
    }

    [Fact]
    public async Task OpenTrashWaitsForTheDraftDecisionBeforeChangingTheBrowser()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "image.png", "folder");
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (workspace, _) = await fixture.RenderWorkspaceAsync();
        var session = workspace.Session;

        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await session.Navigation.SwitchWorkflowAsync("duplicates");
            await session.Presentation.OpenAsync(image);
            ComponentTestAccess.Set(fixture.LatestComponent<MediaDetailDialog>(), "editUserDescription", "draft");
            await session.Navigation.OpenTrashAsync();
            Assert.True(session.Guard.IsPending);
            session.Guard.KeepEditing();

            Assert.Equal("duplicates", session.Navigation.Section);
            Assert.False(session.Browser.TrashActive);
            Assert.Equal(image.Id, session.Presentation.Selected?.Id);

            await session.Navigation.OpenTrashAsync();
            await session.Guard.DiscardAsync();

            Assert.Equal("gallery", session.Navigation.Section);
            Assert.True(session.Browser.TrashActive);
            Assert.Null(session.Presentation.Selected);
        });
    }

    [Fact]
    public async Task Sidebar_KeyboardShortcutsOpensAndClosesWithoutNavigating()
    {
        using var vault = new TestVault();
        await vault.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (home, root) = await fixture.RenderWorkspaceAsync("settings");
        var html = await Html(fixture, root);
        var shortcuts = html.IndexOf("aria-label=\"Keyboard shortcuts\"", StringComparison.Ordinal);
        var settings = html.IndexOf("aria-label=\"Settings\"", StringComparison.Ordinal);
        Assert.True(shortcuts >= 0 && shortcuts < settings);
        Assert.DoesNotContain("id=\"keyboard-shortcuts-title\"", html);

        await Invoke(fixture, fixture.LatestComponent<GalleryWorkspaceLayout>(), "OpenShortcuts");
        var openHtml = await Html(fixture, root);
        Assert.Contains("aria-labelledby=\"keyboard-shortcuts-title\"", openHtml);
        Assert.Contains("Duplicate review", openHtml);
        Assert.Contains("<kbd>Shift+X</kbd>", openHtml);
        Assert.Contains("<kbd>Ctrl+Shift+L</kbd>", openHtml);

        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.LatestComponent<KeyboardShortcutsDialog>().Close.InvokeAsync());
        Assert.DoesNotContain("id=\"keyboard-shortcuts-title\"", await Html(fixture, root));
        Assert.Equal("settings", home.Session.Navigation.Section);
    }

    [Fact]
    public async Task QuickFilters_SaveAnEncryptedSnapshotAndClearOnLock()
    {
        using var vault = new TestVault();
        await vault.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (host, root) = await Render<Home>(fixture, new());
        var home = fixture.LatestComponent<GalleryWorkspace>();
        var savedFilter = new GalleryFilter { Tag = "cat, dog", MinRating = 4 };
        ComponentTestAccess.Set(home, "Browser.Filter", savedFilter);
        await Invoke(fixture, home, "Navigation.SetActiveToolAsync", "filters");
        var drawer = fixture.LatestComponent<FilterDrawer>();
        await Invoke(fixture, drawer, "BeginSaveQuickFilter");
        await Invoke(fixture, drawer, "SaveQuickFilterAsync");
        Assert.Empty(await vault.Scope.Settings.LoadViewsAsync());
        typeof(FilterDrawer).GetField("quickFilterName", Private)!.SetValue(drawer, "Portraits");
        ComponentTestAccess.Set(home, "Browser.Filter", new GalleryFilter());
        await Invoke(fixture, drawer, "SaveQuickFilterAsync");
        var saved = Assert.Single(await vault.Scope.Settings.LoadViewsAsync());
        Assert.Equal("Portraits", saved.Name);
        Assert.Equal(savedFilter, saved.GalleryFilter);
        Assert.Single(Field<IReadOnlyList<SavedView>>(home, "Browser.QuickFilters"));
        await Invoke(fixture, host, "Lock");
        Assert.Null(Field<WorkspaceSession?>(host, "session"));
        Assert.Empty(home.Session.Browser.QuickFilters);
        Assert.DoesNotContain("Portraits", await Html(fixture, root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tagging_CancelDiscardsBothAdditionsAndRemovals(bool removeAll)
    {
        await using var fixture = ComponentRenderFixture.Create();
        TagEdit? saved = null;
        var cancelled = false;
        var (dialog, root) = await Render<Tagging>(fixture, new()
        {
            [nameof(Tagging.Images)] = new[] { new ImageRecord { Tags = "cat, dog" } },
            [nameof(Tagging.Save)] = EventCallback.Factory.Create<TagEdit>(this, value => saved = value),
            [nameof(Tagging.Cancel)] = EventCallback.Factory.Create(this, () => cancelled = true)
        });
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.LatestComponent<AdvancedSelect>().ValuesChanged.InvokeAsync(removeAll ? [] : ["dog", "bird"]));
        Assert.Null(saved);
        var html = await Html(fixture, root);
        Assert.Contains("Save changes", html);
        Assert.DoesNotContain("aria-label=\"Remove tag cat\"", html);
        await Invoke(fixture, dialog, "CancelAsync");
        Assert.True(cancelled);
        Assert.Null(saved);
    }

    [Fact]
    public async Task GalleryExport_DownloadsOnlySelectedMediaAsOneFileOrAZip()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var first = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var second = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(4, 4)), "second.png", "folder");
        var other = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(5, 5)), "other.png", "folder");
        var js = new DownloadRuntime();
        await using var fixture = ComponentRenderFixture.Create(vault.Scope, js);
        await fixture.RenderWorkspaceAsync();
        var browser = fixture.LatestComponent<GalleryBrowser>();
        Task Key(string key, string id) => Invoke(fixture, browser, "HandleGridKeyAsync", key, id, false);

        await Key("e", other.Id);
        Assert.Null(js.FileName);
        await Key("x", first.Id);
        await Key("e", other.Id);
        Assert.Equal("first.png", js.FileName);
        Assert.Equal("image/png", js.ContentType);
        Assert.StartsWith("89504E470D0A1A0A", Convert.ToHexString(js.Content));

        await Key("x", second.Id);
        await Key("e", other.Id);
        Assert.EndsWith(".zip", js.FileName);
        Assert.Equal("application/zip", js.ContentType);
        using var archive = new ZipArchive(new MemoryStream(js.Content));
        Assert.Equal(["first.png", "second.png"], archive.Entries.Select(entry => entry.FullName).Order());
    }

    [Fact]
    public async Task Organize_ChangingSelectionDiscardsDraftEvenWhenTheNewItemHasTheSameTags()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var first = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var second = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(4, 4)), "second.png", "folder");
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (home, root) = await fixture.RenderWorkspaceAsync();
        await Invoke(fixture, home, "Navigation.NavigateSidebarAsync", "organize");
        var browser = fixture.LatestComponent<GalleryBrowser>();
        await Invoke(fixture, browser, "HandleGridKeyAsync", "x", first.Id, false);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.LatestComponent<AdvancedSelect>().ValuesChanged.InvokeAsync(["draft"]));
        Assert.Contains("aria-label=\"Remove tag draft\"", await Html(fixture, root));

        await Invoke(fixture, browser, "HandleGridKeyAsync", "x", second.Id, false);
        Assert.DoesNotContain("aria-label=\"Remove tag draft\"", await Html(fixture, root));
        Assert.All(await vault.Scope.Catalog.ListAsync(), image => Assert.Empty(image.Tags));
    }

    [Fact]
    public async Task BulkTags_SaveAppliesOnlyChangedAssignments()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var first = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var second = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(4, 4)), "second.png", "folder");
        first = await vault.Scope.Records.UpdateAsync(first.Id, image => image with { Tags = "cat, shared" });
        second = await vault.Scope.Records.UpdateAsync(second.Id, image => image with { Tags = "dog, shared" });
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (workspace, root) = await Render<CollectionsWorkspace>(fixture, new()
        {
            [nameof(CollectionsWorkspace.Images)] = new[] { first, second },
            [nameof(CollectionsWorkspace.Reload)] = (Func<Task<(CollectionCatalog, IReadOnlyList<ImageRecord>)>>)(async () =>
                (await vault.Scope.Collections.LoadCollectionCatalogAsync(), await vault.Scope.Catalog.ListAsync())),
            [nameof(CollectionsWorkspace.Location)] = new CollectionLocation(Unorganized: true)
        });
        await Invoke(fixture, workspace, "SelectAll");
        await Invoke(fixture, workspace, "OpenTags");
        var tagHtml = await Html(fixture, root);
        Assert.Contains("1 of 2 selected", tagHtml);
        Assert.Contains("2 of 2 selected", tagHtml);
        var editor = fixture.LatestComponent<Tagging>();
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.LatestComponent<AdvancedSelect>().ValuesChanged.InvokeAsync(["dog", "shared", "bird"]));
        await Invoke(fixture, editor, "SaveAsync");
        var saved = await vault.Scope.Catalog.ListAsync();
        Assert.Equal("shared, bird", saved.Single(image => image.Id == first.Id).Tags);
        Assert.Equal("dog, shared, bird", saved.Single(image => image.Id == second.Id).Tags);
        Assert.Equal("Tags saved for 2 media item(s).", Field<string>(workspace, "notice"));
    }

    [Theory]
    [InlineData("unorganized")]
    [InlineData("set")]
    [InlineData("all")]
    public async Task Collections_HideTrashByDefaultAndResetIncludeTrashWithOtherFilters(string scope)
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var collection = await vault.Scope.Collections.CreateCollectionAsync("Series");
        var set = await vault.Scope.Collections.CreateSetAsync(collection.Id, "Set");
        var location = scope == "unorganized" ? new CollectionLocation(Unorganized: true)
            : scope == "set" ? new CollectionLocation(collection.Id, set.Id)
            : new CollectionLocation(collection.Id, AllMedia: true);
        var active = new ImageRecord { Id = "active", Name = "Active", SetId = scope == "unorganized" ? null : set.Id };
        var trashed = active with { Id = "trashed", Name = "Trashed", IsMarkedForDeletion = true };
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (workspace, root) = await Render<CollectionsWorkspace>(fixture, new()
        {
            [nameof(CollectionsWorkspace.Catalog)] = await vault.Scope.Collections.LoadCollectionCatalogAsync(),
            [nameof(CollectionsWorkspace.Images)] = new[] { active, trashed },
            [nameof(CollectionsWorkspace.Location)] = location
        });
        Assert.DoesNotContain("data-image-id=\"trashed\"", await Html(fixture, root));
        await Invoke(fixture, workspace, "IncludeTrashChanged", new ChangeEventArgs { Value = true });
        Assert.Contains("data-image-id=\"trashed\"", await Html(fixture, root));
        await Invoke(fixture, workspace, "SelectAll");
        Assert.Equal(2, Field<HashSet<string>>(workspace, "selection").Count);
        await Invoke(fixture, workspace, "ClearFiltersAsync");
        Assert.Empty(Field<HashSet<string>>(workspace, "selection"));
        Assert.DoesNotContain("data-image-id=\"trashed\"", await Html(fixture, root));
    }

    [Theory]
    [InlineData("gallery", false)]
    [InlineData("collections", false)]
    [InlineData("fullscreen", false)]
    [InlineData("gallery", true)]
    [InlineData("collections", true)]
    public async Task OrganizationUndoSurvivesChangingScreens(string origin, bool undoInViewer)
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (home, root) = await fixture.RenderWorkspaceAsync();
        await Invoke(fixture, home, "Catalog.ReloadAsync");
        var moved = await vault.Scope.Collections.MoveMediaToDestinationAsync([image.Id], "New collection", "New set");
        if (origin == "gallery")
            await Invoke(fixture, fixture.LatestComponent<LibraryWorkbench>(), "DestinationSavedAsync", moved);
        else if (origin == "collections")
        {
            await Invoke(fixture, home, "Navigation.SwitchWorkflowAsync", "unorganized");
            await Invoke(fixture, Field<CollectionsWorkspace>(home, "collectionsWorkspace"), "MovedAsync", moved);
        }
        else
        {
            var (viewer, _) = await Render<FullscreenViewer>(fixture, new() { [nameof(FullscreenViewer.Images)] = new[] { image } });
            await Invoke(fixture, viewer, "CollectionMovedAsync", moved);
            await Invoke(fixture, viewer, "CloseAsync");
        }
        await vault.Scope.Records.UpdateAsync(image.Id, edit => edit with { Tags = "preserved", Rating = 4 });
        await Invoke(fixture, home, "Navigation.SwitchWorkflowAsync", "settings");
        Assert.Contains("Undo organization change", await Html(fixture, root));
        if (undoInViewer)
        {
            var (viewer, viewerRoot) = await Render<FullscreenViewer>(fixture, new()
            { [nameof(FullscreenViewer.Images)] = await vault.Scope.Catalog.ListAsync() });
            Assert.Contains("Undo organization change", await Html(fixture, viewerRoot));
            await Invoke(fixture, viewer, "UndoCollectionAsync");
        }
        else await Invoke(fixture, home, "Session.UndoOrganizationAsync");
        var restored = Assert.Single(await vault.Scope.Catalog.ListAsync());
        Assert.Null(restored.SetId);
        Assert.Equal("preserved", restored.Tags);
        Assert.Equal(4, restored.Rating);
        Assert.Empty((await vault.Scope.Collections.LoadCollectionCatalogAsync()).Collections);
        Assert.DoesNotContain("Undo organization change", await Html(fixture, root));
    }

    [Fact]
    public async Task Collections_BlockViewAndSelectionChangesDuringSaveAndReleaseAfterFailure()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var images = Enumerable.Range(0, 13).Select(i => new ImageRecord { Id = $"image-{i}", Name = $"Image {i}" }).ToArray();
        var busyStates = new List<bool>();
        var (workspace, root) = await Render<CollectionsWorkspace>(fixture, new()
        {
            [nameof(CollectionsWorkspace.Images)] = images,
            [nameof(CollectionsWorkspace.Location)] = new CollectionLocation(Unorganized: true),
            [nameof(CollectionsWorkspace.BusyChanged)] = EventCallback.Factory.Create<bool>(this, value => busyStates.Add(value))
        });
        await Invoke(fixture, workspace, "Select", images[0].Id, false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task save = null!;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() =>
        {
            save = (Task)typeof(CollectionsWorkspace).GetMethod("RunAsync", Private)!.Invoke(workspace,
                [(Func<Task>)(async () => { await release.Task; throw new IOException("Save failed"); })])!;
            typeof(ComponentBase).GetMethod("StateHasChanged", Private)!.Invoke(workspace, null);
        });
        var html = await Html(fixture, root);
        Assert.Contains("aria-busy=\"true\"", html);
        var controls = Regex.Matches(html, @"<(?:input|select|button)\b[^>]*>");
        Assert.NotEmpty(controls);
        foreach (Match control in controls) Assert.Matches(@"\sdisabled(?:=|\s|/?>)", control.Value);
        await Invoke(fixture, workspace, "ClearSelection");
        await Invoke(fixture, workspace, "SortChanged", "name");
        await Invoke(fixture, workspace, "IncludeTrashChanged", new ChangeEventArgs { Value = true });
        await Invoke(fixture, workspace, "NextPageAsync");
        Assert.Single(Field<HashSet<string>>(workspace, "selection"));
        Assert.Equal("newest", Field<string>(workspace, "sort"));
        Assert.False(Field<bool>(workspace, "includeTrash"));
        Assert.Equal(0, Field<int>(workspace, "page"));
        release.SetResult();
        await save;
        Assert.Equal(new[] { true, false }, busyStates);
        Assert.False(Field<bool>(workspace, "working"));
        Assert.Equal("Save failed", Field<string>(workspace, "error"));
        await Invoke(fixture, workspace, "SortChanged", "name");
        Assert.Equal("name", Field<string>(workspace, "sort"));
        Assert.Empty(Field<HashSet<string>>(workspace, "selection"));
    }

    [Fact]
    public async Task WorkspaceNavigationWaitsForCollectionSaveAndLockClearsSharedUndo()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (host, root) = await Render<Home>(fixture, new());
        var home = fixture.LatestComponent<GalleryWorkspace>();
        await Invoke(fixture, home, "Catalog.ReloadAsync");
        var moved = await vault.Scope.Collections.MoveMediaToDestinationAsync([image.Id], "Collection", "Set");
        await Invoke(fixture, fixture.LatestComponent<LibraryWorkbench>(), "DestinationSavedAsync", moved);
        await Invoke(fixture, home, "Navigation.SwitchWorkflowAsync", "unorganized");
        ComponentTestAccess.Set(home, "Session.Collections.IsBusy", true);
        await Invoke(fixture, home, "Navigation.SwitchWorkflowAsync", "settings");
        await Invoke(fixture, home, "Navigation.NavigateCollectionsAsync", new CollectionLocation(moved.Catalog.Collections[0].Id));
        Assert.Equal("unorganized", Field<string>(home, "Navigation.Section"));
        Assert.True(Field<CollectionLocation>(home, "Session.Collections.Location").Unorganized);
        var html = await Html(fixture, root);
        Assert.Matches(@"\sdisabled(?:=|\s|/?>)", Regex.Match(html, @"<button[^>]*aria-label=""Settings""[^>]*>").Value);
        await Invoke(fixture, host, "Lock");
        await Invoke(fixture, host, "UnlockAsync", (TestVault.Passphrase, false));
        await WaitForWorkspaceAsync(fixture, host);
        Assert.DoesNotContain("Undo organization change", await Html(fixture, root));
        await vault.Scope.DriveOperations.WaitForDriveOperationsAsync();
    }

    [Fact]
    public async Task UndoFromNewDestinationReturnsToExistingCollectionNavigation()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (home, _) = await fixture.RenderWorkspaceAsync();
        var moved = await vault.Scope.Collections.MoveMediaToDestinationAsync([image.Id], "New collection", "New set");
        await Invoke(fixture, fixture.LatestComponent<LibraryWorkbench>(), "DestinationSavedAsync", moved);
        await Invoke(fixture, home, "Navigation.NavigateCollectionsAsync", new CollectionLocation(moved.Catalog.Collections[0].Id, moved.Catalog.Sets[0].Id));
        await Invoke(fixture, home, "Session.UndoOrganizationAsync");
        Assert.Equal(new CollectionLocation(), Field<CollectionLocation>(home, "Session.Collections.Location"));
        Assert.Null(Assert.Single(await vault.Scope.Catalog.ListAsync()).SetId);
    }

    [Fact]
    public async Task Viewer_RepeatedStarClickClearsButRepeatedShortcutSetsAndZeroClears()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");
        image = await vault.Scope.Records.UpdateAsync(image.Id, record => vault.Scope.Records.SetCuration(record, 3, null));
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (viewer, _) = await Render<FullscreenViewer>(fixture, new() { [nameof(FullscreenViewer.Images)] = new[] { image } });
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => viewer.HandleKey("3"));
        Assert.Equal(3, Assert.Single(await vault.Scope.Catalog.ListAsync()).Rating);
        await Invoke(fixture, viewer, "RateAsync", 3);
        Assert.Equal(0, Assert.Single(await vault.Scope.Catalog.ListAsync()).Rating);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => viewer.HandleKey("5"));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => viewer.HandleKey("0"));
        var saved = Assert.Single(await vault.Scope.Catalog.ListAsync());
        Assert.Equal(0, saved.Rating);
        Assert.True(saved.IsReviewed);
    }

    [Fact]
    public async Task DestinationCreation_IsStagedAndDiscardedWhenMoveIsCancelled()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var cancelled = false;
        var (dialog, root) = await Render<CollectionDestinationDialog>(fixture, new()
        {
            [nameof(CollectionDestinationDialog.Cancel)] = EventCallback.Factory.Create(this, () => cancelled = true)
        });
        await Invoke(fixture, dialog, "SelectCollectionAsync", "New collection");
        await Invoke(fixture, dialog, "SelectSetAsync", "New set");
        var html = await Html(fixture, root);
        Assert.Contains("Destination: New collection / New set", html);
        Assert.Contains("Cancel discards this destination", html);
        await Invoke(fixture, dialog, "CancelAsync");
        Assert.True(cancelled);
        var catalog = await vault.Scope.Collections.LoadCollectionCatalogAsync();
        Assert.Empty(catalog.Collections);
        Assert.Empty(catalog.Sets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Modal_EscapeClosesOnlyWhenIdle(bool busy)
    {
        await using var fixture = ComponentRenderFixture.Create();
        var closed = false;
        var (dialog, root) = await Render<ModalDialog>(fixture, new()
        {
            [nameof(ModalDialog.LabelledBy)] = "dialog-title",
            [nameof(ModalDialog.Busy)] = busy,
            [nameof(ModalDialog.Close)] = EventCallback.Factory.Create(this, () => closed = true)
        });
        var html = await Html(fixture, root);
        Assert.Contains("<dialog", html);
        Assert.Contains("aria-labelledby=\"dialog-title\"", html);
        await Invoke(fixture, dialog, "CloseAsync");
        Assert.Equal(!busy, closed);
    }

    [Fact]
    public async Task ViewingManagedTagFromTrash_OpensFilterDrawerAndResetsTheBrowser()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var matching = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "matching.png", "folder");
        var marked = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(5, 5)), "marked.png", "folder");
        await vault.Scope.Records.UpdateAsync(matching.Id, image => image with { Tags = "cat" });
        await vault.Scope.Records.UpdateAsync(marked.Id, image => image with { Tags = "cat", IsMarkedForDeletion = true });
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (home, root) = await fixture.RenderWorkspaceAsync();
        var browser = home.Session.Browser;
        await Invoke(fixture, home, "Browser.ApplyFilterAsync", new GalleryFilter { Search = "no match", Sort = "name" });
        await Invoke(fixture, home, "Navigation.SetActiveToolAsync", "trash");
        await Invoke(fixture, home, "Navigation.SwitchWorkflowAsync", "tags");
        Assert.Contains("View media tagged cat", await Html(fixture, root));
        ComponentTestAccess.Set(browser, "Page", 2);
        Field<HashSet<string>>(browser, "librarySelection").Add(matching.Id);
        browser.SelectCard(marked.Id, range: false);
        var revision = browser.FilterInputRevision;

        await Invoke(fixture, fixture.LatestComponent<TagManagement>(), "ViewAsync", "cat");

        Assert.Equal("gallery", home.Session.Navigation.Section);
        Assert.Equal(new GalleryFilter { Tag = "cat" }, browser.Filter);
        Assert.Null(browser.BrowsingFilter);
        Assert.Equal("filters", browser.ActiveTool);
        Assert.Contains("aria-label=\"Close filters\"", await Html(fixture, root));
        Assert.Equal(0, browser.Page);
        Assert.True(browser.FilterInputRevision > revision);
        Assert.Empty(browser.LibrarySelection);
        Assert.Empty(browser.RemovalSelection);
        Assert.Null(browser.SelectionAnchor);
        Assert.Equal(matching.Id, Assert.Single(browser.Filtered).Id);
    }

    [Theory]
    [InlineData("", "cat", 1)]
    [InlineData("trash", "Unused tag", 0)]
    public async Task ViewingManagedTag_ShowsItsMatches(string initialTool, string tag, int matches)
    {
        using var vault = new TestVault();
        using var home = await BrowsingWorkspaceFixture.OpenSessionAsync(vault, [new ImageRecord { Id = "cat-photo", Tags = "cat" }]);
        await home.Browser.SetActiveToolAsync(initialTool);
        await home.Navigation.SwitchWorkflowAsync("tags");

        await home.Navigation.ViewTagAsync(tag);

        Assert.Equal("gallery", home.Navigation.Section);
        Assert.Equal(tag, home.Browser.Filter.Tag);
        Assert.Equal(matches, home.Browser.Filtered.Count);
    }

    [Fact]
    public void RangeSelection_IncludesBothEndpointsAndTheIdsBetweenThem()
    {
        var selection = new HashSet<string> { "first" };
        MediaRangeSelection.Select(selection, ["first", "middle", "last", "outside"], "first", "last", range: true);

        Assert.True(selection.SetEquals(["first", "middle", "last"]));
    }

    [Fact]
    public void RangeSelection_ClickingASelectedIdRemovesOnlyThatId()
    {
        var selection = new HashSet<string> { "first", "middle", "last" };
        MediaRangeSelection.Select(selection, ["first", "middle", "last"], "last", "middle", range: false);

        Assert.True(selection.SetEquals(["first", "last"]));
    }

    [Fact]
    public void RangeSelection_WhenTargetHasLeftTheResults_KeepsSelectionAndAnchor()
    {
        var selection = new HashSet<string> { "last" };

        var anchor = MediaRangeSelection.Select(selection, ["first", "middle", "last"], "last", "stale", range: true);

        Assert.Equal("last", anchor);
        Assert.Equal("last", Assert.Single(selection));
    }

    [Fact]
    public async Task Collections_SortingClearsSelectionAndRangeAnchor()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (workspace, _) = await Render<CollectionsWorkspace>(fixture, new());
        var selection = (HashSet<string>)typeof(CollectionsWorkspace).GetField("selection", Private)!.GetValue(workspace)!;
        selection.Add("hidden");
        typeof(CollectionsWorkspace).GetField("anchor", Private)!.SetValue(workspace, "hidden");
        await Invoke(fixture, workspace, "SortChanged", "name");
        Assert.Empty(selection);
        Assert.Null(typeof(CollectionsWorkspace).GetField("anchor", Private)!.GetValue(workspace));
    }

    [Fact]
    public async Task CollectionThumbnailOpensDetailsAndFullscreenKeepsItsOriginatingPage()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var first = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var second = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(4, 4)), "second.png", "folder");
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (home, _) = await fixture.RenderWorkspaceAsync();
        await Invoke(fixture, home, "Navigation.SwitchWorkflowAsync", "unorganized");
        await Invoke(fixture, home, "OpenCollectionDetailsAsync", (new[] { second, first }, first.Id));
        Assert.Equal(first.Id, Field<ImageRecord>(home, "Presentation.Selected").Id);
        Assert.Empty(Field<List<ImageRecord>>(home, "Presentation.ViewerImages"));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.LatestComponent<MediaDetailDialog>().Favorite.InvokeAsync());
        await Invoke(fixture, fixture.LatestComponent<MediaDetailDialog>(), "RequestDetailViewerAsync");
        Assert.True(Field<List<ImageRecord>>(home, "Presentation.ViewerImages").Single(image => image.Id == first.Id).IsFavorite);
        Assert.Null(Field<ImageRecord?>(home, "Presentation.Selected"));
        Assert.Equal(new[] { second.Id, first.Id }, Field<List<ImageRecord>>(home, "Presentation.ViewerImages").Select(image => image.Id));
        Assert.Equal(first.Id, Field<string>(home, "Presentation.ViewerInitialId"));
    }

    [Fact]
    public async Task CollectionShortcutsCurateSelectRangesAndClearSelectionThroughHome()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var first = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var second = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(4, 4)), "second.png", "folder");
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (home, _) = await fixture.RenderWorkspaceAsync();
        await Invoke(fixture, home, "Catalog.ReloadAsync");
        await Invoke(fixture, home, "Navigation.SwitchWorkflowAsync", "unorganized");
        var workspace = Field<CollectionsWorkspace>(home, "collectionsWorkspace");
        foreach (var key in new[] { "3", "3", "0", "f", "d" })
            await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.LatestComponent<GalleryGridShortcuts>().HandleGridKey(key, first.Id, false));
        var saved = (await vault.Scope.Catalog.ListAsync()).Single(image => image.Id == first.Id);
        Assert.Equal(0, saved.Rating);
        Assert.True(saved.IsFavorite);
        Assert.True(saved.IsMarkedForDeletion);
        Assert.True(saved.IsReviewed);
        await Invoke(fixture, workspace, "IncludeTrashChanged", new ChangeEventArgs { Value = true });
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.LatestComponent<GalleryGridShortcuts>().HandleGridKey("d", first.Id, false));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.LatestComponent<GalleryGridShortcuts>().HandleGridKey("x", first.Id, false));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.LatestComponent<GalleryGridShortcuts>().HandleGridKey("x", second.Id, true));
        Assert.Equal(2, Field<HashSet<string>>(workspace, "selection").Count);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.LatestComponent<GalleryGridShortcuts>().HandleGridKey("escape", "", false));
        Assert.Empty(Field<HashSet<string>>(workspace, "selection"));
        Assert.Null(Field<string?>(workspace, "anchor"));
    }

    [Theory]
    [InlineData("original-name")]
    [InlineData("metadata-value")]
    [InlineData("vision-notes")]
    [InlineData("user-notes")]
    [InlineData("shared-tag")]
    public async Task CollectionSearchUsesGalleryMatchingWithinItsScope(string query)
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var collection = await vault.Scope.Collections.CreateCollectionAsync("Series");
        var set = await vault.Scope.Collections.CreateSetAsync(collection.Id, "Set");
        var image = new ImageRecord { Id = "in-scope", OriginalName = "original-name.png", Metadata = "metadata-value",
            Description = "vision-notes", UserDescription = "user-notes", Tags = "shared-tag", SetId = set.Id };
        var outside = image with { Id = "out-of-scope", SetId = null };
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (workspace, root) = await Render<CollectionsWorkspace>(fixture, new()
        {
            [nameof(CollectionsWorkspace.Catalog)] = await vault.Scope.Collections.LoadCollectionCatalogAsync(),
            [nameof(CollectionsWorkspace.Images)] = new[] { image, outside },
            [nameof(CollectionsWorkspace.Location)] = new CollectionLocation(collection.Id, set.Id)
        });
        await Invoke(fixture, workspace, "SearchChanged", query.ToUpperInvariant());
        var media = (IReadOnlyList<ImageRecord>)typeof(CollectionsWorkspace).GetProperty("Media", Private)!.GetValue(workspace)!;
        Assert.Equal("in-scope", Assert.Single(media).Id);
        Assert.Contains("id=\"collection-search\"", await Html(fixture, root));
        var previousRevision = Field<long>(workspace, "searchResetVersion");
        await Invoke(fixture, workspace, "ClearFiltersAsync");
        Assert.True(Field<long>(workspace, "searchResetVersion") > previousRevision);
    }

    [Fact]
    public async Task DragAndButtonReorderingBothOfferUndoAndToastFeedback()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var collection = await vault.Scope.Collections.CreateCollectionAsync("Series");
        var first = await vault.Scope.Collections.CreateSetAsync(collection.Id, "First");
        var second = await vault.Scope.Collections.CreateSetAsync(collection.Id, "Second");
        var third = await vault.Scope.Collections.CreateSetAsync(collection.Id, "Third");
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (home, root) = await fixture.RenderWorkspaceAsync();
        await Invoke(fixture, home, "Catalog.ReloadAsync");
        await Invoke(fixture, home, "Navigation.NavigateCollectionsAsync", new CollectionLocation(collection.Id));
        var workspace = Field<CollectionsWorkspace>(home, "collectionsWorkspace");
        typeof(CollectionsWorkspace).GetField("draggedSet", Private)!.SetValue(workspace, first.Id);
        await Invoke(fixture, workspace, "DropSetAsync", third.Id);
        Assert.Equal(new[] { second.Id, third.Id, first.Id }, (await vault.Scope.Collections.LoadCollectionCatalogAsync()).Sets.OrderBy(item => item.Order).Select(item => item.Id));
        var html = await Html(fixture, root);
        Assert.Contains("Undo organization change", html);
        Assert.DoesNotContain("collection-notice", html);
        await Invoke(fixture, home, "Session.UndoOrganizationAsync");
        Assert.Equal(new[] { first.Id, second.Id, third.Id }, (await vault.Scope.Collections.LoadCollectionCatalogAsync()).Sets.OrderBy(item => item.Order).Select(item => item.Id));
        await Invoke(fixture, workspace, "ReorderSet", first.Id, 1);
        await Invoke(fixture, home, "Session.UndoOrganizationAsync");
        Assert.Equal(new[] { first.Id, second.Id, third.Id }, (await vault.Scope.Collections.LoadCollectionCatalogAsync()).Sets.OrderBy(item => item.Order).Select(item => item.Id));
    }

    [Fact]
    public async Task TagCsvSettings_ImportRefreshesCatalogAndExportsAllDefinitions()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await vault.Scope.Tags.AddTagAsync("Unused");
        var js = new DownloadRuntime();
        await using var fixture = ComponentRenderFixture.Create(vault.Scope, js);
        var changes = 0;
        var (manager, root) = await Render<TagCsvSettings>(fixture, new()
        {
            [nameof(TagCsvSettings.Changed)] = EventCallback.Factory.Create(this, () => changes++)
        });
        await Invoke(fixture, manager, "CsvSelected", new InputFileChangeEventArgs([new CsvBrowserFile("tag\nAdded\nADDED\n")]));
        Assert.Equal(new[] { "Unused" }, await vault.Scope.Tags.LoadTagNamesAsync());

        await Invoke(fixture, manager, "ImportCsvAsync");

        Assert.Equal(1, changes);
        Assert.Contains("Import CSV", await Html(fixture, root));
        Assert.Equal(new[] { "Added", "Unused" }, await vault.Scope.Tags.LoadTagNamesAsync());
        Assert.Null(typeof(TagCsvSettings).GetField("csvFile", Private)!.GetValue(manager));
        Assert.Equal("Imported 1 tag(s); skipped 1 duplicate(s).", Field<string>(manager, "message"));
        await Invoke(fixture, manager, "ExportCsvAsync");
        Assert.Equal("tags.csv", js.FileName);
        Assert.Equal("\uFEFFtag\r\n\"Added\"\r\n\"Unused\"\r\n", Encoding.UTF8.GetString(js.Content));

        await Invoke(fixture, manager, "CsvSelected", new InputFileChangeEventArgs([new CsvBrowserFile("tag\nGood\nbad,columns\n")]));
        await Invoke(fixture, manager, "ImportCsvAsync");
        Assert.Equal(1, changes);
        Assert.Equal(new[] { "Added", "Unused" }, await vault.Scope.Tags.LoadTagNamesAsync());
        Assert.Contains("exactly one", Field<string>(manager, "error"));
    }

    [Fact]
    public async Task TagManagementAndCsvSettings_AreSeparateDestinations()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await vault.Scope.Tags.AddTagAsync("Unused");
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (home, root) = await fixture.RenderWorkspaceAsync();
        await Invoke(fixture, home, "Navigation.SwitchWorkflowAsync", "tags");
        var management = await Html(fixture, root);
        Assert.Equal("tags", Field<string>(home, "Navigation.Section"));
        Assert.Contains("Manage tags", management);
        Assert.Contains("View media tagged Unused", management);
        Assert.DoesNotContain("Import CSV", management);
        Assert.DoesNotContain("Export CSV", management);

        await Invoke(fixture, home, "Navigation.SwitchWorkflowAsync", "settings");
        await Invoke(fixture, home, "Navigation.SwitchSettingsTabAsync", "tags");
        var settings = await Html(fixture, root);
        Assert.Contains("Import CSV", settings);
        Assert.Contains("Export CSV", settings);
        Assert.Contains("id=\"tag-csv-file\"", settings);
        Assert.DoesNotContain("Manage tags", settings);
        Assert.DoesNotContain("aria-label=\"Tag catalog\"", settings);
    }

    private sealed class CsvBrowserFile(string content) : IBrowserFile
    {
        public string Name => "tags.csv";
        public DateTimeOffset LastModified => DateTimeOffset.UtcNow;
        public long Size => Encoding.UTF8.GetByteCount(content);
        public string ContentType => "text/csv";
        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            if (Size > maxAllowedSize) throw new IOException("File exceeds the upload limit.");
            return new MemoryStream(Encoding.UTF8.GetBytes(content));
        }
    }

    private sealed class DownloadRuntime : IJSRuntime
    {
        public string? FileName { get; private set; }
        public string? ContentType { get; private set; }
        public byte[] Content { get; private set; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "galleryMedia.download")
            {
                FileName = (string)args![1]!;
                ContentType = (string)args[2]!;
                var reference = (DotNetStreamReference)args[0]!;
                using var bytes = new MemoryStream();
                await reference.Stream.CopyToAsync(bytes, cancellationToken);
                Content = bytes.ToArray();
            }
            return default!;
        }
    }

    private static T Field<T>(object target, string name) => ComponentTestAccess.Get<T>(target, name);

    private static Task Invoke(ComponentRenderFixture fixture, object target, string method, params object[] args) =>
        fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = ComponentTestAccess.Call(target, method, args);
            if (result is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", Private)!.Invoke(target, null);
        });

    private static async Task WaitForWorkspaceAsync(ComponentRenderFixture fixture, Home home)
    {
        // Unlock mounts a new child whose catalog load can finish after the access form returns.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() =>
            Field<WorkspaceSession?>(home, "session") is { IsInitialized: true, Activity.Busy: false }))
            await Task.Delay(10, timeout.Token);
    }

    private static Task<string> Html(ComponentRenderFixture fixture, HtmlRootComponent root) =>
        fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => WebUtility.HtmlDecode(root.ToHtmlString()));

    private static async Task<(T Component, HtmlRootComponent Root)> Render<T>(ComponentRenderFixture fixture, Dictionary<string, object?> parameters) where T : ComponentBase
    {
        T? component = null;
        var root = await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.HtmlRenderer.RenderComponentAsync<Host<T>>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Host<T>.Parameters)] = parameters,
                [nameof(Host<T>.Capture)] = (Action<T>)(value => component = value)
            })));
        return (component!, root);
    }

    private sealed class Host<T> : ComponentBase where T : ComponentBase
    {
        [Parameter] public Dictionary<string, object?> Parameters { get; set; } = [];
        [Parameter] public Action<T>? Capture { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<T>(0);
            builder.AddMultipleAttributes(1, Parameters.Select(pair => new KeyValuePair<string, object>(pair.Key, pair.Value!)));
            builder.AddComponentReferenceCapture(2, value => Capture?.Invoke((T)value));
            builder.CloseComponent();
        }
    }
}
