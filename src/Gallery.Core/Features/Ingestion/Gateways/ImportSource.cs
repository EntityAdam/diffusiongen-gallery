using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Gallery.Core.Infrastructure.Persistence;
using Gallery.Core.Models;

namespace Gallery.Core.Features.Ingestion.Gateways;

/// <summary>Opens import sources and supports deleting the verified source by handle or by path.</summary>
internal sealed class ImportSource : IAsyncDisposable
{
    internal const string StagingDirectoryPrefix = ".gallery-delete-";
    private const int GuidTextLength = 32;

    public FileStream Stream { get; }
    public bool CanDeleteByHandle { get; }

    public ImportSource(string path, bool deleteAfterVerification)
    {
        if (!deleteAfterVerification || !OperatingSystem.IsWindows())
        {
            Stream = File.OpenRead(path);
            return;
        }

        // FileShare.Delete would allow another process to replace the pathname while we verify.
        var safeFileHandle = CreateFile(path, 0x80000000u | 0x00010000u, 1, IntPtr.Zero, 3,
            0x08000000u | 0x40000000u, IntPtr.Zero);
        if (safeFileHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            safeFileHandle.Dispose();
            throw new IOException($"Could not exclusively read and delete source '{path}'.", new Win32Exception(error));
        }
        try { Stream = new FileStream(safeFileHandle, FileAccess.Read, 81920, isAsync: true); }
        catch { safeFileHandle.Dispose(); throw; }
        CanDeleteByHandle = true;
    }

    public void DeleteVerified()
    {
        RequireHandleDeletion();
        MarkVerifiedSourceForDeletion();
    }

    private void RequireHandleDeletion()
    {
        if (!CanDeleteByHandle) throw new InvalidOperationException("Handle-based deletion is unavailable.");
    }

    private void MarkVerifiedSourceForDeletion()
    {
        byte disposition = 1;
        if (!SetFileInformationByHandle(Stream.SafeFileHandle, 4, ref disposition, sizeof(byte)))
            throw new IOException("The verified source could not be marked for deletion.",
                new Win32Exception(Marshal.GetLastWin32Error()));
    }

    public static async Task DeleteVerifiedPathAsync(string sourcePath, string expectedSha256)
    {
        var claim = ClaimSourcePath(sourcePath);
        string actualSha256;
        try
        {
            actualSha256 = await ReadClaimedSourceSha256Async(claim.ClaimedPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RestoreClaimedSource(claim, exception);
            throw;
        }

        if (!actualSha256.Equals(expectedSha256, StringComparison.Ordinal))
        {
            RestoreClaimedSource(claim);
            throw new InvalidOperationException("Source changed since import; it was not deleted.");
        }

        try { File.Delete(claim.ClaimedPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RestoreClaimedSource(claim, exception);
            throw new InvalidOperationException("Encrypted import succeeded, but source deletion failed.", exception);
        }

        DeleteEmptyStagingDirectory(claim.StagingDirectory);
    }

    public static async Task ReconcilePendingDeletesAsync(
        string folder, FolderSearchScope searchScope, IReadOnlyCollection<CatalogImportSource> catalogSources)
    {
        ArgumentNullException.ThrowIfNull(catalogSources);
        if (searchScope is not FolderSearchScope.SelectedFolder and not FolderSearchScope.Descendants)
            throw new ArgumentOutOfRangeException(nameof(searchScope));

        var fullFolder = Path.GetFullPath(folder);
        if (!Directory.Exists(fullFolder))
            throw new DirectoryNotFoundException("Choose an existing local folder.");

        var catalogHashesByPath = BuildCatalogHashIndex(catalogSources);
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(fullFolder);
        while (pendingDirectories.TryPop(out var currentDirectory))
        {
            var childDirectories = Directory.EnumerateDirectories(currentDirectory).ToArray();
            foreach (var childDirectory in childDirectories)
            {
                if (IsStagingDirectoryName(Path.GetFileName(childDirectory)))
                {
                    await ReconcileStagingDirectoryAsync(childDirectory, catalogHashesByPath);
                    continue;
                }

                if (searchScope == FolderSearchScope.Descendants
                    && (File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) == 0)
                    pendingDirectories.Push(childDirectory);
            }

            foreach (var file in Directory.EnumerateFiles(currentDirectory))
            {
                if (TryGetLegacySourcePath(file, out var originalPath))
                    await ReconcileStagedFileAsync(file, originalPath, catalogHashesByPath);
            }
        }
    }

    internal static bool IsStagingDirectoryName(string name) =>
        name.StartsWith(StagingDirectoryPrefix, StringComparison.Ordinal)
        && Guid.TryParseExact(name[StagingDirectoryPrefix.Length..], "N", out _);

    private static ClaimedSource ClaimSourcePath(string sourcePath)
    {
        var sourceDirectory = Path.GetDirectoryName(sourcePath)
            ?? throw new IOException($"Cannot determine the source directory for '{sourcePath}'.");
        var stagingDirectory = Path.Combine(sourceDirectory,
            StagingDirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        var claimedPath = Path.Combine(stagingDirectory, Path.GetFileName(sourcePath));
        try
        {
            File.Move(sourcePath, claimedPath);
        }
        catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
        {
            try { Directory.Delete(stagingDirectory); }
            catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
            {
                throw new AggregateException(
                    $"Source claim failed and its staging directory remains at '{stagingDirectory}'.",
                    moveError, cleanupError);
            }
            throw;
        }
        return new ClaimedSource(sourcePath, stagingDirectory, claimedPath);
    }

    private static async Task<string> ReadClaimedSourceSha256Async(string claimedPath)
    {
        await using var fileStream = new FileStream(claimedPath, FileMode.Open, FileAccess.Read, FileShare.None);
        return Convert.ToHexString(await SHA256.HashDataAsync(fileStream));
    }

    private static void RestoreClaimedSource(ClaimedSource claim, Exception? cause = null)
    {
        try
        {
            File.Move(claim.ClaimedPath, claim.SourcePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The source could not be restored to '{claim.SourcePath}' and is preserved at '{claim.ClaimedPath}'.",
                cause is null ? exception : new AggregateException(cause, exception));
        }

        DeleteEmptyStagingDirectory(claim.StagingDirectory);
    }

    private static async Task ReconcileStagingDirectoryAsync(
        string stagingDirectory, IReadOnlyDictionary<string, HashSet<string>> catalogHashesByPath)
    {
        if ((File.GetAttributes(stagingDirectory) & FileAttributes.ReparsePoint) != 0)
            throw RecoveryRequired(stagingDirectory, "the staging directory is a reparse point");

        var entries = Directory.EnumerateFileSystemEntries(stagingDirectory).ToArray();
        if (entries.Length == 0)
        {
            DeleteEmptyStagingDirectory(stagingDirectory);
            return;
        }

        if (entries.Length != 1 || Directory.Exists(entries[0]))
            throw RecoveryRequired(stagingDirectory, "the staging directory has unexpected contents");

        var stagedPath = entries[0];
        if ((File.GetAttributes(stagedPath) & FileAttributes.ReparsePoint) != 0)
            throw RecoveryRequired(stagedPath, "the staged source is a reparse point");

        var originalPath = Path.Combine(
            Path.GetDirectoryName(stagingDirectory)!, Path.GetFileName(stagedPath));
        await ReconcileStagedFileAsync(stagedPath, originalPath, catalogHashesByPath);
        DeleteEmptyStagingDirectory(stagingDirectory);
    }

    private static async Task ReconcileStagedFileAsync(
        string stagedPath, string originalPath, IReadOnlyDictionary<string, HashSet<string>> catalogHashesByPath)
    {
        if ((File.GetAttributes(stagedPath) & FileAttributes.ReparsePoint) != 0)
            throw RecoveryRequired(stagedPath, "the staged source is a reparse point");

        var fullOriginalPath = Path.GetFullPath(originalPath);
        if (!catalogHashesByPath.TryGetValue(fullOriginalPath, out var catalogHashes))
        {
            try
            {
                File.Move(stagedPath, fullOriginalPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw RecoveryRequired(stagedPath, $"it could not be restored to '{fullOriginalPath}'", exception);
            }
            return;
        }

        var actualSha256 = await ReadClaimedSourceSha256Async(stagedPath);
        if (catalogHashes.Contains(actualSha256))
        {
            try { File.Delete(stagedPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw RecoveryRequired(stagedPath, "the catalog-verified staged source could not be deleted", exception);
            }
            return;
        }

        throw RecoveryRequired(stagedPath,
            $"its content does not match the committed catalog record for '{fullOriginalPath}'");
    }

    private static Dictionary<string, HashSet<string>> BuildCatalogHashIndex(
        IEnumerable<CatalogImportSource> catalogSources)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = new Dictionary<string, HashSet<string>>(comparer);
        foreach (var source in catalogSources)
        {
            if (string.IsNullOrWhiteSpace(source.SourcePath)) continue;
            string fullPath;
            try { fullPath = Path.GetFullPath(source.SourcePath); }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!result.TryGetValue(fullPath, out var hashes))
                result.Add(fullPath, hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            hashes.Add(source.Sha256);
        }
        return result;
    }

    private static bool TryGetLegacySourcePath(string stagedPath, out string originalPath)
    {
        var fileName = Path.GetFileName(stagedPath);
        var guidStart = fileName.Length - GuidTextLength;
        var prefixStart = guidStart - StagingDirectoryPrefix.Length;
        if (prefixStart <= 0
            || !fileName.AsSpan(prefixStart, StagingDirectoryPrefix.Length)
                .SequenceEqual(StagingDirectoryPrefix.AsSpan())
            || !Guid.TryParseExact(fileName[guidStart..], "N", out _))
        {
            originalPath = "";
            return false;
        }

        originalPath = Path.Combine(Path.GetDirectoryName(stagedPath)!, fileName[..prefixStart]);
        return true;
    }

    private static InvalidOperationException RecoveryRequired(
        string stagedPath, string reason, Exception? cause = null) =>
        new($"Source deletion recovery requires manual attention: {reason}. Staged content is preserved at '{stagedPath}'.",
            cause);

    private static void DeleteEmptyStagingDirectory(string stagingDirectory)
    {
        try { Directory.Delete(stagingDirectory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Source content was reconciled, but its empty staging directory remains at '{stagingDirectory}'.",
                exception);
        }
    }

    public ValueTask DisposeAsync() => Stream.DisposeAsync();

    private sealed record ClaimedSource(string SourcePath, string StagingDirectory, string ClaimedPath);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle safeFileHandle, int informationClass,
        ref byte information, int size);
}
