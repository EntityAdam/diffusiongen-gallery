using System.Security.Cryptography;
using Gallery.Core.Services.Gateways;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Sessions;
using Gallery.Core.Services.Sessions.Locations;
using Gallery.Core.Models;

namespace Gallery.Core.Services;

public sealed partial class GalleryService : IDisposable
{
    private readonly HttpClient? ownedHttpClient;
    private readonly System.Runtime.CompilerServices.StrongBox<long> explicitLockRequests = new();

    private static VaultHistory CreateVaultHistory(GalleryRuntimeOptions galleryRuntimeOptions) =>
        new(galleryRuntimeOptions.StateDirectory ?? DefaultApplicationDirectory);

    private static VideoTools CreateVideoTools(GalleryRuntimeOptions galleryRuntimeOptions)
    {
        var videoToolsOptions = VideoToolsOptionsNormalizer.Normalize(
            galleryRuntimeOptions.FFmpegPath, galleryRuntimeOptions.ToolSearchPaths);
        return new VideoTools(videoToolsOptions.ConfiguredPath, videoToolsOptions.SearchPaths);
    }

    private static GalleryService CreateGalleryService(
        VaultHistory vaultHistory, string vaultRoot, VideoTools videoTools, HttpClient ownedHttpClient)
    {
        var root = vaultHistory.Read() ?? vaultRoot;
        return CreateOwnedScope(new GalleryStore(Path.Combine(root, CatalogFileName)),
            new VaultStore(root), videoTools, ownedHttpClient);
    }

    public bool IsUnlocked => vaultSession.IsUnlocked;
    public bool VaultExists => vaultStore.Exists;
    public string VaultRoot => vaultStore.Root;

    /// <summary>Creates an owned gallery scope; the returned service disposes the supplied HTTP client.</summary>
    public static GalleryService Create(string vaultRoot, GalleryRuntimeOptions galleryRuntimeOptions, HttpClient ownedHttpClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);
        ArgumentNullException.ThrowIfNull(galleryRuntimeOptions);
        ArgumentNullException.ThrowIfNull(ownedHttpClient);
        try
        {
            var vaultHistory = CreateVaultHistory(galleryRuntimeOptions);
            var videoTools = CreateVideoTools(galleryRuntimeOptions);
            var galleryService = CreateGalleryService(vaultHistory, vaultRoot, videoTools, ownedHttpClient);
            galleryService.galleryLifetime.InitializeHistory(vaultHistory);
            return galleryService;
        }
        catch
        {
            ownedHttpClient.Dispose();
            throw;
        }
    }

    private GalleryService(
        GalleryStore galleryStore, VaultStore vaultStore, VaultSession vaultSession, VideoTools videoTools, HttpClient httpClient)
        : this(galleryStore, vaultStore, vaultSession, videoTools, new VisionManager(httpClient))
    {
        ownedHttpClient = httpClient;
    }

    private static GalleryService CreateOwnedScope(
     GalleryStore galleryStore, VaultStore vaultStore, VideoTools videoTools, HttpClient httpClient)
    {
     var vaultSession = new VaultSession();
     try
     {
         return new GalleryService(galleryStore, vaultStore, vaultSession, videoTools, httpClient);
     }
     catch
     {
         vaultSession.Dispose();
         throw;
     }
    }

    public Task<string> CreateVaultAsync(string passphrase, string confirmation, string? name = null,
     MediaStorageOptions? storageOptions = null) =>
     vaultManager.CreateVaultAsync(passphrase, confirmation, name, storageOptions);

    public async Task UnlockWithPassphraseAsync(string passphrase)
    {
        await lifecycleGate.WaitAsync();
        try
        {
            EnsureCheckout();
            var key = await vaultStore.UnlockWithPassphraseAsync(passphrase);
            try { vaultSession.Unlock(key); }
            finally { CryptographicOperations.ZeroMemory(key); }
            history?.Save(VaultRoot);
        }
        finally { lifecycleGate.Release(); }
    }

    public async Task UnlockWithRecoveryPhraseAsync(string recoveryPhrase)
    {
        var normalizedRecoveryPhrase = Gallery.Core.Services.Validators.RecoveryPhraseValidator.Normalize(recoveryPhrase);
        await lifecycleGate.WaitAsync();
        try
        {
            EnsureCheckout();
            var key = await vaultStore.UnlockWithRecoveryPhraseAsync(normalizedRecoveryPhrase);
            try { vaultSession.Unlock(key); }
            finally { CryptographicOperations.ZeroMemory(key); }
            history?.Save(VaultRoot);
        }
        finally { lifecycleGate.Release(); }
    }

    public Task ChangePassphraseAsync(string passphrase, string confirmation) =>
        vaultManager.ChangePassphraseAsync(passphrase, confirmation);

    public event Action? OnAutoLocked;

    public void ConfigureAutoLock(int minutes) =>
        vaultSession.InactivityTimeout = minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;

    public void ConfigureInactivityTimeout(TimeSpan? timeout) =>
        vaultSession.InactivityTimeout = timeout;

    public void RecordActivity() => vaultSession.RecordActivity();

    public void Panic() => LockVault();

    public void LockVault()
    {
        Interlocked.Increment(ref explicitLockRequests.Value);
        vaultSession.Lock();
        _ = ReleaseCheckoutAfterOperationsAsync();
    }

    private long ReadExplicitLockRequests() => Interlocked.Read(ref explicitLockRequests.Value);

    // Restores a session revoked for a failed location change unless a later explicit lock superseded it.
    private void RestoreUnlockedSessionAfterFailure(byte[]? key, long lockRequestsAtStart)
    {
        if (key is not null && !galleryLifetime.IsDisposed && ReadExplicitLockRequests() == lockRequestsAtStart)
            vaultSession.Unlock(key);
    }

    public void Dispose()
    {
        if (!galleryLifetime.TryBeginDisposal()) return;
        driveOperationPlayer.Dispose();
        vaultSession.Dispose();
        _ = ReleaseCheckoutAfterOperationsAsync();
        ownedHttpClient?.Dispose();
    }

    // Waits for leased work (ingestion, queued drive work) without holding the lifecycle gate, so the user can
    // unlock again while that work continues. The checkout is kept if the gallery was unlocked meanwhile.
    private async Task ReleaseCheckoutAfterOperationsAsync()
    {
        var expectedCheckout = checkout;
        if (expectedCheckout is null) return;
        while (true)
        {
            await vaultSession.WhenDrainedAsync();
            await lifecycleGate.WaitAsync();
            Task? remainingDrainTask;
            try { remainingDrainTask = TryReleaseCheckoutAfterOperationsAsync(expectedCheckout); }
            finally { lifecycleGate.Release(); }
            if (remainingDrainTask is null) return;
            await remainingDrainTask;
        }
    }

    // Caller owns lifecycleGate, which excludes public unlock and location replacement.
    internal Task? TryReleaseCheckoutAfterOperationsAsync(VaultCheckout expectedCheckout)
    {
        if (!ReferenceEquals(checkout, expectedCheckout)) return null;
        bool vaultIsUnlocked;
        Task drainTask;
        (vaultIsUnlocked, drainTask) = vaultSession.ReadCheckoutReleaseState();
        if (vaultIsUnlocked) return null;
        if (!drainTask.IsCompleted) return drainTask;
        galleryLifetime.ReleaseCheckout();
        return null;
    }
}
