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
            if (session.Browser.ToolboxVisible) await session.Browser.ToggleToolboxAsync();

            await session.Navigation.OpenTrashAsync();
            Assert.True(session.Guard.IsPending);
            session.Guard.KeepEditing();

            Assert.Equal("duplicates", session.Navigation.Section);
            Assert.False(session.Browser.ToolboxVisible);
            Assert.False(session.Browser.TrashActive);
            Assert.Equal(image.Id, session.Presentation.Selected?.Id);

            await session.Navigation.OpenTrashAsync();
            await session.Guard.DiscardAsync();

            Assert.Equal("gallery", session.Navigation.Section);
            Assert.True(session.Browser.ToolboxVisible);
            Assert.True(session.Browser.TrashActive);
            Assert.Null(session.Presentation.Selected);
        });
    }

    [Fact]
    public async Task Sidebar_UsesTheRequestedOrderAndOpensGalleryTools()
    {
        using var vault = new TestVault();
        await vault.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (home, root) = await fixture.RenderWorkspaceAsync();
        var html = await Html(fixture, root);
        var navigation = Regex.Match(html, @"<nav class=""workspace-navigation""[^>]*>(.*?)</nav>", RegexOptions.Singleline).Groups[1].Value;
        var labels = Regex.Matches(navigation, @"aria-label=""([^""]+)""").Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(new[] { "Gallery", "Change view", "Filters", "Organize", "Review", "AI analysis", "Collections", "Tags", "Duplicates", "Import media" }, labels);
        Assert.Equal(2, Regex.Matches(navigation, "role=\"separator\"").Count);
        Assert.DoesNotContain("Trash", navigation);
        Assert.DoesNotContain("Unorganized", navigation);
        ComponentTestAccess.Set(home, "Browser.ToolboxVisible", false);
        await Invoke(fixture, home, "Navigation.NavigateSidebarAsync", "views");
        Assert.Equal("views", Field<string>(home, "Browser.ActiveTool"));
        var viewHtml = await Html(fixture, root);
        Assert.Contains("gallery-view-drawer", viewHtml);
        Assert.Contains("Thumbnail size", viewHtml);
        Assert.DoesNotContain("data-tool=\"views\"", viewHtml);
        Assert.False(Field<bool>(home, "Browser.ToolboxVisible"));
        await Invoke(fixture, home, "Navigation.NavigateSidebarAsync", "views");
        Assert.Equal("", Field<string>(home, "Browser.ActiveTool"));
        await Invoke(fixture, home, "Navigation.NavigateSidebarAsync", "filters");
        Assert.Equal("filters", Field<string>(home, "Browser.ActiveTool"));
        Assert.Contains("class=\"gallery-filter-drawer\"", await Html(fixture, root));
        await Invoke(fixture, home, "Navigation.NavigateSidebarAsync", "tags");
        await Invoke(fixture, home, "Navigation.NavigateSidebarAsync", "gallery");
        Assert.Equal("gallery", Field<string>(home, "Navigation.Section"));
        Assert.Equal("", Field<string>(home, "Browser.ActiveTool"));
        await Invoke(fixture, home, "Navigation.NavigateSidebarAsync", "tags");
        await Invoke(fixture, home, "Navigation.NavigateSidebarAsync", "review");
        Assert.Equal("gallery", Field<string>(home, "Navigation.Section"));
        Assert.Equal("review", Field<string>(home, "Browser.ActiveTool"));
        var reviewHtml = await Html(fixture, root);
        Assert.Contains("gallery-review-drawer", reviewHtml);
        Assert.Contains("review-random-order", reviewHtml);
        Assert.Contains("review-goal", reviewHtml);
        Assert.Contains("Start Review", reviewHtml);
        Assert.DoesNotContain("data-tool=\"review\"", reviewHtml);
        Assert.False(Field<bool>(home, "Browser.ToolboxVisible"));
        await Invoke(fixture, home, "Navigation.NavigateSidebarAsync", "review");
        Assert.Equal("", Field<string>(home, "Browser.ActiveTool"));
        await Invoke(fixture, home, "Navigation.NavigateSidebarAsync", "intelligence");
        Assert.Equal("gallery", Field<string>(home, "Navigation.Section"));
        Assert.Equal("intelligence", Field<string>(home, "Browser.ActiveTool"));
        var analysisHtml = await Html(fixture, root);
        Assert.Contains("gallery-analysis-drawer", analysisHtml);
        Assert.Contains("Start analyzing unanalyzed media", analysisHtml);
        Assert.DoesNotContain("data-tool=\"intelligence\"", analysisHtml);
        Assert.False(Field<bool>(home, "Browser.ToolboxVisible"));
        var workbench = fixture.LatestComponent<LibraryWorkbench>();
        await Invoke(fixture, home, "Navigation.NavigateSidebarAsync", "intelligence");
        Assert.Equal("", Field<string>(home, "Browser.ActiveTool"));
        Assert.Same(workbench, fixture.LatestComponent<LibraryWorkbench>());
        await Invoke(fixture, home, "Navigation.NavigateSidebarAsync", "tags");
        await Invoke(fixture, home, "Navigation.NavigateSidebarAsync", "organize");
        Assert.Equal("gallery", Field<string>(home, "Navigation.Section"));
        Assert.Equal("organize", Field<string>(home, "Browser.ActiveTool"));
        var organizeHtml = await Html(fixture, root);
        Assert.Contains("gallery-organize-drawer", organizeHtml);
        Assert.Contains("Move to set", organizeHtml);
        Assert.Contains("bulk-tags", organizeHtml);
        Assert.DoesNotContain("data-tool=\"organize\"", organizeHtml);
        Assert.False(Field<bool>(home, "Browser.ToolboxVisible"));
        Assert.Same(workbench, fixture.LatestComponent<LibraryWorkbench>());
        await Invoke(fixture, home, "Navigation.NavigateSidebarAsync", "organize");
        Assert.Equal("", Field<string>(home, "Browser.ActiveTool"));
    }

    [Fact]
    public async Task FilterChipRemoval_UpdatesTheCriterionAndInvalidatesPendingTextEdits()
    {
        await using var fixture = ComponentRenderFixture.Create();
        GalleryFilter? changed = null;
        var (panel, root) = await Render<GalleryFilterPanel>(fixture, new()
        {
            [nameof(GalleryFilterPanel.Filter)] = new GalleryFilter { Search = "portrait", Tag = "cat, dog", MinRating = 4 },
            [nameof(GalleryFilterPanel.Changed)] = EventCallback.Factory.Create<GalleryFilter>(this, value => changed = value)
        });
        var html = await Html(fixture, root);
        Assert.Contains("aria-label=\"Active filters\"", html);
        Assert.Contains("aria-label=\"Remove Search: portrait\"", html);
        Assert.DoesNotContain("filter-panel-footer", html);
        Assert.DoesNotContain("Reset filters", html);
        await Invoke(fixture, panel, "RemoveChipAsync", "search");
        Assert.Equal("", changed!.Search);
        Assert.Equal("cat, dog", changed.Tag);
        Assert.Equal(4, changed.MinRating);
        Assert.True(Field<long>(panel, "chipResetRevision") > 0);
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
    [InlineData("Review")]
    [InlineData("Deletion")]
    [InlineData("Media")]
    [InlineData("Orientation")]
    [InlineData("Analysis")]
    [InlineData("Rating")]
    public async Task FilterDropdown_ClearingRemovesTheCriterion(string property)
    {
        await using var fixture = ComponentRenderFixture.Create();
        GalleryFilter? changed = null;
        var (panel, _) = await Render<GalleryFilterPanel>(fixture, new()
        {
            [nameof(GalleryFilterPanel.Filter)] = new GalleryFilter
            {
                Review = "reviewed", Deletion = "marked", Media = "video", Orientation = "portrait", Analysis = "analyzed", MinRating = 4
            },
            [nameof(GalleryFilterPanel.Changed)] = EventCallback.Factory.Create<GalleryFilter>(this, value => changed = value)
        });
        var draft = Field<object>(panel, "draft");
        draft.GetType().GetProperty(property)!.SetValue(draft, "");
        await Invoke(fixture, panel, "NotifyChangedAsync");

        if (property == "Rating") Assert.Equal(0, changed!.MinRating);
        else Assert.Equal(GalleryFilter.AnyValue, typeof(GalleryFilter).GetProperty(property)!.GetValue(changed));
    }

    [Fact]
    public async Task TagFilter_RestoresMultipleSelectionsAndPublishesChangesAndClearing()
    {
        await using var fixture = ComponentRenderFixture.Create();
        GalleryFilter? changed = null;
        var (panel, root) = await Render<GalleryFilterPanel>(fixture, new()
        {
            [nameof(GalleryFilterPanel.Filter)] = new GalleryFilter { Tag = "cat, dog" },
            [nameof(GalleryFilterPanel.KnownTags)] = new[] { "cat", "dog", "bird" },
            [nameof(GalleryFilterPanel.Changed)] = EventCallback.Factory.Create<GalleryFilter>(this, value => changed = value)
        });
        var html = await Html(fixture, root);
        Assert.Contains("aria-multiselectable=\"true\"", html);
        Assert.Contains("aria-label=\"Remove tag cat\"", html);
        Assert.Contains("aria-label=\"Remove tag dog\"", html);

        var draft = Field<object>(panel, "draft");
        var tags = draft.GetType().GetProperty("Tags")!;
        tags.SetValue(draft, new[] { "dog", "bird" });
        await Invoke(fixture, panel, "NotifyChangedAsync");
        Assert.Equal("dog, bird", changed!.Tag);

        tags.SetValue(draft, Array.Empty<string>());
        await Invoke(fixture, panel, "NotifyChangedAsync");
        Assert.Equal("", changed!.Tag);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuickTag_CancelDiscardsBothAdditionsAndRemovals(bool removeAll)
    {
        await using var fixture = ComponentRenderFixture.Create();
        string? saved = null;
        var cancelled = false;
        var (dialog, root) = await Render<QuickOrganizationDialog>(fixture, new()
        {
            [nameof(QuickOrganizationDialog.Tags)] = "cat, dog",
            [nameof(QuickOrganizationDialog.Save)] = EventCallback.Factory.Create<string>(this, value => saved = value),
            [nameof(QuickOrganizationDialog.Cancel)] = EventCallback.Factory.Create(this, () => cancelled = true)
        });
        await Invoke(fixture, dialog, "TagsChangedAsync", (object)(removeAll ? Array.Empty<string>() : new[] { "dog", "bird" }));
        Assert.Null(saved);
        var html = await Html(fixture, root);
        Assert.Contains("Save changes", html);
        Assert.DoesNotContain("aria-label=\"Remove tag cat\"", html);
        await Invoke(fixture, dialog, "CancelAsync");
        Assert.True(cancelled);
        Assert.Null(saved);
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
        typeof(CollectionsWorkspace).GetField("quickTagIds", Private)!.SetValue(workspace, new[] { first.Id, second.Id });
        await Invoke(fixture, workspace, "SelectAll");
        await Invoke(fixture, workspace, "OpenTags");
        var tagHtml = await Html(fixture, root);
        Assert.Contains("1 of 2 selected", tagHtml);
        Assert.Contains("2 of 2 selected", tagHtml);
        await Invoke(fixture, workspace, "SaveTagsAsync", "dog, shared, bird");
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

    [Theory]
    [InlineData("Browser.SetSortAsync")]
    [InlineData("Browser.ResetFiltersAsync")]
    public async Task Gallery_ViewChangesClearSelectionAndRangeAnchor(string action)
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (home, _) = await fixture.RenderWorkspaceAsync();
        var selected = (HashSet<string>)ComponentTestAccess.Get<object>(home, "Browser.librarySelection")!;
        var trashSelected = (HashSet<string>)ComponentTestAccess.Get<object>(home, "Browser.removalSelection")!;
        selected.Add("hidden"); trashSelected.Add("marked");
        ComponentTestAccess.Set(home, "Browser.SelectionAnchor", "hidden");
        await Invoke(fixture, home, action, action == "Browser.SetSortAsync" ? ["name"] : []);
        Assert.Empty(selected);
        Assert.Empty(trashSelected);
        Assert.Null(ComponentTestAccess.Get<object>(home, "Browser.SelectionAnchor"));
    }

    [Theory]
    [InlineData(false, "cat")]
    [InlineData(true, "cat")]
    [InlineData(true, "Unused tag")]
    public async Task ViewingManagedTag_OpensGalleryWithOnlyThatTagAndClearsTrashAndSelection(bool fromTrash, string tag)
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var matching = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "matching.png", "folder");
        var other = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(4, 4)), "other.png", "folder");
        var marked = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(5, 5)), "marked.png", "folder");
        await vault.Scope.Records.UpdateAsync(matching.Id, image => image with { Tags = "cat" });
        await vault.Scope.Records.UpdateAsync(other.Id, image => image with { Tags = "catfish" });
        await vault.Scope.Records.UpdateAsync(marked.Id, image => image with { Tags = "cat", IsMarkedForDeletion = true });
        await vault.Scope.Tags.AddTagAsync("Unused tag");
        await using var fixture = ComponentRenderFixture.Create(vault.Scope);
        var (home, root) = await fixture.RenderWorkspaceAsync();
        await Invoke(fixture, home, "Catalog.ReloadAsync");
        ComponentTestAccess.Set(home, "Browser.Filter", new GalleryFilter
        {
            Search = "no match", Tag = "previous", MinRating = 5, IncludesFavoritesOnly = true,
            Collection = "old collection", Vision = new() { Subject = "old subject" }, Sort = "name"
        });
        if (fromTrash) await Invoke(fixture, home, "Navigation.SetActiveToolAsync", "trash");
        await Invoke(fixture, home, "Navigation.SwitchWorkflowAsync", "tags");
        Assert.Contains("View media tagged", await Html(fixture, root));
        ComponentTestAccess.Set(home, "Browser.Page", 2);
        Field<HashSet<string>>(home, "Browser.librarySelection").Add(other.Id);
        Field<HashSet<string>>(home, "Browser.removalSelection").Add(marked.Id);
        ComponentTestAccess.Set(home, "Browser.SelectionAnchor", other.Id);
        var revision = Field<long>(home, "Browser.FilterInputRevision");

        await Invoke(fixture, home, "Navigation.ViewTagAsync", tag);

        Assert.Equal("gallery", Field<string>(home, "Navigation.Section"));
        Assert.Equal(new GalleryFilter { Tag = tag }, Field<GalleryFilter>(home, "Browser.Filter"));
        Assert.Equal("filters", Field<string>(home, "Browser.ActiveTool"));
        Assert.Null(ComponentTestAccess.Get<object>(home, "Browser.BrowsingFilter"));
        Assert.True(Field<bool>(home, "Browser.ToolboxVisible"));
        var galleryHtml = await Html(fixture, root);
        Assert.Contains($"<aside id=\"{GalleryToolbox.FilterDrawerId}\" class=\"gallery-filter-drawer\"", galleryHtml);
        Assert.Contains("aria-label=\"Close filters\"", galleryHtml);
        Assert.Equal(0, Field<int>(home, "Browser.Page"));
        Assert.True(Field<long>(home, "Browser.FilterInputRevision") > revision);
        Assert.Empty(Field<IReadOnlySet<string>>(home, "Browser.LibrarySelection"));
        Assert.Empty(Field<IReadOnlySet<string>>(home, "Browser.RemovalSelection"));
        Assert.Null(ComponentTestAccess.Get<object>(home, "Browser.SelectionAnchor"));
        var filtered = (IReadOnlyList<ImageRecord>)ComponentTestAccess.Get<object>(home, "Browser.Filtered")!;
        Assert.Equal(tag == "cat" ? new[] { matching.Id } : Array.Empty<string>(), filtered.Select(image => image.Id));
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
        var js = new CsvDownloadRuntime();
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
        Assert.Equal("\uFEFFtag\r\n\"Added\"\r\n\"Unused\"\r\n", js.Content);

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

    private sealed class CsvDownloadRuntime : IJSRuntime
    {
        public string? FileName { get; private set; }
        public string? Content { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "galleryMedia.download")
            {
                FileName = (string)args![1]!;
                var reference = (DotNetStreamReference)args[0]!;
                using var bytes = new MemoryStream();
                await reference.Stream.CopyToAsync(bytes, cancellationToken);
                Content = Encoding.UTF8.GetString(bytes.ToArray());
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
