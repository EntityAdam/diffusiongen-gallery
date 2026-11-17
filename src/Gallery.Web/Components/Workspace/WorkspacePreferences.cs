using Gallery.Core.Models;
using Gallery.Core.Services;
using Microsoft.JSInterop;

namespace Gallery.Web.Components.Workspace;

public sealed class WorkspacePreferences(GalleryService gallery, IJSRuntime js, CancellationToken lifetime)
{
    private readonly SemaphoreSlim saves = new(1, 1);
    private UiPreferences current = new();

    public int AutoLockMinutes => current.AutoLockMinutes;

    internal async Task<UiPreferences> LoadAsync()
    {
        current = await gallery.LoadPreferencesAsync();
        return current;
    }

    internal Task SaveBrowserAsync(string density, bool visible, double? x, double? y, string tool) =>
        SaveAsync(value => value with
        {
            GridDensity = density, ToolboxVisible = visible, ToolboxX = x, ToolboxY = y, ToolboxTool = tool
        });

    internal Task UpdateAutoLockAsync(string? value, WorkspaceCommands commands)
    {
        if (!int.TryParse(value, out var minutes)) return Task.CompletedTask;
        return commands.RunAsync("Saving auto-lock timer...", async () =>
        {
            try { await SaveAsync(preferences => preferences with { AutoLockMinutes = Math.Clamp(minutes, 0, 1440) }); }
            catch (Exception exception) when (!lifetime.IsCancellationRequested)
            {
                throw new InvalidOperationException("Auto-lock timer could not be saved. The previous timer is still active.", exception);
            }
            if (!lifetime.IsCancellationRequested && gallery.IsUnlocked) await ApplyAutoLockAsync();
        });
    }

    internal async Task ApplyAutoLockAsync()
    {
        gallery.ConfigureAutoLock(AutoLockMinutes);
        try { await js.InvokeVoidAsync("gallerySecurity.configureAutoLock", AutoLockMinutes); }
        catch (Exception exception) when (exception is JSDisconnectedException or TaskCanceledException or ObjectDisposedException) { }
    }

    private async Task SaveAsync(Func<UiPreferences, UiPreferences> update)
    {
        await saves.WaitAsync(lifetime);
        try
        {
            lifetime.ThrowIfCancellationRequested();
            var next = update(current);
            await gallery.SavePreferencesAsync(next);
            current = next;
        }
        finally { saves.Release(); }
    }
}
