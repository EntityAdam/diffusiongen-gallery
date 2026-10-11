using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core;
using Gallery.Models;

namespace Gallery.Services;

internal sealed record SavedView(string Name, GalleryFilter Filter);
internal sealed record OrganizationSuggestion(string Id, string Name, string Description, string Tags, string Collection, string Model, string Warning = "");

internal sealed class LibraryService(GalleryStore galleryStore, VaultSession vaultSession)
{
    private const string SavedViewsId = "saved-views";

    public async Task<List<SavedView>> LoadViewsAsync()
    {
        using var keyLease = vaultSession.Borrow();
        var payload = await galleryStore.ReadSettingsAsync(SavedViewsId);
        return payload is null ? [] : SavedViewCodec.Deserialize(payload, keyLease.Bytes);
    }

    public async Task SaveViewAsync(string name, GalleryFilter galleryFilter)
    {
        name = ValidateViewName(name);
        await galleryStore.Gate.WaitAsync();
        try
        {
            var views = await LoadViewsAsync();
            SavedViewProcessor.Replace(views, name, galleryFilter);
            await SaveViewsAsync(views);
        }
        finally
        {
            galleryStore.Gate.Release();
        }
    }

    private static string ValidateViewName(string name)
    {
        var normalizedName = name.Trim();
        if (normalizedName.Length is 0 or > 80)
            throw new InvalidOperationException("Saved view names must have 1 to 80 characters.");
        return normalizedName;
    }

    public async Task DeleteViewAsync(string name)
    {
        await galleryStore.Gate.WaitAsync();
        try
        {
            var views = await LoadViewsAsync();
            SavedViewProcessor.Remove(views, name);
            await SaveViewsAsync(views);
        }
        finally
        {
            galleryStore.Gate.Release();
        }
    }

    private async Task SaveViewsAsync(List<SavedView> views)
    {
        using var keyLease = vaultSession.Borrow();
        var payload = SavedViewCodec.Serialize(views, keyLease.Bytes);
        await galleryStore.SaveSettingsAsync(payload, SavedViewsId);
    }
}

internal static class SavedViewCodec
{
    internal const string SavedViewsPurpose = "saved-views:v1";

    public static List<SavedView> Deserialize(byte[] payload, byte[] keyBytes)
    {
        var plain = VaultCrypto.Decrypt(payload, keyBytes, SavedViewsPurpose);
        try
        {
            return JsonSerializer.Deserialize<List<SavedView>>(plain)
                ?? throw new InvalidOperationException("Invalid saved views.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static byte[] Serialize(List<SavedView> views, byte[] keyBytes)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(views);
        try
        {
            return VaultCrypto.Encrypt(plain, keyBytes, SavedViewsPurpose);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }
}

internal static class SavedViewProcessor
{
    public static void Replace(List<SavedView> views, string name, GalleryFilter galleryFilter)
    {
        views.RemoveAll(savedView => savedView.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        views.Add(new SavedView(name, galleryFilter with { }));
    }

    public static void Remove(List<SavedView> views, string name) =>
        views.RemoveAll(savedView => savedView.Name == name);
}
