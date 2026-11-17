using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components.Workspace;

// The drawer owns the workbench UI. Other features use this session-scoped
// interface to its analysis actions without reaching into the drawer.
public sealed class LibraryAnalysis(GalleryService gallery)
{
    private ILibraryAnalysisActions? actions;
    public LibraryJobStatus Status { get; private set; } = new(false, "", "", 0, false);
    internal event Action? Changed;

    internal void Attach(ILibraryAnalysisActions value) => actions = value;

    internal void Detach(ILibraryAnalysisActions value)
    {
        if (actions == value) actions = null;
    }

    internal void Clear() => actions = null;

    internal void SetStatus(LibraryJobStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }

    internal void EnsureIdle()
    {
        if (Status.IsRunning)
            throw new InvalidOperationException("Wait for the running LLM job to finish, or stop it after the current media item, before changing files or gallery settings.");
    }

    internal Task AnalyzeAsync(ImageRecord image, WorkspaceCommands commands) =>
        commands.RunLibraryChangeAsync("Analyzing with your local vision model...", async () =>
        {
            if (actions is not { } target) return;
            await target.AnalyzeDetailAsync(image);
            if (actions == target && gallery.IsUnlocked)
                commands.Activity.Message = "Review available suggestions in the AI analysis drawer. Unfinished analysis can be resumed there.";
        });

    internal Task UpdateTagsAsync(TagCatalogChange change) =>
        actions?.UpdateSuggestionTagsAsync(change) ?? Task.CompletedTask;

    internal Task<ImageRecord?> ApproveAsync(OrganizationSuggestion suggestion) =>
        actions?.ApproveSuggestionAsync(suggestion) ?? Task.FromResult<ImageRecord?>(null);

    internal Task DismissAsync(OrganizationSuggestion suggestion) =>
        actions?.DismissSuggestionAsync(suggestion) ?? Task.CompletedTask;
}

public interface ILibraryAnalysisActions
{
    Task AnalyzeDetailAsync(ImageRecord image);
    Task UpdateSuggestionTagsAsync(TagCatalogChange change);
    Task<ImageRecord?> ApproveSuggestionAsync(OrganizationSuggestion suggestion);
    Task DismissSuggestionAsync(OrganizationSuggestion suggestion);
}
