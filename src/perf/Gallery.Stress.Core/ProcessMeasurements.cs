using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Gallery.Stress.Core;

public static class ProcessMeasurements
{
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr handle, out IoCounters counters);

    // Darwin SDK sys/resource.h: rusage is 144 bytes; ru_maxrss is a byte count at offset 32.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct ResourceUsage { [FieldOffset(32)] public long PeakResidentBytes; }
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getrusage", SetLastError = true)]
    private static extern int GetResourceUsage(int who, out ResourceUsage usage);

    // Darwin SDK sys/resource.h: rusage_info_v2 is 160 bytes, including its 16-byte UUID.
    [StructLayout(LayoutKind.Explicit, Size = 160)]
    private struct ResourceUsageInfo { [FieldOffset(144)] public ulong DiskReadBytes; }
    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_pid_rusage", SetLastError = true)]
    private static extern int GetProcessResourceUsage(int pid, int flavor, out ResourceUsageInfo usage);

    public static long? PeakWorkingSetBytes()
    {
        if (OperatingSystem.IsMacOS())
            return GetResourceUsage(0, out var usage) == 0 && usage.PeakResidentBytes > 0 ? usage.PeakResidentBytes : null;
        using var process = Process.GetCurrentProcess();
        return process.PeakWorkingSet64 > 0 ? process.PeakWorkingSet64 : null;
    }

    public static (long? ReadBytes, long? ReadOps) ReadIo()
    {
        if (OperatingSystem.IsWindows() && GetProcessIoCounters(Process.GetCurrentProcess().Handle, out var io))
            return ((long)io.ReadBytes, (long)io.ReadOperations);
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var values = File.ReadAllLines("/proc/self/io").Select(line => line.Split(':')).ToDictionary(x => x[0], x => long.Parse(x[1].Trim()));
                return (values["read_bytes"], values["syscr"]);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        if (OperatingSystem.IsMacOS() && GetProcessResourceUsage(Environment.ProcessId, 2, out var usage) == 0)
            return (checked((long)usage.DiskReadBytes), null);
        return (null, null);
    }
}
