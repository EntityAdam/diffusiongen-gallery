using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Gallery.Core.Features.Backups;
using Gallery.Core.Features.Backups.Targets;
using Gallery.Core.Features.Vaults.Security;
using Microsoft.Data.Sqlite;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class BackupTests
{
    [Fact]
    public async Task Backup_WhenFullAndIncrementalRun_CreatesLinkedArchivesWithOnlyChangedMedia()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 24)), "first.png", "folder");
        var second = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(24, 16)), "second.png", "folder");
        var destination = Path.Combine(Path.GetTempPath(), "gallery-backup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await fixture.Scope.Backup.SaveBackupOptionsAsync("local", destination, "", "", "");

            // Act - create the baseline archive
            var full = await fixture.Scope.Backup.CreateFullBackupAsync();

            // Assert - gather the full archive evidence
            var fullPath = Path.Combine(destination, full.BackupName);
            var fullMedia = ReadMediaEntries(fullPath);

            // Assert
            Assert.Equal(2, full.MediaFilesCopied);
            Assert.Equal(2, full.TotalMediaFiles);
            Assert.True(File.Exists(fullPath));
            Assert.Equal(2, fullMedia);
            AssertArchiveContains(fullPath, "vault.json");
            AssertArchiveContains(fullPath, "gallery.db");

            // Assert - gather an independent database snapshot count
            var snapshotPath = Path.Combine(Path.GetTempPath(), "gallery-backup-db-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var archive = ZipFile.OpenRead(fullPath))
                    archive.GetEntry("gallery.db")!.ExtractToFile(snapshotPath);
                await using var snapshot = new SqliteConnection($"Data Source={snapshotPath};Pooling=False;Mode=ReadOnly");
                await snapshot.OpenAsync();
                using var countCommand = snapshot.CreateCommand();
                countCommand.CommandText = "SELECT COUNT(*) FROM images";
                var imageCount = (long)(await countCommand.ExecuteScalarAsync())!;

                // Assert
                Assert.Equal(2L, imageCount);
            }
            finally
            {
                if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
            }

            // Arrange - only catalog metadata changes
            await fixture.Scope.Records.UpdateAsync(first.Id, image => image with { Description = "Updated caption" });

            // Act
            var metadataOnly = await fixture.Scope.Backup.CreateIncrementalBackupAsync();

            // Assert
            Assert.Equal(full.BackupName, ReadParentName(Path.Combine(destination, metadataOnly.BackupName), fixture.Session.Key));
            Assert.Equal(0, ReadMediaEntries(Path.Combine(destination, metadataOnly.BackupName)));

            // Arrange - add media after the metadata-only increment
            var third = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(20, 12)), "third.png", "folder");

            // Act
            var changed = await fixture.Scope.Backup.CreateIncrementalBackupAsync();

            // Assert
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
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var first = await fixture.Scope.Ingestion.ImportAsync(new MemoryStream(GalleryTests.MakePng(32, 24)), "first.png", "folder");
        var destination = Path.Combine(Path.GetTempPath(), "gallery-backup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await fixture.Scope.Backup.SaveBackupOptionsAsync("local", destination, "", "", "");
            var full = await fixture.Scope.Backup.CreateFullBackupAsync();
            var filesAfterFull = Directory.GetFiles(destination).Length;

            // Act - no catalog or media changes
            var skipped = await fixture.Scope.Backup.CreateIncrementalBackupAsync();

            // Assert
            Assert.True(skipped.WasSkipped);
            Assert.Equal(full.BackupName, skipped.BackupName);
            Assert.Equal(0, skipped.MediaFilesCopied);
            Assert.Equal(0, skipped.BytesWritten);
            Assert.Equal(filesAfterFull, Directory.GetFiles(destination).Length);
            Assert.Equal(full.BackupName, (await fixture.Scope.Backup.LoadBackupOptionsAsync()).LastBackupName);

            // Arrange - change the catalog to create a new baseline
            await fixture.Scope.Records.UpdateAsync(first.Id, image => image with { Description = "Changed" });

            // Act
            var changed = await fixture.Scope.Backup.CreateIncrementalBackupAsync();

            // Assert
            Assert.False(changed.WasSkipped);
            Assert.Equal(full.BackupName, ReadParentName(Path.Combine(destination, changed.BackupName), fixture.Session.Key));

            // Act - unchanged since the latest increment
            var skippedAgain = await fixture.Scope.Backup.CreateIncrementalBackupAsync();

            // Assert
            Assert.True(skippedAgain.WasSkipped);
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
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var destination = Path.Combine(Path.GetTempPath(), "gallery-backup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await fixture.Scope.Backup.SaveBackupOptionsAsync("local", destination, "", "", "");

            // Act
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Scope.Backup.CreateIncrementalBackupAsync());

            // Assert
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
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var insideVault = Path.Combine(fixture.Root, "backups");
        await fixture.Scope.Backup.SaveBackupOptionsAsync("local", insideVault, "", "", "");

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Scope.Backup.CreateFullBackupAsync());

        // Assert
        Assert.Contains("inside", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(insideVault));
    }

    [Fact]
    public async Task Backup_WhenDestinationAliasResolvesInsideVault_RejectsItAndAllowsASibling()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var alias = Path.Combine(Path.GetTempPath(), "gallery-backup-alias-" + Guid.NewGuid().ToString("N"));
        var sibling = Path.Combine(Path.GetTempPath(), "gallery-backup-sibling-" + Guid.NewGuid().ToString("N"));
        try
        {
            try
            {
                Directory.CreateSymbolicLink(alias, fixture.Root);
            }
            catch (Exception exception) when (OperatingSystem.IsWindows()
                && exception is IOException or UnauthorizedAccessException)
            {
                var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                foreach (var argument in new[] { "/d", "/c", "mklink", "/J", alias, fixture.Root })
                    startInfo.ArgumentList.Add(argument);
                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Could not start junction creation.", exception);
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0,
                    $"Could not create a directory junction: {await output} {await error}");
            }

            var link = new DirectoryInfo(alias);
            Assert.NotNull(link.LinkTarget);
            var resolvedTarget = link.ResolveLinkTarget(returnFinalTarget: true);
            Assert.NotNull(resolvedTarget);
            Assert.True(string.Equals(Path.GetFullPath(fixture.Root).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(resolvedTarget.FullName).TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

            await fixture.Scope.Backup.SaveBackupOptionsAsync("local", alias, "", "", "");
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Scope.Backup.CreateFullBackupAsync());

            Assert.Contains("inside", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFiles(alias, "*.zip"));

            await fixture.Scope.Backup.SaveBackupOptionsAsync("local", sibling, "", "", "");
            var result = await fixture.Scope.Backup.CreateFullBackupAsync();

            Assert.True(File.Exists(Path.Combine(sibling, result.BackupName)));
        }
        finally
        {
            if (Directory.Exists(alias)) Directory.Delete(alias);
            if (Directory.Exists(sibling)) Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public async Task Backup_WhenStoredMediaAuthenticationFails_DoesNotPublishAnArchiveOrAdvanceTheHead()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var media = await fixture.Scope.Ingestion.ImportAsync(
            new MemoryStream(GalleryTests.MakePng(32, 24)), "damaged.png", "folder");
        var destination = Path.Combine(Path.GetTempPath(), "gallery-backup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await fixture.Scope.Backup.SaveBackupOptionsAsync("local", destination, "", "", "");
            var corruptedMedia = await File.ReadAllBytesAsync(media.StoredPath);
            corruptedMedia[^1] ^= 0x01;
            await File.WriteAllBytesAsync(media.StoredPath, corruptedMedia);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Scope.Backup.CreateFullBackupAsync());

            Assert.Contains(media.Name, failure.Message, StringComparison.Ordinal);
            Assert.Contains("authentication", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null((await fixture.Scope.Backup.LoadBackupOptionsAsync()).LastBackupName);
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task Backup_WhenVaultHeaderExceedsRestoreLimit_DoesNotPublishOrAdvanceTheHead()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var destination = Path.Combine(Path.GetTempPath(), "gallery-backup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await fixture.Scope.Backup.SaveBackupOptionsAsync("local", destination, "", "", "");
            var headerPath = Path.Combine(fixture.Root, "vault.json");
            await using (var header = new FileStream(headerPath, FileMode.Open, FileAccess.Write, FileShare.None))
                header.SetLength(BackupArchive.MaximumVaultHeaderBytes + 1);

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Scope.Backup.CreateFullBackupAsync());

            Assert.Contains("header", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Null((await fixture.Scope.Backup.LoadBackupOptionsAsync()).LastBackupName);
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task BackupSettingsSave_WaitsForTheBackupOperationGate()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var destination = Path.Combine(Path.GetTempPath(), "gallery-backup-options-" + Guid.NewGuid().ToString("N"));
        await fixture.Store.BackupGate.WaitAsync();
        var gateHeld = true;
        try
        {
            var saving = fixture.Scope.Backup.SaveBackupOptionsAsync("local", destination, "", "", "");

            Assert.False(saving.IsCompleted);

            fixture.Store.BackupGate.Release();
            gateHeld = false;
            var saved = await saving;
            Assert.Equal(Path.GetFullPath(destination), saved.LocalDirectory);
        }
        finally
        {
            if (gateHeld) fixture.Store.BackupGate.Release();
        }
    }

    [Fact]
    public async Task IncrementalBackup_WhenQueued_UsesTheParentHeadReadAfterGateAdmission()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var image = await fixture.Scope.Ingestion.ImportAsync(
            new MemoryStream(GalleryTests.MakePng(32, 24)), "first.png", "folder");
        var destination = Path.Combine(Path.GetTempPath(), "gallery-backup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await fixture.Scope.Backup.SaveBackupOptionsAsync("local", destination, "", "", "");
            await fixture.Scope.Backup.CreateFullBackupAsync();
            await fixture.Scope.Records.UpdateAsync(image.Id, record => record with { Description = "changed" });

            await fixture.Store.BackupGate.WaitAsync();
            var gateHeld = true;
            try
            {
                var queuedBackup = fixture.Scope.Backup.CreateIncrementalBackupAsync();
                Assert.False(queuedBackup.IsCompleted);
                var settings = await fixture.Store.ReadSettingsAsync<StoredBackupSettings>(
                    StoredBackupSettings.SettingId, StoredBackupSettings.SettingPurpose, fixture.Session.Key);
                await fixture.Store.SaveSettingsAsync(settings! with
                {
                    LastBackupName = "missing-parent.zip"
                }, StoredBackupSettings.SettingId, StoredBackupSettings.SettingPurpose, fixture.Session.Key);

                fixture.Store.BackupGate.Release();
                gateHeld = false;
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => queuedBackup);

                Assert.Contains("missing", failure.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("missing-parent.zip",
                    (await fixture.Scope.Backup.LoadBackupOptionsAsync()).LastBackupName);
            }
            finally
            {
                if (gateHeld) fixture.Store.BackupGate.Release();
            }
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public void BackupChain_IncrementalAppendReservesTheFinalSupportedSlot()
    {
        Assert.True(BackupChain.CanAppendIncremental(BackupChain.MaximumChainLength - 1));
        Assert.False(BackupChain.CanAppendIncremental(BackupChain.MaximumChainLength));
    }

    [Fact]
    public void BackupChain_TracksDuplicatesPerTraversal()
    {
        var target = new BackupTargetLocal(Path.GetTempPath());
        var firstTraversal = new HashSet<string>(StringComparer.Ordinal);
        var secondTraversal = new HashSet<string>(StringComparer.Ordinal);

        Assert.Equal(1, BackupChain.Visit(target, "full.zip", firstTraversal, "invalid chain"));
        Assert.Throws<InvalidOperationException>(
            () => BackupChain.Visit(target, "full.zip", firstTraversal, "invalid chain"));
        Assert.Equal(1, BackupChain.Visit(target, "full.zip", secondTraversal, "invalid chain"));
    }

    [Fact]
    public async Task Backup_WhenPreviousArchiveManifestIsMissing_RejectsBrokenChain()
    {
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var destination = Path.Combine(Path.GetTempPath(), "gallery-backup-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await fixture.Scope.Backup.SaveBackupOptionsAsync("local", destination, "", "", "");
            var full = await fixture.Scope.Backup.CreateFullBackupAsync();
            File.Delete(Path.Combine(destination, full.BackupName + ".manifest"));


            // Act
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Scope.Backup.CreateIncrementalBackupAsync());

            // Assert
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
        // Arrange
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var connectionString = "DefaultEndpointsProtocol=https;AccountName=fakeaccount;AccountKey="
            + Convert.ToBase64String(new byte[32]) + ";EndpointSuffix=core.windows.net";

        // Act
        var saved = await fixture.Scope.Backup.SaveBackupOptionsAsync(
            "azure", "", "gallery-backups", "nightly", connectionString);

        // Assert - gather persisted options and their ciphertext
        var loaded = await fixture.Scope.Backup.LoadBackupOptionsAsync();
        var encryptedSettings = await fixture.RawStore.ReadSettingsAsync("backup-settings");
        var ciphertext = Encoding.UTF8.GetString(encryptedSettings!);

        // Assert
        Assert.True(saved.HasAzureCredentials);
        Assert.True(loaded.HasAzureCredentials);
        Assert.Equal("azure", loaded.DestinationType);
        Assert.DoesNotContain(connectionString, ciphertext, StringComparison.Ordinal);

        var savedWithExistingCredentials = await fixture.Scope.Backup.SaveBackupOptionsAsync(
            "azure", "", "gallery-backups", "nightly", "");
        Assert.True(savedWithExistingCredentials.HasAzureCredentials);
    }

    [Theory]
    [InlineData("local", "", "", "..", "", "Azure backup prefixes")]
    [InlineData("local", "", "", "", "invalid", "local folder")]
    [InlineData("azure", "", "", "", "", "container name")]
    [InlineData("azure", "", "bad!", "", "", "connection string")]
    [InlineData("azure", "", "bad!", "", "invalid", "only lowercase")]
    [InlineData("azure", "", "valid-name", "", "invalid", "connection string is invalid")]
    public async Task BackupOptions_WhenMultipleFieldsAreInvalid_PreservesValidationOrder(
        string type, string localDirectory, string container, string prefix, string connection, string expectedMessage)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Scope.Backup.SaveBackupOptionsAsync(type, localDirectory, container, prefix, connection));

        Assert.Contains(expectedMessage, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False((await fixture.Scope.Backup.LoadBackupOptionsAsync()).HasAzureCredentials);
    }

    private static int ReadMediaEntries(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return archive.Entries.Count(entry => entry.FullName.StartsWith("media/", StringComparison.Ordinal));
    }

    private static void AssertArchiveContains(string archivePath, string entryName)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var actualEntry = archive.GetEntry(entryName);
        Assert.True(actualEntry is not null,
            $"Expected archive '{archivePath}' to contain entry '{entryName}'; Actual: <missing entry>.");
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
