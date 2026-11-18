using Gallery.Core.Models;
using Gallery.Core.Services;
using Microsoft.JSInterop;
using Gallery.Web.Components.Browsing;
using Gallery.Web.Components.Collections;
using Gallery.Web.Components.Media;

namespace Gallery.Web.Components.Workspace;

public sealed class WorkspaceSession : IDisposable
{
    private readonly IGalleryVault vault;
    private readonly IGallerySettings settings;
    private readonly ILogger logger;
    private readonly OrganizationHistory history;
    private readonly CancellationToken ownerLifetime;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Action dispatchChanged;
    private bool disposed;

    internal WorkspaceSession(WorkspaceGalleryServices services, GalleryActivity activity, OrganizationHistory history,
        IJSRuntime js, ILogger logger, Func<Func<Task>, Task> dispatch, Action savedFeedback,
        CancellationToken ownerLifetime = default)
    {
        vault = services.Vault;
        settings = services.Settings;
        this.logger = logger;
        this.history = history;
        this.ownerLifetime = ownerLifetime;
        Activity = activity;
        Preferences = new(vault, settings, js, lifetime.Token);
        Catalog = new(vault, services.Catalog, services.Collections, services.Tags, logger, dispatch);
        dispatchChanged = () =>
        {
            if (!disposed) _ = dispatch(NotifyChangedAsync);
        };
        Browser = new(vault, services.Catalog, settings, services.Media, Catalog, logger, dispatchChanged, Preferences);
        Analysis = new(vault);
        Commands = new(activity, (status, action) => RunAsync(status, action), Analysis.EnsureIdle);
        Presentation = new(vault, services.Media, js, Commands);
        Removal = new(vault, services.MediaMaintenance, Commands, logger, () =>
        {
            Presentation.CloseDetail();
            return Catalog.ReloadAsync();
        });
        Curation = new(services.Records, Catalog, Commands);
        Duplicates = new(services.Duplicates, vault, services.Media, Catalog);
        Collections = new(Catalog);
        Guard = new(activity, () => IsBusy, savedFeedback);
        Navigation = new(Browser, Catalog, Duplicates, Collections, Analysis, Presentation, Commands, Guard, () => IsBusy);

        Catalog.Changed += Collections.CatalogChangedAsync;
        Catalog.Changed += Duplicates.CatalogChangedAsync;
        Catalog.Changed += () => Presentation.ReconcileAsync(Catalog.ImagesById);
        Catalog.Changed += Browser.CatalogChangedAsync;
        Catalog.Changed += NotifyChangedAsync;
        Catalog.RecordsChanged += (records, _) =>
        {
            foreach (var record in records) Presentation.Update(record);
            return Task.CompletedTask;
        };
        Catalog.RecordsChanged += (_, reloadThumbnails) => Browser.RecordsChangedAsync(reloadThumbnails);
        Catalog.RecordsChanged += (_, _) => NotifyChangedAsync();
        Analysis.Changed += NotifyChanged;
        history.Changed += dispatchChanged;
        Activity.Ingestion.ProgressChanged += NotifyChanged;
        Activity.Ingestion.Completed += Catalog.ReloadAsync;
    }

    public GalleryActivity Activity { get; }
    internal CancellationToken ImportLifetime => ownerLifetime;
    public WorkspaceCatalog Catalog { get; }
    public GalleryBrowserState Browser { get; }
    public WorkspaceCommands Commands { get; }
    public WorkspacePreferences Preferences { get; }
    public MediaPresentationState Presentation { get; }
    public RemovalWorkflowState Removal { get; }
    public MediaCurationCommands Curation { get; }
    public LibraryAnalysis Analysis { get; }
    public DuplicateWorkflowState Duplicates { get; }
    public CollectionBrowsingState Collections { get; }
    public UnsavedChangesGuardState Guard { get; }
    public WorkspaceNavigationState Navigation { get; }
    internal bool IsBusy => Activity.Busy || Collections.IsBusy || history.IsBusy;
    internal bool CanUndo => history.Current is not null;
    internal bool IsInitialized { get; private set; }
    internal event Action? Changed;

    internal async Task InitializeAsync(Func<Task>? openingFailed = null)
    {
        await RunAsync("Opening gallery...", async () =>
        {
            try
            {
                var preferences = await Preferences.LoadAsync();
                if (disposed || !vault.IsUnlocked) return;
                await Preferences.ApplyAutoLockAsync();
                if (disposed || !vault.IsUnlocked) return;
                await Browser.InitializeAsync(preferences);
                if (disposed || !vault.IsUnlocked) return;
                var ingestionOptions = await settings.LoadIngestionOptionsAsync();
                if (disposed || !vault.IsUnlocked) return;
                Activity.Ingestion.LoadOptions(ingestionOptions);
                await Catalog.OpenAsync(Browser.PageSize, Browser.TrashActive);
                IsInitialized = !disposed && vault.IsUnlocked;
            }
            catch
            {
                if (!disposed && openingFailed is not null) await openingFailed();
                throw;
            }
        }, allowDuringImport: true);
    }

    private async Task RunAsync(string status, Func<Task> action, bool allowDuringImport = false)
    {
        if (disposed) return;
        try
        {
            await Activity.RunAsync(status, async () =>
            {
                NotifyChanged();
                try
                {
                    await Browser.PauseAsync();
                    if (!disposed) await action();
                }
                finally
                {
                    if (ownerLifetime.IsCancellationRequested) vault.LockVault();
                    else if (!disposed && vault.IsUnlocked) Browser.Resume();
                }
            }, logger, allowDuringImport);
        }
        finally { NotifyChanged(); }
    }

    internal Task UndoOrganizationAsync() => IsBusy ? Task.CompletedTask
        : Commands.RunLibraryChangeAsync("Restoring organization...", async () =>
        {
            await history.UndoAsync();
            await Catalog.ReloadAsync();
            if (!disposed) Activity.Message = "Organization restored.";
        });

    internal async Task TagsManagedAsync(TagCatalogChange change)
    {
        if (!Catalog.CanRead) return;
        Browser.RenameTag(change);
        await Analysis.UpdateTagsAsync(change);
        await Catalog.ReloadAsync();
    }

    private void NotifyChanged()
    {
        if (!disposed) Changed?.Invoke();
    }

    private Task NotifyChangedAsync()
    {
        NotifyChanged();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
        history.Changed -= dispatchChanged;
        Activity.Ingestion.ProgressChanged -= NotifyChanged;
        Activity.Ingestion.Completed -= Catalog.ReloadAsync;
        Analysis.Changed -= NotifyChanged;
        Analysis.Clear();
        Changed = null;
        Navigation.ClearDraftSources();
        Guard.Dispose();
        Catalog.Dispose();
        Duplicates.Dispose();
        Browser.Dispose();
        Presentation.Dispose();
        Removal.Dispose();
    }
}
