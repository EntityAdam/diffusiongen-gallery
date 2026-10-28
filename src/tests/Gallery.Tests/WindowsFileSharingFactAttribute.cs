namespace Gallery.Tests;

internal sealed class WindowsFileSystemFactAttribute : FactAttribute
{
    public WindowsFileSystemFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "This scenario requires Windows file-system access semantics.";
    }
}
