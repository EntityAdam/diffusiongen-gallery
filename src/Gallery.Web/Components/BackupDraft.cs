using Gallery.Core.Models;
using Gallery.Core.Services;

namespace Gallery.Web.Components;

public sealed class BackupDraft
{
    public BackupOptions? Saved { get; private set; }
    public string DestinationType { get; set; } = "local";
    public string LocalDirectory { get; set; } = "";
    public string Container { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string ConnectionString { get; set; } = "";

    public bool HasChanges => Saved is not null &&
        (ConnectionString.Length > 0 || Saved.DestinationType != DestinationType
        || Saved.LocalDirectory != LocalDirectory.Trim()
        || Saved.AzureContainer != Container.Trim()
        || Saved.AzurePrefix != Prefix.Trim().Trim('/'));
    public bool Ready => Saved is not null
        && ConnectionString.Length == 0
        && Saved.DestinationType == DestinationType
        && Saved.LocalDirectory == LocalDirectory.Trim()
        && Saved.AzureContainer == Container.Trim()
        && Saved.AzurePrefix == Prefix.Trim().Trim('/')
        && (DestinationType == "local"
            ? LocalDirectory.Trim().Length > 0
            : Container.Trim().Length > 0 && Saved.HasAzureCredentials);

    public void Load(BackupOptions options)
    {
        Saved = options;
        Discard();
    }

    public void Discard()
    {
        DestinationType = Saved?.DestinationType ?? "local";
        LocalDirectory = Saved?.LocalDirectory ?? "";
        Container = Saved?.AzureContainer ?? "";
        Prefix = Saved?.AzurePrefix ?? "";
        ConnectionString = "";
    }

    public async Task SaveAsync(GalleryService gallery) =>
        Load(await gallery.SaveBackupOptionsAsync(DestinationType, LocalDirectory, Container, Prefix, ConnectionString));
}
