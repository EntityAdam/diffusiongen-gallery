using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Components.Features.Tags;

namespace Gallery.Web.Components.Features.Vision;

/// <summary>Owns the analysis job and its drafts for one unlocked workspace.</summary>
public sealed class LibraryAnalysis(IGalleryCatalog galleryCatalog, IGalleryVision galleryVision, ILogger logger) : IDisposable
{
    private readonly CancellationTokenSource analysisLifetime = new();
    private volatile bool disposed;
    private Task<VisionCheckpointSession>? openingCheckpoint;
    private VisionCheckpointSession? checkpoint;
    private bool working;
    private bool analyzing;
    private volatile bool cancelRequested;
    private bool reportingJob;
    private string jobWorkflow = "";
    private string jobMessage = "";
    private string status = "";
    private string notice = "";
    private string error = "";

    public LibraryJobStatus Status => new(working && reportingJob, jobWorkflow, working && reportingJob ? status : jobMessage,
        Suggestions.Count + TagSuggestions.Count, analyzing && !cancelRequested);
    internal bool IsBusy => working;
    internal bool IsAnalyzing => analyzing;
    internal string Progress => status;
    internal IReadOnlyList<OrganizationSuggestion> Suggestions { get; private set; } = [];
    internal IReadOnlyList<OrganizationSuggestion> TagSuggestions { get; private set; } = [];
    internal event Action? Changed;
    internal event Action<string, string>? Feedback;
    internal event Func<IReadOnlyList<OrganizationSuggestion>, Task>? SuggestionsChanged;
    internal event Func<Task>? RecordsChanged;

    internal Task InitializeAsync() => RunAsync("Loading saved drafts...", async () =>
    {
        var current = await GetCheckpointAsync();
        await RestoreDraftsAsync(current);
    });

    internal void EnsureIdle()
    {
        if (Status.IsRunning)
            throw new InvalidOperationException("Wait for the running LLM job to finish, or stop it after the current media item, before changing files or gallery settings.");
    }

    internal void StopAnalysis()
    {
        if (!analyzing) return;
        cancelRequested = true;
        status = "Stopping after active requests finish...";
        NotifyChanged();
    }

    private async Task RunAsync(string message, Func<Task> action, bool reportJob = false, string workflow = "intelligence")
    {
        if (disposed || working) return;
        working = true;
        status = message;
        error = notice = "";
        reportingJob = reportJob;
        if (reportJob) jobWorkflow = workflow;
        try
        {
            NotifyChanged();
            await action();
        }
        catch (Exception) when (disposed || checkpoint?.RevocationToken.IsCancellationRequested == true) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Library analysis operation failed");
            error = exception is InvalidOperationException or IOException or System.Text.Json.JsonException
                ? exception.Message : "The operation failed. Check the application console.";
        }
        finally
        {
            if (reportJob) jobMessage = error.Length > 0 ? "Operation reported errors. Open workflow for details." : notice;
            working = analyzing = reportingJob = false;
            if (!disposed && checkpoint?.RevocationToken.IsCancellationRequested != true && (error.Length > 0 || notice.Length > 0))
                Feedback?.Invoke(error, notice);
            NotifyChanged();
        }
    }

    private void NotifyChanged()
    {
        if (!disposed) Changed?.Invoke();
    }

    private Task NotifyRecordsChangedAsync() => disposed ? Task.CompletedTask : RecordsChanged?.Invoke() ?? Task.CompletedTask;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        analysisLifetime.Cancel();
        analysisLifetime.Dispose();
        checkpoint = null;
        openingCheckpoint = null;
        Suggestions = [];
        TagSuggestions = [];
        Changed = null;
        Feedback = null;
        SuggestionsChanged = null;
        RecordsChanged = null;
    }

    internal async Task UpdateTagsAsync(TagCatalogChange change)
    {
        if (change.PreviousName is null) return;
        var current = await GetCheckpointAsync();
        await current.UpdateDraftTagsAsync(change.PreviousName, change.Name);
        await RestoreDraftsAsync(current);
    }

    internal async Task DismissAsync(OrganizationSuggestion suggestion)
    {
        var current = await GetCheckpointAsync();
        await current.DismissDraftAsync(ToDraft(suggestion));
        if (!working) jobMessage = $"{current.State.Drafts.Count(draft => !draft.TagsOnly)} suggestions await approval.";
        await RestoreDraftsAsync(current);
    }

    internal Task ApplyTagSuggestionAsync(OrganizationSuggestion suggestion) => RunAsync("Saving approved tags...", async () =>
    {
        var current = await GetCheckpointAsync();
        if (await current.ApproveDraftAsync(ToDraft(suggestion, tagsOnly: true)) is null) return;
        await RestoreDraftsAsync(current);
        await NotifyRecordsChangedAsync();
    });

    // Approval runs outside RunAsync so suggestions can be reviewed while a Play analysis is still running.
    internal async Task<ImageRecord?> ApproveAsync(OrganizationSuggestion suggestion)
    {
        var current = await GetCheckpointAsync();
        var imageRecord = await current.ApproveDraftAsync(ToDraft(suggestion));
        if (imageRecord is null) return null;
        if (!working) jobMessage = $"{current.State.Drafts.Count(draft => !draft.TagsOnly)} suggestions await approval.";
        await RestoreDraftsAsync(current);
        await NotifyRecordsChangedAsync();
        return imageRecord;
    }

    internal Task AcceptAllVisionAsync() => RunAsync("Accepting pending analysis...", async () =>
    {
        var current = await GetCheckpointAsync();
        var pending = current.State.Drafts.Where(draft => !draft.TagsOnly).ToArray();
        var accepted = 0;
        var failures = new List<string>();
        try
        {
            foreach (var draft in pending)
            {
                current.RevocationToken.ThrowIfCancellationRequested();
                status = $"Accepting {accepted + failures.Count + 1} / {pending.Length}: {draft.Name}";
                NotifyChanged();
                try
                {
                    if (await current.ApproveDraftAsync(draft) is not null) accepted++;
                }
                catch (Exception) when (current.RevocationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Bulk vision approval failed for {ImageId}", draft.Id);
                    failures.Add($"{draft.Name}: {exception.Message}");
                }
            }
            notice = $"Accepted {accepted} suggestion(s). {failures.Count} could not be accepted and remain pending.";
            error = string.Join(" / ", failures);
        }
        finally
        {
            if (!disposed && !current.RevocationToken.IsCancellationRequested)
            {
                await RestoreDraftsAsync(current);
                if (pending.Length > 0) await NotifyRecordsChangedAsync();
            }
        }
    }, reportJob: true);

    internal Task DiscardAllVisionAsync() => RunAsync("Discarding pending analysis...", async () =>
    {
        var current = await GetCheckpointAsync();
        var count = await current.DiscardFullDraftsAsync();
        await RestoreDraftsAsync(current);
        notice = $"Discarded {count} pending suggestion(s). Saved analysis is unchanged.";
    });

    internal int PendingAnalysisCount => checkpoint?.State.Queue.Length ?? 0;

    private async Task<VisionCheckpointSession> GetCheckpointAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (checkpoint is { } existing)
        {
            existing.RevocationToken.ThrowIfCancellationRequested();
            return existing;
        }

        var opening = openingCheckpoint ??= galleryVision.OpenVisionCheckpointAsync();
        try
        {
            var current = await opening;
            ObjectDisposedException.ThrowIf(disposed, this);
            current.RevocationToken.ThrowIfCancellationRequested();
            return checkpoint = current;
        }
        finally
        {
            if (openingCheckpoint == opening) openingCheckpoint = null;
        }
    }

    private async Task RestoreDraftsAsync(VisionCheckpointSession current)
    {
        if (disposed || current.RevocationToken.IsCancellationRequested) return;
        var drafts = current.State.Drafts;
        Suggestions = drafts.Where(draft => !draft.TagsOnly).Select(ToSuggestion).ToArray();
        TagSuggestions = drafts.Where(draft => draft.TagsOnly).Select(ToSuggestion).ToArray();
        if (SuggestionsChanged is { } notify) await notify(Suggestions);
        NotifyChanged();
    }

    private static OrganizationSuggestion ToSuggestion(VisionDraft draft) =>
        new(draft.Id, draft.Name, draft.Description, draft.Tags, draft.Model, draft.Warning, draft.Analysis, draft.UseExistingTags, draft.SuggestNewTags);

    private static VisionDraft ToDraft(OrganizationSuggestion suggestion, bool tagsOnly = false) =>
        new(suggestion.Id, suggestion.Name, suggestion.Description, suggestion.Tags, suggestion.Model, suggestion.Warning,
            tagsOnly, suggestion.Analysis, suggestion.UseExistingTags, suggestion.SuggestNewTags);

    internal bool NeedsVisionAnalysis(ImageRecord image) => VisionQueueRunner.NeedsVisionAnalysis(image, checkpoint?.State);

    internal Task AnalyzeUnanalyzedAsync() => RunAsync("Analyzing unanalyzed media...", async () =>
    {
        var current = await GetCheckpointAsync();
        // A whole-library run is derived from current records, never a saved queue or UI selection.
        var records = await galleryCatalog.ListAsync();
        current.RevocationToken.ThrowIfCancellationRequested();
        var items = records.Where(NeedsVisionAnalysis).OrderBy(image => image.ImportedAt).ThenBy(image => image.Id, StringComparer.Ordinal)
            .Select(image => new VisionQueueItem(image.Id)).ToArray();
        if (items.Length == 0) { notice = "No unanalyzed media remains."; return; }
        await ProcessAnalysisAsync(current, items, useSavedQueue: false);
    }, reportJob: true);

    internal Task AnalyzeDetailAsync(ImageRecord image) =>
        AnalyzeAsync([image], tagsOnly: false, "Analyzing media...", "intelligence");

    internal Task AnalyzeSelectionAsync(ImageRecord[] selected, bool tagsOnly, string workflow = "intelligence") =>
        AnalyzeAsync(selected, tagsOnly, "Analyzing selection...", workflow);

    private Task AnalyzeAsync(ImageRecord[] selected, bool tagsOnly, string message, string workflow) =>
        RunAsync(message, async () =>
        {
            if (selected.Length == 0) throw new InvalidOperationException("There is no media to analyze.");
            var current = await GetCheckpointAsync();
            var queued = selected.Select(image => new VisionQueueItem(image.Id, tagsOnly)).ToArray();
            await current.UpdateAsync(state => state with
            {
                Queue = [.. state.Queue.Where(item => !queued.Any(next => next.Id == item.Id && next.TagsOnly == item.TagsOnly)), .. queued]
            });
            await ProcessAnalysisAsync(current, queued, useSavedQueue: true);
        }, reportJob: true, workflow);

    internal Task ResumeAnalysisAsync(string workflow = "intelligence") => RunAsync("Resuming saved analysis...", async () =>
    {
        var current = await GetCheckpointAsync();
        await ProcessAnalysisAsync(current, current.State.Queue.ToArray(), useSavedQueue: true);
    }, reportJob: true, workflow);

    internal Task DiscardQueueAsync() => RunAsync("Discarding unfinished queue...", async () =>
    {
        var current = await GetCheckpointAsync();
        await current.UpdateAsync(state => state with { Queue = [] });
    });

    internal async Task DismissTagSuggestionAsync(OrganizationSuggestion suggestion)
    {
        var current = await GetCheckpointAsync();
        await current.DismissDraftAsync(ToDraft(suggestion, tagsOnly: true));
        await RestoreDraftsAsync(current);
    }

    private async Task ProcessAnalysisAsync(VisionCheckpointSession current, VisionQueueItem[] items, bool useSavedQueue)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancelRequested = false;
        analyzing = true;
        var runner = new VisionQueueRunner(galleryCatalog, galleryVision, logger);
        var result = await runner.RunAsync(current, items, useSavedQueue, analysisLifetime.Token,
            stopRequested: () => cancelRequested,
            reportProgressAsync: (completed, active) =>
            {
                status = $"Analyzing media · {completed} of {items.Length} processed · {active} active";
                NotifyChanged();
                return Task.CompletedTask;
            },
            draftsChangedAsync: () => RestoreDraftsAsync(current),
            recordsChangedAsync: NotifyRecordsChangedAsync);

        var outcome = result.Stopped ? "Stopped" : result.EndpointsExhausted
            ? "No vision endpoints remain available" : "Analysis finished";
        notice = $"{outcome}. {result.Completed} of {items.Length} processed, {result.Accepted} auto-accepted, {result.Failed} failed.";
        error = string.Join(" / ", result.Errors);
    }
}
