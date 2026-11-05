using Gallery.Core.Services.Sessions.Generations.Leases;
using Gallery.Core.Services.Ports.Models;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    internal async Task<List<CatalogImportSource>> ReadImportSourcesAsync()
    {
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        return await galleryStore.ReadImportSourcesAsync(key.Bytes);
    }

    // Stress recovery makes many independent verification calls. Bind their shared resources to a
    // real vault-key lease so lock, close and relocation still drain all keyed handles first.
    internal async ValueTask<IAsyncDisposable> OpenCatalogOperationAsync()
    {
        var key = vaultSession.AcquireUnlockedSession().Borrow();
        try { return new LeasedCatalogOperation(await galleryStore.OpenOperationAsync(key.Bytes), key); }
        catch { key.Dispose(); throw; }
    }

    private sealed class LeasedCatalogOperation(IAsyncDisposable operation, KeyLease key) : IAsyncDisposable
    {
        private int disposed;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { await operation.DisposeAsync(); }
            finally { key.Dispose(); }
        }
    }
}
