using Gallery.Core.Features.Backups.Targets;
using Gallery.Core.Models;

namespace Gallery.Core.Features.Backups;

internal static class BackupSettingsProcessor
{
    internal const string LocalDestinationType = "local";
    internal const string AzureDestinationType = "azure";

    internal static BackupOptions ToOptions(StoredBackupSettings? settings) =>
        settings is null
            ? new(LocalDestinationType, "", "", "", false, null, null)
            : new(settings.DestinationType, settings.LocalDirectory, settings.AzureContainer,
                settings.AzurePrefix, settings.AzureConnectionString.Length > 0,
                settings.LastBackupName, settings.LastBackupType);

    internal static (string Type, string LocalDirectory, string AzureContainer, string AzurePrefix) Normalize(
        string destinationType, string localDirectory, string azureContainer, string azurePrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationType);
        var type = destinationType.Trim().ToLowerInvariant();
        if (type is not (LocalDestinationType or AzureDestinationType))
            throw new InvalidOperationException("Choose either a local/NAS folder or Azure Blob Storage.");
        return (type, localDirectory.Trim(), azureContainer.Trim(), BackupTargetAzure.NormalizePrefix(azurePrefix));
    }

    internal static StoredBackupSettings Prepare(string type, string localDirectory, string azureContainer,
        string azurePrefix, string azureConnectionString, StoredBackupSettings? current)
    {
        var connectionString = azureConnectionString.Trim();
        return type == LocalDestinationType
            ? PrepareLocal(localDirectory, azureContainer, azurePrefix, connectionString)
            : PrepareAzure(localDirectory, azureContainer, azurePrefix, connectionString, current);
    }

    private static StoredBackupSettings PrepareLocal(
        string localDirectory, string azureContainer, string azurePrefix, string connectionString)
    {
        if (localDirectory.Length == 0)
            throw new InvalidOperationException("Enter a local folder or SMB network share path.");
        return new(LocalDestinationType, Path.GetFullPath(localDirectory),
            azureContainer, azurePrefix, connectionString, null, null);
    }

    private static StoredBackupSettings PrepareAzure(string localDirectory, string azureContainer,
        string azurePrefix, string connectionString, StoredBackupSettings? current)
    {
        if (azureContainer.Length == 0)
            throw new InvalidOperationException("Enter the Azure Blob container name.");
        if (connectionString.Length == 0 && current?.DestinationType == AzureDestinationType)
            connectionString = current.AzureConnectionString;
        if (connectionString.Length == 0)
            throw new InvalidOperationException("Enter the Azure Storage connection string.");
        if (azureContainer.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            throw new InvalidOperationException("Azure container names may contain only lowercase letters, digits, and hyphens.");
        if (azureContainer != azureContainer.ToLowerInvariant() || azureContainer.Length is < 3 or > 63)
            throw new InvalidOperationException("Azure container names must be 3 to 63 lowercase letters, digits, or hyphens.");
        var settings = new StoredBackupSettings(AzureDestinationType,
            localDirectory, azureContainer, azurePrefix, connectionString, null, null);
        BackupTargetAzure.ValidateDestination(settings);
        return settings;
    }

    internal static bool SameDestination(StoredBackupSettings left, StoredBackupSettings right) =>
        left.DestinationType == right.DestinationType
        && left.LocalDirectory == right.LocalDirectory
        && left.AzureContainer == right.AzureContainer
        && left.AzurePrefix == right.AzurePrefix
        && left.AzureConnectionString == right.AzureConnectionString;
}
