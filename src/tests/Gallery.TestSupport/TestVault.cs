using Gallery.Core.Services.Gateways;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Sessions;
using Gallery.Core.Services;
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
            ["Gallery:DataDirectory"] = Root,
            ["Gallery:StateDirectory"] = Root + "-state"
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
        Service.Dispose();
        // Disposal cancels the background drive player; let its in-flight operation finish before deleting files.
        Service.WaitForDriveOperationsAsync().GetAwaiter().GetResult();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        if (Directory.Exists(Root + "-state")) Directory.Delete(Root + "-state", recursive: true);
    }
}
