namespace Gallery.Tests;

internal sealed class WindowsFileSharingFactAttribute : FactAttribute
{
    public WindowsFileSharingFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "This scenario requires Windows FileShare.None delete-denial semantics; Unix permits unlinking an open file.";
    }
}
