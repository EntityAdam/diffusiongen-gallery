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
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Flags)!.SetValue(target, value);
    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, Flags)!.GetValue(target)!;
    private static object? Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Flags)!.Invoke(target, args);
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
    [InlineData("KeepEditingNotes", false, false)]
    [InlineData("DiscardNotesAndContinueAsync", true, false)]
    [InlineData("SaveNotesAndContinueAsync", true, true)]
    public async Task LeavingDetailsOffersKeepDiscardOrSave(string choice, bool closes, bool saves)
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");
        image = await vault.Service.UpdateAsync(image.Id, edit => edit with { UserDescription = "original" });
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        Home home = null!;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.HtmlRenderer.RenderComponentAsync<CaptureHost<Home>>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Capture"] = (Action<Home>)(value => home = value) })));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await Invoke(home, "ReloadAsync");
            await Invoke(home, "OpenAsync", image);
            Set(home, "editUserDescription", "edited");
            await Invoke(home, "RequestCloseDetailAsync");
            Assert.NotNull(Get<Func<Task>?>(home, "pendingDetailAction"));
            Assert.NotNull(Get<ImageRecord?>(home, "selected"));
            if (Call(home, choice) is Task task) await task;
        });
        Assert.Equal(closes, Get<ImageRecord?>(home, "selected") is null);
        Assert.Equal(saves ? "edited" : "original", Assert.Single(await vault.Service.ListAsync()).UserDescription);
        if (!closes) Assert.Equal("edited", Get<string>(home, "editUserDescription"));
        Assert.Null(Get<Func<Task>?>(home, "pendingDetailAction"));
    }

    [Fact]
    public async Task FailedNoteSaveKeepsDraftAndPendingNavigation()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var home = new Home();
        typeof(Home).GetProperty("Gallery", Flags)!.SetValue(home, vault.Service);
        typeof(Home).GetProperty("Logger", Flags)!.SetValue(home, Microsoft.Extensions.Logging.Abstractions.NullLogger<Home>.Instance);
        Set(home, "selected", new ImageRecord { Id = "missing" });
        Set(home, "editUserDescription", "draft");
        var left = false;
        await Invoke(home, "RequestDetailActionAsync", (Func<Task>)(() => { left = true; return Task.CompletedTask; }));
        await Invoke(home, "SaveNotesAndContinueAsync");
        Assert.False(left);
        Assert.Equal("draft", Get<string>(home, "editUserDescription"));
        Assert.NotNull(Get<Func<Task>?>(home, "pendingDetailAction"));
        Assert.NotEmpty(Get<string>(home, "error"));
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
        Home home = null!;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.HtmlRenderer.RenderComponentAsync<CaptureHost<Home>>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Capture"] = (Action<Home>)(value => home = value) })));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await Invoke(home, "ReloadAsync");
            await Invoke(home, "OpenAsync", image);
            Set(home, "editUserDescription", "unsaved draft");
            await Invoke(home, "AnalyzeAsync");
        });
        Assert.Empty(Get<string>(home, "error"));
        Assert.NotNull(Get<LibraryWorkbench?>(home, "workbench"));
        var unchanged = Assert.Single(await vault.Service.ListAsync());
        Assert.Equal("old analysis", unchanged.Description);
        Assert.Equal("existing", unchanged.Tags);
        Assert.Equal("my notes", unchanged.UserDescription);
        Assert.Single(Get<IReadOnlyDictionary<string, OrganizationSuggestion>>(home, "aiSuggestions"));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => Invoke(home, "ApproveDetailSuggestionAsync"));
        Assert.Equal("old analysis", Assert.Single(await vault.Service.ListAsync()).Description);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            Call(home, "DiscardNotes");
            await Invoke(home, "ApproveDetailSuggestionAsync");
        });
        var approved = Assert.Single(await vault.Service.ListAsync());
        Assert.Equal("new analysis", approved.Description);
        Assert.Equal("existing, suggested", approved.Tags);
        Assert.Equal("my notes", approved.UserDescription);
        Assert.Equal("vision", approved.AnalysisModel);
        Assert.Empty(Get<IReadOnlyDictionary<string, OrganizationSuggestion>>(home, "aiSuggestions"));
        Assert.Equal("new analysis", Get<string>(home, "editDescription"));
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
        Home home = null!;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.HtmlRenderer.RenderComponentAsync<CaptureHost<Home>>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { ["Capture"] = (Action<Home>)(value => home = value) })));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            await Invoke(home, "ReloadAsync");
            Set(home, "ingestionWorkers", 2);
            Assert.True((bool)typeof(Home).GetProperty("IngestionSettingsDirty", Flags)!.GetValue(home)!);
            Assert.NotEqual(2, (await vault.Service.LoadIngestionOptionsAsync()).Workers);
            await Invoke(home, "SaveIngestionOptionsAsync");
            Assert.Equal(2, (await vault.Service.LoadIngestionOptionsAsync()).Workers);
            Assert.False((bool)typeof(Home).GetProperty("IngestionSettingsDirty", Flags)!.GetValue(home)!);
            await Invoke(home, "SwitchSettingsTabAsync", "backups");
            Set(home, "backupLocalDirectory", vault.Root + "-backups");
            Assert.True((bool)typeof(Home).GetProperty("BackupSettingsDirty", Flags)!.GetValue(home)!);
            Assert.False((bool)typeof(Home).GetProperty("BackupDestinationReady", Flags)!.GetValue(home)!);
            await Invoke(home, "SaveBackupOptionsAsync");
            Assert.False((bool)typeof(Home).GetProperty("BackupSettingsDirty", Flags)!.GetValue(home)!);
            Assert.True((bool)typeof(Home).GetProperty("BackupDestinationReady", Flags)!.GetValue(home)!);
        });
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
