using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class VisionCheckpointTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(null, "new", "sunlit, keep")]
    [InlineData("SUNLIT", "Sunshine", "Sunshine, keep")]
    [InlineData("SUNLIT", "keep", "keep")]
    [InlineData("SUNLIT", null, "keep")]
    public async Task Managing_tags_preserves_analysis_drafts_and_queue_across_unlock(
        string? previous, string? replacement, string expectedTags)
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        await vault.Service.AddTagAsync("sunlit");
        await vault.Service.AddTagAsync("keep");
        var checkpoint = await vault.Service.OpenVisionCheckpointAsync();
        VisionDraft analysis = new(image.Id, image.Name, "Completed description", "sunlit, keep", "vision", "Review this");
        VisionDraft tags = analysis with { TagsOnly = true };
        VisionDraft other = new("off-page", "other.png", "Other description", "unrelated", "vision");
        VisionQueueItem pending = new("unfinished", Failed: true);
        await checkpoint.UpdateAsync(state => state with { Drafts = [analysis, tags, other], Queue = [pending] });
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        var workbench = await RenderAsync(fixture, [image]);

        if (previous is null) await vault.Service.AddTagAsync(replacement!);
        else if (replacement is null) await vault.Service.DeleteTagAsync(previous);
        else await vault.Service.RenameTagAsync(previous, replacement);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => workbench.UpdateSuggestionTagsAsync(new(previous, replacement)));

        var saved = (await vault.Service.OpenVisionCheckpointAsync()).State;
        Assert.Equal(new[] { analysis with { Tags = expectedTags }, tags with { Tags = expectedTags }, other }, saved.Drafts.ToArray());
        Assert.Equal(pending, Assert.Single(saved.Queue));
        Assert.Equal(expectedTags, Assert.Single(Suggestions(workbench, true)).Tags);
        Assert.Equal(2, Suggestions(workbench, false).Count);
        Assert.Empty(Assert.Single(await vault.Service.ListAsync()).Description);

        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(workbench.Dispose);
        vault.Service.LockVault();
        await vault.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);
        var restored = await RenderAsync(fixture, [image]);
        var suggestion = Suggestions(restored, false).Single(item => item.Id == image.Id);
        Assert.Equal("Completed description", suggestion.Description);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => restored.ApproveSuggestionAsync(suggestion));
        var approved = Assert.Single(await vault.Service.ListAsync());
        Assert.Equal("Completed description", approved.Description);
        Assert.Equal(expectedTags, approved.Tags);
        Assert.Equal("vision", approved.AnalysisModel);
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task Encrypted_drafts_and_queue_survive_restart_and_revoked_handles_cannot_write(CatalogBackend backend)
    {
        var root = Path.Combine(Path.GetTempPath(), "vision-checkpoint-" + Guid.NewGuid().ToString("N"));
        var options = new GalleryRuntimeOptions { CatalogBackend = backend, StateDirectory = root + "-state" };
        try
        {
            using (var gallery = GalleryService.Create(root, options, new HttpClient()))
            {
                await gallery.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
                var session = await gallery.OpenVisionCheckpointAsync();
                await session.UpdateAsync(state => state with { Queue = [new("completed"), new("unfinished")] });
                await session.StageAsync(new("completed", "private-name-sentinel", "private-description-sentinel", "private-tag-sentinel", "model"));
                gallery.LockVault();
                await gallery.UnlockWithPassphraseAsync(TestVault.Passphrase);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.StageAsync(new("late", "late", "late", "", "model")));
                await gallery.CloseVaultAsync();
            }
            // Recreate the service to exercise catalog persistence, not an in-memory cache.
            using (var gallery = GalleryService.Create(root, options, new HttpClient()))
            {
                await gallery.UnlockWithPassphraseAsync(TestVault.Passphrase);
                var session = await gallery.OpenVisionCheckpointAsync();
                Assert.Equal("private-description-sentinel", Assert.Single(session.State.Drafts).Description);
                Assert.Equal("unfinished", Assert.Single(session.State.Queue).Id);
                await session.DismissAsync("completed");
                Assert.Empty((await gallery.OpenVisionCheckpointAsync()).State.Drafts);
                await gallery.CloseVaultAsync();
            }
            // Inspect the closed catalog, including any sidecars, for draft plaintext.
            foreach (var file in Directory.GetFiles(root, "gallery.db*"))
            {
                var bytes = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file));
                Assert.DoesNotContain("private-name-sentinel", bytes);
                Assert.DoesNotContain("private-description-sentinel", bytes);
                Assert.DoesNotContain("private-tag-sentinel", bytes);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (Directory.Exists(root + "-state")) Directory.Delete(root + "-state", true);
        }
    }

    [Theory]
    [InlineData(false, "manual")]
    [InlineData(false, "panic")]
    [InlineData(false, "auto")]
    [InlineData(true, "manual")]
    public async Task Lock_during_analysis_restores_completed_drafts_and_resumes_only_unfinished_items(bool tagsOnly, string lockKind)
    {
        using var handler = new InterruptibleVisionHandler();
        using var client = new HttpClient(handler);
        using var vault = new TestVault(vision: new VisionManager(client));
        await vault.InitializeAsync();
        var first = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var second = await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(30, 20)), "second.png", "folder");
        await vault.Service.SaveSettingsAsync(new() { Model = "vision" });
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        var original = await RenderAsync(fixture, [first, second]);
        var action = tagsOnly ? "SuggestTagsAsync" : "AnalyzeUnanalyzedAsync";
        var run = fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => InvokeAsync(original, action));
        await handler.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var saved = (await vault.Service.OpenVisionCheckpointAsync()).State;
        Assert.Equal(first.Id, Assert.Single(saved.Drafts).Id);
        Assert.Equal(second.Id, Assert.Single(saved.Queue).Id);
        Assert.DoesNotContain(await vault.Service.ListAsync(), image => image.Description.Length > 0 || image.Tags.Length > 0);

        if (lockKind == "auto") vault.Service.ConfigureInactivityTimeout(TimeSpan.FromTicks(1));
        else if (lockKind == "panic") vault.Service.Panic();
        else vault.Service.LockVault();
        await handler.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => original.Dispose());
        vault.Service.ConfigureInactivityTimeout(null);
        await vault.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);
        // Let the old server response arrive after unlock. It must not be checkpointed.
        handler.ReleaseSecond.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single((await vault.Service.OpenVisionCheckpointAsync()).State.Drafts);

        // Restore with a partial UI catalog: drafts for other pages must survive.
        var restored = await RenderAsync(fixture, [second]);
        Assert.Single(Suggestions(restored, tagsOnly));
        Assert.Equal(first.Id, Suggestions(restored, tagsOnly)[0].Id);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => InvokeAsync(restored, "ResumeAnalysisAsync"));
        Assert.Equal(3, handler.RequestCount);
        var completed = (await vault.Service.OpenVisionCheckpointAsync()).State;
        Assert.Empty(completed.Queue);
        Assert.Equal(2, completed.Drafts.Length);
        Assert.DoesNotContain(completed.Drafts, draft => draft.Description == "late response");
        Assert.All(await vault.Service.ListAsync(), image => Assert.Empty(image.Tags));

        var suggestion = Suggestions(restored, tagsOnly).Single(item => item.Id == first.Id);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            if (tagsOnly) await InvokeAsync(restored, "ApplyTagSuggestionAsync", suggestion);
            else await restored.ApproveSuggestionAsync(suggestion);
        });
        var approved = (await vault.Service.ListAsync()).Single(image => image.Id == first.Id);
        Assert.Equal("sunlit", approved.Tags);
        Assert.Equal(tagsOnly ? "" : "completed response", approved.Description);
        Assert.DoesNotContain((await vault.Service.OpenVisionCheckpointAsync()).State.Drafts, draft => draft.Id == first.Id);
        var remaining = Suggestions(restored, tagsOnly).Single();
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(async () =>
        {
            if (tagsOnly) await InvokeAsync(restored, "DismissTagSuggestionAsync", remaining);
            else await restored.DismissSuggestionAsync(remaining);
        });
        Assert.Empty((await vault.Service.OpenVisionCheckpointAsync()).State.Drafts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_and_failure_keep_unfinished_items_for_explicit_resume(bool failSecond)
    {
        using var handler = new InterruptibleVisionHandler { FailSecond = failSecond };
        using var client = new HttpClient(handler);
        using var vault = new TestVault(vision: new VisionManager(client));
        await vault.InitializeAsync();
        var images = new List<ImageRecord>();
        for (var index = 0; index < 3; index++)
            images.Add(await vault.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(20 + index, 20)), $"{index}.png", "folder"));
        await vault.Service.SaveSettingsAsync(new() { Model = "vision" });
        await using var fixture = ComponentRenderFixture.Create(vault.Service);
        var workbench = await RenderAsync(fixture, images.ToArray());
        var run = fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => InvokeAsync(workbench, "AnalyzeUnanalyzedAsync"));
        await handler.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!failSecond) await fixture.HtmlRenderer.Dispatcher.InvokeAsync(workbench.StopAnalysisAsync);
        handler.ReleaseSecond.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        var state = (await vault.Service.OpenVisionCheckpointAsync()).State;
        Assert.Equal(2, state.Drafts.Length);
        var pending = Assert.Single(state.Queue);
        Assert.Equal(failSecond ? images[1].Id : images[2].Id, pending.Id);
        Assert.Equal(failSecond, pending.Failed);
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => InvokeAsync(workbench, "ResumeAnalysisAsync"));
        Assert.Equal(failSecond ? 4 : 3, handler.RequestCount);
        var resumed = (await vault.Service.OpenVisionCheckpointAsync()).State;
        Assert.Empty(resumed.Queue);
        Assert.Equal(3, resumed.Drafts.Length);
    }

    [Fact]
    public async Task Save_waiting_for_catalog_gate_is_revoked_before_a_new_unlock_can_write()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var original = await vault.Service.OpenVisionCheckpointAsync();
        await vault.Store.Gate.WaitAsync();
        var gateHeld = true;
        try
        {
            var waiting = original.StageAsync(new("late", "late", "late", "", "", "vision"));
            Assert.False(waiting.IsCompleted);
            vault.Service.LockVault();
            await vault.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
            var fresh = vault.Service.OpenVisionCheckpointAsync();
            Assert.False(fresh.IsCompleted);
            vault.Store.Gate.Release();
            gateHeld = false;
            Assert.Empty((await fresh.WaitAsync(TimeSpan.FromSeconds(10))).State.Drafts);
        }
        finally
        {
            if (gateHeld) vault.Store.Gate.Release();
        }
    }

    [Fact]
    public async Task Stale_checkpoint_cannot_write_to_a_different_vault()
    {
        using var original = new TestVault();
        var nextRoot = Path.Combine(Directory.GetCurrentDirectory(), ".gallery-vision-next-" + Guid.NewGuid().ToString("N"));
        try
        {
            await original.InitializeAsync();
            var checkpoint = await original.Service.OpenVisionCheckpointAsync();
            await original.Service.SelectNewVaultAsync(nextRoot);
            await original.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checkpoint.StageAsync(new("late", "late", "late", "", "", "vision")));
            Assert.Empty((await original.Service.OpenVisionCheckpointAsync()).State.Drafts);
            await original.Service.CloseVaultAsync();
        }
        finally
        {
            await original.Service.CloseVaultAsync();
            if (Directory.Exists(nextRoot)) Directory.Delete(nextRoot, true);
        }
    }

    private static List<OrganizationSuggestion> Suggestions(LibraryWorkbench component, bool tagsOnly) =>
        (List<OrganizationSuggestion>)typeof(LibraryWorkbench).GetField(tagsOnly ? "tagSuggestions" : "suggestions", Flags)!.GetValue(component)!;

    private static Task InvokeAsync(object component, string method, params object[] arguments) =>
        (Task)component.GetType().GetMethod(method, Flags)!.Invoke(component, arguments)!;

    private static async Task<LibraryWorkbench> RenderAsync(ComponentRenderFixture fixture, ImageRecord[] images)
    {
        LibraryWorkbench result = null!;
        await fixture.HtmlRenderer.Dispatcher.InvokeAsync(() => fixture.HtmlRenderer.RenderComponentAsync<Host>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Host.Images)] = images,
                [nameof(Host.Capture)] = (Action<LibraryWorkbench>)(value => result = value)
            })));
        return result;
    }

    private sealed class Host : ComponentBase
    {
        [Parameter] public ImageRecord[] Images { get; set; } = [];
        [Parameter] public Action<LibraryWorkbench>? Capture { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<LibraryWorkbench>(0);
            builder.AddAttribute(1, nameof(LibraryWorkbench.Images), Images);
            builder.AddAttribute(2, nameof(LibraryWorkbench.SelectedIds), Images.Select(image => image.Id).ToArray());
            builder.AddAttribute(3, nameof(LibraryWorkbench.Workflow), "intelligence");
            builder.AddComponentReferenceCapture(4, value => Capture?.Invoke((LibraryWorkbench)value));
            builder.CloseComponent();
        }
    }

    private sealed class InterruptibleVisionHandler : HttpMessageHandler
    {
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RequestCount;
        public bool FailSecond { get; init; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref RequestCount);
            if (count == 2)
            {
                using var registration = cancellationToken.Register(() => Canceled.TrySetResult());
                SecondStarted.TrySetResult();
                await ReleaseSecond.Task;
                if (FailSecond) return new(HttpStatusCode.ServiceUnavailable);
            }
            var content = JsonSerializer.Serialize(new { description = count == 2 ? "late response" : "completed response", tags = new[] { "sunlit" } });
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }))
            };
        }
    }
}
