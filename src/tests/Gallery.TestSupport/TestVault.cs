using Gallery.Managers.Gateways;
using Gallery.Services.DataManagers;
using Gallery.Services.Sessions;
using Gallery.Services;
using Microsoft.Extensions.Configuration;

namespace Gallery.Tests;

internal sealed class TestVault : IDisposable
{
    public const string Passphrase = "a strong unique test passphrase";
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "gallery-tests-" + Guid.NewGuid().ToString("N"));
    public IConfiguration Configuration { get; }
    public VaultStore Vault { get; }
    public GalleryStore Store { get; }
    public RawGalleryStore RawStore { get; }
    public VaultSession Session { get; } = new();
    public GalleryService Service { get; }

    public TestVault(VideoTools? video = null)
    {
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gallery:DataDirectory"] = Root
        }).Build();
        Vault = new(Root);
        Store = new(Path.Combine(Root, "gallery.db"));
        RawStore = new(Path.Combine(Root, "gallery.db"));
        Service = new(Store, Vault, Session, video);
    }

    public async Task InitializeAsync()
    {
        var created = await Vault.CreateAsync(Passphrase);
        Session.Unlock(created.Key);
    }

    public void Dispose()
    {
        Session.Dispose();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
