namespace Gallery.Core.Features.Backups;

internal sealed record StoredBackupSettings(
    string DestinationType,
    string LocalDirectory,
    string AzureContainer,
    string AzurePrefix,
    string AzureConnectionString,
    string? LastBackupName,
    string? LastBackupType)
{
    internal const string SettingId = "backup-settings";
    internal const string SettingPurpose = "backup-settings:v1";
}
