namespace Gallery.Web.Components.Workspace;

// A feature supplies its own validation and persistence; the guard owns continuation.
public sealed record UnsavedChangesSource(
    string Subject,
    Func<bool> HasUnsavedChanges,
    Func<bool> IsBusy,
    Func<Task<bool>> SaveAsync,
    Func<Task> DiscardAsync);
