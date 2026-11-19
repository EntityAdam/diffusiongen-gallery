using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Web.Components.Workspace;

namespace Gallery.Web.Components.Features.Media.Removal;

public sealed class RemovalWorkflowState(
    IGalleryVault vault, IGalleryMediaMaintenance mediaMaintenance, WorkspaceCommands commands, ILogger logger, Func<Task> completed) : IDisposable
{
    public IReadOnlyList<RemovalItem> Plan { get; private set; } = [];
    public IReadOnlyList<RemovalResult> Results => results;
    public bool IsOpen => Plan.Count > 0;
    private List<RemovalResult> results = [];
    private bool disposed;
    private long generation;

    public Task PrepareAsync(IEnumerable<string> ids)
    {
        if (disposed) return Task.CompletedTask;
        var selected = ids.ToArray();
        return commands.RunLibraryChangeAsync("Checking originals...", async () =>
        {
            if (disposed || !vault.IsUnlocked) return;
            var revision = ++generation;
            var plan = await mediaMaintenance.PrepareRemovalAsync(selected);
            if (disposed || revision != generation || !vault.IsUnlocked) return;
            Plan = plan;
            results.Clear();
        });
    }

    public void Cancel()
    {
        if (commands.Activity.Busy) return;
        generation++;
        Plan = [];
    }

    public Task ExecuteAsync((ExistingOriginalAction Existing, MissingOriginalAction Missing, string Folder) choices)
    {
        if (disposed || !IsOpen) return Task.CompletedTask;
        return commands.RunLibraryChangeAsync("Removing marked media...", async () =>
        {
            if (disposed || !vault.IsUnlocked) return;
            var plan = Plan;
            var currentResults = new List<RemovalResult>();
            results = currentResults;
            try
            {
                await mediaMaintenance.RemoveAsync(plan, choices.Existing, choices.Missing, choices.Folder, currentResults.Add);
            }
            finally
            {
                foreach (var result in currentResults.Where(result => result.Error is not null))
                    logger.LogWarning("Media removal issue for {ImageId}: {Error}", result.Id, result.Error);
                Plan = [];
                if (!disposed && vault.IsUnlocked) await completed();
            }
            if (!disposed)
                commands.Activity.Message = $"Removed {currentResults.Count(result => result.WasRemoved)} media item(s). {currentResults.Count(result => result.Error is not null)} issue(s); review the removal results.";
        });
    }

    public void Dispose()
    {
        disposed = true;
        generation++;
        Plan = [];
        results = [];
    }
}
