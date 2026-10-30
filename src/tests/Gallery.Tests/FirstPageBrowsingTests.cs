using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Gallery.Core.Models;
using Gallery.Core.Services;
using Gallery.Core.Services.DataManagers.Models;
using Gallery.Core.Services.Processors;
using Gallery.Core.Services.Processors.Codecs;
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
        var bootstrap = await fixture.Service.LoadInitialPageAsync(12);
        Assert.False(bootstrap.CacheHit);
        Assert.True(bootstrap.IsComplete);
        var first = await fixture.Service.LoadInitialPageAsync(12);
        Assert.True(first.CacheHit);
        Assert.False(first.IsComplete);
        Assert.Equal(all.Count, first.TotalRecords);
        Assert.Equal(fixture.Service.Filter(all, new()).Take(12).Select(r => r.Id), first.Records.Select(r => r.Id));
        var changed = await fixture.Service.UpdateAsync(first.Records[0].Id, edit => edit with { Rating = 5, UserDescription = "private city notes" });
        var fresh = await fixture.Service.LoadInitialPageAsync(12);
        Assert.True(fresh.CacheHit);
        Assert.Equal(5, fresh.Records[0].Rating);
        Assert.Equal(changed.UserDescription, fresh.Records[0].UserDescription);
        var trash = await fixture.Service.LoadInitialPageAsync(12, markedOnly: true);
        Assert.Equal(fixture.Service.Filter(all, new() { Deletion = "marked" }).Take(12).Select(r => r.Id), trash.Records.Select(r => r.Id));
        await using var db = new SqliteConnection($"Data Source={Path.Combine(fixture.Root, "gallery.db")};Mode=ReadOnly");
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
        await fixture.Service.LoadInitialPageAsync(12);
        var first = await fixture.Service.LoadInitialPageAsync(12);
        await fixture.Service.UpdateAsync(first.Records[0].Id, edit => edit with { IsMarkedForDeletion = true });
        var rebuilt = await fixture.Service.LoadInitialPageAsync(12);
        Assert.False(rebuilt.CacheHit);
        var page = await fixture.Service.LoadInitialPageAsync(12);
        Assert.DoesNotContain(page.Records, r => r.Id == first.Records[0].Id);
        await fixture.Store.DeleteAsync(page.Records[0].Id);
        Assert.False((await fixture.Service.LoadInitialPageAsync(12)).CacheHit);
        var extra = await SeedAsync(fixture, 1, 10000);
        var afterImport = await fixture.Service.LoadInitialPageAsync(12);
        Assert.False(afterImport.CacheHit);
        Assert.Equal(extra[0].Id, (await fixture.Service.LoadInitialPageAsync(12)).Records[0].Id);
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
            ShouldListenTo = source => source.Name == GalleryService.VaultOpenActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => options.TraceId == trace ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            ActivityStarted = activity =>
            {
                if (activity.TraceId == trace && activity.OperationName == "gallery.load")
                { started.TrySetResult(); release.Wait(TimeSpan.FromSeconds(10)); }
            }
        };
        ActivitySource.AddActivityListener(listener);
        var load = fixture.Service.LoadCatalogAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Service.LockVault();
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        await fixture.Session.WhenDrainedAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Service.IsUnlocked);
    }

    [Fact]
    public async Task Legacy_metadata_backfill_rebuilds_a_current_index_and_small_catalogs_are_complete()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var records = await SeedAsync(fixture, 8);
        using (var key = fixture.Session.AcquireUnlockedSession().Borrow())
            await fixture.Store.UpdateAsync(records[1] with { MetadataVersion = 0 }, key.Bytes);
        var bootstrap = await fixture.Service.LoadInitialPageAsync(12);
        Assert.True(bootstrap.IsComplete);
        Assert.True(bootstrap.IsCurrent);
        Assert.All(bootstrap.Records, record => Assert.Equal(ComfyMetadata.Version, record.MetadataVersion));
        var cached = await fixture.Service.LoadInitialPageAsync(12);
        Assert.True(cached.CacheHit);
        Assert.True(cached.IsComplete);
        Assert.Equal(8, cached.TotalRecords);
        Assert.Equal(bootstrap.Revision, cached.Revision);
        Assert.Equal(bootstrap.Records.Select(record => record.Id).Order(), cached.Records.Select(record => record.Id).Order());
    }

    [Fact]
    public async Task A_snapshot_cannot_publish_an_index_for_a_different_revision()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        await SeedAsync(fixture, 8);
        var snapshot = await fixture.Service.LoadCatalogAsync();
        await fixture.Service.UpdateAsync(snapshot.Records[0].Id, edit => edit with { IsMarkedForDeletion = !edit.IsMarkedForDeletion });
        using var key = fixture.Session.AcquireUnlockedSession().Borrow();
        Assert.False(await fixture.Store.SaveBrowseIndexAsync(snapshot.Records, snapshot.Revision, key.Bytes, CancellationToken.None));
        var rebuilt = await fixture.Service.LoadInitialPageAsync(12);
        Assert.False(rebuilt.CacheHit);
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
}
