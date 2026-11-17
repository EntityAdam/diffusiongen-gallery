using Gallery.Web.Components.Workspace;
using System.Net;
using System.Reflection;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Components;
using Gallery.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Gallery.Tests;

[Trait("Category", "class_test")]
public sealed class UiSaveBehaviorTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static void Set(object target, string field, object value) => ComponentTestAccess.Set(target, field, value);
    private static T Get<T>(object target, string field) => ComponentTestAccess.Get<T>(target, field);
    private static object? Call(object target, string method, params object[] args) => ComponentTestAccess.Call(target, method, args);
    private static Task Invoke(object target, string method, params object[] args) => (Task)Call(target, method, args)!;

    [Fact]
    public async Task DestinationCancelDiscardsNewCollectionAndSet_AndMoveCommitsBoth()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        CollectionDestinationDialog dialog = null!;
        var canceled = false;
        CollectionMutation? saved = null;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.HtmlRenderer.RenderComponentAsync<CaptureHost<CollectionDestinationDialog>>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Capture"] = (Action<CollectionDestinationDialog>)(value => dialog = value),
                ["Parameters"] = new Dictionary<string, object?>
                {
                    ["Images"] = new[] { image },
                    ["Cancel"] = EventCallback.Factory.Create(this, () => canceled = true),
                    ["Saved"] = EventCallback.Factory.Create<CollectionMutation>(this, value => saved = value)
                }
            })));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await Invoke(dialog, "SelectCollectionAsync", "New collection");
            await Invoke(dialog, "SelectSetAsync", "New set");
            await Invoke(dialog, "CancelAsync");
        });
        Assert.True(canceled);
        var catalog = await vault.Service.LoadCollectionCatalogAsync();
        Assert.Empty(catalog.Collections);
        Assert.Empty(catalog.Sets);
        Assert.Null(Assert.Single(await vault.Service.ListAsync()).SetId);

        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => Invoke(dialog, "MoveAsync"));
        Assert.NotNull(saved);
        Assert.Equal("New collection", Assert.Single(saved.Catalog.Collections).Name);
        Assert.Equal("New set", Assert.Single(saved.Catalog.Sets).Name);
        Assert.Equal(saved.Catalog.Sets[0].Id, Assert.Single(await vault.Service.ListAsync()).SetId);
        await vault.Service.UndoCollectionChangeAsync(saved.Undo);
        Assert.Empty((await vault.Service.LoadCollectionCatalogAsync()).Collections);
        Assert.Null(Assert.Single(await vault.Service.ListAsync()).SetId);
    }

    [Theory]
    [InlineData("Guard.KeepEditing", false, false)]
    [InlineData("Guard.DiscardAsync", true, false)]
    [InlineData("Guard.SaveAsync", true, true)]
    public async Task LeavingDetailsOffersKeepDiscardOrSave(string choice, bool closes, bool saves)
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");
        image = await vault.Service.UpdateAsync(image.Id, edit => edit with { UserDescription = "original" });
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        GalleryWorkspace home = null!;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.HtmlRenderer.RenderComponentAsync<CaptureHost<GalleryWorkspace>>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Capture"] = (Action<GalleryWorkspace>)(value => home = value) })));
        MediaDetailDialog detail = null!;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await Invoke(home, "ReloadAsync");
            await Invoke(home, "OpenAsync", image);
            Call(home, "StateHasChanged");
            detail = Get<MediaDetailDialog>(home, "mediaHost.detailDialog");
            Set(detail, "editUserDescription", "edited");
            await Invoke(detail, "RequestCloseDetailAsync");
            Assert.True(Get<bool>(detail, "Guard.IsPending"));
            Assert.NotNull(Get<ImageRecord?>(home, "Presentation.Selected"));
            if (Call(detail, choice) is Task task) await task;
        });
        Assert.Equal(closes, Get<ImageRecord?>(home, "Presentation.Selected") is null);
        Assert.Equal(saves ? "edited" : "original", Assert.Single(await vault.Service.ListAsync()).UserDescription);
        if (!closes) Assert.Equal("edited", Get<string>(detail, "editUserDescription"));
        Assert.False(Get<bool>(detail, "Guard.IsPending"));
    }

    [Fact]
    public async Task FailedNoteSaveKeepsDraftAndPendingNavigation()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        GalleryWorkspace home = null!;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.HtmlRenderer.RenderComponentAsync<CaptureHost<GalleryWorkspace>>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Capture"] = (Action<GalleryWorkspace>)(value => home = value) })));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            Set(home, "Presentation.Selected", new ImageRecord { Id = "missing" });
            Call(home, "StateHasChanged");
            var detail = Get<MediaDetailDialog>(home, "mediaHost.detailDialog");
            Set(detail, "editUserDescription", "draft");
            var left = false;
            await detail.RequestDetailActionAsync(() => { left = true; return Task.CompletedTask; });
            await Invoke(detail, "Guard.SaveAsync");
            Assert.False(left);
            Assert.Equal("draft", Get<string>(detail, "editUserDescription"));
            Assert.True(Get<bool>(detail, "Guard.IsPending"));
            Assert.NotEmpty(home.Activity.Error);
        });
    }

    [Fact]
    public async Task DetailAnalysisStagesUntilApproval_AndApprovalMergesTagsWithoutSavingDraftDescription()
    {
        using var client = new HttpClient(new AnalysisHandler());
        using var vault = new TestVault(vision: new VisionManager(client));
        await vault.InitializeAsync();
        await vault.Service.SaveSettingsAsync(new VisionSettings { Model = "vision" });
        var image = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");
        image = await vault.Service.UpdateAsync(image.Id, edit => edit with
        { Description = "old analysis", UserDescription = "my notes", Tags = "existing" });
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        GalleryWorkspace home = null!;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.HtmlRenderer.RenderComponentAsync<CaptureHost<GalleryWorkspace>>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Capture"] = (Action<GalleryWorkspace>)(value => home = value) })));
        MediaDetailDialog detail = null!;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await Invoke(home, "ReloadAsync");
            await Invoke(home, "OpenAsync", image);
            Call(home, "StateHasChanged");
            detail = Get<MediaDetailDialog>(home, "mediaHost.detailDialog");
            Set(detail, "editUserDescription", "unsaved draft");
            await Invoke(home, "AnalyzeAsync", image);
        });
        Assert.Empty(home.Activity.Error);
        Assert.NotNull(Get<LibraryWorkbench?>(home, "galleryBrowser.workbench"));
        var unchanged = Assert.Single(await vault.Service.ListAsync());
        Assert.Equal("old analysis", unchanged.Description);
        Assert.Equal("existing", unchanged.Tags);
        Assert.Equal("my notes", unchanged.UserDescription);
        Assert.Single(Get<IReadOnlyDictionary<string, OrganizationSuggestion>>(home, "Browser.Suggestions"));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => Invoke(detail, "ApproveDetailSuggestionAsync"));
        Assert.Equal("old analysis", Assert.Single(await vault.Service.ListAsync()).Description);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            Call(detail, "DiscardNotes");
            await Invoke(detail, "ApproveDetailSuggestionAsync");
        });
        var approved = Assert.Single(await vault.Service.ListAsync());
        Assert.Equal("new analysis", approved.Description);
        Assert.Equal("existing, suggested", approved.Tags);
        Assert.Equal("my notes", approved.UserDescription);
        Assert.Equal("vision", approved.AnalysisModel);
        Assert.Empty(Get<IReadOnlyDictionary<string, OrganizationSuggestion>>(home, "Browser.Suggestions"));
        Assert.Equal("new analysis", Get<string>(detail, "editDescription"));
    }

    [Fact]
    public async Task LlmSettingsRemainDraftUntilSaveAndReportTheirState()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var original = new VisionSettings { Model = "original" };
        await vault.Service.SaveSettingsAsync(original);
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        LlmEndpointSettings component = null!;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.HtmlRenderer.RenderComponentAsync<CaptureHost<LlmEndpointSettings>>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Capture"] = (Action<LlmEndpointSettings>)(value => component = value),
                ["Parameters"] = new Dictionary<string, object?> { ["SavedSettings"] = original }
            })));
        var dirty = typeof(LlmEndpointSettings).GetProperty("HasUnsavedChanges", Flags)!;
        Assert.False((bool)dirty.GetValue(component)!);
        var draft = Get<object>(component, "draft");
        draft.GetType().GetProperty("Model")!.SetValue(draft, "changed");
        Assert.True((bool)dirty.GetValue(component)!);
        Assert.Equal("original", (await vault.Service.SettingsAsync()).Model);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => Invoke(component, "SaveAsync"));
        Assert.Equal("changed", (await vault.Service.SettingsAsync()).Model);
        Assert.False((bool)dirty.GetValue(component)!);
    }

    [Fact]
    public async Task IngestionAndBackupChangesAreDraftsUntilSaved()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        GalleryWorkspace home = null!;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.HtmlRenderer.RenderComponentAsync<CaptureHost<GalleryWorkspace>>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Capture"] = (Action<GalleryWorkspace>)(value => home = value) })));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await Invoke(home, "ReloadAsync");
            home.Activity.Ingestion.Workers = 2;
            Assert.True(home.Activity.Ingestion.SettingsDirty);
            Assert.NotEqual(2, (await vault.Service.LoadIngestionOptionsAsync()).Workers);
            await home.Activity.Ingestion.SaveOptionsAsync(vault.Service);
            Assert.Equal(2, (await vault.Service.LoadIngestionOptionsAsync()).Workers);
            Assert.False(home.Activity.Ingestion.SettingsDirty);
            await Invoke(home, "SwitchSettingsTabAsync", "backups");
            Get<BackupDraft>(home, "settingsWorkspace.backup").LocalDirectory = vault.Root + "-backups";
            Assert.True(Get<BackupDraft>(home, "settingsWorkspace.backup").HasChanges);
            Assert.False(Get<BackupDraft>(home, "settingsWorkspace.backup").Ready);
            await Get<BackupDraft>(home, "settingsWorkspace.backup").SaveAsync(vault.Service);
            Assert.False(Get<BackupDraft>(home, "settingsWorkspace.backup").HasChanges);
            Assert.True(Get<BackupDraft>(home, "settingsWorkspace.backup").Ready);
        });
    }

    [Theory]
    [InlineData("Guard.KeepEditing", false, false)]
    [InlineData("Guard.DiscardAsync", true, false)]
    [InlineData("Guard.SaveAsync", true, true)]
    public async Task LeavingLlmSettingsOffersKeepDiscardOrSave(string choice, bool leaves, bool saves)
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await vault.Service.SaveSettingsAsync(new VisionSettings { Model = "original" });
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        GalleryWorkspace home = null!;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.HtmlRenderer.RenderComponentAsync<CaptureHost<GalleryWorkspace>>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Capture"] = (Action<GalleryWorkspace>)(value => home = value) })));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await Invoke(home, "SwitchWorkflowAsync", "settings");
            await Invoke(home, "SwitchSettingsTabAsync", "endpoints");
            Call(home, "StateHasChanged");
        });
        var component = Get<LlmEndpointSettings>(home, "settingsWorkspace.llmEndpointSettings");
        var draft = Get<object>(component, "draft");
        draft.GetType().GetProperty("Model")!.SetValue(draft, "edited");
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await Invoke(home, "SwitchSettingsTabAsync", "overview");
            Assert.True(Get<bool>(home, "Guard.IsPending"));
            Assert.Equal("endpoints", Get<string>(home, "settingsTab"));
            if (Call(home, choice) is Task task) await task;
        });
        Assert.Equal(leaves ? "overview" : "endpoints", Get<string>(home, "settingsTab"));
        Assert.Equal(saves ? "edited" : "original", (await vault.Service.SettingsAsync()).Model);
        Assert.False(Get<bool>(home, "Guard.IsPending"));
        Assert.Equal(!leaves, component.HasUnsavedChanges);
    }

    [Fact]
    public async Task InvalidLlmSaveKeepsDraftAndPendingNavigation()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await vault.Service.SaveSettingsAsync(new VisionSettings { Model = "original" });
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        GalleryWorkspace home = null!;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.HtmlRenderer.RenderComponentAsync<CaptureHost<GalleryWorkspace>>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Capture"] = (Action<GalleryWorkspace>)(value => home = value) })));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await Invoke(home, "SwitchWorkflowAsync", "settings");
            await Invoke(home, "SwitchSettingsTabAsync", "endpoints");
            Call(home, "StateHasChanged");
        });
        var component = Get<LlmEndpointSettings>(home, "settingsWorkspace.llmEndpointSettings");
        var draft = Get<object>(component, "draft");
        draft.GetType().GetProperty("Endpoint")!.SetValue(draft, "invalid-url");
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await Invoke(home, "SwitchWorkflowAsync", "gallery");
            await Invoke(home, "Guard.SaveAsync");
        });
        Assert.Equal("settings", Get<string>(home, "workflow"));
        Assert.Equal("endpoints", Get<string>(home, "settingsTab"));
        Assert.True(Get<bool>(home, "Guard.IsPending"));
        Assert.True(component.HasUnsavedChanges);
        Assert.Equal("invalid-url", draft.GetType().GetProperty("Endpoint")!.GetValue(draft));
        Assert.Equal("original", (await vault.Service.SettingsAsync()).Model);
    }

    [Theory]
    [InlineData("ingest", false)]
    [InlineData("ingest", true)]
    [InlineData("backups", false)]
    [InlineData("backups", true)]
    public async Task LeavingIngestionOrBackupsProtectsDrafts(string section, bool save)
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        GalleryWorkspace home = null!;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.HtmlRenderer.RenderComponentAsync<CaptureHost<GalleryWorkspace>>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Capture"] = (Action<GalleryWorkspace>)(value => home = value) })));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await Invoke(home, "ReloadAsync");
            if (section == "ingest")
            {
                await Invoke(home, "SwitchWorkflowAsync", "ingest");
                home.Activity.Ingestion.Workers = 2;
            }
            else
            {
                await Invoke(home, "SwitchWorkflowAsync", "settings");
                await Invoke(home, "SwitchSettingsTabAsync", "backups");
                Get<BackupDraft>(home, "settingsWorkspace.backup").LocalDirectory = vault.Root + "-backups";
            }
            await Invoke(home, "SwitchWorkflowAsync", "gallery");
            Assert.True(Get<bool>(home, "Guard.IsPending"));
            Assert.NotEqual("gallery", Get<string>(home, "workflow"));
            await Invoke(home, save ? "Guard.SaveAsync" : "Guard.DiscardAsync");
        });
        Assert.Equal("gallery", Get<string>(home, "workflow"));
        Assert.False(Get<bool>(home, "Guard.IsPending"));
        if (section == "ingest") Assert.Equal(save, (await vault.Service.LoadIngestionOptionsAsync()).Workers == 2);
        else Assert.Equal(save ? vault.Root + "-backups" : "", (await vault.Service.LoadBackupOptionsAsync()).LocalDirectory);
    }

    private sealed class AnalysisHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"{\"description\":\"new analysis\",\"tags\":[\"suggested\"]}"}}]}""")
            });
    }

    private sealed class CaptureHost<T> : ComponentBase where T : IComponent
    {
        [Parameter] public Action<T>? Capture { get; set; }
        [Parameter] public Dictionary<string, object?> Parameters { get; set; } = [];
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<T>(0);
            builder.AddMultipleAttributes(1, Parameters!);
            builder.AddComponentReferenceCapture(2, instance => Capture?.Invoke((T)instance));
            builder.CloseComponent();
        }
    }
}
