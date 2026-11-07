using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components;

// One pending organization change per browser circuit, independent of the active screen.
public sealed class OrganizationHistory(GalleryService gallery)
{
    public CollectionUndo? Current { get; private set; }
    public bool IsBusy { get; private set; }
    public event Action? Changed;

    public void Record(CollectionUndo undo)
    {
        Current = undo;
        Changed?.Invoke();
    }

    public void Clear()
    {
        Current = null;
        Changed?.Invoke();
    }

    public async Task UndoAsync()
    {
        if (IsBusy || Current is not { } undo) return;
        IsBusy = true;
        Changed?.Invoke();
        try
        {
            await gallery.UndoCollectionChangeAsync(undo);
            if (ReferenceEquals(Current, undo)) Clear();
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }
}
