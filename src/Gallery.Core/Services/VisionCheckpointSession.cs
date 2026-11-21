using Gallery.Core.Models;

namespace Gallery.Core.Services;

// Bound to one unlocked vault generation. Never reacquire a key from a later unlock.
public sealed class VisionCheckpointSession
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim reviewGate = new(1, 1);
    private readonly Func<VisionCheckpoint, Task> save;
    private readonly Func<VisionDraft, VisionCheckpoint, Task<ImageRecord>> approve;
    public CancellationToken RevocationToken { get; }
    public VisionCheckpoint State { get; private set; }

    internal VisionCheckpointSession(VisionCheckpoint state, CancellationToken revocationToken,
        Func<VisionCheckpoint, Task> save, Func<VisionDraft, VisionCheckpoint, Task<ImageRecord>> approve)
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

    public Task StageAsync(VisionDraft draft) => UpdateAsync(state => StageDraftAndDequeue(state, draft));

    private static VisionCheckpoint StageDraftAndDequeue(VisionCheckpoint state, VisionDraft draft) => state with
    {
        Drafts = [.. state.Drafts.Where(item => item.Id != draft.Id || item.TagsOnly != draft.TagsOnly), draft],
        Queue = [.. state.Queue.Where(item => item.Id != draft.Id || item.TagsOnly != draft.TagsOnly)]
    };

    public Task DismissAsync(string id, bool tagsOnly = false) => UpdateAsync(state => state with
    {
        Drafts = [.. state.Drafts.Where(item => item.Id != id || item.TagsOnly != tagsOnly)]
    });

    public Task<ImageRecord?> ApproveDraftAsync(VisionDraft expected) => ReviewAsync<ImageRecord?>(async () =>
    {
        await gate.WaitAsync(RevocationToken);
        try
        {
            RevocationToken.ThrowIfCancellationRequested();
            var draft = State.Drafts.FirstOrDefault(item => item.Id == expected.Id && item.TagsOnly == expected.TagsOnly);
            if (draft is null) return null;
            if (draft != expected)
                throw new InvalidOperationException("This suggestion changed. Refresh it before accepting.");

            var next = State with { Drafts = [.. State.Drafts.Where(item => item != draft)] };
            var approved = await approve(draft, next);
            RevocationToken.ThrowIfCancellationRequested();
            State = next;
            return approved;
        }
        finally { gate.Release(); }
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
            var transition = DiscardFullDrafts(state);
            count = transition.Count;
            return transition.Checkpoint;
        });
        return count;
    });

    private static (VisionCheckpoint Checkpoint, int Count) DiscardFullDrafts(VisionCheckpoint state)
    {
        var count = state.Drafts.Count(draft => !draft.TagsOnly);
        return (state with { Drafts = [.. state.Drafts.Where(draft => draft.TagsOnly)] }, count);
    }

    public Task UpdateDraftTagsAsync(string previousName, string? name) =>
        UpdateAsync(state => RewriteDraftTags(state, previousName, name));

    private static VisionCheckpoint RewriteDraftTags(VisionCheckpoint state, string previousName, string? name) =>
        state with { Drafts = [.. state.Drafts.Select(draft => RewriteDraftTags(draft, previousName, name))] };

    private static VisionDraft RewriteDraftTags(VisionDraft draft, string previousName, string? name) =>
        draft with { Tags = RewriteTags(draft.Tags, previousName, name) };

    private static string RewriteTags(string tags, string previousName, string? name) =>
        string.Join(", ", tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(tag => tag.Equals(previousName, StringComparison.OrdinalIgnoreCase) ? name : tag)
            .Where(tag => tag is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase));

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
