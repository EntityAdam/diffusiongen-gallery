using System.Net;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components.Features.Vision;

internal sealed class VisionQueueRunner(IGalleryCatalog galleryCatalog, IGalleryVision galleryVision, ILogger logger)
{
    internal sealed record Result(bool Stopped, bool EndpointsExhausted, int Completed, int Accepted, int Failed,
        IReadOnlyList<string> Errors);

    internal static bool NeedsVisionAnalysis(ImageRecord image, VisionCheckpoint? checkpoint) => !image.IsMarkedForDeletion
        && string.IsNullOrWhiteSpace(image.Description) && string.IsNullOrWhiteSpace(image.AnalysisModel)
        && image.VisionFacts is null && !(checkpoint?.Drafts.Any(draft => draft.Id == image.Id && !draft.TagsOnly) ?? false);

    public async Task<Result> RunAsync(VisionCheckpointSession checkpoint, VisionQueueItem[] items, bool useSavedQueue,
        CancellationToken lifetime, Func<bool> stopRequested,
        Func<int, int, Task> reportProgressAsync, Func<Task> draftsChangedAsync, Func<Task> recordsChangedAsync)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime, checkpoint.RevocationToken);
        var token = cancellation.Token;
        var settings = await galleryVision.LoadSettingsAsync();
        token.ThrowIfCancellationRequested();
        var availableLanes = new LinkedList<VisionEndpointLane>(settings.GetLanes(checkpoint.State.AutoAcceptVisionAnalysis)
            .Where(lane => lane.Enabled));
        if (availableLanes.Count == 0)
            throw new InvalidOperationException("Enable a vision endpoint in LLM settings before starting analysis.");

        var activeRequests = availableLanes.ToDictionary(lane => lane, _ => 0);
        var records = useSavedQueue ? (await galleryCatalog.ListAsync()).ToDictionary(image => image.Id) : null;
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
                while (!stopRequested() && availableLanes.First is { } available)
                {
                    // A saved queue can contain both full and tag-only work for one image.
                    // Wait for its current result to be checkpointed before starting the other mode.
                    var next = pending.First;
                    while (next is not null && activeIds.Contains(next.Value.Id)) next = next.Next;
                    if (next is null) break;
                    var item = next.Value;
                    pending.Remove(next);

                    if (useSavedQueue && !checkpoint.State.Queue.Any(entry => entry.Id == item.Id && entry.TagsOnly == item.TagsOnly))
                        continue;
                    var image = useSavedQueue
                        ? records!.GetValueOrDefault(item.Id)
                        : (await galleryCatalog.ReloadRecordsAsync([item.Id], token)).SingleOrDefault();
                    if (!useSavedQueue && (image is null || !NeedsVisionAnalysis(image, checkpoint.State))) continue;
                    if (image is null || image.IsMarkedForDeletion)
                    {
                        await checkpoint.UpdateAsync(state => state with { Queue = [.. state.Queue.Where(entry => entry != item)] });
                        continue;
                    }

                    token.ThrowIfCancellationRequested();
                    if (stopRequested()) break;
                    var lane = available.Value;
                    availableLanes.RemoveFirst();
                    if (++activeRequests[lane] < lane.MaxConcurrentRequests)
                        availableLanes.AddLast(lane);
                    var request = galleryVision.AnalyzeForDraftAsync(image.Id, settings.ForLane(lane), token);
                    inFlight.Add(request, (lane, item, image));
                    activeIds.Add(item.Id);
                }

                if (inFlight.Count == 0) break;
                if (!stopRequested()) await reportProgressAsync(completed, inFlight.Count);

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
                    logger.LogError(exception, "Vision endpoint {EndpointName} failed for {ImageId}", finishedLane.Name, finishedImage.Id);
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
                            await checkpoint.UpdateAsync(state => state with
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
                await checkpoint.StageAsync(draft);
                token.ThrowIfCancellationRequested();
                if (finishedLane.AutoAccept && !draft.TagsOnly)
                {
                    try
                    {
                        if (await checkpoint.ApproveDraftAsync(draft) is not null) accepted++;
                    }
                    catch (Exception exception) when (!token.IsCancellationRequested)
                    {
                        logger.LogError(exception, "Automatic vision approval failed for {ImageId}", draft.Id);
                        failures.Add($"{finishedImage.Name} ({finishedLane.Name}): automatic acceptance failed; suggestion retained for review. {exception.Message}");
                    }
                }
                await draftsChangedAsync();
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
            if (accepted > 0 && !checkpoint.RevocationToken.IsCancellationRequested)
                await recordsChangedAsync();
        }

        return new Result(stopRequested(), activeRequests.Count == 0, completed, accepted, failures.Count,
            laneFailures.Concat(failures).ToArray());
    }

    private static bool IsVisionEndpointUnavailable(Exception exception) =>
        exception is HttpRequestException { StatusCode: null or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
        || exception.InnerException is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };
}
