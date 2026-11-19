using Gallery.Core.Models;

namespace Gallery.Core.Services;

// Bound to one unlocked vault generation. Never reacquire a key from a later unlock.
public sealed class VisionCheckpointSession
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim reviewGate = new(1, 1);
    private readonly Func<VisionCheckpoint, Task> save;
    private readonly Func<VisionDraft, Task<ImageRecord>> approve;
    public CancellationToken RevocationToken { get; }
    public VisionCheckpoint State { get; private set; }

    internal VisionCheckpointSession(VisionCheckpoint state, CancellationToken revocationToken,
        Func<VisionCheckpoint, Task> save, Func<VisionDraft, Task<ImageRecord>> approve)
    {
        State = state;
        RevocationToken = revocationToken;
        this.save = save;
        this.approve = approve;
    }

    public async Task UpdateAsync(Func<VisionCheckpoint, VisionCheckpoint> update)
    {
        await gate.WaitAsync(RevocationToken);
        try
        {
            RevocationToken.ThrowIfCancellationRequested();
            var next = update(State);
            await save(next);
            RevocationToken.ThrowIfCancellationRequested();
            State = next;
        }
        finally { gate.Release(); }
    }

    public Task StageAsync(VisionDraft draft) => UpdateAsync(state => state with
    {
        Drafts = [.. state.Drafts.Where(item => item.Id != draft.Id || item.TagsOnly != draft.TagsOnly), draft],
        Queue = [.. state.Queue.Where(item => item.Id != draft.Id || item.TagsOnly != draft.TagsOnly)]
    });

    public Task DismissAsync(string id, bool tagsOnly = false) => UpdateAsync(state => state with
    {
        Drafts = [.. state.Drafts.Where(item => item.Id != id || item.TagsOnly != tagsOnly)]
    });

    public Task<ImageRecord?> ApproveDraftAsync(VisionDraft expected) => ReviewAsync<ImageRecord?>(async () =>
    {
        var draft = State.Drafts.FirstOrDefault(item => item.Id == expected.Id && item.TagsOnly == expected.TagsOnly);
        if (draft is null) return null;
        if (draft != expected)
            throw new InvalidOperationException("This suggestion changed. Refresh it before accepting.");

        var approved = await approve(draft);
        // The analysis queue can stage a replacement while approval is saving the record.
        await RemoveDraftAsync(draft);
        return approved;
    });

    public Task<bool> DismissDraftAsync(VisionDraft expected) => ReviewAsync(async () =>
    {
        if (!State.Drafts.Contains(expected)) return false;
        await RemoveDraftAsync(expected);
        return true;
    });

    public Task<int> DiscardFullDraftsAsync() => ReviewAsync(async () =>
    {
        var count = 0;
        await UpdateAsync(state =>
        {
            count = state.Drafts.Count(draft => !draft.TagsOnly);
            return state with { Drafts = [.. state.Drafts.Where(draft => draft.TagsOnly)] };
        });
        return count;
    });

    public Task UpdateDraftTagsAsync(string previousName, string? name) => UpdateAsync(state => state with
    {
        Drafts = [.. state.Drafts.Select(draft => draft with
        {
            Tags = string.Join(", ", draft.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(tag => tag.Equals(previousName, StringComparison.OrdinalIgnoreCase) ? name : tag)
                .Where(tag => tag is not null).Distinct(StringComparer.OrdinalIgnoreCase))
        })]
    });

    private Task RemoveDraftAsync(VisionDraft draft) => UpdateAsync(state => state with
    {
        Drafts = [.. state.Drafts.Where(item => item != draft)]
    });

    private async Task<T> ReviewAsync<T>(Func<Task<T>> review)
    {
        await reviewGate.WaitAsync(RevocationToken);
        try
        {
            RevocationToken.ThrowIfCancellationRequested();
            return await review();
        }
        finally { reviewGate.Release(); }
    }
}
