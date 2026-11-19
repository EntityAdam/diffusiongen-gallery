using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Components.Features.Vision;

namespace Gallery.Web.Components.Features.Curation;

public partial class LibraryWorkbench
{
    private VisionCheckpointSession? checkpoint;
    private readonly CancellationTokenSource analysisLifetime = new();
    private readonly SemaphoreSlim visionApprovalGate = new(1, 1);
    private async Task<ImageRecord?> ApprovePendingVisionAsync(OrganizationSuggestion suggestion, VisionCheckpointSession approvalCheckpoint)
    {
        await visionApprovalGate.WaitAsync(approvalCheckpoint.RevocationToken);
        try
        {
            var draft = approvalCheckpoint.State.Drafts.FirstOrDefault(item => item.Id == suggestion.Id && !item.TagsOnly);
            if (draft is null) return null;
            if (ToSuggestion(draft) != suggestion)
                throw new InvalidOperationException("This suggestion changed. Refresh it before accepting.");
            approvalCheckpoint.RevocationToken.ThrowIfCancellationRequested();
            var approved = await PersistApprovedSuggestionAsync(GalleryVision, suggestion);
            // A replacement draft staged during approval must remain available for review.
            await approvalCheckpoint.UpdateAsync(state => state with { Drafts = [.. state.Drafts.Where(item => item != draft)] });
            return approved;
        }
        finally { visionApprovalGate.Release(); }
    }

    private Task AcceptAllVisionAsync() => RunAsync("Accepting pending analysis...", async () =>
    {
        await EnsureCheckpointAsync();
        var pending = checkpoint!.State.Drafts.Where(draft => !draft.TagsOnly).ToArray();
        var accepted = 0;
        var failures = new List<string>();
        try
        {
            foreach (var draft in pending)
            {
                checkpoint.RevocationToken.ThrowIfCancellationRequested();
                status = $"Accepting {accepted + failures.Count + 1} / {pending.Length}: {draft.Name}";
                await InvokeAsync(StateHasChanged);
                try
                {
                    if (await ApprovePendingVisionAsync(ToSuggestion(draft), checkpoint) is not null) accepted++;
                }
                catch (Exception) when (checkpoint.RevocationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    Logger.LogError(exception, "Bulk vision approval failed for {ImageId}", draft.Id);
                    failures.Add($"{draft.Name}: {exception.Message}");
                }
            }
            notice = $"Accepted {accepted} suggestion(s). {failures.Count} could not be accepted and remain pending.";
            error = string.Join(" / ", failures);
        }
        finally
        {
            if (!disposed && !checkpoint.RevocationToken.IsCancellationRequested)
            {
                await RestoreDraftsAsync();
                if (pending.Length > 0) await Changed.InvokeAsync();
            }
        }
    }, reportJob: true);

    private Task DiscardAllVisionAsync() => RunAsync("Discarding pending analysis...", async () =>
    {
        await EnsureCheckpointAsync();
        await visionApprovalGate.WaitAsync(checkpoint!.RevocationToken);
        try
        {
            var count = checkpoint.State.Drafts.Count(draft => !draft.TagsOnly);
            await checkpoint.UpdateAsync(state => state with { Drafts = [.. state.Drafts.Where(draft => draft.TagsOnly)] });
            await RestoreDraftsAsync();
            notice = $"Discarded {count} pending suggestion(s). Saved analysis is unchanged.";
        }
        finally { visionApprovalGate.Release(); }
    });

    private int PendingAnalysisCount => checkpoint?.State.Queue.Length ?? 0;

    private async Task EnsureCheckpointAsync()
    {
        analysisLifetime.Token.ThrowIfCancellationRequested();
        checkpoint ??= await GalleryVision.OpenVisionCheckpointAsync();
        analysisLifetime.Token.ThrowIfCancellationRequested();
        checkpoint.RevocationToken.ThrowIfCancellationRequested();
    }

    private async Task RestoreDraftsAsync()
    {
        if (disposed) return;
        suggestions = checkpoint!.State.Drafts.Where(draft => !draft.TagsOnly).Select(ToSuggestion).ToList();
        tagSuggestions.Clear();
        tagSuggestions.AddRange(checkpoint.State.Drafts.Where(draft => draft.TagsOnly).Select(ToSuggestion));
        await PublishSuggestionsAsync();
        await PublishJobAsync();
    }

    private static OrganizationSuggestion ToSuggestion(VisionDraft draft) =>
        new(draft.Id, draft.Name, draft.Description, draft.Tags, draft.Model, draft.Warning, draft.Analysis, draft.UseExistingTags, draft.SuggestNewTags);

    private bool NeedsVisionAnalysis(ImageRecord image) => VisionQueueRunner.NeedsVisionAnalysis(image, checkpoint?.State);

    private Task AnalyzeUnanalyzedAsync() => RunAsync("Analyzing unanalyzed media...", async () =>
    {
        await EnsureCheckpointAsync();
        // A whole-library run is derived from current records, never a saved queue or UI selection.
        var records = await GalleryCatalog.ListAsync();
        checkpoint!.RevocationToken.ThrowIfCancellationRequested();
        var items = records.Where(NeedsVisionAnalysis).OrderBy(image => image.ImportedAt).ThenBy(image => image.Id, StringComparer.Ordinal)
            .Select(image => new VisionQueueItem(image.Id)).ToArray();
        if (items.Length == 0) { notice = "No unanalyzed media remains."; return; }
        await ProcessAnalysisAsync(items, useSavedQueue: false);
    }, reportJob: true);

    public Task AnalyzeDetailAsync(ImageRecord image) =>
        AnalyzeAsync([image], suggestions, "Analyzing media...");

    private Task AnalyzeAsync(ImageRecord[] selected, List<OrganizationSuggestion> target, string message) =>
        RunAsync(message, async () =>
        {
            if (selected.Length == 0) throw new InvalidOperationException("There is no media to analyze.");
            await EnsureCheckpointAsync();
            var tagsOnly = ReferenceEquals(target, tagSuggestions);
            var queued = selected.Select(image => new VisionQueueItem(image.Id, tagsOnly)).ToArray();
            await checkpoint!.UpdateAsync(state => state with
            {
                Queue = [.. state.Queue.Where(item => !queued.Any(next => next.Id == item.Id && next.TagsOnly == item.TagsOnly)), .. queued]
            });
            await ProcessAnalysisAsync(queued, useSavedQueue: true);
        }, reportJob: true);

    private Task ResumeAnalysisAsync() => RunAsync("Resuming saved analysis...", async () =>
    {
        await EnsureCheckpointAsync();
        await ProcessAnalysisAsync(checkpoint!.State.Queue.ToArray(), useSavedQueue: true);
    }, reportJob: true);

    private Task DiscardQueueAsync() => RunAsync("Discarding unfinished queue...", async () =>
    {
        await EnsureCheckpointAsync();
        await checkpoint!.UpdateAsync(state => state with { Queue = [] });
    });

    private async Task DismissTagSuggestionAsync(OrganizationSuggestion suggestion)
    {
        await EnsureCheckpointAsync();
        await checkpoint!.DismissAsync(suggestion.Id, tagsOnly: true);
        tagSuggestions.RemoveAll(item => item.Id == suggestion.Id);
        await PublishJobAsync();
    }

    private async Task ProcessAnalysisAsync(VisionQueueItem[] items, bool useSavedQueue)
    {
        cancelRequested = false;
        analyzing = true;
        var runCheckpoint = checkpoint!;
        var runner = new VisionQueueRunner(GalleryCatalog, GalleryVision, Logger);
        var result = await runner.RunAsync(runCheckpoint, items, useSavedQueue, analysisLifetime.Token,
            stopRequested: () => cancelRequested,
            approveDraftAsync: draft => ApprovePendingVisionAsync(ToSuggestion(draft), runCheckpoint),
            reportProgressAsync: async (completed, active) =>
            {
                status = $"Analyzing media · {completed} of {items.Length} processed · {active} active";
                await PublishJobAsync();
                await InvokeAsync(StateHasChanged);
            },
            draftsChangedAsync: RestoreDraftsAsync,
            recordsChangedAsync: () => disposed ? Task.CompletedTask : Changed.InvokeAsync());

        var outcome = result.Stopped ? "Stopped" : result.EndpointsExhausted
            ? "No vision endpoints remain available" : "Analysis finished";
        notice = $"{outcome}. {result.Completed} of {items.Length} processed, {result.Accepted} auto-accepted, {result.Failed} failed.";
        error = string.Join(" / ", result.Errors);
    }
}
