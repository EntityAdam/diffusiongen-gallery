using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class BackupParentIntegrityTests
{
    [Fact]
    public async Task IncrementalBackup_WhenParentZipIsTruncatedButSidecarIsIntact_DoesNotPublishChild()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 24)), "first.png", "folder");
        var destination = Path.Combine(Directory.GetCurrentDirectory(), "gallery-backup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await fixture.Service.SaveBackupOptionsAsync("local", destination, "", "", "");
            var parent = await fixture.Service.CreateFullBackupAsync();
            var archivePath = Path.Combine(destination, parent.BackupName);
            var sidecarPath = archivePath + ".manifest";
            await using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Write))
                stream.SetLength(stream.Length / 2);
            var sidecar = await File.ReadAllBytesAsync(sidecarPath);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Service.CreateIncrementalBackupAsync());

            Assert.Contains("damaged", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(sidecar, await File.ReadAllBytesAsync(sidecarPath));
            Assert.Equal(parent.BackupName, (await fixture.Service.LoadBackupOptionsAsync()).LastBackupName);
            Assert.Equal(new[] { parent.BackupName, parent.BackupName + ".manifest" },
                Directory.GetFiles(destination).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task IncrementalBackup_WhenParentIsMissingReferencedMedia_DoesNotPublishChild()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await fixture.Service.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 24)), "first.png", "folder");
        var destination = Path.Combine(Directory.GetCurrentDirectory(), "gallery-backup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await fixture.Service.SaveBackupOptionsAsync("local", destination, "", "", "");
            var parent = await fixture.Service.CreateFullBackupAsync();
            using (var archive = ZipFile.Open(Path.Combine(destination, parent.BackupName), ZipArchiveMode.Update))
                archive.Entries.Single(entry => entry.FullName.StartsWith("media/", StringComparison.Ordinal)).Delete();

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Service.CreateIncrementalBackupAsync());

            Assert.Contains("missing referenced media", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(parent.BackupName, (await fixture.Service.LoadBackupOptionsAsync()).LastBackupName);
            Assert.Equal(2, Directory.GetFiles(destination).Length);
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Theory]
    [InlineData("vault.json")]
    [InlineData("gallery.db")]
    public async Task IncrementalBackup_WhenParentMetadataDisagreesWithManifest_DoesNotPublishChild(string entryName)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var destination = Path.Combine(Directory.GetCurrentDirectory(), "gallery-backup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await fixture.Service.SaveBackupOptionsAsync("local", destination, "", "", "");
            var parent = await fixture.Service.CreateFullBackupAsync();
            var replacement = Path.Combine(destination, "replacement.db");
            if (entryName == "gallery.db")
            {
                await using var connection = new SqliteConnection($"Data Source={replacement};Pooling=False");
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE changed (id INTEGER)";
                await command.ExecuteNonQueryAsync();
            }
            using (var archive = ZipFile.Open(Path.Combine(destination, parent.BackupName), ZipArchiveMode.Update))
            {
                archive.GetEntry(entryName)!.Delete();
                var entry = archive.CreateEntry(entryName);
                await using var output = entry.Open();
                if (entryName == "vault.json")
                    await output.WriteAsync("{}"u8.ToArray());
                else
                {
                    await using var input = File.OpenRead(replacement);
                    await input.CopyToAsync(output);
                }
            }
            if (File.Exists(replacement)) File.Delete(replacement);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Service.CreateIncrementalBackupAsync());

            Assert.Contains("damaged", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(parent.BackupName, (await fixture.Service.LoadBackupOptionsAsync()).LastBackupName);
            Assert.Equal(2, Directory.GetFiles(destination).Length);
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }
}
