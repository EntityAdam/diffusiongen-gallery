using Gallery.Core.Models;
using Gallery.Core.Services;
using System.Net;

namespace Gallery.Web.Components;

public partial class LibraryWorkbench
{
    private VisionCheckpointSession? checkpoint;
    private readonly CancellationTokenSource analysisLifetime = new();
    private readonly SemaphoreSlim visionApprovalGate = new(1, 1);
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
            var approved = await PersistApprovedSuggestionAsync(GalleryVision, suggestion);
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

    private bool NeedsVisionAnalysis(ImageRecord image) => !image.IsMarkedForDeletion
        && string.IsNullOrWhiteSpace(image.Description) && string.IsNullOrWhiteSpace(image.AnalysisModel)
        && image.VisionFacts is null && !(checkpoint?.State.Drafts.Any(draft => draft.Id == image.Id && !draft.TagsOnly) ?? false);

    private Task AnalyzeUnanalyzedAsync() => RunAsync("Analyzing unanalyzed media...", async () =>
    {
        await EnsureCheckpointAsync();
        // A whole-library run is derived from current records, never a saved queue or UI selection.
        var records = await GalleryCatalog.ListAsync();
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
        var runCheckpoint = checkpoint!;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(analysisLifetime.Token, runCheckpoint.RevocationToken);
        var token = cancellation.Token;
        var settings = await GalleryVision.LoadSettingsAsync();
        token.ThrowIfCancellationRequested();
        var availableLanes = new LinkedList<VisionEndpointLane>(settings.GetLanes(runCheckpoint.State.AutoAcceptVisionAnalysis)
            .Where(lane => lane.Enabled));
        if (availableLanes.Count == 0)
            throw new InvalidOperationException("Enable a vision endpoint in LLM settings before starting analysis.");

        var activeRequests = availableLanes.ToDictionary(lane => lane, _ => 0);
        var records = (catalog ?? await GalleryCatalog.ListAsync()).ToDictionary(image => image.Id);
        var pending = new LinkedList<VisionQueueItem>(items);
        var inFlight = new Dictionary<Task<VisionAnalysisResult>, (VisionEndpointLane Lane, VisionQueueItem Item, ImageRecord Image)>();
        var activeIds = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<string>();
        var laneFailures = new List<string>();
        var completed = 0;
        var accepted = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                while (!cancelRequested && availableLanes.First is { } available)
                {
                    // A saved queue can contain both full and tag-only work for one image.
                    // Wait for its current result to be checkpointed before starting the other mode.
                    var next = pending.First;
                    while (next is not null && activeIds.Contains(next.Value.Id)) next = next.Next;
                    if (next is null) break;
                    var item = next.Value;
                    pending.Remove(next);

                    if (useSavedQueue && !runCheckpoint.State.Queue.Any(entry => entry.Id == item.Id && entry.TagsOnly == item.TagsOnly))
                        continue;
                    var image = useSavedQueue
                        ? records.GetValueOrDefault(item.Id)
                        : (await GalleryCatalog.ReloadRecordsAsync([item.Id], token)).SingleOrDefault();
                    if (!useSavedQueue && (image is null || !NeedsVisionAnalysis(image))) continue;
                    if (image is null || image.IsMarkedForDeletion)
                    {
                        await runCheckpoint.UpdateAsync(state => state with { Queue = [.. state.Queue.Where(entry => entry != item)] });
                        continue;
                    }

                    token.ThrowIfCancellationRequested();
                    if (cancelRequested) break;
                    var lane = available.Value;
                    availableLanes.RemoveFirst();
                    if (++activeRequests[lane] < lane.MaxConcurrentRequests)
                        availableLanes.AddLast(lane);
                    var request = GalleryVision.AnalyzeForDraftAsync(image.Id, settings.ForLane(lane), token);
                    inFlight.Add(request, (lane, item, image));
                    activeIds.Add(item.Id);
                }

                if (inFlight.Count == 0) break;
                if (!cancelRequested)
                {
                    status = $"Analyzing media · {completed} of {items.Length} processed · {inFlight.Count} active";
                    await PublishJobAsync();
                    await InvokeAsync(StateHasChanged);
                }

                var finished = await Task.WhenAny(inFlight.Keys);
                var (finishedLane, finishedItem, finishedImage) = inFlight[finished];
                inFlight.Remove(finished);
                activeIds.Remove(finishedItem.Id);
                if (activeRequests.TryGetValue(finishedLane, out var activeCount))
                {
                    activeRequests[finishedLane] = activeCount - 1;
                    if (activeCount == finishedLane.MaxConcurrentRequests)
                        availableLanes.AddLast(finishedLane);
                }
                VisionAnalysisResult result;
                try
                {
                    result = await finished;
                    token.ThrowIfCancellationRequested();
                }
                catch (Exception exception) when (!token.IsCancellationRequested)
                {
                    Logger.LogError(exception, "Vision endpoint {EndpointName} failed for {ImageId}", finishedLane.Name, finishedImage.Id);
                    if (IsVisionEndpointUnavailable(exception))
                    {
                        if (activeRequests.Remove(finishedLane))
                        {
                            availableLanes.Remove(finishedLane);
                            laneFailures.Add($"{finishedLane.Name} is unavailable for this run. {exception.Message}");
                        }
                        pending.AddFirst(finishedItem);
                    }
                    else
                    {
                        failures.Add($"{finishedImage.Name} ({finishedLane.Name}): {exception.Message}");
                        if (useSavedQueue)
                            await runCheckpoint.UpdateAsync(state => state with
                            {
                                Queue = [.. state.Queue.Select(entry => entry.Id == finishedItem.Id && entry.TagsOnly == finishedItem.TagsOnly
                                    ? entry with { Failed = true } : entry)]
                            });
                        completed++;
                    }
                    continue;
                }

                var draft = new VisionDraft(finishedImage.Id, finishedImage.Name, result.Description, result.Tags, finishedLane.Model,
                    result.Warning, finishedItem.TagsOnly, result.Analysis, settings.UseExistingTags, settings.SuggestNewTags);
                // Keep persistence and approval serial, even when several results arrive together.
                await runCheckpoint.StageAsync(draft);
                token.ThrowIfCancellationRequested();
                if (finishedLane.AutoAccept && !draft.TagsOnly)
                {
                    try
                    {
                        if (await ApprovePendingVisionAsync(ToSuggestion(draft)) is not null) accepted++;
                    }
                    catch (Exception exception) when (!token.IsCancellationRequested)
                    {
                        Logger.LogError(exception, "Automatic vision approval failed for {ImageId}", draft.Id);
                        failures.Add($"{finishedImage.Name} ({finishedLane.Name}): automatic acceptance failed; suggestion retained for review. {exception.Message}");
                    }
                }
                await RestoreDraftsAsync();
                completed++;
            }
        }
        finally
        {
            if (inFlight.Count > 0)
            {
                // A fatal checkpoint error or revocation ends the run and cancels outstanding work.
                await cancellation.CancelAsync();
                try { await Task.WhenAll(inFlight.Keys); }
                catch (Exception) when (token.IsCancellationRequested) { }
            }
            if (accepted > 0 && !disposed && !runCheckpoint.RevocationToken.IsCancellationRequested)
                await Changed.InvokeAsync();
        }

        var outcome = cancelRequested ? "Stopped" : activeRequests.Count == 0 ? "No vision endpoints remain available" : "Analysis finished";
        notice = $"{outcome}. {completed} of {items.Length} processed, {accepted} auto-accepted, {failures.Count} failed.";
        error = string.Join(" / ", laneFailures.Concat(failures));
    }

    private static bool IsVisionEndpointUnavailable(Exception exception) =>
        exception is HttpRequestException { StatusCode: null or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
        || exception.InnerException is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };
}
