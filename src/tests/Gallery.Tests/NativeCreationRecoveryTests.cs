using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class NativeCreationRecoveryTests
{
    [Theory]
    [InlineData("receipt", false)]
    [InlineData("catalog", false)]
    [InlineData("catalog", true)]
    public async Task Creation_WhenProcessStopsDuringPublication_ThenRetryReturnsUsableRecovery(
        string phase, bool selectDestination)
    {
        using var fixture = new TestVault();
        Directory.CreateDirectory(fixture.Root);
        var sentinel = Path.Combine(fixture.Root, "keep.txt");
        if (!selectDestination) await File.WriteAllTextAsync(sentinel, "caller-owned");
        await InterruptCreationAsync(fixture.Root, phase);
        Assert.True(File.Exists(Path.Combine(fixture.Root, ".vault-create.json")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "vault.json")));
        Assert.Equal(phase == "catalog", File.Exists(Path.Combine(fixture.Root, "gallery.db")));

        if (selectDestination) await fixture.Service.SelectNewVaultAsync(fixture.Root);
        var recovery = await fixture.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "Retried creation");
        fixture.Service.LockVault();
        await fixture.Service.UnlockWithRecoveryPhraseAsync(recovery);

        Assert.True(fixture.Service.IsUnlocked);
        Assert.Equal("Retried creation", fixture.Service.VaultName);
        Assert.Empty(await fixture.Service.ListAsync());
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".vault-create.json")));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root, ".vault-create-*"));
        if (!selectDestination) Assert.Equal("caller-owned", await File.ReadAllTextAsync(sentinel));
        await fixture.Service.CloseVaultAsync();
    }

    [Fact]
    public async Task Creation_WhenHeaderWasPublishedBeforeProcessStopped_ThenRetainsTheCompletedVault()
    {
        using var fixture = new TestVault();
        await InterruptCreationAsync(fixture.Root, "header");
        var catalog = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "gallery.db"));
        var header = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "vault.json"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase));
        Assert.Equal(catalog, await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "gallery.db")));
        Assert.Equal(header, await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "vault.json")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".vault-create.json")));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Root, ".vault-create-*"));
        await fixture.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);
        Assert.Equal("Interrupted creation", fixture.Service.VaultName);
        await fixture.Service.CloseVaultAsync();
    }

    [Theory]
    [InlineData("gallery.db")]
    [InlineData("gallery.db-wal")]
    [InlineData("gallery.db-shm")]
    public async Task Creation_WhenInterruptedPublicationContainsChangedFiles_ThenPreservesTheConflict(string changedFile)
    {
        using var fixture = new TestVault();
        await InterruptCreationAsync(fixture.Root, "catalog");
        var changedPath = Path.Combine(fixture.Root, changedFile);
        await File.WriteAllTextAsync(changedPath, "caller-owned replacement");

        var failure = await Assert.ThrowsAsync<IOException>(() =>
            fixture.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase));

        Assert.Contains("preserved", failure.Message);
        Assert.Equal("caller-owned replacement", await File.ReadAllTextAsync(changedPath));
        Assert.True(File.Exists(Path.Combine(fixture.Root, ".vault-create.json")));
        Assert.Single(Directory.EnumerateDirectories(fixture.Root, ".vault-create-*"));
        await fixture.Service.CloseVaultAsync();
    }

    [Fact]
    public async Task Creation_WhenCatalogExistsWithoutAReceipt_ThenPreservesItsBytes()
    {
        using var fixture = new TestVault();
        Directory.CreateDirectory(fixture.Root);
        var catalog = Path.Combine(fixture.Root, "gallery.db");
        await File.WriteAllTextAsync(catalog, "caller-owned catalog");

        await Assert.ThrowsAsync<IOException>(() =>
            fixture.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase));

        Assert.Equal("caller-owned catalog", await File.ReadAllTextAsync(catalog));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "vault.json")));
        await fixture.Service.CloseVaultAsync();
    }

    [Theory]
    [InlineData("history")]
    [InlineData("existing-catalog")]
    [InlineData("header-publication")]
    public async Task Creation_WhenSetupFails_ThenPreservesExistingFilesAndRetryDeliversUsableRecovery(string failure)
    {
        var root = Path.Combine(Path.GetTempPath(), "gallery-create-recovery-" + Guid.NewGuid().ToString("N"));
        var state = root + "-state";
        var media = root + "-media";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(media);
        var sentinel = Path.Combine(root, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "caller-owned");
        if (failure == "history") await File.WriteAllTextAsync(state, "block history directory");
        else Directory.CreateDirectory(Path.Combine(root, failure == "existing-catalog" ? "gallery.db" : "vault.json"));
        using var service = GalleryService.Create(root, new GalleryRuntimeOptions { StateDirectory = state }, new HttpClient());
        try
        {
            var storage = new MediaStorageOptions { MediaDirectory = media };
            await Assert.ThrowsAnyAsync<IOException>(() => service.CreateVaultAsync(
                TestVault.Passphrase, TestVault.Passphrase, "Recovery test", storage));

            Assert.False(service.VaultExists);
            Assert.False(service.IsUnlocked);
            Assert.Equal("caller-owned", await File.ReadAllTextAsync(sentinel));
            if (failure == "history")
            {
                Assert.True(File.Exists(state));
                Assert.False(File.Exists(Path.Combine(root, "gallery.db")));
                File.Delete(state);
            }
            else
            {
                var blocker = Path.Combine(root, failure == "existing-catalog" ? "gallery.db" : "vault.json");
                Assert.True(Directory.Exists(blocker));
                if (failure == "header-publication") Assert.False(File.Exists(Path.Combine(root, "gallery.db")));
                Directory.Delete(blocker);
            }
            Assert.Empty(Directory.EnumerateDirectories(root, ".vault-create-*"));

            var recovery = await service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase, "Recovery test", storage);
            service.LockVault();
            await service.UnlockWithRecoveryPhraseAsync(recovery);

            Assert.True(service.IsUnlocked);
            Assert.Equal("Recovery test", service.VaultName);
            Assert.Equal(storage, await service.LoadMediaStorageOptionsAsync());
            Assert.Equal("caller-owned", await File.ReadAllTextAsync(sentinel));
            await service.CloseVaultAsync();
        }
        finally
        {
            await service.CloseVaultAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            if (Directory.Exists(state)) Directory.Delete(state, recursive: true);
            if (File.Exists(state)) File.Delete(state);
            if (Directory.Exists(media)) Directory.Delete(media, recursive: true);
        }
    }

    private static async Task InterruptCreationAsync(string root, string phase)
    {
        Directory.CreateDirectory(root);
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList =
            {
                Path.Combine(AppContext.BaseDirectory, "ProcessMediator", "Gallery.ProcessMediator.dll"),
                "interrupt-creation", root, phase
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false
        })!;
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            var ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if (ready != "ready")
            {
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync();
                Assert.Fail($"Publication process did not become ready: {ready}; {await standardError}");
            }
            process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                await process.WaitForExitAsync();
            }
        }
    }
}
