using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gallery.Web.Services.Processors;
using Microsoft.Data.Sqlite;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class BackupTests
{
    [Fact]
    public async Task Backup_WhenFullAndIncrementalRun_CreatesLinkedArchivesWithOnlyChangedMedia()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 24)), "first.png", "folder");
        var second = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(24, 16)), "second.png", "folder");
        var destination = Path.Combine(Path.GetTempPath(), "gallery-backup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await fixture.Service.SaveBackupOptionsAsync("local", destination, "", "", "");
            var full = await fixture.Service.CreateFullBackupAsync();
            Assert.Equal(2, full.MediaFilesCopied);
            Assert.Equal(2, full.TotalMediaFiles);
            var fullPath = Path.Combine(destination, full.BackupName);
            Assert.True(File.Exists(fullPath));
            var fullMedia = ReadMediaEntries(fullPath);
            Assert.Equal(2, fullMedia);
            AssertArchiveContains(fullPath, "vault.json");
            AssertArchiveContains(fullPath, "gallery.db");
            var snapshotPath = Path.Combine(Path.GetTempPath(), "gallery-backup-db-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var archive = ZipFile.OpenRead(fullPath))
                    archive.GetEntry("gallery.db")!.ExtractToFile(snapshotPath);
                await using var snapshot = new SqliteConnection($"Data Source={snapshotPath};Pooling=False;Mode=ReadOnly");
                await snapshot.OpenAsync();
                using var countCommand = snapshot.CreateCommand();
                countCommand.CommandText = "SELECT COUNT(*) FROM images";
                Assert.Equal(2L, (long)(await countCommand.ExecuteScalarAsync())!);
            }
            finally
            {
                if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
            }

            await fixture.Service.UpdateAsync(first.Id, image => image with { Description = "Updated caption" });
            var metadataOnly = await fixture.Service.CreateIncrementalBackupAsync();
            Assert.Equal(full.BackupName, ReadParentName(Path.Combine(destination, metadataOnly.BackupName), fixture.Session.Key));
            Assert.Equal(0, ReadMediaEntries(Path.Combine(destination, metadataOnly.BackupName)));

            var third = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(20, 12)), "third.png", "folder");
            var changed = await fixture.Service.CreateIncrementalBackupAsync();
            Assert.Equal(1, changed.MediaFilesCopied);
            Assert.Equal(3, changed.TotalMediaFiles);
            Assert.Equal(metadataOnly.BackupName, ReadParentName(Path.Combine(destination, changed.BackupName), fixture.Session.Key));
            Assert.Equal(1, ReadMediaEntries(Path.Combine(destination, changed.BackupName)));
            Assert.Contains(third.Id, ReadManifestImageIds(Path.Combine(destination, changed.BackupName), fixture.Session.Key));
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task Backup_WhenNothingChanged_SkipsIncrementalBackup()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 24)), "first.png", "folder");
        var destination = Path.Combine(Path.GetTempPath(), "gallery-backup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await fixture.Service.SaveBackupOptionsAsync("local", destination, "", "", "");
            var full = await fixture.Service.CreateFullBackupAsync();
            var filesAfterFull = Directory.GetFiles(destination).Length;

            var skipped = await fixture.Service.CreateIncrementalBackupAsync();
            Assert.True(skipped.Skipped);
            Assert.Equal(full.BackupName, skipped.BackupName);
            Assert.Equal(0, skipped.MediaFilesCopied);
            Assert.Equal(0, skipped.BytesWritten);
            Assert.Equal(filesAfterFull, Directory.GetFiles(destination).Length);
            Assert.Equal(full.BackupName, (await fixture.Service.LoadBackupOptionsAsync()).LastBackupName);

            await fixture.Service.UpdateAsync(first.Id, image => image with { Description = "Changed" });
            var changed = await fixture.Service.CreateIncrementalBackupAsync();
            Assert.False(changed.Skipped);
            Assert.Equal(full.BackupName, ReadParentName(Path.Combine(destination, changed.BackupName), fixture.Session.Key));

            var skippedAgain = await fixture.Service.CreateIncrementalBackupAsync();
            Assert.True(skippedAgain.Skipped);
            Assert.Equal(changed.BackupName, skippedAgain.BackupName);
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task Backup_WhenIncrementalHasNoFullBase_RequiresFullBackupFirst()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var destination = Path.Combine(Path.GetTempPath(), "gallery-backup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await fixture.Service.SaveBackupOptionsAsync("local", destination, "", "", "");
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Service.CreateIncrementalBackupAsync());
            Assert.Contains("full backup", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task Backup_WhenDestinationIsInsideVault_RejectsItBeforeWriting()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var insideVault = Path.Combine(fixture.Root, "backups");
        await fixture.Service.SaveBackupOptionsAsync("local", insideVault, "", "", "");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.CreateFullBackupAsync());

        Assert.Contains("inside", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(insideVault));
    }

    [Fact]
    public async Task Backup_WhenPreviousArchiveManifestIsMissing_RejectsBrokenChain()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var destination = Path.Combine(Path.GetTempPath(), "gallery-backup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await fixture.Service.SaveBackupOptionsAsync("local", destination, "", "", "");
            var full = await fixture.Service.CreateFullBackupAsync();
            File.Delete(Path.Combine(destination, full.BackupName + ".manifest"));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Service.CreateIncrementalBackupAsync());

            Assert.Contains("missing", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task BackupOptions_WhenAzureCredentialsAreSaved_RemainEncryptedAndAreNotReturned()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var connectionString = "DefaultEndpointsProtocol=https;AccountName=fakeaccount;AccountKey="
            + Convert.ToBase64String(new byte[32]) + ";EndpointSuffix=core.windows.net";

        var saved = await fixture.Service.SaveBackupOptionsAsync(
            "azure", "", "gallery-backups", "nightly", connectionString);
        var loaded = await fixture.Service.LoadBackupOptionsAsync();
        var encryptedSettings = await fixture.RawStore.ReadSettingsAsync("backup-settings");
        var ciphertext = Encoding.UTF8.GetString(encryptedSettings!);

        Assert.True(saved.HasAzureCredentials);
        Assert.True(loaded.HasAzureCredentials);
        Assert.Equal("azure", loaded.DestinationType);
        Assert.DoesNotContain(connectionString, ciphertext, StringComparison.Ordinal);
    }

    private static int ReadMediaEntries(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return archive.Entries.Count(entry => entry.FullName.StartsWith("media/", StringComparison.Ordinal));
    }

    private static void AssertArchiveContains(string archivePath, string entryName)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        Assert.NotNull(archive.GetEntry(entryName));
    }

    private static string? ReadParentName(string archivePath, byte[] key)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        using var entry = archive.GetEntry("manifest.enc")!.Open();
        using var buffer = new MemoryStream();
        entry.CopyTo(buffer);
        var plaintext = VaultCrypto.Decrypt(buffer.ToArray(), key, "backup-manifest:v1");
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            return document.RootElement.GetProperty("ParentBackupName").GetString();
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static string[] ReadManifestImageIds(string archivePath, byte[] key)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        using var entry = archive.GetEntry("manifest.enc")!.Open();
        using var buffer = new MemoryStream();
        entry.CopyTo(buffer);
        var plaintext = VaultCrypto.Decrypt(buffer.ToArray(), key, "backup-manifest:v1");
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            return document.RootElement.GetProperty("Media").EnumerateObject()
                .Select(property => property.Name).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}
