using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gallery.Core.Features.Vaults.Session;
using Gallery.Core.Features.Ingestion.Metadata;
using Gallery.Core.Features.Vaults.Security;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;
using Gallery.Core.Features.Vaults;
using Gallery.Core.Services;
using Microsoft.Data.Sqlite;

namespace Gallery.Tests;

public sealed class FirstPageBrowsingTests
{
    [Fact]
    public async Task Encrypted_index_returns_the_real_first_page_and_preserves_updates_without_loading_all_records()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var all = await SeedAsync(fixture);
        var bootstrap = await fixture.Scope.Catalog.LoadInitialPageAsync(12);
        Assert.False(bootstrap.CacheHit);
        Assert.True(bootstrap.IsComplete);
        var first = await fixture.Scope.Catalog.LoadInitialPageAsync(12);
        Assert.True(first.CacheHit);
        Assert.False(first.IsComplete);
        Assert.Equal(all.Count, first.TotalRecords);
        Assert.Equal(fixture.Scope.Catalog.Filter(all, new()).Take(12).Select(r => r.Id), first.Records.Select(r => r.Id));
        var changed = await fixture.Scope.Records.UpdateAsync(first.Records[0].Id, edit => edit with { Rating = 5, UserDescription = "private city notes" });
        var fresh = await fixture.Scope.Catalog.LoadInitialPageAsync(12);
        Assert.True(fresh.CacheHit);
        Assert.Equal(5, fresh.Records[0].Rating);
        Assert.Equal(changed.UserDescription, fresh.Records[0].UserDescription);
        var trash = await fixture.Scope.Catalog.LoadInitialPageAsync(12, markedOnly: true);
        Assert.Equal(fixture.Scope.Catalog.Filter(all, new() { Deletion = "marked" }).Take(12).Select(r => r.Id), trash.Records.Select(r => r.Id));
        await using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.Root, "gallery.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await db.OpenAsync();
        using var command = db.CreateCommand(); command.CommandText = "SELECT payload FROM settings WHERE id='browse-first-page'";
        var blob = (byte[])(await command.ExecuteScalarAsync())!;
        Assert.DoesNotContain(first.Records[0].Id, Encoding.UTF8.GetString(blob));
        Assert.DoesNotContain("private city notes", Encoding.UTF8.GetString(blob));
    }

    [Fact]
    public async Task Imports_marks_and_removals_invalidate_stale_membership_and_rebuild_in_default_order()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await SeedAsync(fixture);
        await fixture.Scope.Catalog.LoadInitialPageAsync(12);
        var first = await fixture.Scope.Catalog.LoadInitialPageAsync(12);
        await fixture.Scope.Records.UpdateAsync(first.Records[0].Id, edit => edit with { IsMarkedForDeletion = true });
        var rebuilt = await fixture.Scope.Catalog.LoadInitialPageAsync(12);
        Assert.False(rebuilt.CacheHit);
        var page = await fixture.Scope.Catalog.LoadInitialPageAsync(12);
        Assert.DoesNotContain(page.Records, r => r.Id == first.Records[0].Id);
        await fixture.Store.DeleteAsync(page.Records[0].Id);
        Assert.False((await fixture.Scope.Catalog.LoadInitialPageAsync(12)).CacheHit);
        var extra = await SeedAsync(fixture, 1, 10000);
        var afterImport = await fixture.Scope.Catalog.LoadInitialPageAsync(12);
        Assert.False(afterImport.CacheHit);
        Assert.Equal(extra[0].Id, (await fixture.Scope.Catalog.LoadInitialPageAsync(12)).Records[0].Id);
    }

    [Fact]
    public async Task Canceled_or_revoked_background_reads_release_the_location_lease_and_never_return_a_snapshot()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await SeedAsync(fixture, 8);
        using var scope = new Activity("first-page-test").Start();
        var trace = scope.TraceId;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == VaultOpenTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => options.TraceId == trace ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            ActivityStarted = activity =>
            {
                if (activity.TraceId == trace && activity.OperationName == "gallery.load")
                { started.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); }
            }
        };
        ActivitySource.AddActivityListener(listener);
        var load = fixture.Scope.Catalog.LoadCatalogAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Scope.Vault.LockVault();
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        await fixture.Session.WhenDrainedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Scope.Vault.IsUnlocked);
    }

    [Fact]
    public async Task Legacy_metadata_backfill_rebuilds_a_current_index_and_small_catalogs_are_complete()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var records = await SeedAsync(fixture, 8);
        using (var key = fixture.Session.AcquireUnlockedSession().Borrow())
            await fixture.Store.UpdateAsync(records[1] with { MetadataVersion = 0 }, key.Bytes);
        var bootstrap = await fixture.Scope.Catalog.LoadInitialPageAsync(12);
        Assert.True(bootstrap.IsComplete);
        Assert.True(bootstrap.IsCurrent);
        Assert.All(bootstrap.Records, record => Assert.Equal(ComfyMetadata.Version, record.MetadataVersion));
        var cached = await fixture.Scope.Catalog.LoadInitialPageAsync(12);
        Assert.True(cached.CacheHit);
        Assert.True(cached.IsComplete);
        Assert.Equal(8, cached.TotalRecords);
        Assert.Equal(bootstrap.Revision, cached.Revision);
        Assert.Equal(bootstrap.Records.Select(record => record.Id).Order(), cached.Records.Select(record => record.Id).Order());
    }

    [Fact]
    public async Task Legacy_metadata_refresh_conflict_returns_stale_snapshot_then_recovers()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var records = await SeedAsync(fixture, 2);
        await fixture.Store.UpdateAsync(records[0] with { MetadataVersion = 0, Generation = null }, fixture.Session.Key);

        var proxy = DispatchProxy.Create<IGalleryStore, MetadataRefreshConflictProxy>();
        var conflictProxy = (MetadataRefreshConflictProxy)(object)proxy;
        conflictProxy.Inner = fixture.Store;
        conflictProxy.ConcurrentEdit = records[1] with { Rating = 5 };
        var lifetime = (GalleryLifetime)typeof(GalleryScope)
            .GetField("galleryLifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Scope)!;
        var location = lifetime.Current;
        lifetime.PublishLocation(proxy, location.VaultStore, location.Checkout);

        var stale = await fixture.Scope.Catalog.LoadCatalogAsync();

        Assert.Equal(1, conflictProxy.RefreshConflictCount);
        Assert.False(stale.IsCurrent);
        Assert.Equal(0, Assert.Single(stale.Records, record => record.Id == records[0].Id).MetadataVersion);
        Assert.Equal(records[1].Rating, Assert.Single(stale.Records, record => record.Id == records[1].Id).Rating);
        var concurrentEdit = await fixture.Store.ReadRecordAsync(records[1].Id, fixture.Session.Key);
        Assert.Equal(5, concurrentEdit.Rating);

        var recovered = await fixture.Scope.Catalog.LoadCatalogAsync();

        Assert.True(recovered.IsCurrent);
        Assert.Equal(ComfyMetadata.Version, Assert.Single(recovered.Records, record => record.Id == records[0].Id).MetadataVersion);
        Assert.Equal(5, Assert.Single(recovered.Records, record => record.Id == records[1].Id).Rating);
    }

    [Fact]
    public async Task Browse_record_reads_batch_large_id_sets_and_preserve_distinct_request_order()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var records = await SeedAsync(fixture, 3);
        var requestedIds = Enumerable.Range(0, 40000).Select(index => $"missing-{index}")
            .Concat([records[2].Id, records[0].Id, records[2].Id, "missing-final", records[1].Id]).ToArray();

        var actual = await fixture.Store.ReadBrowseRecordsAsync(requestedIds, fixture.Session.Key, CancellationToken.None);

        Assert.Equal(new[] { records[2].Id, records[0].Id, records[1].Id }, actual.Select(record => record.Id));
    }

    [Fact]
    public async Task First_page_read_reports_the_encrypted_record_bytes_scanned()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await SeedAsync(fixture, 3);
        await fixture.Scope.Catalog.LoadInitialPageAsync(12);

        long expectedBytes;
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.Root, "gallery.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT SUM(length(record)) FROM images";
            expectedBytes = Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        using var trace = new Activity("first-page-byte-metric").Start();
        var traceId = trace.TraceId;
        long? observedBytes = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == VaultOpenTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                options.TraceId == traceId ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == traceId && activity.OperationName == "gallery.first_page")
                    observedBytes = (long?)activity.GetTagItem("gallery.first_page.record_bytes");
            }
        };
        ActivitySource.AddActivityListener(listener);

        var page = await fixture.Store.ReadInitialPageAsync(12, InitialPageMode.Active,
            fixture.Session.Key, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal(expectedBytes, observedBytes);
    }

    [Fact]
    public async Task Browse_index_written_with_array_members_remains_readable()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var records = await SeedAsync(fixture, 3);
        var revision = await fixture.Store.ReadStorageRevisionAsync(fixture.Session.Key);
        var orderedIds = records.OrderByDescending(record => record.ImportedAt).Select(record => record.Id).ToArray();
        var legacyIndex = new
        {
            Version = 1,
            Revision = revision,
            TotalRecords = records.Count,
            All = orderedIds,
            Active = orderedIds,
            Marked = Array.Empty<string>()
        };
        var payload = VaultCrypto.Encrypt(JsonSerializer.SerializeToUtf8Bytes(legacyIndex),
            fixture.Session.Key, "browse-first-page:v1");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.Root, "gallery.db"),
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO settings (id, payload) VALUES ('browse-first-page', $payload)
                ON CONFLICT(id) DO UPDATE SET payload = excluded.payload
                """;
            command.Parameters.AddWithValue("$payload", payload);
            await command.ExecuteNonQueryAsync();
        }

        var page = await fixture.Store.ReadInitialPageAsync(12, InitialPageMode.Active,
            fixture.Session.Key, CancellationToken.None);

        Assert.NotNull(page);
        Assert.True(page.IsComplete);
        Assert.True(page.CacheHit);
        Assert.Equal(orderedIds, page.Records.Select(record => record.Id));
    }

    [Fact]
    public async Task Partial_browse_index_selects_active_or_marked_mode()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var records = await SeedAsync(fixture, 3);
        var marked = records[1] with { IsMarkedForDeletion = true };
        await fixture.Store.UpdateAsync(marked, fixture.Session.Key);
        var revision = await fixture.Store.ReadStorageRevisionAsync(fixture.Session.Key);
        var index = new
        {
            Version = 1,
            Revision = revision,
            TotalRecords = records.Count,
            All = new[] { records[0].Id, marked.Id },
            Active = new[] { records[0].Id, records[2].Id },
            Marked = new[] { marked.Id }
        };
        var payload = VaultCrypto.Encrypt(JsonSerializer.SerializeToUtf8Bytes(index),
            fixture.Session.Key, "browse-first-page:v1");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.Root, "gallery.db"),
            Pooling = false
        }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO settings (id, payload) VALUES ('browse-first-page', $payload)
                ON CONFLICT(id) DO UPDATE SET payload = excluded.payload
                """;
            command.Parameters.AddWithValue("$payload", payload);
            await command.ExecuteNonQueryAsync();
        }

        var activePage = await fixture.Store.ReadInitialPageAsync(1, InitialPageMode.Active, fixture.Session.Key, CancellationToken.None);
        var markedPage = await fixture.Store.ReadInitialPageAsync(1, InitialPageMode.Marked, fixture.Session.Key, CancellationToken.None);

        Assert.False(activePage!.IsComplete);
        Assert.Equal(records[0].Id, Assert.Single(activePage.Records).Id);
        Assert.False(markedPage!.IsComplete);
        Assert.Equal(marked.Id, Assert.Single(markedPage.Records).Id);
    }

    [Fact]
    public async Task Unreadable_browse_cache_falls_back_and_rebuilds_but_authoritative_record_failures_remain_fatal()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var record = Assert.Single(await SeedAsync(fixture, 1));
        await fixture.Scope.Catalog.LoadInitialPageAsync(12);
        await MakeBrowseIndexUnreadableAsync(fixture);

        await fixture.Store.UpdateAsync(record with { Rating = 5 }, fixture.Session.Key);
        var fallback = await fixture.Scope.Catalog.LoadInitialPageAsync(12);

        Assert.False(fallback.CacheHit);
        Assert.Equal(5, Assert.Single(fallback.Records).Rating);
        Assert.True((await fixture.Scope.Catalog.LoadInitialPageAsync(12)).CacheHit);

        await MakeBrowseIndexUnreadableAsync(fixture);
        await fixture.RawStore.UpdateAsync(record.Id, new byte[40]);
        await MakeBrowseIndexUnreadableAsync(fixture);
        await Assert.ThrowsAsync<CryptographicException>(() => fixture.Scope.Catalog.LoadInitialPageAsync(12));
    }

    private static async Task<List<ImageRecord>> SeedAsync(TestVault fixture, int count = 1030, int offset = 0)
    {
        Directory.CreateDirectory(Path.Combine(fixture.Root, "images"));
        var rows = Enumerable.Range(offset, count).Select(i => new ImageRecord
        {
            Name = $"private-{i}.png", ImportedAt = DateTimeOffset.UnixEpoch.AddSeconds(i / 3),
            StoredPath = Path.Combine(fixture.Root, "images", $"{i}.dpng"),
            Sha256 = Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(i))),
            MetadataVersion = ComfyMetadata.Version, IsMarkedForDeletion = i % 7 == 0
        }).ToList();
        using var key = fixture.Session.AcquireUnlockedSession().Borrow();
        var png = GalleryTests.MakePng(1, 1);
        var entries = rows.Select(row => new ImportCatalogEntry(row, VaultCrypto.Encrypt(png, key.Bytes, $"thumbnail:{row.Id}"))).ToArray();
        await fixture.Store.CommitImportsAsync(entries, key.Bytes);
        return rows;
    }

    public class MetadataRefreshConflictProxy : DispatchProxy
    {
        private int conflictPending = 1;

        internal IGalleryStore Inner { get; set; } = null!;
        public ImageRecord ConcurrentEdit { get; set; } = null!;
        public int RefreshConflictCount { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IGalleryStore.RefreshGenerationMetadataAsync) &&
                Interlocked.Exchange(ref conflictPending, 0) == 1)
            {
                RefreshConflictCount++;
                return RefreshAfterConcurrentEditAsync(
                    (GalleryCatalog)args![0]!,
                    (IReadOnlyList<ImageRecord>)args[1]!,
                    (byte[])args[2]!,
                    (CancellationToken)args[3]!);
            }

            return targetMethod!.Invoke(Inner, args);
        }

        private async Task<long> RefreshAfterConcurrentEditAsync(GalleryCatalog sourceSnapshot,
            IReadOnlyList<ImageRecord> refreshedRecords, byte[] key, CancellationToken token)
        {
            await Inner.UpdateAsync(ConcurrentEdit, key);
            return await Inner.RefreshGenerationMetadataAsync(sourceSnapshot, refreshedRecords, key, token);
        }
    }

    private static async Task MakeBrowseIndexUnreadableAsync(TestVault fixture)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fixture.Root, "gallery.db"),
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE settings SET payload = $payload WHERE id = 'browse-first-page'";
        command.Parameters.AddWithValue("$payload", new byte[40]);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
