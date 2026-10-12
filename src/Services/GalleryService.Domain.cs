using System.Security.Cryptography;
using Gallery.Managers.Gateways;
using Gallery.Services.DataManagers;
using Gallery.Services.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Gallery.Services;

public sealed partial class GalleryService : IDisposable
{
    private const string VisionHttpClientName = "gallery-vision";
    private readonly HttpClient? ownedHttpClient;

    public bool IsUnlocked => vaultSession.IsUnlocked;
    public bool VaultExists => vaultStore.Exists;
    public string VaultRoot => vaultStore.Root;

    public static void Register(IServiceCollection services, IConfiguration configuration, string vaultRoot)
    {
        var sharedResources = CreateSharedResources(configuration, vaultRoot);
        ConfigureVisionTransport(services);
        RegisterOwnedScopes(services, sharedResources.GalleryStore, sharedResources.VaultStore, sharedResources.VideoTools);
    }

    private static (GalleryStore GalleryStore, VaultStore VaultStore, VideoTools VideoTools) CreateSharedResources(
        IConfiguration configuration, string vaultRoot)
    {
        var vaultStore = new VaultStore(vaultRoot);
        var galleryStore = new GalleryStore(Path.Combine(vaultStore.Root, "gallery.db"));
        var options = new ConfigurationProviders.ConfigurationProvider(configuration).GetVideoToolsOptions();
        return (galleryStore, vaultStore, new VideoTools(options));
    }

    private static void ConfigureVisionTransport(IServiceCollection services) =>
        services.AddHttpClient(VisionHttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false
            });

    private static void RegisterOwnedScopes(
        IServiceCollection services, GalleryStore galleryStore, VaultStore vaultStore, VideoTools videoTools) =>
        services.AddScoped(provider => CreateOwnedScope(
            galleryStore, vaultStore, videoTools,
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(VisionHttpClientName)));

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

    public async Task<string> CreateVaultAsync(string passphrase, string confirmation)
    {
        ValidateCredentialConfirmation(passphrase, confirmation);
        var created = await vaultStore.CreateAsync(passphrase);
        try { vaultSession.Unlock(created.Key); }
        finally { CryptographicOperations.ZeroMemory(created.Key); }
        return created.Recovery;
    }

    public async Task UnlockWithPassphraseAsync(string passphrase)
    {
        var key = await vaultStore.UnlockWithPassphraseAsync(passphrase);
        try { vaultSession.Unlock(key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public async Task UnlockWithRecoveryPhraseAsync(string recoveryPhrase)
    {
        var key = await vaultStore.UnlockWithRecoveryPhraseAsync(recoveryPhrase);
        try { vaultSession.Unlock(key); }
        finally { CryptographicOperations.ZeroMemory(key); }
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

    public void LockVault() => vaultSession.Lock();

    public void Dispose()
    {
        vaultSession.Dispose();
        ownedHttpClient?.Dispose();
    }
}
