using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core;
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
            views.Add(new(name, filter.Clone()));
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

}
