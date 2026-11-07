using Gallery.Core.Models;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    internal const string VisionCheckpointId = "vision-checkpoint";
    private const string VisionCheckpointPurpose = "vision-checkpoint:v1";

    public async Task<VisionCheckpointSession> OpenVisionCheckpointAsync()
    {
        var generation = vaultSession.AcquireUnlockedSession();
        var store = galleryStore;
        VisionCheckpoint state;
        // Serialize reads with writes across unlock generations. A save already in flight at
        // lock time must finish before a newly unlocked UI reads its checkpoint.
        await store.Gate.WaitAsync(generation.RevocationToken);
        try
        {
            using var key = generation.Borrow();
            state = await store.ReadSettingsAsync<VisionCheckpoint>(VisionCheckpointId, VisionCheckpointPurpose, key.Bytes) ?? new();
        }
        finally { store.Gate.Release(); }
        generation.RevocationToken.ThrowIfCancellationRequested();
        return new(state, generation.RevocationToken, async checkpoint =>
        {
            await store.Gate.WaitAsync(generation.RevocationToken);
            try
            {
                using var key = generation.Borrow();
                await store.SaveSettingsAsync(checkpoint, VisionCheckpointId, VisionCheckpointPurpose, key.Bytes);
            }
            finally { store.Gate.Release(); }
        });
    }
}
