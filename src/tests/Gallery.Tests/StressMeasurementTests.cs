using System.Runtime;
using System.Text.Json;
using Gallery.Stress.Core;

namespace Gallery.Tests;

public sealed class StressMeasurementTests
{
    [Fact]
    public void Legacy_unknown_and_explicit_GC_modes_are_separate_series()
    {
        var legacy = JsonSerializer.SerializeToElement(new { env = new { schemaVersion = 1, os = "same" } });
        var unknown = JsonSerializer.SerializeToElement(new { env = new { schemaVersion = 2, os = "same" } });
        var server = JsonSerializer.SerializeToElement(new { env = new { schemaVersion = 2, os = "same", gcMode = "server" } });
        var workstation = JsonSerializer.SerializeToElement(new { env = new { schemaVersion = 2, os = "same", gcMode = "workstation" } });
        Assert.Equal(4, new[] { legacy, unknown, server, workstation }.Select(MeasurementSeries.EnvironmentKey).Distinct().Count());
    }

    [Fact]
    public void Page_size_and_opening_target_are_part_of_series_identity()
    {
        var small = JsonSerializer.SerializeToElement(new { env = new { schemaVersion = 2, gcMode = "server" }, pageSize = 12, measurementTarget = "passphrase_to_gallery_ready" });
        var large = JsonSerializer.SerializeToElement(new { env = new { schemaVersion = 2, gcMode = "server" }, pageSize = 49, measurementTarget = "passphrase_to_gallery_ready" });
        var legacyTarget = JsonSerializer.SerializeToElement(new { env = new { schemaVersion = 2, gcMode = "server" }, pageSize = 12, measurementTarget = "total_to_interactive" });
        Assert.Equal(3, new[] { small, large, legacyTarget }.Select(MeasurementSeries.EnvironmentKey).Distinct().Count());
    }

    [Fact]
    public void Optional_background_waits_do_not_mix_resource_measurements_with_first_page_only_runs()
    {
        var pageOnly = JsonSerializer.SerializeToElement(new { steps = new { passphrase_to_gallery_ready = 100 } });
        var catalog = JsonSerializer.SerializeToElement(new { steps = new { passphrase_to_gallery_ready = 100, catalog_background = 9000 } });
        var drive = JsonSerializer.SerializeToElement(new { steps = new { passphrase_to_gallery_ready = 100, drive_idle = 500 } });
        Assert.Equal(3, new[] { pageOnly, catalog, drive }.Select(MeasurementSeries.EnvironmentKey).Distinct().Count());
    }

    [Fact]
    public void New_primary_time_excludes_thumbnails_and_legacy_time_is_not_relabelled()
    {
        var legacy = JsonSerializer.SerializeToElement(new { steps = new { total_to_interactive = 900 } });
        var current = JsonSerializer.SerializeToElement(new { steps = new { passphrase_to_gallery_ready = 120, total_to_interactive = 900 } });
        Assert.Equal("total_to_interactive", MeasurementSeries.OpenStep(legacy));
        Assert.Equal(900, MeasurementSeries.OpenMilliseconds(legacy));
        Assert.Equal("passphrase_to_gallery_ready", MeasurementSeries.OpenStep(current));
        Assert.Equal(120, MeasurementSeries.OpenMilliseconds(current));
    }

    [Fact]
    public void Missing_measurements_are_unavailable_while_supported_zero_IO_is_retained()
    {
        var missing = JsonSerializer.SerializeToElement<object?>(null);
        var zero = JsonSerializer.SerializeToElement(0);
        var measured = JsonSerializer.SerializeToElement(20);
        Assert.Equal("—", MeasurementSeries.MedianAvailable([missing]));
        Assert.Equal("0", MeasurementSeries.MedianAvailable([missing, zero]));
        Assert.Equal("—", MeasurementSeries.MedianAvailable([zero], positiveOnly: true));
        Assert.Equal("20", MeasurementSeries.MedianAvailable([missing, measured]));
    }

    [Fact]
    public void Environment_records_the_effective_runtime_GC_mode()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gallery-stress-env-" + Guid.NewGuid().ToString("N"));
        try
        {
            var writer = new ResultWriter(directory);
            Assert.Equal(3, writer.Environment.SchemaVersion);
            Assert.Equal(GCSettings.IsServerGC ? "server" : "workstation", writer.Environment.GcMode);
            Assert.Equal(GCSettings.LatencyMode.ToString(), writer.Environment.GcLatencyMode);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void MacOS_native_process_readings_report_peak_memory_and_disk_bytes()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.True(ProcessMeasurements.PeakWorkingSetBytes() > 0);
        var io = ProcessMeasurements.ReadIo();
        Assert.NotNull(io.ReadBytes);
        Assert.True(io.ReadBytes >= 0);
        Assert.Null(io.ReadOps); // macOS does not expose the Windows read-operation counter here.
    }
}
