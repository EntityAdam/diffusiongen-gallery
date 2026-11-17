namespace Gallery.Web.Components.Workspace;

// Shared operations run through the session so busy state and feedback are
// rendered even when a nested form initiates the operation.
public sealed class WorkspaceCommands(
    GalleryActivity activity, Func<string, Func<Task>, Task> execute, Action ensureNoLibraryJob)
{
    public GalleryActivity Activity { get; } = activity;

    public Task RunAsync(string status, Func<Task> action) => execute(status, action);

    public Task RunLibraryChangeAsync(string status, Func<Task> action) =>
        RunAsync(status, () =>
        {
            ensureNoLibraryJob();
            return action();
        });
}
