using System.Security.Cryptography;
using System.Text.Json;
using Gallery.Core.Infrastructure.Persistence;

namespace Gallery.Core.Features.Vaults.Publication;

internal static class VaultCreationPublication
{
    private const string CreationMarkerFileName = ".vault-create.json";
    private const string CreationStagingPrefix = ".vault-create-";
    private const string InvalidCreationReceiptMessage = "The interrupted gallery creation receipt is invalid; existing files were preserved.";
    private sealed record CreationPublication(string StagingDirectory, string CatalogSha256, string HeaderSha256);

    internal static string CreateStagingDirectory(string vaultRoot) =>
        Path.Combine(vaultRoot, CreationStagingPrefix + Guid.NewGuid().ToString("N"));

    internal static bool HasReceipt(string vaultRoot) =>
        File.Exists(Path.Combine(vaultRoot, CreationMarkerFileName));

    internal static void RemoveReceipt(string vaultRoot) =>
        File.Delete(Path.Combine(vaultRoot, CreationMarkerFileName));

    internal static void Prepare(string staging, string vaultRoot)
    {
        var publication = new CreationPublication(Path.GetFileName(staging),
            FileHash(Path.Combine(staging, GalleryStoreFactory.CatalogFileName)),
            FileHash(Path.Combine(staging, VaultStore.HeaderFileName)));
        var stagedMarker = Path.Combine(staging, CreationMarkerFileName);
        using (var stream = new FileStream(stagedMarker, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, publication);
            stream.Flush(flushToDisk: true);
        }
        File.Move(stagedMarker, Path.Combine(vaultRoot, CreationMarkerFileName), overwrite: false);
    }

    // Caller holds the destination checkout. A durable receipt identifies only this creation's files.
    internal static void RecoverInterruptedCreation(string vaultRoot)
    {
        var markerPath = Path.Combine(vaultRoot, CreationMarkerFileName);
        if (!File.Exists(markerPath)) return;
        CreationPublication? publication;
        try { publication = JsonSerializer.Deserialize<CreationPublication>(File.ReadAllText(markerPath)); }
        catch (JsonException exception)
        {
            throw new IOException(InvalidCreationReceiptMessage, exception);
        }
        if (!HasValidCreationReceipt(publication))
            throw new IOException(InvalidCreationReceiptMessage);

        var staging = Path.Combine(vaultRoot, publication!.StagingDirectory);
        if (Directory.Exists(staging) && File.GetAttributes(staging).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The interrupted gallery staging directory cannot be a symbolic link.");
        var catalogPath = Path.Combine(vaultRoot, GalleryStoreFactory.CatalogFileName);
        var headerPath = Path.Combine(vaultRoot, VaultStore.HeaderFileName);
        if (File.Exists(headerPath))
        {
            if (FileHash(headerPath) != publication.HeaderSha256)
                throw new IOException("The gallery header changed after interrupted creation; existing files were preserved.");
        }
        else if (File.Exists(catalogPath))
        {
            var stagedCatalog = Path.Combine(staging, GalleryStoreFactory.CatalogFileName);
            var stagedHeader = Path.Combine(staging, VaultStore.HeaderFileName);
            if (File.Exists(stagedCatalog) || !File.Exists(stagedHeader)
                || FileHash(stagedHeader) != publication.HeaderSha256
                || FileHash(catalogPath) != publication.CatalogSha256
                || File.Exists(catalogPath + "-wal") || File.Exists(catalogPath + "-shm"))
                throw new IOException("The catalog cannot be identified as an interrupted creation; existing files were preserved.");
            File.Move(catalogPath, stagedCatalog, overwrite: false);
        }
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        File.Delete(markerPath);
    }

    private static bool IsFileHash(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static bool HasValidCreationReceipt(CreationPublication? publication) =>
        publication is not null && publication.StagingDirectory is not null
        && publication.StagingDirectory.StartsWith(CreationStagingPrefix, StringComparison.Ordinal)
        && Guid.TryParseExact(publication.StagingDirectory[CreationStagingPrefix.Length..], "N", out _)
        && IsFileHash(publication.CatalogSha256) && IsFileHash(publication.HeaderSha256);

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static void DeleteStaging(string staging)
    {
        try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
