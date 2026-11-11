using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components;

public partial class LibraryWorkbench
{
    private VisionCheckpointSession? checkpoint;
    private readonly CancellationTokenSource analysisLifetime = new();
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
        new(draft.Id, draft.Name, draft.Description, draft.Tags, draft.Collection, draft.Model, draft.Warning, draft.Analysis, draft.UseExistingTags, draft.SuggestNewTags);

    public Task AnalyzeDetailAsync(ImageRecord image) =>
        AnalyzeAsync([image], suggestions, "Analyzing media...", includeCollection: false);

    private Task AnalyzeAsync(ImageRecord[] selected, List<OrganizationSuggestion> target, string message, bool includeCollection = true) =>
        RunAsync(message, async () =>
        {
            if (selected.Length == 0) throw new InvalidOperationException("There is no media to analyze.");
            await EnsureCheckpointAsync();
            var tagsOnly = ReferenceEquals(target, tagSuggestions);
            var queued = selected.Select(image => new VisionQueueItem(image.Id, tagsOnly, includeCollection)).ToArray();
            await checkpoint!.UpdateAsync(state => state with
            {
                Queue = [.. state.Queue.Where(item => !queued.Any(next => next.Id == item.Id && next.TagsOnly == item.TagsOnly)), .. queued]
            });
            await ProcessQueueAsync(queued);
        }, reportJob: true);

    private Task ResumeAnalysisAsync() => RunAsync("Resuming saved analysis...", async () =>
    {
        await EnsureCheckpointAsync();
        await ProcessQueueAsync(checkpoint!.State.Queue.ToArray());
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

    private async Task ProcessQueueAsync(VisionQueueItem[] queue)
    {
        cancelRequested = false;
        analyzing = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(analysisLifetime.Token, checkpoint!.RevocationToken);
        var token = cancellation.Token;
        token.ThrowIfCancellationRequested();
        var settings = await Gallery.SettingsAsync();
        token.ThrowIfCancellationRequested();
        // Read the authoritative catalog: the UI can still be loading pages after unlock.
        var records = (await Gallery.ListAsync()).ToDictionary(image => image.Id);
        var failures = new List<string>();
        var completed = 0;
        foreach (var item in queue)
        {
            token.ThrowIfCancellationRequested();
            if (cancelRequested) break;
            // A user may discard a queue while another UI callback is completing.
            if (!checkpoint.State.Queue.Any(pending => pending.Id == item.Id && pending.TagsOnly == item.TagsOnly)) continue;
            if (!records.TryGetValue(item.Id, out var image) || image.IsMarkedForDeletion)
            {
                await checkpoint.UpdateAsync(state => state with { Queue = [.. state.Queue.Where(pending => pending != item)] });
                continue;
            }
            status = $"Analyzing {completed + 1} / {queue.Length}: {image.Name}";
            await PublishJobAsync();
            await InvokeAsync(StateHasChanged);
            VisionDraft draft;
            try
            {
                token.ThrowIfCancellationRequested();
                var result = await Gallery.AnalyzeForDraftAsync(image.Id, settings, item.IncludeCollection, Collections.ToArray(), token);
                draft = new(image.Id, image.Name, result.Description, result.Tags, result.Collection, settings.Model,
                    result.Warning, item.TagsOnly, result.Analysis, settings.UseExistingTags, settings.SuggestNewTags);
                token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) when (token.IsCancellationRequested) { token.ThrowIfCancellationRequested(); throw; }
            catch (Exception exception)
            {
                Logger.LogError(exception, "Local analysis failed for {ImageId}", image.Id);
                failures.Add($"{image.Name}: {exception.Message}");
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
            await RestoreDraftsAsync();
            completed++;
        }
        notice = $"{(cancelRequested ? "Stopped" : "Analysis finished")}: {completed} / {queue.Length} processed; {failures.Count} failed. Completed suggestions saved as encrypted drafts.";
        error = string.Join(" / ", failures);
    }
}
