using Gallery.Core.Features.Ingestion;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Models;
using Gallery.Core.Services;
using GalleryLocation = Gallery.Core.Features.Vaults.Session.GalleryLocation;

namespace Gallery.Core.Features.Settings;

internal sealed class GallerySettings(GalleryLifetime galleryLifetime, VaultSession vaultSession) : IGallerySettings
{
    private const string PreferencesId = "ui-preferences";
    private const string PreferencesPurpose = "ui-preferences:v1";

    public async Task<UiPreferences> LoadPreferencesAsync() =>
        PreferencesNormalizer.Normalize(
            await ReadEncryptedSettingAsync<UiPreferences>(PreferencesId, PreferencesPurpose) ?? new());

    public Task SavePreferencesAsync(UiPreferences preferences) =>
        WriteEncryptedSettingAsync(PreferencesNormalizer.Normalize(preferences), PreferencesId, PreferencesPurpose);

    public async Task<IngestionOptions> LoadIngestionOptionsAsync() =>
        await ReadEncryptedSettingAsync<IngestionOptions>(IngestionOptions.SettingId, IngestionOptions.SettingPurpose) ?? new();

    public Task SaveIngestionOptionsAsync(IngestionOptions options)
    {
        IngestionOptionsValidator.Validate(options);
        return WriteEncryptedSettingAsync(options, IngestionOptions.SettingId, IngestionOptions.SettingPurpose);
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

    public static async Task<string> ReadMediaDirectoryAsync(GalleryLocation location, byte[] key)
    {
        var options = await location.GalleryStore.ReadSettingsAsync<MediaStorageOptions>(MediaStorageOptions.SettingId, MediaStorageOptions.SettingPurpose, key);
        return options is { MediaDirectory.Length: > 0 } ? options.MediaDirectory : location.VaultStore.Root;
    }

    public async Task<List<SavedView>> LoadViewsAsync()
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        return await galleryLifetime.Current.GalleryStore.LoadViewsAsync(keyLease.Bytes);
    }

    public async Task SaveViewAsync(string name, GalleryFilter filter)
    {
        var normalizedName = SavedViewNameValidator.NormalizeAndValidate(name);
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryLifetime.Current.GalleryStore.SaveViewAsync(normalizedName, filter, keyLease.Bytes);
    }

    public async Task DeleteViewAsync(string name)
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryLifetime.Current.GalleryStore.DeleteViewAsync(name.Trim(), keyLease.Bytes);
    }

    private async Task<T?> ReadEncryptedSettingAsync<T>(string id, string purpose) where T : class
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        return await galleryLifetime.Current.GalleryStore.ReadSettingsAsync<T>(id, purpose, keyLease.Bytes);
    }

    private async Task WriteEncryptedSettingAsync<T>(T value, string id, string purpose)
    {
        using var keyLease = vaultSession.AcquireUnlockedSession().Borrow();
        await galleryLifetime.Current.GalleryStore.SaveSettingsAsync(value, id, purpose, keyLease.Bytes);
    }
}
