using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Models;

namespace Gallery.Services;

public sealed record SavedView(string Name, GalleryFilter Filter);
public sealed record OrganizationSuggestion(string Id, string Name, string Description, string Tags, string Collection, string Model, string Warning = "");

public sealed class LibraryService(GalleryStore store, VaultSession session)
{
    public async Task<List<SavedView>> LoadViewsAsync()
    {
        using var key = session.Borrow();
        var payload = await store.ReadSettingsAsync("saved-views");
        if (payload is null) return [];
        var plain = VaultCrypto.Decrypt(payload, key.Bytes, "saved-views:v1");
        try { return JsonSerializer.Deserialize<List<SavedView>>(plain) ?? throw new InvalidOperationException("Invalid saved views."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task SaveViewAsync(string name, GalleryFilter filter)
    {
        name = name.Trim();
        if (name.Length is 0 or > 80) throw new InvalidOperationException("Saved view names must have 1 to 80 characters.");
        await store.Gate.WaitAsync();
        try
        {
            var views = await LoadViewsAsync();
            views.RemoveAll(view => view.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            views.Add(new(name, Clone(filter)));
            await SaveViewsAsync(views);
        }
        finally { store.Gate.Release(); }
    }

    public async Task DeleteViewAsync(string name)
    {
        await store.Gate.WaitAsync();
        try
        {
            var views = await LoadViewsAsync();
            views.RemoveAll(view => view.Name == name);
            await SaveViewsAsync(views);
        }
        finally { store.Gate.Release(); }
    }

    private async Task SaveViewsAsync(List<SavedView> views)
    {
        using var key = session.Borrow();
        var plain = JsonSerializer.SerializeToUtf8Bytes(views);
        try { await store.SaveSettingsAsync(VaultCrypto.Encrypt(plain, key.Bytes, "saved-views:v1"), "saved-views"); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public static GalleryFilter Clone(GalleryFilter filter) =>
        JsonSerializer.Deserialize<GalleryFilter>(JsonSerializer.Serialize(filter))
        ?? throw new InvalidOperationException("Could not copy gallery filters.");

    public static string MergeTags(string existing, string additions) =>
        string.Join(", ", (existing + "," + additions).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase));

    public static void SetOrganization(ImageRecord image, int rating, bool reviewed, string? collection)
    {
        if (rating is < 0 or > 5) throw new InvalidOperationException("Rating must be between 0 and 5.");
        if (collection is not null && (string.IsNullOrWhiteSpace(collection) || collection.Trim().Length > 80))
            throw new InvalidOperationException("Collection names must have 1 to 80 characters.");
        image.Rating = rating;
        image.Reviewed = reviewed;
        if (collection is not null && !image.Collections.Contains(collection.Trim(), StringComparer.OrdinalIgnoreCase))
            image.Collections.Add(collection.Trim());
    }

    public static void SetCuration(ImageRecord image, int? rating, bool? reviewed)
    {
        if (rating is < 0 or > 5) throw new InvalidOperationException("Rating must be between 0 and 5.");
        if (rating is not null) image.Rating = rating.Value;
        if (reviewed is not null) image.Reviewed = reviewed.Value;
    }
}
