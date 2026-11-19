using Gallery.Core.Models;
using Gallery.Web.Components.Features.Collections;
using Gallery.Web.Components.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Gallery.Web.Components.Pages;

public partial class Home
{
    [Parameter] public string? Route { get; set; }
    [Parameter] public NavigationManager? Router { get; set; }

    private readonly GalleryActivity activity = new();
    private readonly CancellationTokenSource lifetime = new();
    private DotNetObjectReference<Home>? securityReference;
    private WorkspaceSession? session;
    private int accessRevision;
    private bool ready;
    private bool startWithCreate;
    private bool disposed;
    private string recoveryPhrase = "";

    protected override Task OnInitializedAsync()
    {
        activity.Completed += ActivityCompleted;
        activity.Ingestion.ProgressChanged += ImportProgressChanged;
        ready = GalleryVault.IsUnlocked;
        GalleryVault.OnAutoLocked += HandleAutoLocked;
        return StartWorkspaceAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        securityReference = DotNetObjectReference.Create(this);
        await JS.InvokeVoidAsync("gallerySecurity.attach", securityReference, session?.Preferences.AutoLockMinutes ?? UiPreferences.DefaultAutoLockMinutes);
    }

    private void ClearFeedback() => activity.Error = activity.Message = "";
    private Task RunAccessAsync((string Status, Func<Task> Action) operation) =>
        RunAsync(operation.Status, operation.Action);

    private async Task RunAsync(string status, Func<Task> action, bool allowDuringImport = false)
    {
        try
        {
            await activity.RunAsync(status, action, Logger, allowDuringImport);
            await StartWorkspaceAsync();
        }
        finally { if (disposed) GalleryVault.LockVault(); }
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
                if (!GalleryVault.VaultExists)
                    throw new InvalidOperationException("No gallery is selected. Choose Switch gallery to open or create one.");
                if (input.UseRecovery) await GalleryVault.UnlockWithRecoveryPhraseAsync(input.Credential);
                else await GalleryVault.UnlockWithPassphraseAsync(input.Credential);
                EnterWorkspace();
            }
            catch
            {
                Lock();
                throw;
            }
        }, allowDuringImport: true);

    private Task FinishSetupAsync()
    {
        EnterWorkspace();
        return StartWorkspaceAsync();
    }

    private void EnterWorkspace()
    {
        if (disposed || !GalleryVault.IsUnlocked) return;
        recoveryPhrase = "";
        startWithCreate = false;
        ready = true;
    }

    private Task StartWorkspaceAsync()
    {
        if (disposed || !ready || !GalleryVault.IsUnlocked || recoveryPhrase.Length > 0 || session is not null)
            return Task.CompletedTask;

        session = new(WorkspaceServices, activity, OrganizationHistory, JS, Logger, action => InvokeAsync(action),
            FlushSavedFeedback, lifetime.Token);
        session.Analysis.Feedback += ShowAnalysisFeedback;
        return session.InitializeAsync(() =>
        {
            Lock();
            return Task.CompletedTask;
        });
    }

    private void ShowAnalysisFeedback(string error, string notice)
    {
        if (error.Length > 0) Toasts.Show("Something went wrong", error, ShadCn.Blazor.Components.SonnerToastVariant.Destructive, 8000);
        if (notice.Length > 0) Toasts.Show(notice, "", ShadCn.Blazor.Components.SonnerToastVariant.Default, 4000);
    }

    private void FlushSavedFeedback()
    {
        if (activity.Message.Length == 0) return;
        Toasts.Show(activity.Message, "", ShadCn.Blazor.Components.SonnerToastVariant.Default, 4000);
        activity.Message = "";
    }

    private void ActivityCompleted()
    {
        if (!disposed) _ = InvokeAsync(StateHasChanged);
    }

    private void ImportProgressChanged()
    {
        if (session is null || !session.IsInitialized) ActivityCompleted();
    }

    private void HandleAutoLocked() { if (!disposed) _ = TriggerAutoLock(); }

    [JSInvokable]
    public Task TriggerAutoLock() => InvokeAsync(() =>
    {
        if (disposed || (!ready && !GalleryVault.IsUnlocked)) return;
        Lock();
        activity.Message = "Gallery locked after inactivity. Its checkout was released.";
        StateHasChanged();
    });

    [JSInvokable]
    public Task TriggerPanicLock() => InvokeAsync(PanicAsync);

    private void PanicAsync()
    {
        GalleryVault.Panic();
        Lock();
        activity.Message = "Gallery locked and memory purged. Its checkout was released.";
        StateHasChanged();
    }

    private void Lock()
    {
        accessRevision++;
        session?.Dispose();
        session = null;
        ready = startWithCreate = false;
        recoveryPhrase = "";
        ClearFeedback();
        activity.Ingestion.ResetOnLock();
        OrganizationHistory.Clear();
        try { _ = JS.InvokeVoidAsync("gallerySecurity.purgeAllMedia"); }
        catch (Exception exception) when (exception is JSDisconnectedException or TaskCanceledException or ObjectDisposedException) { }
        GalleryVault.LockVault();
    }

    public void Dispose()
    {
        disposed = true;
        GalleryVault.OnAutoLocked -= HandleAutoLocked;
        activity.Completed -= ActivityCompleted;
        activity.Ingestion.ProgressChanged -= ImportProgressChanged;
        lifetime.Cancel();
        lifetime.Dispose();
        Lock();
        securityReference?.Dispose();
    }
}
