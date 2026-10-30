namespace Gallery.Stress.Core;

public static class Safety
{
    public const string MarkerName = "stress-vault.marker";
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string CanonicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null) current = entry.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Cannot resolve output path link.");
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    public static bool Contains(string parent, string child)
    {
        parent = CanonicalPath(parent);
        child = CanonicalPath(child);
        return child.Equals(parent, Comparison) || child.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, Comparison);
    }

    public static void ValidateOutputPath(string path)
    {
        var full = CanonicalPath(path);
        if (Path.GetPathRoot(full) == full) throw new InvalidOperationException("A filesystem root cannot be used as a stress directory.");
        var roots = new[] { Environment.CurrentDirectory, AppContext.BaseDirectory };
        foreach (var start in roots)
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git")))
                {
                    if (Contains(directory.FullName, full) || Contains(full, directory.FullName))
                        throw new InvalidOperationException("Stress directories must be outside the git repository and cannot contain it.");
                    break;
                }
                directory = directory.Parent;
            }
        }
        var applicationData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiffusionGenGallery");
        if (Contains(applicationData, full) || Contains(full, applicationData))
            throw new InvalidOperationException("Stress directories must be outside the application's data directory and cannot contain it.");
    }

    public static void RequireStressVault(string vault)
    {
        ValidateOutputPath(vault);
        if (!File.Exists(Path.Combine(vault, MarkerName))) throw new InvalidOperationException("The vault is missing its stress-vault.marker; refusing to modify it.");
    }

    public static void MarkStressVault(string vault)
    {
        ValidateOutputPath(vault);
        Directory.CreateDirectory(vault);
        File.WriteAllText(Path.Combine(vault, MarkerName), "Gallery.Stress disposable synthetic data\n");
    }

    public static void PrecheckDisk(IEnumerable<(string Path, long Bytes)> requirements)
    {
        var totals = new Dictionary<string, (DriveInfo Drive, long Bytes)>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var requirement in requirements)
        {
            var full = CanonicalPath(requirement.Path);
            var drive = DriveInfo.GetDrives().Where(d => d.IsReady && Contains(d.RootDirectory.FullName, full))
                .OrderByDescending(d => d.RootDirectory.FullName.Length).FirstOrDefault() ?? new DriveInfo(Path.GetPathRoot(full)!);
            var key = drive.Name;
            totals[key] = (drive, checked((totals.TryGetValue(key, out var previous) ? previous.Bytes : 0) + requirement.Bytes));
        }
        foreach (var (_, requirement) in totals)
            if (requirement.Drive.AvailableFreeSpace < requirement.Bytes * 1.1 + 2L * 1024 * 1024 * 1024)
                throw new IOException("Insufficient free disk space for the next batch plus 10% and a 2 GiB reserve. Reduce --batch-files or free space.");
    }
}
