using Gallery.Core.Features.Ingestion.Gateways;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Features.Vision.Endpoints;
using Gallery.Core.Infrastructure.Persistence.Sqlite;
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
    public GalleryScope Scope { get; }

    public TestVault(VideoTools? video = null, VisionManager? vision = null)
    {
        Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gallery:DataDirectory"] = Root,
            ["Gallery:StateDirectory"] = Root + "-state"
        }).Build();
        Vault = new(Root);
        Store = new(Path.Combine(Root, "gallery.db"));
        RawStore = new(Path.Combine(Root, "gallery.db"));
        Scope = new(Store, Vault, Session, video, vision);
    }

    public async Task InitializeAsync()
    {
        var created = await Vault.CreateVaultAsync(Passphrase);
        Session.Unlock(created.Key);
    }

    public void Dispose()
    {
        Scope.Dispose();
        // Disposal cancels the background drive player; let its in-flight operation finish before deleting files.
        Scope.DriveOperations.WaitForDriveOperationsAsync().GetAwaiter().GetResult();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        if (Directory.Exists(Root + "-state")) Directory.Delete(Root + "-state", recursive: true);
    }
}
