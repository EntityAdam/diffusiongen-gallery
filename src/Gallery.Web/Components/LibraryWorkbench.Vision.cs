using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components;

public partial class LibraryWorkbench
{
    private VisionCheckpointSession? checkpoint;
    private readonly CancellationTokenSource analysisLifetime = new();
    private readonly SemaphoreSlim visionApprovalGate = new(1, 1);
    private bool AutoAcceptVisionAnalysis => checkpoint?.State.AutoAcceptVisionAnalysis == true;

    private Task SetAutoAcceptVisionAsync(bool enabled) => RunAsync("Saving analysis approval preference...", async () =>
    {
        await EnsureCheckpointAsync();
        await checkpoint!.UpdateAsync(state => state with { AutoAcceptVisionAnalysis = enabled });
    });

    private async Task<ImageRecord?> ApprovePendingVisionAsync(OrganizationSuggestion suggestion)
    {
        await visionApprovalGate.WaitAsync(checkpoint!.RevocationToken);
        try
        {
            var draft = checkpoint.State.Drafts.FirstOrDefault(item => item.Id == suggestion.Id && !item.TagsOnly);
            if (draft is null) return null;
            if (ToSuggestion(draft) != suggestion)
                throw new InvalidOperationException("This suggestion changed. Refresh it before accepting.");
            checkpoint.RevocationToken.ThrowIfCancellationRequested();
            var approved = await PersistApprovedSuggestionAsync(Gallery, suggestion);
            // A replacement draft staged during approval must remain available for review.
            await checkpoint.UpdateAsync(state => state with { Drafts = [.. state.Drafts.Where(item => item != draft)] });
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
                    if (await ApprovePendingVisionAsync(ToSuggestion(draft)) is not null) accepted++;
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
        checkpoint ??= await Gallery.OpenVisionCheckpointAsync();
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

    private bool NeedsVisionAnalysis(ImageRecord image) => !image.IsMarkedForDeletion
        && string.IsNullOrWhiteSpace(image.Description) && string.IsNullOrWhiteSpace(image.AnalysisModel)
        && image.VisionFacts is null && !(checkpoint?.State.Drafts.Any(draft => draft.Id == image.Id && !draft.TagsOnly) ?? false);

    private Task AnalyzeUnanalyzedAsync() => RunAsync("Analyzing unanalyzed media...", async () =>
    {
        await EnsureCheckpointAsync();
        // A whole-library run is derived from current records, never a saved queue or UI selection.
        var records = await Gallery.ListAsync();
        checkpoint!.RevocationToken.ThrowIfCancellationRequested();
        var items = records.Where(NeedsVisionAnalysis).OrderBy(image => image.ImportedAt).ThenBy(image => image.Id, StringComparer.Ordinal)
            .Select(image => new VisionQueueItem(image.Id)).ToArray();
        if (items.Length == 0) { notice = "No unanalyzed media remains."; return; }
        await ProcessAnalysisAsync(items, useSavedQueue: false, records);
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

    private async Task ProcessAnalysisAsync(VisionQueueItem[] items, bool useSavedQueue, IReadOnlyList<ImageRecord>? catalog = null)
    {
        cancelRequested = false;
        analyzing = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(analysisLifetime.Token, checkpoint!.RevocationToken);
        var token = cancellation.Token;
        token.ThrowIfCancellationRequested();
        var settings = await Gallery.SettingsAsync();
        token.ThrowIfCancellationRequested();
        // Read the authoritative catalog: the UI can still be loading pages after unlock.
        var records = (catalog ?? await Gallery.ListAsync()).ToDictionary(image => image.Id);
        var failures = new List<string>();
        var completed = 0;
        var accepted = 0;
        try
        {
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                if (cancelRequested) break;
                // A user may discard a queue while another UI callback is completing.
                if (useSavedQueue && !checkpoint.State.Queue.Any(pending => pending.Id == item.Id && pending.TagsOnly == item.TagsOnly)) continue;
                if (!useSavedQueue)
                {
                    var current = (await Gallery.ReloadRecordsAsync([item.Id], token)).SingleOrDefault();
                    if (current is null || !NeedsVisionAnalysis(current)) continue;
                    records[item.Id] = current;
                }
                if (!records.TryGetValue(item.Id, out var image) || image.IsMarkedForDeletion)
                {
                    if (useSavedQueue)
                        await checkpoint.UpdateAsync(state => state with { Queue = [.. state.Queue.Where(pending => pending != item)] });
                    continue;
                }
                status = $"Analyzing {completed + 1} / {items.Length}: {image.Name}";
                await PublishJobAsync();
                await InvokeAsync(StateHasChanged);
                VisionDraft draft;
                try
                {
                    token.ThrowIfCancellationRequested();
                    var result = await Gallery.AnalyzeForDraftAsync(image.Id, settings, token);
                    draft = new(image.Id, image.Name, result.Description, result.Tags, settings.Model,
                        result.Warning, item.TagsOnly, result.Analysis, settings.UseExistingTags, settings.SuggestNewTags);
                    token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) when (token.IsCancellationRequested) { token.ThrowIfCancellationRequested(); throw; }
                catch (Exception exception)
                {
                    Logger.LogError(exception, "Local analysis failed for {ImageId}", image.Id);
                    failures.Add($"{image.Name}: {exception.Message}");
                    if (useSavedQueue)
                        await checkpoint.UpdateAsync(state => state with
                        {
                            Queue = [.. state.Queue.Select(pending => pending.Id == item.Id && pending.TagsOnly == item.TagsOnly ? pending with { Failed = true } : pending)]
                        });
                    completed++;
                    continue;
                }
                // Checkpoint failures stop the run; never report an unpersisted result as completed.
                await checkpoint.StageAsync(draft);
                token.ThrowIfCancellationRequested();
                if (AutoAcceptVisionAnalysis && !draft.TagsOnly)
                {
                    try
                    {
                        if (await ApprovePendingVisionAsync(ToSuggestion(draft)) is not null) accepted++;
                    }
                    catch (Exception) when (token.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        Logger.LogError(exception, "Automatic vision approval failed for {ImageId}", draft.Id);
                        failures.Add($"{image.Name}: automatic acceptance failed; suggestion retained for review. {exception.Message}");
                    }
                }
                await RestoreDraftsAsync();
                completed++;
            }
        }
        finally
        {
            if (accepted > 0 && !disposed && !token.IsCancellationRequested) await Changed.InvokeAsync();
        }
        notice = $"{(cancelRequested ? "Stopped" : "Analysis finished")}: {completed} / {items.Length} processed; {accepted} auto-accepted; {failures.Count} failed. Other completed suggestions are saved as encrypted drafts.";
        error = string.Join(" / ", failures);
    }
}
