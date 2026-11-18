using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Gallery.Core.Models;
using Gallery.Core.Services.Processors.Codecs;

namespace Gallery.Core.Services.Processors;

internal static class IngestionSourceScanner
{
    internal const string Mp4Extension = ".mp4";
    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".webp", Mp4Extension];

    public static bool IsVideoName(string name) =>
        string.Equals(Path.GetExtension(name), Mp4Extension, StringComparison.OrdinalIgnoreCase);

    public static async Task<IngestionScan> ScanAsync(string folder, FolderSearchScope searchScope, string catalogRoot, string mediaRoot)
    {
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Choose an existing local folder.");
        var files = await Task.Run(() => DiscoverMediaPaths(folder, searchScope).Select(path =>
        {
            var info = new FileInfo(path);
            return new IngestionScanFile(path, info.Length, info.LastWriteTimeUtc, IsVideoName(path));
        }).ToImmutableArray());
        return new IngestionScan(folder, searchScope, catalogRoot, mediaRoot, files);
    }

    public static string[] DiscoverMediaPaths(string folder, FolderSearchScope searchScope)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = searchScope == FolderSearchScope.Descendants,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        return Directory.EnumerateFiles(folder, "*", options)
            .Where(path => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    public static void ValidateScan(IngestionScan scan, string folder, FolderSearchScope scope, string catalogRoot, string mediaRoot)
    {
        if (scan.Folder != folder || scan.SearchScope != scope
            || scan.CatalogRoot != catalogRoot || scan.MediaRoot != mediaRoot)
            throw new InvalidOperationException("The source or gallery location changed. Scan the folder again before ingesting.");
    }

    public static IngestionSpaceEstimate MeasureSpace(
        IngestionScan scan, ImmutableArray<IngestionScanFile> files)
    {
        var mediaDrive = ReadIngestionDiskSpace(scan.MediaRoot);
        var catalogDrive = ReadIngestionDiskSpace(scan.CatalogRoot);
        var mediaAvailable = mediaDrive.AvailableBytes;
        var catalogAvailable = mediaDrive.Name == catalogDrive.Name
            ? mediaAvailable : catalogDrive.AvailableBytes;
        return CalculateIngestionSpace(files.Length, files.Sum(file => file.Bytes),
            mediaAvailable, catalogAvailable, mediaDrive.Name == catalogDrive.Name);
    }

    internal static IngestionSpaceEstimate CalculateIngestionSpace(
        int fileCount, long sourceBytes, long mediaAvailable, long catalogAvailable, bool sameDrive)
    {
        // Typical-size allowance, not a measurement: 128 KiB thumbnail + 4 KiB catalog data per file.
        var estimated = checked(sourceBytes + fileCount *
            (128L * 1024 + 4L * 1024 + 2L * VaultCrypto.EnvelopeHeaderSizeBytes));
        // Keep the larger pre-deletion safety allowance separate from expected persistent growth.
        var mediaRequired = checked(sourceBytes + 64L * fileCount);
        var catalogRequired = fileCount == 0 ? 0 : checked(4L * 1024 * 1024 * fileCount + 16L * 1024 * 1024);
        var required = checked(mediaRequired + catalogRequired);
        return new(fileCount, sourceBytes, estimated, required,
            sameDrive ? required : mediaRequired, mediaAvailable,
            sameDrive ? required : catalogRequired, catalogAvailable);
    }

    private static DriveInfo FindIngestionDrive(string directory)
    {
        var path = Path.GetFullPath(directory);
        var drives = DriveInfo.GetDrives();
        var mountName = SelectIngestionMount(path, drives.Select(drive => drive.Name));
        return drives.FirstOrDefault(drive => drive.Name == mountName)
            ?? new DriveInfo(Path.GetPathRoot(path) ?? throw new IOException("Cannot determine the destination drive."));
    }

    /// <summary>Selects the longest mount name that contains the path at a directory boundary.</summary>
    internal static string? SelectIngestionMount(string fullPath, IEnumerable<string> mountNames)
    {
        var path = Path.TrimEndingDirectorySeparator(fullPath) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return mountNames.OrderByDescending(name => name.Length)
            .FirstOrDefault(name => name.Length > 0 && path.StartsWith(name, comparison)
                && (Path.EndsInDirectorySeparator(name) || path[name.Length] == Path.DirectorySeparatorChar));
    }

    private static (string Name, long AvailableBytes) ReadIngestionDiskSpace(string directory)
    {
        if (OperatingSystem.IsWindows() && directory.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var uncDirectory = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
            if (!GetDiskFreeSpaceEx(uncDirectory, out var available, out _, out _))
                throw new IOException("Cannot measure available disk space at the media destination.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            return (Path.GetPathRoot(directory)!.ToUpperInvariant(), checked((long)available));
        }
        var drive = FindIngestionDrive(directory);
        return (drive.Name, drive.AvailableFreeSpace);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeBytesAvailable,
        out ulong totalBytes, out ulong totalFreeBytes);

    public static void ValidateScannedFiles(ImmutableArray<IngestionScanFile> files)
    {
        foreach (var file in files)
        {
            var info = new FileInfo(file.Path);
            if (!info.Exists || info.Length != file.Bytes || info.LastWriteTimeUtc != file.LastWriteTimeUtc)
                throw new IOException("A scanned source file changed or was removed. Scan the folder again before ingesting.");
        }
    }
}
