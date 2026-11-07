using Gallery.Web.Components;

namespace Gallery.Web.Components.Pages;

public partial class Home
{
    private Func<Task>? pendingDetailAction;
    private bool NotesDirty => selected is not null &&
        (editDescription != selected.Description || editUserDescription != selected.UserDescription);

    private void DiscardNotes()
    {
        if (busy || selected is null) return;
        editDescription = selected.Description;
        editUserDescription = selected.UserDescription;
    }

    private Task RequestDetailActionAsync(Func<Task> action)
    {
        if (busy) return Task.CompletedTask;
        if (!NotesDirty) return action();
        error = "";
        pendingDetailAction = action;
        return Task.CompletedTask;
    }

    private Task RequestCloseDetailAsync() => RequestDetailActionAsync(() =>
    {
        CloseDetail();
        return Task.CompletedTask;
    });

    private Task RequestDetailViewerAsync() => RequestDetailActionAsync(() =>
    {
        OpenViewer(selected?.Id);
        return Task.CompletedTask;
    });

    private Task RequestDetailPromptAsync() => RequestDetailActionAsync(() => ShowPromptAsync(selected!.Prompt));
    private Task RequestDetailRemovalAsync() => RequestDetailActionAsync(() => PrepareRemovalAsync([selected!.Id]));
    private void KeepEditingNotes() { if (!busy) pendingDetailAction = null; }

    private async Task DiscardNotesAndContinueAsync()
    {
        if (busy || pendingDetailAction is not { } action) return;
        DiscardNotes();
        pendingDetailAction = null;
        await action();
    }

    private async Task SaveNotesAndContinueAsync()
    {
        var saved = false;
        await RunAsync("Saving notes...", async () =>
        {
            await SaveDetailNotesCoreAsync();
            saved = true;
        });
        if (!saved || pendingDetailAction is not { } action) return;
        pendingDetailAction = null;
        await action();
    }

    private async Task SaveDetailNotesCoreAsync()
    {
        if (selected is null) return;
        var updated = await Gallery.UpdateAsync(selected.Id, image => image with
        { Description = editDescription.Trim(), UserDescription = editUserDescription.Trim() });
        ViewerImageChanged(updated);
        editDescription = updated.Description;
        editUserDescription = updated.UserDescription;
        message = "Notes saved.";
    }

    private Task ApproveDetailSuggestionAsync() => RunAsync("Saving approved analysis...", async () =>
    {
        if (NotesDirty || selected is null || !aiSuggestions.TryGetValue(selected.Id, out var suggestion)) return;
        if (await ApproveSuggestionAsync(suggestion) is not { } updated) return;
        ViewerImageChanged(updated);
        editDescription = updated.Description;
        message = "Vision analysis approved and saved.";
    });

    private Task DismissDetailSuggestionAsync() => selected is not null && aiSuggestions.TryGetValue(selected.Id, out var suggestion)
        ? DismissSuggestionAsync(suggestion) : Task.CompletedTask;
}
