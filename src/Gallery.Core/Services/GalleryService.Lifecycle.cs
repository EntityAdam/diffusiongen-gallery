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

    public bool IsUnlocked => vaultSession.IsUnlocked;
    public bool VaultExists => vaultStore.Exists;
    public string VaultRoot => vaultStore.Root;

    /// <summary>Creates an owned gallery scope; the returned service disposes the supplied HTTP client.</summary>
    public static GalleryService Create(string vaultRoot, GalleryRuntimeOptions options, HttpClient ownedHttpClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ownedHttpClient);
        try
        {
            var history = new VaultHistory(options.StateDirectory ?? DefaultApplicationDirectory);
            var videoToolsOptions = VideoToolsOptionsNormalizer.Normalize(options.FFmpegPath, options.ToolSearchPaths);
            var video = new VideoTools(videoToolsOptions.ConfiguredPath, videoToolsOptions.SearchPaths);
            var root = history.Read() ?? vaultRoot;
            var service = CreateOwnedScope(new GalleryStore(Path.Combine(root, "gallery.db")),
                new VaultStore(root), video, ownedHttpClient);
            service.galleryLifetime.InitializeHistory(history);
            return service;
        }
        catch
        {
            ownedHttpClient.Dispose();
            throw;
        }
    }

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
            throw;
        }
    }

    public async Task<string> CreateVaultAsync(string passphrase, string confirmation, string? name = null,
        Gallery.Core.Models.MediaStorageOptions? storageOptions = null)
    {
        await lifecycleGate.WaitAsync();
        try
        {
            ValidateCredentialConfirmation(passphrase, confirmation);
            RequireLocalCatalogDirectory(VaultRoot);
            if (storageOptions is not null) storageOptions = NormalizeMediaStorageOptions(storageOptions);
            EnsureCheckout();
            if (vaultStore.Exists) throw new InvalidOperationException("A gallery already exists. Unlock it instead.");
            foreach (var fileName in new[] { "gallery.db", "gallery.db-wal", "gallery.db-shm" })
            {
                var path = Path.Combine(VaultRoot, fileName);
                if (File.Exists(path) || Directory.Exists(path))
                    throw new IOException("A new gallery cannot replace an existing catalog or its journal files.");
            }
            var staging = Path.Combine(VaultRoot, ".vault-create-" + Guid.NewGuid().ToString("N"));
            byte[]? createdKey = null;
            try
            {
                var stagedVault = new VaultStore(staging);
                var created = await stagedVault.CreateAsync(passphrase, name ?? GenerateVaultName());
                createdKey = created.Key;
                var stagedStore = new GalleryStore(Path.Combine(staging, "gallery.db"));
                await stagedStore.InitializeAsync();
                if (storageOptions is not null)
                    await stagedStore.SaveSettingsAsync(storageOptions, "media-storage", "media-storage:v1", createdKey);
                history?.Save(VaultRoot);
                EnsureActive();
                // No leases can observe the new key before catalog/header publication completes.
                using (var activation = vaultSession.PrepareUnlock(createdKey))
                {
                    PublishCreatedVault(staging, VaultRoot);
                    activation.Commit();
                }
                return created.Recovery;
            }
            finally
            {
                if (createdKey is not null) CryptographicOperations.ZeroMemory(createdKey);
                DeleteCreationStaging(staging);
            }
        }
        finally { lifecycleGate.Release(); }
    }

    private static void PublishCreatedVault(string staging, string root)
    {
        var database = Path.Combine(root, "gallery.db");
        File.Move(Path.Combine(staging, "gallery.db"), database, overwrite: false);
        try { File.Move(Path.Combine(staging, "vault.json"), Path.Combine(root, "vault.json"), overwrite: false); }
        catch
        {
            File.Move(database, Path.Combine(staging, "gallery.db"), overwrite: false);
            throw;
        }
    }

    private static void DeleteCreationStaging(string staging)
    {
        // Staging cleanup is nonessential after publication: it must never withhold delivered recovery.
        try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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

    public async Task ChangePassphraseAsync(string passphrase, string confirmation)
    {
        ValidateCredentialConfirmation(passphrase, confirmation);
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        await vaultStore.ChangePassphraseAsync(keyLease.Bytes, passphrase);
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
        if (!galleryLifetime.TryBeginDisposal()) return;
        drivePlayer.Dispose();
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
            Task? remainingDrain;
            try { remainingDrain = TryReleaseCheckoutAfterOperations(expectedCheckout); }
            finally { lifecycleGate.Release(); }
            if (remainingDrain is null) return;
            await remainingDrain;
        }
    }

    // Caller owns lifecycleGate, which excludes public unlock and location replacement.
    internal Task? TryReleaseCheckoutAfterOperations(VaultCheckout expectedCheckout)
    {
        if (!ReferenceEquals(checkout, expectedCheckout)) return null;
        var (unlocked, drain) = vaultSession.ReadCheckoutReleaseState();
        if (unlocked) return null;
        if (!drain.IsCompleted) return drain;
        galleryLifetime.ReleaseCheckout();
        return null;
    }
}
