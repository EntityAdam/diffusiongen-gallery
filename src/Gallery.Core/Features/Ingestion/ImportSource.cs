using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Gallery.Core.Features.Ingestion;

/// <summary>Opens import sources and supports deleting the verified source by handle or by path.</summary>
internal sealed class ImportSource : IAsyncDisposable
{
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
        var claimedPath = ClaimSourcePath(sourcePath);
        string actualSha256;
        try
        {
            actualSha256 = await ReadClaimedSourceSha256Async(claimedPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RestoreClaimedSource(claimedPath, sourcePath, exception);
            throw;
        }

        if (!actualSha256.Equals(expectedSha256, StringComparison.Ordinal))
        {
            RestoreClaimedSource(claimedPath, sourcePath);
            throw new InvalidOperationException("Source changed since import; it was not deleted.");
        }

        try { File.Delete(claimedPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RestoreClaimedSource(claimedPath, sourcePath, exception);
            throw new InvalidOperationException("Encrypted import succeeded, but source deletion failed.", exception);
        }
    }

    private static string ClaimSourcePath(string sourcePath)
    {
        var claimedPath = sourcePath + ".gallery-delete-" + Guid.NewGuid().ToString("N");
        File.Move(sourcePath, claimedPath);
        return claimedPath;
    }

    private static async Task<string> ReadClaimedSourceSha256Async(string claimedPath)
    {
        await using var fileStream = new FileStream(claimedPath, FileMode.Open, FileAccess.Read, FileShare.None);
        return Convert.ToHexString(await SHA256.HashDataAsync(fileStream));
    }

    private static void RestoreClaimedSource(string claimedPath, string sourcePath, Exception? cause = null)
    {
        try
        {
            File.Move(claimedPath, sourcePath);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"The source could not be restored to '{sourcePath}' and is preserved at '{claimedPath}'.",
                cause is null ? exception : new AggregateException(cause, exception));
        }
    }

    public ValueTask DisposeAsync() => Stream.DisposeAsync();

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle safeFileHandle, int informationClass,
        ref byte information, int size);
}
