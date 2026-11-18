using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components.Features.Ingestion;

public sealed class IngestionSession
{
    internal event Action? ProgressChanged;
    internal event Func<Task>? Completed;

    internal void ReportProgress() => ProgressChanged?.Invoke();
    internal async Task NotifyCompletedAsync()
    {
        if (Completed is not { } handlers) return;
        foreach (Func<Task> handler in handlers.GetInvocationList()) await handler();
    }

    public string SourcePath { get; set; } = "";
    public bool Recursive { get; private set; } = true;
    public bool DeleteSources { get; set; }
    public bool RecordTimings { get; set; }
    public bool IncludeImages { get; set; } = true;
    public bool IncludeVideos { get; set; } = true;
    public IngestionScan? Scan { get; internal set; }
    public IngestionSpaceEstimate? Space { get; internal set; }
    public bool IsRunning { get; internal set; }
    public ImportProgress? Progress { get; internal set; }
    public List<string> Failures { get; internal set; } = [];
    public string? TimingReport { get; internal set; }
    public IngestionOptions SavedOptions { get; private set; } = IngestionOptions.Hdd;
    public int Workers { get; set; } = IngestionOptions.Hdd.Workers;
    public int BatchSize { get; set; } = IngestionOptions.Hdd.CatalogBatchSize;
    public string Profile { get; set; } = "hdd";
    public bool SettingsDirty => Workers != SavedOptions.Workers || BatchSize != SavedOptions.CatalogBatchSize;
    public bool CanIngest(bool busy) => !busy && !SettingsDirty && Scan is not null && Space?.HasEnoughSpace == true;
    public long EstimatedChangeBytes => Space is { } space ? space.EstimatedBytes - (DeleteSources ? space.SelectedSourceBytes : 0) : 0;

    public void LoadOptions(IngestionOptions options)
    {
        SavedOptions = options;
        ApplyProfile(options);
    }

    public async Task SaveOptionsAsync(IGallerySettings gallery)
    {
        var options = new IngestionOptions { Workers = Workers, CatalogBatchSize = BatchSize };
        await gallery.SaveIngestionOptionsAsync(options);
        LoadOptions(options);
    }

    public void ResetOnLock()
    {
        SourcePath = "";
        ClearScan();
        if (IsRunning) return;
        Failures.Clear();
        Progress = null;
        TimingReport = null;
    }
    public void ApplyProfile(IngestionOptions options, string? profile = null)
    {
        Workers = options.Workers;
        BatchSize = options.CatalogBatchSize;
        Profile = profile ?? (options == IngestionOptions.Hdd ? "hdd" : options == IngestionOptions.Ssd ? "ssd" : "custom");
    }

    public void ClearScan()
    {
        Scan = null;
        Space = null;
        DeleteSources = false;
    }

    public void ToggleRecursion()
    {
        Recursive = !Recursive;
        ClearScan();
    }
}
