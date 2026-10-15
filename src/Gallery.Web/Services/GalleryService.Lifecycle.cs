using System.Security.Cryptography;
using Gallery.Web.Managers.Gateways;
using Gallery.Web.Services.DataManagers;
using Gallery.Web.Services.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Gallery.Web.Services;

public sealed partial class GalleryService : IDisposable
{
    private const string VisionHttpClientName = "gallery-vision";
    private readonly HttpClient? ownedHttpClient;

    public bool IsUnlocked => vaultSession.IsUnlocked;
    public bool VaultExists => vaultStore.Exists;
    public string VaultRoot => vaultStore.Root;

    public static void Register(IServiceCollection services, IConfiguration configuration, string vaultRoot)
    {
        var history = new VaultHistory(configuration["Gallery:StateDirectory"] ?? DefaultApplicationDirectory);
        var video = new VideoTools(new ConfigurationProviders.ConfigurationProvider(configuration).GetVideoToolsOptions());
        ConfigureVisionTransport(services);
        services.AddScoped(provider =>
        {
            var root = history.Read() ?? vaultRoot;
            var service = CreateOwnedScope(new GalleryStore(Path.Combine(root, "gallery.db")),
                new VaultStore(root), video,
                provider.GetRequiredService<IHttpClientFactory>().CreateClient(VisionHttpClientName));
            service.history = history;
            return service;
        });
    }

    private static void ConfigureVisionTransport(IServiceCollection services) =>
        services.AddHttpClient(VisionHttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false
            });

    private GalleryService(
        GalleryStore galleryStore, VaultStore vaultStore, VaultSession session, VideoTools videoTools, HttpClient httpClient)
        : this(galleryStore, vaultStore, session, videoTools, new VisionService(httpClient))
    {
        ownedHttpClient = httpClient;
    }

    private static GalleryService CreateOwnedScope(
        GalleryStore galleryStore, VaultStore vaultStore, VideoTools videoTools, HttpClient httpClient)
    {
        var session = new VaultSession();
        try
        {
            return new GalleryService(galleryStore, vaultStore, session, videoTools, httpClient);
        }
        catch
        {
            session.Dispose();
            httpClient.Dispose();
            throw;
        }
    }

    public async Task<string> CreateVaultAsync(string passphrase, string confirmation, string? name = null,
        Gallery.Web.Models.WebMediaStorageOptions? storageOptions = null)
    {
        await lifecycleGate.WaitAsync();
        try
        {
            ValidateCredentialConfirmation(passphrase, confirmation);
            RequireLocalCatalogDirectory(VaultRoot);
            if (storageOptions is not null) storageOptions = NormalizeMediaStorageOptions(storageOptions);
            EnsureCheckout();
            var created = await vaultStore.CreateAsync(passphrase, name ?? GenerateVaultName());
            try { vaultSession.Unlock(created.Key); }
            finally { CryptographicOperations.ZeroMemory(created.Key); }
            await galleryStore.InitializeAsync();
            if (storageOptions is not null) await SaveMediaStorageOptionsAsync(storageOptions);
            history?.Save(VaultRoot);
            return created.Recovery;
        }
        finally { lifecycleGate.Release(); }
    }

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
        await lifecycleGate.WaitAsync();
        try
        {
            EnsureCheckout();
            var key = await vaultStore.UnlockWithRecoveryPhraseAsync(recoveryPhrase);
            try { vaultSession.Unlock(key); }
            finally { CryptographicOperations.ZeroMemory(key); }
            history?.Save(VaultRoot);
        }
        finally { lifecycleGate.Release(); }
    }

    public async Task ChangePassphraseAsync(string passphrase, string confirmation)
    {
        ValidateCredentialConfirmation(passphrase, confirmation);
        using var key = vaultSession.AcquireUnlockedSession().Borrow();
        await vaultStore.ChangePassphraseAsync(key.Bytes, passphrase);
    }

    private static void ValidateCredentialConfirmation(string passphrase, string confirmation)
    {
        if (passphrase != confirmation)
            throw new InvalidOperationException("Passphrases do not match.");
    }

    public event Action? OnAutoLocked;

    public void ConfigureAutoLock(int minutes) =>
        vaultSession.InactivityTimeout = minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;

    public void ConfigureInactivityTimeout(TimeSpan? timeout) =>
        vaultSession.InactivityTimeout = timeout;

    public void RecordActivity() => vaultSession.RecordActivity();

    public void Panic() => LockVault();

    public void LockVault()
    {
        vaultSession.Lock();
        _ = ReleaseCheckoutAfterOperationsAsync();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposalStarted, 1) != 0) return;
        drivePlayer.Dispose();
        vaultSession.Dispose();
        _ = ReleaseCheckoutAfterOperationsAsync();
        ownedHttpClient?.Dispose();
    }

    // Waits for leased work (ingestion, queued drive work) without holding the lifecycle gate, so the user can
    // unlock again while that work continues. The checkout is kept if the gallery was unlocked meanwhile.
    private async Task ReleaseCheckoutAfterOperationsAsync()
    {
        await vaultSession.WhenDrainedAsync();
        await lifecycleGate.WaitAsync();
        try
        {
            if (!vaultSession.IsUnlocked)
            {
                checkout?.Dispose();
                checkout = null;
            }
        }
        finally { lifecycleGate.Release(); }
    }
}
