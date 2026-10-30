using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Gallery.Core.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Gallery.Tests;

public sealed class VaultOpenTelemetryTests
{
    [Fact]
    public async Task UnlockLoadAndThumbnailEmitPrivacySafeMeasurements()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var records = new List<Gallery.Core.Models.ImageRecord>();
        for (var index = 0; index < 3; index++)
        {
            using var image = new Image<Rgb24>(32, 32, new Rgb24((byte)(index * 70), 40, 80));
            using var png = new MemoryStream();
            image.SaveAsPng(png);
            png.Position = 0;
            records.Add(await fixture.Service.ImportAsync(png, $"private-record-name-{index}.png", "private-source-folder"));
        }

        using var scope = new Activity("telemetry-test").Start();
        var traceId = scope.TraceId;
        var activities = new ConcurrentQueue<Activity>();
        var measurements = new ConcurrentQueue<(string Name, double Value)>();
        var measurementTags = new ConcurrentQueue<object?>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GalleryService.VaultOpenActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                options.TraceId == traceId ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            ActivityStopped = activity => activities.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(activityListener);
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == GalleryService.CatalogMeterName) listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            measurements.Enqueue((instrument.Name, value));
            foreach (var tag in tags) measurementTags.Enqueue(tag.Value);
        });
        meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            measurements.Enqueue((instrument.Name, value));
            foreach (var tag in tags) measurementTags.Enqueue(tag.Value);
        });
        meterListener.Start();

        await fixture.Service.UnlockWithPassphraseAsync(TestVault.Passphrase);
        Assert.Equal(3, (await fixture.Service.LoadAsync()).Count);
        await fixture.Service.ThumbnailAsync(records[0].Id);
        await fixture.Service.WaitForDriveOperationsAsync();

        var unlock = Assert.Single(activities, activity => activity.OperationName == "vault.unlock");
        Assert.Equal("passphrase", unlock.GetTagItem("vault.unlock.method"));
        foreach (var suffix in new[] { "gate_wait_ms", "header_read_ms", "kdf_ms", "unwrap_ms", "history_save_ms", "elapsed_ms" })
            Assert.IsType<double>(unlock.GetTagItem("vault.unlock." + suffix));
        Assert.True((double)unlock.GetTagItem("vault.unlock.kdf_ms")! > 0);
        Assert.Contains(activities, activity => activity.OperationName == "vault.unlock.kdf");

        var load = Assert.Single(activities, activity => activity.OperationName == "gallery.load");
        Assert.Equal(3, load.GetTagItem("gallery.load.record_count"));
        Assert.True((long)load.GetTagItem("gallery.load.record_bytes")! > 0);
        foreach (var suffix in new[] { "gate_wait_ms", "connection_open_ms", "query_read_ms", "decrypt_ms", "deserialize_ms",
                     "backfill_fingerprint_ms", "backfill_generation_ms", "schedule_media_index_ms", "elapsed_ms",
                     "query_step_ms", "blob_materialize_ms", "other_loop_ms", "gc_pause_ms" })
            Assert.IsType<double>(load.GetTagItem("gallery.load." + suffix));
        Assert.Equal(0, load.GetTagItem("gallery.load.backfill_generation_count"));
        Assert.Equal(0, load.GetTagItem("gallery.load.backfill_fingerprint_count"));
        Assert.IsType<bool>(load.GetTagItem("gallery.load.media_index_scheduled"));
        foreach (var instrument in new[] { "connection.opens", "connection.open.duration", "migration.duration", "thumbnail.read.duration", "thumbnail.bytes" })
            Assert.Contains(measurements, measurement => measurement.Name == "gallery.catalog." + instrument && measurement.Value > 0);

        var values = activities.SelectMany(activity => activity.TagObjects.Select(tag => tag.Value)).Concat(measurementTags);
        foreach (var value in values)
        {
            Assert.True(value is bool or int or long or double || value is "passphrase" or "recovery");
            var text = value?.ToString() ?? "";
            Assert.DoesNotContain(fixture.Root, text);
            Assert.DoesNotContain(TestVault.Passphrase, text);
            Assert.DoesNotContain("private-source-folder", text);
            foreach (var record in records)
            {
                Assert.DoesNotContain(record.Id, text);
                Assert.DoesNotContain(record.Name, text);
            }
        }
    }

    [Fact]
    public async Task RecoveryUnlockEmitsTheSameTimings()
    {
        using var fixture = new TestVault();
        var recovery = await fixture.Service.CreateVaultAsync(TestVault.Passphrase, TestVault.Passphrase);
        using var scope = new Activity("telemetry-test").Start();
        var traceId = scope.TraceId;
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == GalleryService.VaultOpenActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                options.TraceId == traceId ? ActivitySamplingResult.AllData : ActivitySamplingResult.None,
            ActivityStopped = activity => activities.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(listener);
        await fixture.Service.UnlockWithRecoveryPhraseAsync(recovery);
        var unlock = Assert.Single(activities, activity => activity.OperationName == "vault.unlock");
        Assert.Equal("recovery", unlock.GetTagItem("vault.unlock.method"));
        Assert.True((double)unlock.GetTagItem("vault.unlock.kdf_ms")! > 0);
    }

    [Fact]
    public async Task LoadSucceedsWithoutAVaultOpenListener()
    {
        using var fixture = new TestVault();
        await fixture.InitializeAsync();
        var originalActivity = Activity.Current;
        Assert.Empty(await fixture.Service.LoadAsync());
        Assert.Same(originalActivity, Activity.Current);
        await fixture.Service.WaitForDriveOperationsAsync();
    }
}
