using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Stress.Cli;
using Gallery.Stress.Core;

namespace Gallery.Stress;

public static class StressVault
{
    public static string PassphrasePath(string workspace) => Path.Combine(workspace, "passphrase.txt");
    public static GalleryService CreateService(string workspace, string vault) => GalleryService.Create(Safety.CanonicalPath(vault),
        new GalleryRuntimeOptions { StateDirectory = Path.Combine(workspace, "app-state") }, new HttpClient());

    public static async Task<GalleryService> OpenOrCreateAsync(StressOptions options, string vault, CancellationToken token)
    {
        vault = Safety.CanonicalPath(vault);
        Safety.ValidateOutputPath(options.Workspace);
        Safety.ValidateOutputPath(vault);
        Directory.CreateDirectory(options.Workspace);
        var passFile = PassphrasePath(options.Workspace);
        if (!File.Exists(passFile))
        {
            if (File.Exists(Path.Combine(vault, "vault.json"))) throw new InvalidOperationException("Existing vault requires its original workspace credentials.");
            await File.WriteAllTextAsync(passFile, Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)), token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(passFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var passphrase = await File.ReadAllTextAsync(passFile, token);
        var service = CreateService(options.Workspace, vault);
        try
        {
            if (File.Exists(Path.Combine(vault, "vault.json")))
            {
                Safety.RequireStressVault(vault);
                await service.OpenExistingVaultAsync(vault);
                await service.UnlockWithPassphraseAsync(passphrase);
            }
            else
            {
                await service.SelectNewVaultAsync(vault);
                var recovery = await service.CreateVaultAsync(passphrase, passphrase, $"stress-{options.Int("seed", 42)}",
                    new MediaStorageOptions { MediaDirectory = options.Get("media-directory", "") });
                Safety.MarkStressVault(vault);
                await File.WriteAllTextAsync(Path.Combine(options.Workspace, "recovery.txt"), recovery, token);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(options.Workspace, "recovery.txt"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
                await File.WriteAllTextAsync(Path.Combine(vault, "stress-vault.json"), JsonSerializer.Serialize(new { simulated = false }), token);
            }
            await StressResources.RegisterAsync(options.Workspace, vault, "vault", token);
            service.ConfigureAutoLock(0);
            return service;
        }
        catch { service.Dispose(); throw; }
    }

    public static async Task SaveAtomicAsync<T>(string path, T value, CancellationToken token)
    {
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(value), token);
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
