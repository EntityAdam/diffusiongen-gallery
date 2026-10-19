using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Gallery.Core.Services.Processors;

/// <summary>A Windows source opened with delete access, denying writers and renames until the exact handle is deleted.</summary>
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

    public ValueTask DisposeAsync() => Stream.DisposeAsync();

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint sharing, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle safeFileHandle, int informationClass,
        ref byte information, int size);
}
