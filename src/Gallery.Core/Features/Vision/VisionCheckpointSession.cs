using Gallery.Core.Models;

namespace Gallery.Core.Services;

// Bound to one unlocked vault generation. Never reacquire a key from a later unlock.
public sealed class VisionCheckpointSession
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<VisionCheckpoint, Task> save;
    public CancellationToken RevocationToken { get; }
    public VisionCheckpoint State { get; private set; }

    internal VisionCheckpointSession(VisionCheckpoint state, CancellationToken revocationToken, Func<VisionCheckpoint, Task> save)
    {
        State = state;
        RevocationToken = revocationToken;
        this.save = save;
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
}
