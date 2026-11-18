using Gallery.Core.Models;
using Gallery.Core.Services.Processors;

namespace Gallery.Core.Services;

public sealed partial class GalleryService
{
    private const string PreferencesId = "ui-preferences";
    private const string PreferencesPurpose = "ui-preferences:v1";

    public async Task<UiPreferences> LoadPreferencesAsync() =>
        PreferencesNormalizer.Normalize(
            await ReadEncryptedSettingAsync<UiPreferences>(PreferencesId, PreferencesPurpose) ?? new());

    public Task SavePreferencesAsync(UiPreferences preferences) =>
        WriteEncryptedSettingAsync(PreferencesNormalizer.Normalize(preferences), PreferencesId, PreferencesPurpose);

    public bool CatalogIsOnNetworkDrive => CatalogDirectoryRules.IsNetworkDirectory(VaultRoot);

    /// <summary>Relocates only the encrypted catalog and header; all encrypted media stays at its existing paths.</summary>
    public Task RelocateCatalogAsync(string directory) => vaultLocations.RelocateCatalogAsync(directory);

    public async Task<IngestionOptions> LoadIngestionOptionsAsync() =>
        await ReadEncryptedSettingAsync<IngestionOptions>("ingestion-options", "ingestion-options:v1") ?? new();

    public Task SaveIngestionOptionsAsync(IngestionOptions options)
    {
        Gallery.Core.Services.Validators.IngestionOptionsValidator.Validate(options);
        return WriteEncryptedSettingAsync(options, "ingestion-options", "ingestion-options:v1");
    }

    public async Task<MediaStorageOptions> LoadMediaStorageOptionsAsync() =>
        await ReadEncryptedSettingAsync<MediaStorageOptions>(MediaStorageOptions.SettingId, MediaStorageOptions.SettingPurpose) ?? new();

    public async Task SaveMediaStorageOptionsAsync(MediaStorageOptions options)
    {
        options = NormalizeMediaStorageOptions(options);
        await WriteEncryptedSettingAsync(options, MediaStorageOptions.SettingId, MediaStorageOptions.SettingPurpose);
    }

    internal static MediaStorageOptions NormalizeMediaStorageOptions(MediaStorageOptions options)
    {
        var directory = options.MediaDirectory.Trim();
        if (directory.Length > 0)
        {
            directory = CatalogDirectoryRules.NormalizeVaultDirectory(directory);
            if (!Directory.Exists(directory))
                throw new InvalidOperationException($"Media directory '{options.MediaDirectory}' does not exist; choose an existing directory for encrypted media.");
        }
        return options with { MediaDirectory = directory };
    }

    private async Task<string> MediaDirectoryWithKeyAsync(byte[] key)
    {
        var options = await galleryStore.ReadSettingsAsync<MediaStorageOptions>(MediaStorageOptions.SettingId, MediaStorageOptions.SettingPurpose, key);
        return options is { MediaDirectory.Length: > 0 } ? options.MediaDirectory : VaultRoot;
    }

    private async Task<T?> ReadEncryptedSettingAsync<T>(string id, string purpose) where T : class
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        return await galleryStore.ReadSettingsAsync<T>(id, purpose, keyLease.Bytes);
    }

    private async Task WriteEncryptedSettingAsync<T>(T value, string id, string purpose)
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryStore.SaveSettingsAsync(value, id, purpose, keyLease.Bytes);
    }
}
