using Microsoft.AspNetCore.Components;

namespace Gallery.Web.Components;

// Commands run on the workspace's event boundary so shared busy state and feedback
// are rendered even when a nested form initiates the operation.
public sealed class WorkspaceCommands(
    GalleryActivity activity,
    EventCallback<(string Status, Func<Task> Action)> execute,
    Action ensureNoLibraryJob)
{
    public GalleryActivity Activity { get; } = activity;

    public Task RunAsync(string status, Func<Task> action) => execute.InvokeAsync((status, action));

    public Task RunLibraryChangeAsync(string status, Func<Task> action) =>
        RunAsync(status, () =>
        {
            ensureNoLibraryJob();
            return action();
        });
}
