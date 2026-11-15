using System.Text.Json;
using Gallery.Core.Services;
using Gallery.Core.Models;
using Gallery.Core.Services.DataManagers;

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
    public async Task KnownGalleries_TracksCreatedAndOpenedGalleries_AndReadsCurrentNames()
    {
        var first = Path.Combine(root, "first");
        var options = new GalleryRuntimeOptions { StateDirectory = Path.Combine(root, "state") };
        using var gallery = GalleryService.Create(first, options, new HttpClient());
        await gallery.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "First gallery");
        var second = Path.Combine(first, "second");
        await gallery.SelectNewVaultAsync(second);
        await gallery.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "Second gallery");
        await gallery.CloseVaultAsync();
        await gallery.OpenExistingVaultAsync(first);
        Assert.Equal(2, gallery.KnownGalleries.Count);
        Assert.Equal(first, gallery.KnownGalleries[0].Directory);
        Assert.False(gallery.IsUnlocked);
        await gallery.UnlockWithPassphraseAsync(TestVault.Passphrase);
        await gallery.RenameVaultAsync("Renamed gallery");
        Assert.Equal("Renamed gallery", gallery.KnownGalleries[0].Name);
        await gallery.CloseVaultAsync();

        // An invalid selection must not be remembered or replace the current gallery.
        await Assert.ThrowsAsync<InvalidOperationException>(() => gallery.OpenExistingVaultAsync(Path.Combine(root, "missing")));
        Assert.Equal(2, gallery.KnownGalleries.Count);
        Assert.Equal(first, gallery.VaultRoot);

        using var reopened = GalleryService.Create(second, options, new HttpClient());
        Assert.Equal(first, reopened.VaultRoot);
        Assert.Equal(2, reopened.KnownGalleries.Count);
        Assert.Equal("Renamed gallery", reopened.KnownGalleries[0].Name);

        File.Delete(Path.Combine(second, "vault.json"));
        Assert.Equal("Renamed gallery", Assert.Single(gallery.KnownGalleries).Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
