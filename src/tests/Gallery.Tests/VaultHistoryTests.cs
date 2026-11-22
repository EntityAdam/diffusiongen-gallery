using System.Text.Json;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Tests;

public sealed class VaultHistoryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "gallery-history-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void History_PreservesLegacySelection_AndPersistsDistinctRecentLocations()
    {
        Directory.CreateDirectory(root);
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        File.WriteAllText(Path.Combine(root, "last-vault.json"), JsonSerializer.Serialize(first));
        var history = new VaultHistory(root);
        Assert.Equal([first], history.ReadGalleries());

        history.Save(second);
        new VaultHistory(root).Save(first);

        var reopened = new VaultHistory(root);
        Assert.Equal(first, reopened.Read());
        Assert.Equal([first, second], reopened.ReadGalleries());
    }

    [Fact]
    public void History_ConcurrentScopes_RetainEveryLocation()
    {
        var locations = Enumerable.Range(0, 20).Select(index => Path.Combine(root, index.ToString())).ToArray();
        Parallel.ForEach(locations, location => new VaultHistory(root).Save(location));
        var history = new VaultHistory(root);
        Assert.Equal(locations.Order(), history.ReadGalleries().Order());
        Assert.Contains(history.Read(), locations);
    }

    [Fact]
    public void History_MalformedLegacySelection_DoesNotBlockValidGalleryListAndIsRepairedOnSave()
    {
        Directory.CreateDirectory(root);
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        File.WriteAllText(Path.Combine(root, "galleries.json"), JsonSerializer.Serialize(new[] { first }));
        File.WriteAllText(Path.Combine(root, "last-vault.json"), "{not-json");

        var history = new VaultHistory(root);

        Assert.Null(history.Read());
        Assert.Equal([first], history.ReadGalleries());
        history.Save(second);
        Assert.Equal(second, new VaultHistory(root).Read());
        Assert.Equal([second, first], new VaultHistory(root).ReadGalleries());
    }

    [Fact]
    public void History_MalformedGalleries_ArePreservedAndReplacedOnSave()
    {
        Directory.CreateDirectory(root);
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        File.WriteAllText(Path.Combine(root, "last-vault.json"), JsonSerializer.Serialize(first));
        var malformed = "{not-json";
        File.WriteAllText(Path.Combine(root, "galleries.json"), malformed);

        var history = new VaultHistory(root);

        Assert.Equal([first], history.ReadGalleries());
        var preserved = Directory.GetFiles(root, "galleries.json.corrupt.*.json");
        Assert.Single(preserved);
        Assert.Equal(malformed, File.ReadAllText(preserved[0]));
        history.Save(second);
        Assert.Equal([second, first], new VaultHistory(root).ReadGalleries());
    }

    [Fact]
    public async Task KnownGalleries_TracksCreatedAndOpenedGalleries_AndReadsCurrentNames()
    {
        var first = Path.Combine(root, "first");
        var options = new GalleryRuntimeOptions { StateDirectory = Path.Combine(root, "state") };
        using var gallery = GalleryScope.Create(first, options, new HttpClient());
        await gallery.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "First gallery");
        var second = Path.Combine(first, "second");
        await gallery.Vault.SelectNewVaultAsync(second);
        await gallery.Vault.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "Second gallery");
        await gallery.Vault.CloseVaultAsync();
        await gallery.Vault.OpenExistingVaultAsync(first);
        Assert.Equal(2, gallery.Vault.KnownGalleries.Count);
        Assert.Equal(first, gallery.Vault.KnownGalleries[0].Directory);
        Assert.False(gallery.Vault.IsUnlocked);
        await gallery.Vault.UnlockWithPassphraseAsync(TestVault.Passphrase);
        await gallery.Vault.RenameVaultAsync("Renamed gallery");
        Assert.Equal("Renamed gallery", gallery.Vault.KnownGalleries[0].Name);
        await gallery.Vault.CloseVaultAsync();

        // An invalid selection must not be remembered or replace the current gallery.
        await Assert.ThrowsAsync<InvalidOperationException>(() => gallery.Vault.OpenExistingVaultAsync(Path.Combine(root, "missing")));
        Assert.Equal(2, gallery.Vault.KnownGalleries.Count);
        Assert.Equal(first, gallery.Vault.VaultRoot);

        using var reopened = GalleryScope.Create(second, options, new HttpClient());
        Assert.Equal(first, reopened.Vault.VaultRoot);
        Assert.Equal(2, reopened.Vault.KnownGalleries.Count);
        Assert.Equal("Renamed gallery", reopened.Vault.KnownGalleries[0].Name);

        File.Delete(Path.Combine(second, "vault.json"));
        Assert.Equal("Renamed gallery", Assert.Single(gallery.Vault.KnownGalleries).Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
