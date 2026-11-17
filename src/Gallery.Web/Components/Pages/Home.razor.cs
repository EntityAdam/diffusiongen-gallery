using Gallery.Core.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Gallery.Web.Components.Workspace;

namespace Gallery.Web.Components.Pages;

public partial class Home
{
    [Parameter] public string? Route { get; set; }
    [Parameter] public NavigationManager? Router { get; set; }

    private readonly GalleryActivity activity = new();
    private readonly CancellationTokenSource lifetime = new();
    private DotNetObjectReference<Home>? securityReference;
    private GalleryWorkspace? workspace;
    private int accessRevision;
    private int autoLockMinutes = UiPreferences.DefaultAutoLockMinutes;
    private bool ready;
    private bool startWithCreate;
    private bool disposed;
    private string recoveryPhrase = "";

    protected override void OnInitialized()
    {
        activity.Completed += ActivityCompleted;
        ready = Gallery.IsUnlocked;
        Gallery.OnAutoLocked += HandleAutoLocked;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        securityReference = DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("gallerySecurity.attach", securityReference, autoLockMinutes);
    }

    private void ClearFeedback() => activity.Error = activity.Message = "";
    private Task RunAccessAsync((string Status, Func<Task> Action) operation) =>
        RunAsync(operation.Status, operation.Action);

    private async Task RunAsync(string status, Func<Task> action, bool allowDuringImport = false)
    {
        try { await activity.RunAsync(status, action, Logger, allowDuringImport); }
        finally { if (disposed) Gallery.LockVault(); }
    }

    private void GalleryCreated(string phrase)
    {
        recoveryPhrase = phrase;
        ready = true;
    }

    private void GalleryRestored()
    {
        Lock();
        activity.Message = "Backup restored. Sign in to the restored gallery.";
    }

    private void GalleryPurged() => startWithCreate = true;

    private Task UnlockAsync((string Credential, bool UseRecovery) input) =>
        RunAsync("Unlocking gallery...", async () =>
        {
            try
            {
                if (!Gallery.VaultExists)
                    throw new InvalidOperationException("No gallery is selected. Choose Switch gallery to open or create one.");
                if (input.UseRecovery) await Gallery.UnlockWithRecoveryPhraseAsync(input.Credential);
                else await Gallery.UnlockWithPassphraseAsync(input.Credential);
                await EnterWorkspaceAsync();
            }
            catch
            {
                Lock();
                throw;
            }
        }, allowDuringImport: true);

    private Task FinishSetupAsync() => RunAsync("Opening gallery...", EnterWorkspaceAsync);

    private async Task EnterWorkspaceAsync()
    {
        var revision = accessRevision;
        var preferences = await Gallery.LoadPreferencesAsync();
        if (revision != accessRevision || disposed || !Gallery.IsUnlocked) return;
        autoLockMinutes = preferences.AutoLockMinutes;
        Gallery.ConfigureAutoLock(autoLockMinutes);
        try { await JS.InvokeVoidAsync("gallerySecurity.configureAutoLock", autoLockMinutes); }
        catch (Exception exception) when (exception is JSDisconnectedException or TaskCanceledException or ObjectDisposedException) { }
        if (revision != accessRevision || disposed || !Gallery.IsUnlocked) return;
        recoveryPhrase = "";
        startWithCreate = false;
        ready = true;
    }

    private void ActivityCompleted()
    {
        if (!disposed) _ = InvokeAsync(StateHasChanged);
    }

    private void RenderImportProgress() { if (!disposed) StateHasChanged(); }
    private Task ReloadAfterImportAsync() => !disposed && Gallery.IsUnlocked && workspace is not null
        ? workspace.ReloadAsync() : Task.CompletedTask;

    private void HandleAutoLocked() { if (!disposed) _ = TriggerAutoLock(); }

    [JSInvokable]
    public Task TriggerAutoLock() => InvokeAsync(() =>
    {
        if (disposed || (!ready && !Gallery.IsUnlocked)) return;
        Lock();
        activity.Message = "Gallery locked after inactivity. Its checkout was released.";
        StateHasChanged();
    });

    [JSInvokable]
    public Task TriggerPanicLock() => InvokeAsync(PanicAsync);

    private void PanicAsync()
    {
        Gallery.Panic();
        Lock();
        activity.Message = "Gallery locked and memory purged. Its checkout was released.";
        StateHasChanged();
    }

    private void Lock()
    {
        accessRevision++;
        workspace = null;
        ready = startWithCreate = false;
        recoveryPhrase = "";
        ClearFeedback();
        activity.Ingestion.ResetOnLock();
        OrganizationHistory.Clear();
        try { _ = JS.InvokeVoidAsync("gallerySecurity.purgeAllMedia"); }
        catch (Exception exception) when (exception is JSDisconnectedException or TaskCanceledException or ObjectDisposedException) { }
        Gallery.LockVault();
    }

    public void Dispose()
    {
        disposed = true;
        Gallery.OnAutoLocked -= HandleAutoLocked;
        activity.Completed -= ActivityCompleted;
        lifetime.Cancel();
        lifetime.Dispose();
        Lock();
        securityReference?.Dispose();
    }
}
