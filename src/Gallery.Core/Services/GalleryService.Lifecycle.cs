using Gallery.Core.Services.Ports;
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

    private static VaultHistory CreateVaultHistory(GalleryRuntimeOptions galleryRuntimeOptions) =>
        new(galleryRuntimeOptions.StateDirectory ?? DefaultApplicationDirectory);

    private static VideoTools CreateVideoTools(GalleryRuntimeOptions galleryRuntimeOptions)
    {
        var videoToolsOptions = VideoToolsOptionsNormalizer.Normalize(
            galleryRuntimeOptions.FFmpegPath, galleryRuntimeOptions.ToolSearchPaths);
        return new VideoTools(videoToolsOptions.ConfiguredPath, videoToolsOptions.SearchPaths);
    }

    private static GalleryService CreateGalleryService(
        VaultHistory vaultHistory, string vaultRoot, VideoTools videoTools, HttpClient ownedHttpClient, IGalleryStoreFactory storeFactory)
    {
        var root = vaultHistory.Read() ?? vaultRoot;
        return CreateOwnedScope(storeFactory.Create(root),
            new VaultStore(root), videoTools, ownedHttpClient, storeFactory);
    }

    public bool IsUnlocked => vaultSession.IsUnlocked;
    public bool VaultExists => galleryLifetime.Current.VaultStore.Exists;
    public string VaultRoot => galleryLifetime.Current.VaultStore.Root;

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
            var galleryService = CreateGalleryService(vaultHistory, vaultRoot, videoTools, ownedHttpClient,
                new GalleryStoreFactory(galleryRuntimeOptions.CatalogBackend));
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
        IGalleryStore galleryStore, VaultStore vaultStore, VaultSession vaultSession, VideoTools videoTools, HttpClient httpClient, IGalleryStoreFactory storeFactory)
        : this(galleryStore, vaultStore, vaultSession, videoTools, new VisionManager(httpClient), storeFactory, httpClient)
    {
        ownedHttpClient = httpClient;
    }

    private static GalleryService CreateOwnedScope(
     IGalleryStore galleryStore, VaultStore vaultStore, VideoTools videoTools, HttpClient httpClient, IGalleryStoreFactory storeFactory)
    {
     var vaultSession = new VaultSession();
     try
     {
         return new GalleryService(galleryStore, vaultStore, vaultSession, videoTools, httpClient, storeFactory);
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

    public Task UnlockWithPassphraseAsync(string passphrase) => vaultAccess.UnlockWithPassphraseAsync(passphrase);
    public Task UnlockWithRecoveryPhraseAsync(string recoveryPhrase) => vaultAccess.UnlockWithRecoveryPhraseAsync(recoveryPhrase);

    public Task ChangePassphraseAsync(string passphrase, string confirmation) =>
        vaultManager.ChangePassphraseAsync(passphrase, confirmation);

    public event Action? OnAutoLocked
    {
        add => vaultAccess.AutoLocked += value;
        remove => vaultAccess.AutoLocked -= value;
    }

    public void ConfigureAutoLock(int minutes) =>
        vaultSession.InactivityTimeout = minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;

    public void ConfigureInactivityTimeout(TimeSpan? timeout) =>
        vaultSession.InactivityTimeout = timeout;

    public void RecordActivity() => vaultSession.RecordActivity();

    public void Panic() => LockVault();

    public void LockVault() => vaultAccess.LockVault();

    public void Dispose()
    {
        if (!galleryLifetime.TryBeginDisposal()) return;
        driveQueue.Dispose();
        vaultSession.Dispose();
        _ = vaultAccess.ReleaseCheckoutAfterOperationsAsync();
        ownedHttpClient?.Dispose();
    }
}
