namespace Gallery.Core.Infrastructure.Persistence;

/// <summary>Creates catalog snapshots and computes their logical fingerprints.</summary>
internal interface IGalleryCatalogSnapshots
{
    Task CreateSnapshotAsync(string destinationPath, byte[] key);
    Task<string> ComputeSnapshotFingerprintAsync(string snapshotPath, string excludedSettingId, byte[] key);
}
