namespace Gallery.Core.Models;

public sealed record BackupOptions(
    string DestinationType,
    string LocalDirectory,
    string AzureContainer,
    string AzurePrefix,
    bool HasAzureCredentials,
    string? LastBackupName,
    string? LastBackupType);
