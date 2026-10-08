using System.Security.Cryptography;
using Gallery.Models;
using SixLabors.ImageSharp;

namespace Gallery.Services;

public sealed partial class GalleryService
{
    public async Task<List<RemovalItem>> PrepareRemovalAsync(IEnumerable<string> ids)
    {
        using var key = session.Borrow();
        var items = new List<RemovalItem>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            var record = await FindAsync(id, key.Bytes);
            if (!record.MarkedForDeletion)
                throw new InvalidOperationException($"'{record.Name}' must be marked for deletion first.");
            var original = OriginalPath(record);
            items.Add(new(id, record.Name, original, SourceExists(original)));
        }
        return items;
    }

    public async Task<List<RemovalResult>> RemoveAsync(IReadOnlyList<RemovalItem> items,
        ExistingOriginalAction existingAction, MissingOriginalAction missingAction, string restoreFolder)
    {
        using var key = session.Borrow();
        if (!Enum.IsDefined(existingAction) || !Enum.IsDefined(missingAction))
            throw new InvalidOperationException("Invalid removal action.");
        if (items.Count == 0) throw new InvalidOperationException("Select marked images to remove.");
        if (items.Any(item => !item.OriginalExists) && missingAction == MissingOriginalAction.RestorePng)
        {
            restoreFolder = Path.GetFullPath(restoreFolder);
            if (!Directory.Exists(restoreFolder)) throw new DirectoryNotFoundException("Choose an existing restoration folder.");
        }
        var results = new List<RemovalResult>();
        await store.Gate.WaitAsync();
        try
        {
            foreach (var item in items.DistinctBy(item => item.Id))
            {
                string? restored = null;
                var removed = false;
                var originalDeleted = false;
                try
                {
                    var record = await FindAsync(item.Id, key.Bytes);
                    if (!record.MarkedForDeletion)
                        throw new InvalidOperationException("Image is no longer marked for deletion.");
                    var original = OriginalPath(record);
                    if (original != item.OriginalPath || SourceExists(original) != item.OriginalExists)
                        throw new InvalidOperationException("Original file status changed. Review a new removal preview.");
                    if (item.OriginalExists && existingAction == ExistingOriginalAction.Delete)
                    {
                        if (string.Equals(original, record.StoredPath, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Original and gallery file paths must differ.");
                        await using (var source = File.OpenRead(original))
                        {
                            if (Convert.ToHexString(await SHA256.HashDataAsync(source)) != record.Sha256)
                                throw new InvalidOperationException("Original file changed since import; it will not be deleted.");
                        }
                        File.Delete(original);
                        originalDeleted = true;
                    }
                    else if (!item.OriginalExists && missingAction == MissingOriginalAction.RestorePng)
                    {
                        var plain = VaultCrypto.Decrypt(await File.ReadAllBytesAsync(record.StoredPath),
                            key.Bytes, $"image:{record.Id}");
                        try
                        {
                            if (record.PngSha256.Length > 0 && Convert.ToHexString(SHA256.HashData(plain)) != record.PngSha256)
                                throw new CryptographicException("Saved PNG failed verification.");
                            using var image = Image.Load(plain);
                            if (image.Width != record.Width || image.Height != record.Height)
                                throw new CryptographicException("Saved PNG dimensions do not match.");
                            // Use the opaque ID for a safe, collision-free name; never trust imported names as paths.
                            if (!Guid.TryParseExact(record.Id, "N", out _))
                                throw new InvalidOperationException("Invalid image identity for restoration.");
                            var target = Path.Combine(restoreFolder, record.Id + ".png");
                            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            {
                                try
                                {
                                    await output.WriteAsync(plain);
                                    output.Flush(flushToDisk: true);
                                }
                                catch
                                {
                                    output.Close();
                                    File.Delete(target);
                                    throw;
                                }
                            }
                            restored = target;
                            await using var restoredInput = File.OpenRead(target);
                            if (!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(restoredInput), SHA256.HashData(plain)))
                                throw new CryptographicException("Restored PNG failed read-back verification; gallery image retained.");
                        }
                        finally { CryptographicOperations.ZeroMemory(plain); }
                    }

                    var staged = record.StoredPath + ".removing-" + Guid.NewGuid().ToString("N");
                    var hasCiphertext = SourceExists(record.StoredPath);
                    if (hasCiphertext) File.Move(record.StoredPath, staged);
                    try { await store.DeleteAsync(record.Id); }
                    catch
                    {
                        if (hasCiphertext) File.Move(staged, record.StoredPath);
                        throw;
                    }
                    removed = true;
                    if (hasCiphertext)
                    {
                        try { File.Delete(staged); }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                        {
                            throw new IOException($"Gallery entry removed, but encrypted residue remains at '{staged}'. Delete this file manually.", exception);
                        }
                    }
                    results.Add(new(item.Id, true, restored, null));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or InvalidOperationException or CryptographicException or UnknownImageFormatException
                    or InvalidImageContentException or Microsoft.Data.Sqlite.SqliteException)
                {
                    results.Add(new(item.Id, removed, restored,
                        (originalDeleted ? "Original was deleted. " : "") + exception.Message));
                }
            }
        }
        finally { store.Gate.Release(); }
        return results;
    }

    private static string OriginalPath(ImageRecord record)
    {
        if (record.SourcePath.Length > 0) return Path.GetFullPath(record.SourcePath);
        // Legacy local-path imports can be recovered; relative browser folder names cannot.
        return Path.IsPathFullyQualified(record.SourceFolder)
            && Path.GetFileName(record.OriginalName) == record.OriginalName
            ? Path.Combine(record.SourceFolder, record.OriginalName) : "";
    }

    private static bool SourceExists(string path)
    {
        if (path.Length == 0) return false;
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidOperationException($"'{path}' is a directory or link; removal was stopped.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
