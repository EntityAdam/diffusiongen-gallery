using System.Net;
using System.Text.Json;
using System.Text;
using Gallery.Core.Features.Vision.Endpoints;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Components.Features.Vision;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class VisionCheckpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_replacement_draft_survives_in_flight_approval_and_rejects_stale_decisions(bool tagsOnly)
    {
        var draft = new VisionDraft("image", "Image", "Description", "old", "model", TagsOnly: tagsOnly);
        var replacement = draft with { Tags = "new" };
        var record = new ImageRecord { Id = draft.Id, Tags = draft.Tags };
        var approvalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishApproval = new TaskCompletionSource<ImageRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new VisionCheckpointSession(new() { Drafts = [draft] }, CancellationToken.None,
            _ => Task.CompletedTask, _ =>
            {
                approvalStarted.SetResult();
                return finishApproval.Task;
            });

        var approval = session.ApproveDraftAsync(draft);
        await approvalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StageAsync(replacement);
        finishApproval.SetResult(record);

        Assert.Equal(record, await approval);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApproveDraftAsync(draft));
        Assert.False(await session.DismissDraftAsync(draft));
        Assert.Equal(replacement, Assert.Single(session.State.Drafts));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Whole_library_start_rechecks_remaining_media_after_stop_or_failure_without_a_queue(bool failSecond)
    {
        using var handler = new InterruptibleVisionHandler { FailSecond = failSecond };
        using var client = new HttpClient(handler);
        using var vault = new TestVault(vision: new VisionManager(client));
        await vault.InitializeAsync();
        var images = new List<ImageRecord>();
        for (var index = 0; index < 3; index++)
            images.Add(await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(20 + index, 20)), $"{index}.png", "folder"));
        await vault.Scope.Vision.SaveSettingsAsync(new() { Model = "vision" });
        using var analysis = await OpenAnalysisAsync(vault.Scope);
        var run = analysis.AnalyzeUnanalyzedAsync();
        await handler.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty((await vault.Scope.Vision.OpenVisionCheckpointAsync()).State.Queue);
        if (!failSecond) analysis.StopAnalysis();
        handler.ReleaseSecond.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        var stopped = (await vault.Scope.Vision.OpenVisionCheckpointAsync()).State;
        Assert.Empty(stopped.Queue);
        Assert.Equal(2, stopped.Drafts.Length);

        var added = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 20)), "newly-added.png", "folder");
        await analysis.AnalyzeUnanalyzedAsync();
        var restarted = (await vault.Scope.Vision.OpenVisionCheckpointAsync()).State;
        Assert.Empty(restarted.Queue);
        Assert.Equal(4, restarted.Drafts.Length);
        Assert.Contains(restarted.Drafts, draft => draft.Id == added.Id);
        Assert.Equal(failSecond ? 5 : 4, handler.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Parallel_lanes_keep_pulling_work_and_stop_or_lock_preserves_the_unfinished_queue(bool lockVault)
    {
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        var fastRequestCount = 0;
        using var handler = new LaneVisionHandler(async (port, token) =>
        {
            Interlocked.Increment(ref requestCount);
            if (port == 1234)
            {
                slowStarted.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            else
            {
                Assert.Equal(1235, port);
                if (Interlocked.Increment(ref fastRequestCount) == 2)
                {
                    thirdStarted.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
            }
            return LaneVisionHandler.Success();
        });
        using var client = new HttpClient(handler);
        using var vault = new TestVault(vision: new VisionManager(client));
        await vault.InitializeAsync();
        var images = new List<ImageRecord>();
        for (var index = 0; index < 4; index++)
            images.Add(await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(20 + index, 20)), $"{index}.png", "folder"));
        await vault.Scope.Vision.SaveSettingsAsync(new()
        {
            Lanes =
            [
                new() { Name = "Slow", Model = "manual" },
                new() { Name = "Fast", Endpoint = "http://127.0.0.1:1235/v1/", Model = "automatic", AutoAccept = true },
                new() { Name = "Disabled", Endpoint = "http://127.0.0.1:1236/v1/", Model = "disabled", Enabled = false }
            ]
        });
        using var analysis = await OpenAnalysisAsync(vault.Scope);
        var run = analysis.AnalyzeSelectionAsync([.. images], tagsOnly: false);
        try
        {
            await Task.WhenAll(slowStarted.Task, thirdStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("automatic", Assert.Single(await vault.Scope.Catalog.ListAsync(), image => image.AnalysisModel.Length > 0).AnalysisModel);
            if (lockVault) vault.Scope.Vault.LockVault();
            else analysis.StopAnalysis();
            if (!lockVault) release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.TrySetResult(); }

        Assert.Equal(3, requestCount);
        if (lockVault) await vault.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
        var saved = (await vault.Scope.Vision.OpenVisionCheckpointAsync()).State;
        Assert.Equal(lockVault ? 3 : 1, saved.Queue.Length);
        if (lockVault) Assert.Empty(saved.Drafts);
        else Assert.Equal("manual", Assert.Single(saved.Drafts).Model);
        Assert.Equal(lockVault ? 1 : 2, (await vault.Scope.Catalog.ListAsync()).Count(image => image.AnalysisModel == "automatic"));
    }

    [Fact]
    public async Task One_endpoint_refills_a_free_request_slot_without_exceeding_its_limit()
    {
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRemaining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestCount = 0;
        using var handler = new LaneVisionHandler(async (_, token) =>
        {
            switch (Interlocked.Increment(ref requestCount))
            {
                case 1:
                    await releaseFirst.Task.WaitAsync(token);
                    break;
                case 2:
                    secondStarted.TrySetResult();
                    await releaseRemaining.Task.WaitAsync(token);
                    break;
                case 3:
                    thirdStarted.TrySetResult();
                    await releaseRemaining.Task.WaitAsync(token);
                    break;
            }
            return LaneVisionHandler.Success();
        });
        using var client = new HttpClient(handler);
        using var vault = new TestVault(vision: new VisionManager(client));
        await vault.InitializeAsync();
        var images = new List<ImageRecord>();
        for (var index = 0; index < 4; index++)
            images.Add(await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(20 + index, 20)), $"{index}.png", "folder"));
        await vault.Scope.Vision.SaveSettingsAsync(new()
        {
            Lanes = [new() { Name = "Parallel", Model = "vision", MaxConcurrentRequests = 2 }]
        });
        using var analysis = await OpenAnalysisAsync(vault.Scope);
        var run = analysis.AnalyzeSelectionAsync([.. images], tagsOnly: false);
        try
        {
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, Volatile.Read(ref requestCount));
            releaseFirst.TrySetResult();
            await thirdStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            analysis.StopAnalysis();
            Assert.Equal(3, Volatile.Read(ref requestCount));
            releaseRemaining.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseRemaining.TrySetResult();
        }

        Assert.Equal(3, requestCount);
        var state = (await vault.Scope.Vision.OpenVisionCheckpointAsync()).State;
        Assert.Equal(3, state.Drafts.Length);
        Assert.Single(state.Queue);
    }

    [Fact]
    public async Task Unavailable_lane_is_retired_and_healthy_lane_picks_up_its_image()
    {
        var rejected = 0;
        var succeeded = 0;
        var bothRejectedRequestsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new LaneVisionHandler(async (port, token) =>
        {
            if (port == 1234)
            {
                if (Interlocked.Increment(ref rejected) == 2) bothRejectedRequestsStarted.TrySetResult();
                await bothRejectedRequestsStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            Interlocked.Increment(ref succeeded);
            return LaneVisionHandler.Success();
        });
        using var client = new HttpClient(handler);
        using var vault = new TestVault(vision: new VisionManager(client));
        await vault.InitializeAsync();
        var first = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var second = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(30, 20)), "second.png", "folder");
        var third = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 20)), "third.png", "folder");
        await vault.Scope.Vision.SaveSettingsAsync(new()
        {
            Lanes =
            [
                new() { Name = "Unauthorized", Model = "rejected", MaxConcurrentRequests = 2 },
                new() { Name = "Healthy", Endpoint = "http://127.0.0.1:1235/v1/", Model = "healthy" }
            ]
        });
        using var analysis = await OpenAnalysisAsync(vault.Scope);
        await analysis.AnalyzeSelectionAsync([first, second, third], tagsOnly: false);
        var state = (await vault.Scope.Vision.OpenVisionCheckpointAsync()).State;
        Assert.Equal(2, rejected);
        Assert.Equal(3, succeeded);
        Assert.Empty(state.Queue);
        Assert.Equal(3, state.Drafts.Length);
        Assert.All(state.Drafts, draft => Assert.Equal("healthy", draft.Model));
    }

    [Fact]
    public async Task Whole_library_start_uses_current_catalog_and_skips_pending_and_accepted_results()
    {
        using var handler = new InterruptibleVisionHandler();
        using var client = new HttpClient(handler);
        using var vault = new TestVault(vision: new VisionManager(client));
        await vault.InitializeAsync();
        var accepted = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "accepted.png", "folder");
        accepted = await vault.Scope.Vision.ApproveVisionAsync(accepted.Id, "", "", "vision", null);
        var pending = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(30, 20)), "pending.png", "folder");
        var untouched = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(40, 20)), "untouched.png", "folder");
        var checkpoint = await vault.Scope.Vision.OpenVisionCheckpointAsync();
        VisionDraft existing = new(pending.Id, pending.Name, "Existing result", "old", "vision");
        await checkpoint.StageAsync(existing);
        await vault.Scope.Vision.SaveSettingsAsync(new() { Model = "vision" });
        using var analysis = await OpenAnalysisAsync(vault.Scope);
        await analysis.AnalyzeUnanalyzedAsync();
        var result = (await vault.Scope.Vision.OpenVisionCheckpointAsync()).State;
        Assert.Empty(result.Queue);
        Assert.Equal(2, result.Drafts.Length);
        Assert.Contains(existing, result.Drafts);
        Assert.Contains(result.Drafts, draft => draft.Id == untouched.Id);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Whole_library_analysis_restarts_after_lock_without_a_saved_queue(bool autoAccept)
    {
        using var handler = new InterruptibleVisionHandler();
        using var client = new HttpClient(handler);
        using var vault = new TestVault(vision: new VisionManager(client));
        await vault.InitializeAsync();
        var first = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var second = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(30, 20)), "second.png", "folder");
        await vault.Scope.Vision.SaveSettingsAsync(new() { Model = "vision" });
        var legacyCheckpoint = await vault.Scope.Vision.OpenVisionCheckpointAsync();
        await legacyCheckpoint.UpdateAsync(state => state with { AutoAcceptVisionAnalysis = autoAccept });
        using var original = await OpenAnalysisAsync(vault.Scope);
        var run = original.AnalyzeUnanalyzedAsync();
        await handler.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(autoAccept ? "completed response" : "", (await vault.Scope.Catalog.ListAsync()).Single(image => image.Id == first.Id).Description);
        Assert.Empty((await vault.Scope.Vision.OpenVisionCheckpointAsync()).State.Queue);
        Assert.Equal(autoAccept ? 0 : 1, (await vault.Scope.Vision.OpenVisionCheckpointAsync()).State.Drafts.Length);
        vault.Scope.Vault.LockVault();
        await handler.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        original.Dispose();
        await vault.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
        handler.ReleaseSecond.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        using var restored = await OpenAnalysisAsync(vault.Scope);
        await restored.AnalyzeUnanalyzedAsync();
        var checkpoint = (await vault.Scope.Vision.OpenVisionCheckpointAsync()).State;
        Assert.Equal(autoAccept, checkpoint.AutoAcceptVisionAnalysis);
        Assert.Empty(checkpoint.Queue);
        Assert.Equal(autoAccept ? 0 : 2, checkpoint.Drafts.Length);
        Assert.Equal(3, handler.RequestCount);
        Assert.All(await vault.Scope.Catalog.ListAsync(), image => Assert.Equal(autoAccept ? "completed response" : "", image.Description));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bulk_decisions_include_off_page_drafts_and_preserve_tags_only_drafts_and_queue(bool accept)
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var first = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var second = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(30, 20)), "second.png", "folder");
        var checkpoint = await vault.Scope.Vision.OpenVisionCheckpointAsync();
        VisionDraft firstDraft = new(first.Id, first.Name, "First analysis", "sunlit", "vision");
        VisionDraft secondDraft = new(second.Id, second.Name, "Second analysis", "landscape", "vision");
        var tagsOnly = firstDraft with { TagsOnly = true };
        VisionQueueItem queued = new("unfinished");
        await checkpoint.UpdateAsync(state => state with { Drafts = [firstDraft, secondDraft, tagsOnly], Queue = [queued] });
        using var analysis = await OpenAnalysisAsync(vault.Scope);
        await (accept ? analysis.AcceptAllVisionAsync() : analysis.DiscardAllVisionAsync());
        var state = (await vault.Scope.Vision.OpenVisionCheckpointAsync()).State;
        Assert.Equal(tagsOnly, Assert.Single(state.Drafts));
        Assert.Equal(queued, Assert.Single(state.Queue));
        Assert.Empty(Suggestions(analysis, false));
        Assert.Single(Suggestions(analysis, true));
        var records = await vault.Scope.Catalog.ListAsync();
        Assert.Equal(accept ? "First analysis" : "", records.Single(image => image.Id == first.Id).Description);
        Assert.Equal(accept ? "Second analysis" : "", records.Single(image => image.Id == second.Id).Description);
        Assert.All(records, image => Assert.False(image.IsReviewed));
    }

    [Fact]
    public async Task Bulk_accept_keeps_failed_drafts_and_continues_with_valid_drafts()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var image = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "sample.png", "folder");
        var checkpoint = await vault.Scope.Vision.OpenVisionCheckpointAsync();
        VisionDraft missing = new("missing-image", "missing.png", "Keep this draft", "", "vision");
        await checkpoint.UpdateAsync(state => state with { Drafts = [missing, new(image.Id, image.Name, "Accepted description", "sunlit", "vision")] });
        using var analysis = await OpenAnalysisAsync(vault.Scope);
        await analysis.AcceptAllVisionAsync();
        Assert.Equal(missing, Assert.Single((await vault.Scope.Vision.OpenVisionCheckpointAsync()).State.Drafts));
        Assert.Equal("Accepted description", Assert.Single(await vault.Scope.Catalog.ListAsync()).Description);
        Assert.Equal(missing.Id, Assert.Single(Suggestions(analysis, false)).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Auto_accept_applies_only_to_new_full_vision_results(bool tagsOnly)
    {
        using var handler = new InterruptibleVisionHandler();
        using var client = new HttpClient(handler);
        using var vault = new TestVault(vision: new VisionManager(client));
        await vault.InitializeAsync();
        var image = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "new.png", "folder");
        var pending = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(30, 20)), "pending.png", "folder");
        await vault.Scope.Vision.SaveSettingsAsync(new() { Lanes = [new() { Name = "Primary", Model = "vision", AutoAccept = true }] });
        var checkpoint = await vault.Scope.Vision.OpenVisionCheckpointAsync();
        VisionDraft oldDraft = new(pending.Id, pending.Name, "Awaiting review", "old", "vision");
        await checkpoint.StageAsync(oldDraft);
        using var analysis = await OpenAnalysisAsync(vault.Scope);
        await (tagsOnly ? analysis.AnalyzeSelectionAsync([image], tagsOnly: true) : analysis.AnalyzeUnanalyzedAsync());
        var state = (await vault.Scope.Vision.OpenVisionCheckpointAsync()).State;
        Assert.True(Assert.Single((await vault.Scope.Vision.LoadSettingsAsync()).Lanes).AutoAccept);
        Assert.Contains(oldDraft, state.Drafts);
        Assert.Empty(state.Queue);
        var records = await vault.Scope.Catalog.ListAsync();
        var updated = records.Single(record => record.Id == image.Id);
        Assert.Equal(tagsOnly ? "" : "completed response", updated.Description);
        Assert.Equal(tagsOnly ? "" : "sunlit", updated.Tags);
        Assert.Empty(records.Single(record => record.Id == pending.Id).Description);
        Assert.Equal(tagsOnly, state.Drafts.Any(draft => draft.Id == image.Id));
        if (tagsOnly) Assert.True(state.Drafts.Single(draft => draft.Id == image.Id).TagsOnly);
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task Auto_accept_preference_survives_restart(CatalogBackend backend)
    {
        var root = Path.Combine(Path.GetTempPath(), "vision-approval-" + Guid.NewGuid().ToString("N"));
        var options = new GalleryRuntimeOptions { CatalogBackend = backend, StateDirectory = root + "-state" };
        try
        {
            using (var gallery = GalleryScope.Create(root, options, new HttpClient()))
            {
                await gallery.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
                var checkpoint = await gallery.Vision.OpenVisionCheckpointAsync();
                Assert.False(checkpoint.State.AutoAcceptVisionAnalysis);
                await checkpoint.UpdateAsync(state => state with { AutoAcceptVisionAnalysis = true });
            }
            using var reopened = GalleryScope.Create(root, options, new HttpClient());
            await reopened.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
            Assert.True((await reopened.Vision.OpenVisionCheckpointAsync()).State.AutoAcceptVisionAnalysis);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(root + "-state")) Directory.Delete(root + "-state", recursive: true);
        }
    }

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
        var image = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        await vault.Scope.Tags.AddTagAsync("sunlit");
        await vault.Scope.Tags.AddTagAsync("keep");
        var checkpoint = await vault.Scope.Vision.OpenVisionCheckpointAsync();
        VisionDraft fullDraft = new(image.Id, image.Name, "Completed description", "sunlit, keep", "vision", "Review this");
        VisionDraft tags = fullDraft with { TagsOnly = true };
        VisionDraft other = new("off-page", "other.png", "Other description", "unrelated", "vision");
        VisionQueueItem pending = new("unfinished", Failed: true);
        await checkpoint.UpdateAsync(state => state with { Drafts = [fullDraft, tags, other], Queue = [pending] });
        using var analysis = await OpenAnalysisAsync(vault.Scope);

        if (previous is null) await vault.Scope.Tags.AddTagAsync(replacement!);
        else if (replacement is null) await vault.Scope.Tags.DeleteTagAsync(previous);
        else await vault.Scope.Tags.RenameTagAsync(previous, replacement);
        await analysis.UpdateTagsAsync(new(previous, replacement));

        var saved = (await vault.Scope.Vision.OpenVisionCheckpointAsync()).State;
        Assert.Equal(new[] { fullDraft with { Tags = expectedTags }, tags with { Tags = expectedTags }, other }, saved.Drafts.ToArray());
        Assert.Equal(pending, Assert.Single(saved.Queue));
        Assert.Equal(expectedTags, Assert.Single(Suggestions(analysis, true)).Tags);
        Assert.Equal(2, Suggestions(analysis, false).Count);
        Assert.Empty(Assert.Single(await vault.Scope.Catalog.ListAsync()).Description);

        analysis.Dispose();
        vault.Scope.Vault.LockVault();
        await vault.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
        using var restored = await OpenAnalysisAsync(vault.Scope);
        var suggestion = Suggestions(restored, false).Single(item => item.Id == image.Id);
        Assert.Equal("Completed description", suggestion.Description);
        await restored.ApproveAsync(suggestion);
        var approved = Assert.Single(await vault.Scope.Catalog.ListAsync());
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
            using (var gallery = GalleryScope.Create(root, options, new HttpClient()))
            {
                await gallery.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
                var session = await gallery.Vision.OpenVisionCheckpointAsync();
                await session.UpdateAsync(state => state with { Queue = [new("completed"), new("unfinished")] });
                await session.StageAsync(new("completed", "private-name-sentinel", "private-description-sentinel", "private-tag-sentinel", "model"));
                gallery.Vault.LockVault();
                await gallery.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.StageAsync(new("late", "late", "late", "", "model")));
                await gallery.Vault.CloseVaultAsync();
            }
            // Recreate the service to exercise catalog persistence, not an in-memory cache.
            using (var gallery = GalleryScope.Create(root, options, new HttpClient()))
            {
                await gallery.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
                var session = await gallery.Vision.OpenVisionCheckpointAsync();
                Assert.Equal("private-description-sentinel", Assert.Single(session.State.Drafts).Description);
                Assert.Equal("unfinished", Assert.Single(session.State.Queue).Id);
                await session.DismissAsync("completed");
                Assert.Empty((await gallery.Vision.OpenVisionCheckpointAsync()).State.Drafts);
                await gallery.Vault.CloseVaultAsync();
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
        var first = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var second = await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(30, 20)), "second.png", "folder");
        await vault.Scope.Vision.SaveSettingsAsync(new() { Model = "vision" });
        using var original = await OpenAnalysisAsync(vault.Scope);
        var run = original.AnalyzeSelectionAsync([first, second], tagsOnly);
        await handler.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var saved = (await vault.Scope.Vision.OpenVisionCheckpointAsync()).State;
        Assert.Equal(first.Id, Assert.Single(saved.Drafts).Id);
        Assert.Equal(second.Id, Assert.Single(saved.Queue).Id);
        Assert.DoesNotContain(await vault.Scope.Catalog.ListAsync(), image => image.Description.Length > 0 || image.Tags.Length > 0);

        if (lockKind == "auto") vault.Scope.Vault.ConfigureInactivityTimeout(TimeSpan.FromTicks(1));
        else if (lockKind == "panic") vault.Scope.Vault.Panic();
        else vault.Scope.Vault.LockVault();
        await handler.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        original.Dispose();
        vault.Scope.Vault.ConfigureInactivityTimeout(null);
        await vault.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
        // Let the old server response arrive after unlock. It must not be checkpointed.
        handler.ReleaseSecond.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single((await vault.Scope.Vision.OpenVisionCheckpointAsync()).State.Drafts);

        // Restoring the workflow must recover drafts without a rendered catalog.
        using var restored = await OpenAnalysisAsync(vault.Scope);
        Assert.Single(Suggestions(restored, tagsOnly));
        Assert.Equal(first.Id, Suggestions(restored, tagsOnly)[0].Id);
        await restored.ResumeAnalysisAsync();
        Assert.Equal(3, handler.RequestCount);
        var completed = (await vault.Scope.Vision.OpenVisionCheckpointAsync()).State;
        Assert.Empty(completed.Queue);
        Assert.Equal(2, completed.Drafts.Length);
        Assert.DoesNotContain(completed.Drafts, draft => draft.Description == "late response");
        Assert.All(await vault.Scope.Catalog.ListAsync(), image => Assert.Empty(image.Tags));

        var suggestion = Suggestions(restored, tagsOnly).Single(item => item.Id == first.Id);
        if (tagsOnly) await restored.ApplyTagSuggestionAsync(suggestion);
        else await restored.ApproveAsync(suggestion);
        var approved = (await vault.Scope.Catalog.ListAsync()).Single(image => image.Id == first.Id);
        Assert.Equal("sunlit", approved.Tags);
        Assert.Equal(tagsOnly ? "" : "completed response", approved.Description);
        Assert.DoesNotContain((await vault.Scope.Vision.OpenVisionCheckpointAsync()).State.Drafts, draft => draft.Id == first.Id);
        var remaining = Suggestions(restored, tagsOnly).Single();
        if (tagsOnly) await restored.DismissTagSuggestionAsync(remaining);
        else await restored.DismissAsync(remaining);
        Assert.Empty((await vault.Scope.Vision.OpenVisionCheckpointAsync()).State.Drafts);
    }

    [Fact]
    public async Task A_completed_draft_can_be_approved_while_the_next_request_is_running()
    {
        using var handler = new InterruptibleVisionHandler();
        using var client = new HttpClient(handler);
        using var vault = new TestVault(vision: new VisionManager(client));
        await vault.InitializeAsync();
        var scope = vault.Scope;
        var first = await scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng()), "first.png", "folder");
        var second = await scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(30, 20)), "second.png", "folder");
        await scope.Vision.SaveSettingsAsync(new() { Model = "vision" });
        using var analysis = await OpenAnalysisAsync(scope);

        var run = analysis.AnalyzeSelectionAsync([first, second], tagsOnly: false);
        try
        {
            await handler.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(analysis.Status.IsRunning);
            var draft = Assert.Single(analysis.Suggestions);
            Assert.Equal(first.Id, draft.Id);
            var approved = Assert.IsType<ImageRecord>(await analysis.ApproveAsync(draft));
            Assert.Equal("completed response", approved.Description);
            Assert.Empty(analysis.Suggestions);
        }
        finally
        {
            handler.ReleaseSecond.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(second.Id, Assert.Single(analysis.Suggestions).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selected_analysis_keeps_unfinished_items_for_explicit_resume(bool failSecond)
    {
        using var handler = new InterruptibleVisionHandler { FailSecond = failSecond };
        using var client = new HttpClient(handler);
        using var vault = new TestVault(vision: new VisionManager(client));
        await vault.InitializeAsync();
        var images = new List<ImageRecord>();
        for (var index = 0; index < 3; index++)
            images.Add(await vault.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(20 + index, 20)), $"{index}.png", "folder"));
        await vault.Scope.Vision.SaveSettingsAsync(new() { Model = "vision" });
        using var analysis = await OpenAnalysisAsync(vault.Scope);
        var run = analysis.AnalyzeSelectionAsync(images.ToArray(), tagsOnly: false);
        await handler.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!failSecond) analysis.StopAnalysis();
        handler.ReleaseSecond.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        var state = (await vault.Scope.Vision.OpenVisionCheckpointAsync()).State;
        Assert.Equal(2, state.Drafts.Length);
        var pending = Assert.Single(state.Queue);
        Assert.Equal(failSecond ? images[1].Id : images[2].Id, pending.Id);
        Assert.Equal(failSecond, pending.Failed);
        await analysis.ResumeAnalysisAsync();
        Assert.Equal(failSecond ? 4 : 3, handler.RequestCount);
        var resumed = (await vault.Scope.Vision.OpenVisionCheckpointAsync()).State;
        Assert.Empty(resumed.Queue);
        Assert.Equal(3, resumed.Drafts.Length);
    }

    [Fact]
    public async Task Save_waiting_for_catalog_gate_is_revoked_before_a_new_unlock_can_write()
    {
        using var vault = new TestVault();
        await vault.InitializeAsync();
        var original = await vault.Scope.Vision.OpenVisionCheckpointAsync();
        await vault.Store.Gate.WaitAsync();
        var gateHeld = true;
        try
        {
            var waiting = original.StageAsync(new("late", "late", "late", "", "", "vision"));
            Assert.False(waiting.IsCompleted);
            vault.Scope.Vault.LockVault();
            await vault.Scope.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
            var fresh = vault.Scope.Vision.OpenVisionCheckpointAsync();
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
            var checkpoint = await original.Scope.Vision.OpenVisionCheckpointAsync();
            await original.Scope.Vault.SelectNewVaultAsync(nextRoot);
            await original.Scope.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checkpoint.StageAsync(new("late", "late", "late", "", "", "vision")));
            Assert.Empty((await original.Scope.Vision.OpenVisionCheckpointAsync()).State.Drafts);
            await original.Scope.Vault.CloseVaultAsync();
        }
        finally
        {
            await original.Scope.Vault.CloseVaultAsync();
            if (Directory.Exists(nextRoot)) Directory.Delete(nextRoot, true);
        }
    }

    private static IReadOnlyList<OrganizationSuggestion> Suggestions(LibraryAnalysis analysis, bool tagsOnly) =>
        tagsOnly ? analysis.TagSuggestions : analysis.Suggestions;

    private static async Task<LibraryAnalysis> OpenAnalysisAsync(GalleryScope scope)
    {
        var analysis = new LibraryAnalysis(scope.Catalog, scope.Vision, NullLogger.Instance);
        await analysis.InitializeAsync();
        return analysis;
    }

    private sealed class LaneVisionHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request.RequestUri!.Port, cancellationToken);

        public static HttpResponseMessage Success() => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[{"message":{"content":"{\"description\":\"A photo.\",\"tags\":[\"sunlit\"]}"}}]}""")
        };
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
