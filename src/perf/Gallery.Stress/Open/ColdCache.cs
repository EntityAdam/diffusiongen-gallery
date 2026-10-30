using System.Runtime.InteropServices;

namespace Gallery.Stress;

public static class ColdCache
{
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disable, ref TokenPrivileges privileges, uint size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtSetSystemInformation(int informationClass, ref int information, uint length);

    public static string Purge()
    {
        if (!OperatingSystem.IsWindows()) return Failed();
        if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, 0x20 | 0x8, out var token)) return Failed();
        try
        {
            if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out var luid)) return Failed();
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() != 0) return Failed();
            var command = 4;
            return NtSetSystemInformation(80, ref command, sizeof(int)) == 0 ? "standby-list" : Failed();
        }
        finally { CloseHandle(token); }
    }

    private static string Failed()
    {
        Console.Error.WriteLine("WARNING: OS cache purge failed or is unsupported. This run is NOT a verified cold-cache measurement; run elevated on Windows or use a declared reboot.");
        return "failed";
    }
}
