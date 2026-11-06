using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Gallery.Core.Exceptions;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Services.DataManagers;
using Gallery.Core.Services.Ports.Models;
using Gallery.Core.Services.DataManagers.Turso;
using Gallery.Core.Services.Ports;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Processors.Codecs;
using Microsoft.Data.Sqlite;
using Gallery.Migrations.Turso;

namespace Gallery.Tests;

public sealed class CatalogAdapterTests
{
    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task Catalog_roundtrips_all_fields_and_commits_file_work_atomically(CatalogBackend backend)
    {
        using var fixture = new AdapterFixture(backend);
        await fixture.Store.InitializeAsync(fixture.Key);
        var record = Record();
        using var thumbnail = new ImageThumbnail([1, 2, 3, 4]);
        await fixture.Store.InsertAsync(record, thumbnail, fixture.Key);
        Assert.Equal(1L, await fixture.Store.CountRecordsAsync(fixture.Key));
        AsserterImageRecord.AssertValuesMatch(ExpectedImageRecord.CreateFrom(record),
            await fixture.Store.ReadRecordAsync(record.Id, fixture.Key));
        using (var actual = await fixture.Store.ReadThumbnailAsync(record.Id, fixture.Key))
            Assert.Equal(thumbnail.Content.ToArray(), actual.Content.ToArray());
        var update = record with { Rating = 5, UserDescription = "changed private notes", Collections = ["Other"], Generation = null };
        var work = DriveOperationProcessor.Create(DriveOperationKind.DeleteFile, record.StoredPath, record.Id, fixture.Key);
        await fixture.Store.UpdateImageAndEnqueueAsync(update, fixture.Key, work);
        AsserterImageRecord.AssertValuesMatch(ExpectedImageRecord.CreateFrom(update),
            await fixture.Store.ReadRecordAsync(record.Id, fixture.Key));
        Assert.Equal(1, (await fixture.Store.ReadDriveQueueCountsAsync(fixture.Key)).Pending);
        await fixture.Store.DeleteImageAndEnqueueAsync(record.Id, work, fixture.Key);
        Assert.Null(await fixture.Store.TryReadRecordAsync(record.Id, fixture.Key));
        Assert.Equal(0L, await fixture.Store.CountRecordsAsync(fixture.Key));
        Assert.Equal(1, (await fixture.Store.ReadDriveQueueCountsAsync(fixture.Key)).Pending);
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task Import_batch_handles_duplicates_and_rolls_back_on_failure(CatalogBackend backend)
    {
        using var fixture = new AdapterFixture(backend);
        await fixture.Store.InitializeAsync(fixture.Key);
        await using var operation = await fixture.Store.OpenOperationAsync(fixture.Key);
        var first = Record();
        var duplicate = first with { Id = Guid.NewGuid().ToString("N") };
        ImportCatalogEntry Entry(ImageRecord record) => new(record, VaultCrypto.Encrypt([1, 2, 3], fixture.Key, $"thumbnail:{record.Id}"));
        var committed = await fixture.Store.CommitImportsAsync([Entry(first), Entry(duplicate)], fixture.Key);
        Assert.Equal(new[] { first.Id }, committed.ImportedIds.ToArray());
        Assert.Equal(new[] { duplicate.Id }, committed.DuplicateIds.ToArray());
        var second = Record();
        var conflicting = Record() with { Id = first.Id };
        await Assert.ThrowsAsync<ImportCatalogWriteException>(() => fixture.Store.CommitImportsAsync([Entry(second), Entry(conflicting)], fixture.Key));
        Assert.Null(await fixture.Store.TryReadRecordAsync(second.Id, fixture.Key));
        Assert.Single(await fixture.Store.ReadRecordsAsync(fixture.Key));
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task Snapshot_preserves_logical_fingerprint_and_excludes_backup_settings(CatalogBackend backend)
    {
        using var fixture = new AdapterFixture(backend);
        await fixture.Store.InitializeAsync(fixture.Key);
        await using var operation = await fixture.Store.OpenOperationAsync(fixture.Key);
        using var thumbnail = new ImageThumbnail([1, 2, 3]);
        await fixture.Store.InsertAsync(Record(), thumbnail, fixture.Key);
        await fixture.Store.SaveSettingsAsync(new { Text = "private snapshot settings sentinel" }, "included", "included", fixture.Key);
        var snapshot = Path.Combine(fixture.Root, "snapshot.db");
        await fixture.Store.CreateSnapshotAsync(snapshot, fixture.Key);
        var original = Path.Combine(fixture.Root, "gallery.db");
        var expected = await fixture.Store.ComputeSnapshotFingerprintAsync(original, "backup", fixture.Key);
        Assert.Equal(expected, await fixture.Store.ComputeSnapshotFingerprintAsync(snapshot, "backup", fixture.Key));
        await fixture.Store.SaveSettingsAsync(new { Text = "changed backup state" }, "backup", "backup", fixture.Key);
        Assert.Equal(expected, await fixture.Store.ComputeSnapshotFingerprintAsync(original, "backup", fixture.Key));
        await operation.DisposeAsync();
        foreach (var file in Directory.EnumerateFiles(fixture.Root))
            Assert.DoesNotContain("private snapshot settings sentinel", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
    }

    [Theory]
    [InlineData(CatalogBackend.Sqlite)]
    [InlineData(CatalogBackend.Turso)]
    public async Task Failed_queue_write_rolls_back_the_related_catalog_edit(CatalogBackend backend)
    {
        using var fixture = new AdapterFixture(backend);
        await fixture.Store.InitializeAsync(fixture.Key);
        var record = Record();
        using var thumbnail = new ImageThumbnail([1, 2, 3]);
        await fixture.Store.InsertAsync(record, thumbnail, fixture.Key);
        var invalidWork = new NewDriveOperation(null!, new byte[32], record.Id, [1, 2, 3]);
        var error = await Xunit.Record.ExceptionAsync(() => fixture.Store.UpdateImageAndEnqueueAsync(
            record with { Rating = 5, UserDescription = "must roll back" }, fixture.Key, invalidWork));
        Assert.NotNull(error);
        AsserterImageRecord.AssertValuesMatch(ExpectedImageRecord.CreateFrom(record),
            await fixture.Store.ReadRecordAsync(record.Id, fixture.Key));
        Assert.Equal(0, (await fixture.Store.ReadDriveQueueCountsAsync(fixture.Key)).Pending);
    }

    [Fact]
    public async Task Turso_uses_engine_encryption_and_typed_columns_and_rejects_wrong_keys()
    {
        using var fixture = new AdapterFixture(CatalogBackend.Turso);
        await fixture.Store.InitializeAsync(fixture.Key);
        var record = Record();
        using var thumbnail = new ImageThumbnail([1, 2, 3]);
        await fixture.Store.InsertAsync(record, thumbnail, fixture.Key);
        await Assert.ThrowsAsync<CatalogStorageException>(() => fixture.Store.ReadRecordsAsync(RandomNumberGenerator.GetBytes(32)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.ReadStorageRevisionAsync(null!));
        var file = Path.Combine(fixture.Root, "gallery.db");
        Assert.Equal("Turso", Encoding.ASCII.GetString((await File.ReadAllBytesAsync(file))[..5]));
        foreach (var path in Directory.EnumerateFiles(fixture.Root))
            Assert.DoesNotContain(record.Metadata, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
        await using var rawSqlite = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await rawSqlite.OpenAsync();
        using var command = rawSqlite.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master";
        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteScalarAsync());
        await rawSqlite.CloseAsync();
        // Query with the native engine and the same domain-separated key, proving this is a typed SQL row.
        var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, fixture.Key, 32, info: "DiffusionGenGallery/catalog/turso/v1"u8.ToArray());
        try
        {
            var builder = new System.Data.Common.DbConnectionStringBuilder
            {
                ["Data Source"] = file, ["Pooling"] = false,
                ["Encryption Cipher"] = "aes256gcm", ["Encryption Key"] = Convert.ToHexString(derived)
            };
            await using var native = new Turso.TursoConnection(builder.ConnectionString);
            await native.OpenAsync();
            using var query = native.CreateCommand();
            query.CommandText = "SELECT name FROM media";
            Assert.Equal(record.Name, await query.ExecuteScalarAsync());
            query.CommandText = "SELECT COUNT(*) FROM pragma_table_info('media') WHERE name='record'";
            Assert.Equal(0L, await query.ExecuteScalarAsync());
        }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }

    private static ImageRecord Record() => new()
    {
        Name = "private café λ media", OriginalName = "original.jpg", StoredPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dpng"),
        SourceFolder = "source λ", SourcePath = "source λ/original.jpg", SourceCreatedAt = DateTimeOffset.Parse("2025-11-02T01:30:00.1234567-04:00"),
        ImportedAt = DateTimeOffset.Parse("2026-10-30T19:23:44.1234567-04:00"), Width = 640, Height = 480, OriginalBytes = 12345,
        Sha256 = Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant(),
        ContentSha256 = new string('b', 64), Metadata = "{\"workflow\":\"private catalog metadata sentinel\"}",
        Description = "private description", UserDescription = "private notes", Tags = " Café, λ, café ", AnalysisModel = "analysis model",
        IsFavorite = true, IsMarkedForDeletion = false, Collections = ["Travel", "λ photos"], Rating = 3, IsReviewed = true,
        MediaType = "video", ContentType = "video/mp4", DurationSeconds = 1.25, MetadataVersion = ComfyMetadata.Version,
        Generation = new() { Prompt = "positive λ", NegativePrompt = "negative", Cfg = 7.5, Steps = 20, Model = "model", ModelKind = "Checkpoint", Sampler = "euler", Scheduler = "normal", HasWorkflow = true, HasApiPrompt = true }
    };

    [Fact]
    public async Task Turso_adopts_a_pre_journal_catalog_without_changing_its_backup_fingerprint()
    {
        using var fixture = new AdapterFixture(CatalogBackend.Turso);
        await fixture.Store.InitializeAsync(fixture.Key);
        var record = Record();
        using var thumbnail = new ImageThumbnail([1, 2, 3]);
        await fixture.Store.InsertAsync(record, thumbnail, fixture.Key);
        // Removing the new journal leaves the exact schema/content of the pre-DbUp adapter.
        await WithNative(async connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE turso_schema_versions";
            await command.ExecuteNonQueryAsync();
        });
        var path = Path.Combine(fixture.Root, "gallery.db");
        await using var operation = await fixture.Store.OpenOperationAsync(fixture.Key);
        var legacyFingerprint = await fixture.Store.ComputeSnapshotFingerprintAsync(path, "backup", fixture.Key);
        await WithNative(async connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='turso_schema_versions'";
            Assert.Equal(0L, await command.ExecuteScalarAsync()); // Archive verification must not migrate.
        });

        Assert.Equal(1, await fixture.Store.CountRecordsAsync(fixture.Key)); // Normal opens adopt it.
        AsserterImageRecord.AssertValuesMatch(ExpectedImageRecord.CreateFrom(record),
            await fixture.Store.ReadRecordAsync(record.Id, fixture.Key));
        using var actual = await fixture.Store.ReadThumbnailAsync(record.Id, fixture.Key);
        Assert.Equal(thumbnail.Content.ToArray(), actual.Content.ToArray());
        await operation.DisposeAsync();
        Assert.Equal(legacyFingerprint, await fixture.Store.ComputeSnapshotFingerprintAsync(path, "backup", fixture.Key));
        await WithNative(async connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM " + TursoGalleryDatabase.JournalTableName;
            Assert.Equal(2L, await command.ExecuteScalarAsync());
        });

        async Task WithNative(Func<System.Data.Common.DbConnection, Task> operation)
        {
            var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, fixture.Key, 32,
                info: "DiffusionGenGallery/catalog/turso/v1"u8.ToArray());
            try
            {
                var builder = new System.Data.Common.DbConnectionStringBuilder
                {
                    ["Data Source"] = Path.Combine(fixture.Root, "gallery.db"), ["Pooling"] = false,
                    ["Encryption Cipher"] = "aes256gcm", ["Encryption Key"] = Convert.ToHexString(key)
                };
                await using var connection = new Turso.TursoConnection(builder.ConnectionString);
                await connection.OpenAsync();
                await operation(connection);
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
    }

    [Fact]
    public async Task Turso_reuses_one_connection_across_concurrent_calls_and_nested_operations_then_releases_the_file()
    {
        using var fixture = new AdapterFixture(CatalogBackend.Turso);
        await fixture.Store.InitializeAsync(fixture.Key);
        using var activity = new Activity("catalog reuse test").Start();
        long opens = 0;
        using var metrics = Listen("gallery.catalog.connection.opens", () => Interlocked.Increment(ref opens), activity);
        await using (var outer = await fixture.Store.OpenOperationAsync(fixture.Key))
        {
            await using (var inner = await fixture.Store.OpenOperationAsync(fixture.Key))
                await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => fixture.Store.CountRecordsAsync(fixture.Key)));
            Assert.Equal(0, await fixture.Store.CountRecordsAsync(fixture.Key));
            Assert.Equal(1, opens);
            await Assert.ThrowsAsync<CatalogStorageException>(() => fixture.Store.CountRecordsAsync(RandomNumberGenerator.GetBytes(32)));
            Assert.Equal(0, await fixture.Store.CountRecordsAsync(fixture.Key));
            Assert.Equal(2, opens); // A rejected operation invalidates the retained handle safely.
        }
        // No keyed/native handle survives the last operation. File publication and vault lock can proceed.
        File.Copy(Path.Combine(fixture.Root, "gallery.db"), Path.Combine(fixture.Root, "released.db"));
        Assert.Equal(0, await fixture.Store.CountRecordsAsync(fixture.Key));
        Assert.Equal(3, opens);
    }

    [Fact]
    public async Task Turso_checkpoints_completed_transactions_at_the_WAL_threshold_and_preserves_data()
    {
        using var fixture = new AdapterFixture(CatalogBackend.Turso, walCheckpointBytes: 64 * 1024);
        await fixture.Store.InitializeAsync(fixture.Key);
        using var activity = new Activity("catalog checkpoint test").Start();
        long checkpoints = 0;
        using var metrics = Listen("gallery.catalog.checkpoints", () => Interlocked.Increment(ref checkpoints), activity);
        var value = new string('x', 256 * 1024);
        await using (var operation = await fixture.Store.OpenOperationAsync(fixture.Key))
        {
            await fixture.Store.SaveSettingsAsync(new[] { value }, "large", "large", fixture.Key);
            Assert.True(checkpoints > 0);
            Assert.InRange(new FileInfo(Path.Combine(fixture.Root, "gallery.db-wal")).Length, 0, 64 * 1024 - 1);
            var count = checkpoints;
            for (var i = 0; i < 10; i++) Assert.Equal(value, (await fixture.Store.ReadSettingsAsync<string[]>("large", "large", fixture.Key))![0]);
            Assert.Equal(count, checkpoints);
        }
        Assert.Equal(value, (await fixture.Store.ReadSettingsAsync<string[]>("large", "large", fixture.Key))![0]);
    }

    [Fact]
    public async Task Turso_checkpoints_an_existing_oversized_WAL_when_the_catalog_is_reopened()
    {
        using var fixture = new AdapterFixture(CatalogBackend.Turso, walCheckpointBytes: long.MaxValue);
        await fixture.Store.InitializeAsync(fixture.Key);
        var value = new string('y', 1024 * 1024);
        await fixture.Store.SaveSettingsAsync(new[] { value }, "large", "large", fixture.Key);
        var database = Path.Combine(fixture.Root, "gallery.db");
        Assert.True(new FileInfo(database + "-wal").Length > 64 * 1024);
        var reopened = new TursoGalleryStore(database, walCheckpointBytes: 64 * 1024);
        await using var operation = await reopened.OpenOperationAsync(fixture.Key);
        Assert.Equal(value, (await reopened.ReadSettingsAsync<string[]>("large", "large", fixture.Key))![0]);
        Assert.InRange(new FileInfo(database + "-wal").Length, 0, 64 * 1024 - 1);
    }

    private static MeterListener Listen(string name, Action observe, Activity activity)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == GalleryService.CatalogMeterName && instrument.Name == name)
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => { if (Activity.Current == activity) observe(); });
        listener.Start();
        return listener;
    }

    private sealed class AdapterFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "gallery-adapter-" + Guid.NewGuid().ToString("N"));
        public byte[] Key { get; } = RandomNumberGenerator.GetBytes(32);
        public IGalleryStore Store { get; }
        public AdapterFixture(CatalogBackend backend, long? walCheckpointBytes = null)
        {
            Directory.CreateDirectory(Root);
            Store = walCheckpointBytes is { } bytes ? new TursoGalleryStore(Path.Combine(Root, "gallery.db"), bytes)
                : new GalleryStoreFactory().Create(Root, backend);
        }
        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Key);
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
