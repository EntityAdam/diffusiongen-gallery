using System.Net;
using Gallery.Core.Exceptions;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Sessions;
using Microsoft.Data.Sqlite;

namespace Gallery.Tests;

[Trait("Category", "acceptance")]
public sealed class IngestionPerformanceTests
{
    [Theory]
    [InlineData(SourceRetention.Retain)]
    [InlineData(SourceRetention.DeleteAfterVerification)]
    public async Task FolderImport_WhenReopened_ThenRetainsOriginalSourcePathIncludingSubfolders(SourceRetention sourceRetention)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var sourceRoot = Path.Combine(fixture.Root, "source");
        var sourceFolder = Path.Combine(sourceRoot, "nested");
        Directory.CreateDirectory(sourceFolder);
        var sourcePath = Path.Combine(sourceFolder, "original.png");
        await File.WriteAllBytesAsync(sourcePath, GalleryTests.MakePng());
        var sourceCreatedAt = File.GetCreationTimeUtc(sourcePath);

        await fixture.Service.ImportFolderAsync(sourceRoot, FolderSearchScope.Descendants,
            sourceRetention: sourceRetention);
        await fixture.Service.CloseVaultAsync();
        using var reopenedSession = new VaultSession();
        using var reopened = new GalleryService(new GalleryStore(Path.Combine(fixture.Root, "gallery.db")),
            new VaultStore(fixture.Root), reopenedSession);
        await reopened.OpenExistingVaultAsync(fixture.Root);
        await reopened.UnlockWithPassphraseAsync(TestVault.Passphrase);
        var record = Assert.Single(await reopened.ListAsync());

        Assert.Equal(sourcePath, record.SourcePath);
        Assert.Equal(new DateTimeOffset(sourceCreatedAt), record.SourceCreatedAt);
        Assert.Equal(sourceFolder, record.SourceFolder);
        Assert.Equal("original.png", record.OriginalName);
        Assert.NotEqual(sourcePath, record.StoredPath);
        Assert.True(File.Exists(record.StoredPath));
        Assert.Equal(sourceRetention == SourceRetention.Retain, File.Exists(sourcePath));
    }

    [Fact]
    public async Task FolderImport_UsesSavedWorkersAndCommitsBoundedBatches_WithCompleteTimings()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var folder = await CreateSourcesAsync(fixture, 5);
        await fixture.Service.SaveIngestionOptionsAsync(new() { Workers = 3, CatalogBatchSize = 2 });
        IngestionTimingReport? completedTimingReport = null;
        ImportProgress? final = null;

        await fixture.Service.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder,
            sourceRetention: SourceRetention.DeleteAfterVerification, reportTimings: report => completedTimingReport = report,
            reportProgress: update => { final = update; return Task.CompletedTask; });

        var report = completedTimingReport
            ?? throw new Xunit.Sdk.XunitException("Expected a completed ingestion timing report; Actual: null.");
        Assert.Equal(3, report.Workers);
        Assert.Equal(5, report.Imported);
        Assert.Equal(0, report.Failed);
        Assert.Equal(3, report.Stages.Single(stage => stage.Stage == IngestionStage.CatalogCommit).Count);
        foreach (var stage in new[] { IngestionStage.OpenSource, IngestionStage.CloseSource,
            IngestionStage.WriteMedia, IngestionStage.Verify, IngestionStage.DeleteSource,
            IngestionStage.DuplicateWait, IngestionStage.DuplicateCheck })
            Assert.Equal(5, report.Stages.Single(timing => timing.Stage == stage).Count);
        Assert.Contains(report.Stages, stage => stage.Stage == IngestionStage.ReportProgress);
        Assert.DoesNotContain(folder, report.ToString());
        Assert.NotEqual("Unknown", report.CatalogDrive);
        Assert.Equal(new ImportProgress(5, 5, 5, 0, "Ingestion complete."), final);
        Assert.Empty(Directory.EnumerateFiles(folder));
        Assert.Equal(5, (await fixture.Service.ListAsync()).Count);
    }

    [Fact]
    public async Task FolderImport_WhenBatchInsertFails_RollsBackEveryRowAndRetainsEverySource()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var folder = await CreateSourcesAsync(fixture, 3);
        await using var connection = await OpenDatabaseAsync(fixture);
        using var trigger = connection.CreateCommand();
        trigger.CommandText = """
            CREATE TRIGGER reject_second BEFORE INSERT ON images
            WHEN (SELECT COUNT(*) FROM images) = 1
            BEGIN SELECT RAISE(ABORT, 'reject second insert'); END
            """;
        await trigger.ExecuteNonQueryAsync();

        var expectedFailure = ExpectedExceptionContract.Create(typeof(ImportFolderException),
            [folder, "file failures"], HttpStatusCode.InternalServerError, Severity.Error,
            "One or more files could not be imported.", "FolderImport.Failed",
            new Dictionary<string, object?> { ["Folder"] = folder, ["FailureCount"] = 3 });
        var failure = await Assert.ThrowsAsync<ImportFolderException>(() =>
            fixture.Service.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder,
                sourceRetention: SourceRetention.DeleteAfterVerification,
                options: new() { Workers = 3, CatalogBatchSize = 3 }));
        AsserterExceptionContract.AssertMatches(expectedFailure, failure);

        Assert.Equal(3, failure.Failures.InnerExceptions.Count);
        Assert.Empty(await fixture.Service.ListAsync());
        Assert.Equal(3, Directory.EnumerateFiles(folder).Count());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Root, "images"), "*.dpng", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task FolderImport_WhenLaterBatchFails_RetainsEarlierCommittedImports()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var folder = await CreateSourcesAsync(fixture, 4);
        await using var connection = await OpenDatabaseAsync(fixture);
        using var trigger = connection.CreateCommand();
        trigger.CommandText = """
            CREATE TRIGGER reject_after_two BEFORE INSERT ON images
            WHEN (SELECT COUNT(*) FROM images) >= 2
            BEGIN SELECT RAISE(ABORT, 'reject subsequent batch'); END
            """;
        await trigger.ExecuteNonQueryAsync();

        var expectedFailure = ExpectedExceptionContract.Create(typeof(ImportFolderException),
            [folder, "file failures"], HttpStatusCode.InternalServerError, Severity.Error,
            "One or more files could not be imported.", "FolderImport.Failed",
            new Dictionary<string, object?> { ["Folder"] = folder, ["FailureCount"] = 2 });
        var failure = await Assert.ThrowsAsync<ImportFolderException>(() =>
            fixture.Service.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder,
                sourceRetention: SourceRetention.DeleteAfterVerification,
                options: new() { Workers = 2, CatalogBatchSize = 2 }));
        AsserterExceptionContract.AssertMatches(expectedFailure, failure);

        Assert.Equal(2, failure.Failures.InnerExceptions.Count);
        Assert.Equal(2, (await fixture.Service.ListAsync()).Count);
        Assert.Equal(2, Directory.EnumerateFiles(folder).Count());
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(fixture.Root, "images"), "*.dpng", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task FolderImport_WhenCommittedThumbnailIsCorrupt_KeepsSourceAndReportsVerificationFailure()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var folder = await CreateSourcesAsync(fixture, 1);
        await using var connection = await OpenDatabaseAsync(fixture);
        using var trigger = connection.CreateCommand();
        trigger.CommandText = """
            CREATE TRIGGER corrupt_thumbnail AFTER INSERT ON images
            BEGIN UPDATE images SET thumbnail = zeroblob(length(NEW.thumbnail)) WHERE id = NEW.id; END
            """;
        await trigger.ExecuteNonQueryAsync();
        ImportProgress? final = null;

        var expectedFailure = ExpectedExceptionContract.Create(typeof(ImportFolderException),
            [folder, "file failures"], HttpStatusCode.InternalServerError, Severity.Error,
            "One or more files could not be imported.", "FolderImport.Failed",
            new Dictionary<string, object?> { ["Folder"] = folder, ["FailureCount"] = 1 });
        var failure = await Assert.ThrowsAsync<ImportFolderException>(() =>
            fixture.Service.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder,
                sourceRetention: SourceRetention.DeleteAfterVerification,
                reportProgress: update => { final = update; return Task.CompletedTask; }));
        AsserterExceptionContract.AssertMatches(expectedFailure, failure);

        Assert.Single(Directory.EnumerateFiles(folder));
        Assert.Single(await fixture.Service.ListAsync());
        Assert.Equal(1, final!.Imported);
        Assert.Equal(1, final.Failed);
    }

    [Fact]
    public async Task FolderImport_WhenBatchContainsDuplicates_DeletesOnlyTheCommittedSource()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var folder = Path.Combine(fixture.Root, "source");
        Directory.CreateDirectory(folder);
        var bytes = GalleryTests.MakePng();
        await File.WriteAllBytesAsync(Path.Combine(folder, "one.png"), bytes);
        await File.WriteAllBytesAsync(Path.Combine(folder, "two.png"), bytes);

        var expectedFailure = ExpectedExceptionContract.Create(typeof(ImportFolderException),
            [folder, "file failures"], HttpStatusCode.InternalServerError, Severity.Error,
            "One or more files could not be imported.", "FolderImport.Failed",
            new Dictionary<string, object?> { ["Folder"] = folder, ["FailureCount"] = 1 });
        var failure = await Assert.ThrowsAsync<ImportFolderException>(() =>
            fixture.Service.ImportFolderAsync(folder, FolderSearchScope.SelectedFolder,
                sourceRetention: SourceRetention.DeleteAfterVerification,
                options: new() { Workers = 2, CatalogBatchSize = 2 }));
        AsserterExceptionContract.AssertMatches(expectedFailure, failure);

        var imported = Assert.Single(await fixture.Service.ListAsync());
        Assert.False(File.Exists(imported.SourcePath));
        var retained = Assert.Single(Directory.EnumerateFiles(folder));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(retained));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Root, "images"), "*.dpng", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SourceHandle_DeniesWritersAndReplacements_AndDeletesTheHeldFileOnlyOnClose()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new TestVault();
        var folder = await CreateSourcesAsync(fixture, 1);
        var path = Assert.Single(Directory.EnumerateFiles(folder));
        var source = new ImportSource(path, deleteAfterVerification: true);
        try
        {
            Assert.True(source.CanDeleteByHandle);
            Assert.Throws<IOException>(() => File.WriteAllBytes(path, [1]));
            Assert.Throws<IOException>(() => File.Move(path, path + ".replaced"));
            source.DeleteVerified();
        }
        finally { await source.DisposeAsync(); }
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(33, 16)]
    [InlineData(4, 0)]
    [InlineData(4, 65)]
    public async Task Settings_RejectOutOfRangeWorkersOrBatches(int workers, int batchSize)
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var expectedPhrases = workers is < 1 or > 32
            ? new[] { workers.ToString(System.Globalization.CultureInfo.InvariantCulture), "1", "32", "workers" }
            : new[] { batchSize.ToString(System.Globalization.CultureInfo.InvariantCulture), "1", "64", "batch" };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SaveIngestionOptionsAsync(new() { Workers = workers, CatalogBatchSize = batchSize }));
        AsserterExceptionContract.AssertMessageContains(expectedPhrases, error.Message);
    }

    private static async Task<string> CreateSourcesAsync(TestVault fixture, int count)
    {
        var folder = Path.Combine(fixture.Root, "source");
        Directory.CreateDirectory(folder);
        for (var index = 0; index < count; index++)
            await File.WriteAllBytesAsync(Path.Combine(folder, $"image-{index}.png"), GalleryTests.MakePng(20 + index, 10));
        return folder;
    }

    private static async Task<SqliteConnection> OpenDatabaseAsync(TestVault fixture)
    {
        await fixture.Store.InitializeAsync();
        var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Root, "gallery.db")};Pooling=False");
        await connection.OpenAsync();
        return connection;
    }
}
