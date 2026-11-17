namespace Gallery.Web.Components.Workspace;

public sealed class UnsavedChangesGuardState(
    GalleryActivity activity, Func<bool> isBusy, Action? saved = null) : IDisposable
{
    private sealed class Request(IReadOnlyList<UnsavedChangesSource> sources, Func<Task> action)
    {
        public IReadOnlyList<UnsavedChangesSource> Sources { get; } = sources;
        public Func<Task> Action { get; } = action;
        public int Index { get; set; }
        public UnsavedChangesSource Current => Sources[Index];
    }
    private Request? pending;
    private Request? working;
    private bool disposed;
    public bool IsPending => pending is not null;
    public string Subject => pending?.Current.Subject ?? "";
    public bool IsBusy => working is not null || isBusy() || pending?.Current.IsBusy() == true;

    public Task RequestAsync(IEnumerable<UnsavedChangesSource> sources, Func<Task> action)
    {
        if (disposed || IsBusy || pending is not null) return Task.CompletedTask;
        var candidates = sources.ToArray();
        if (candidates.Any(source => source.IsBusy())) return Task.CompletedTask;
        var dirty = candidates.Where(source => source.HasUnsavedChanges()).ToArray();
        if (dirty.Length == 0) return action();
        activity.Error = "";
        pending = new(dirty, action);
        return Task.CompletedTask;
    }

    public void KeepEditing()
    {
        if (!IsBusy) pending = null;
    }

    public async Task DiscardAsync()
    {
        if (disposed || IsBusy || pending is not { } request) return;
        working = request;
        try
        {
            await request.Current.DiscardAsync();
            if (!request.Current.HasUnsavedChanges()) await AdvanceAsync(request);
        }
        finally { if (working == request) working = null; }
    }

    public async Task SaveAsync()
    {
        if (disposed || IsBusy || pending is not { } request) return;
        working = request;
        try
        {
            if (!await request.Current.SaveAsync() || disposed || pending != request
                || request.Current.HasUnsavedChanges() || activity.Error.Length > 0) return;
            saved?.Invoke();
            await AdvanceAsync(request);
        }
        finally { if (working == request) working = null; }
    }

    private async Task AdvanceAsync(Request request)
    {
        if (disposed || pending != request) return;
        request.Index++;
        while (request.Index < request.Sources.Count && !request.Current.HasUnsavedChanges()) request.Index++;
        if (request.Index < request.Sources.Count) return;
        pending = null;
        if (working == request) working = null;
        await request.Action();
    }

    public void Dispose()
    {
        disposed = true;
        pending = null;
    }
}
