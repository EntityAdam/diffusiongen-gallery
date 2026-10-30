namespace Gallery.Core.Services.Sessions.Locations;

internal sealed class VaultCheckout : IDisposable
{
    private readonly FileStream fileStream;

    private VaultCheckout(FileStream fileStream) => this.fileStream = fileStream;

    public static VaultCheckout Acquire(string root)
    {
        Directory.CreateDirectory(root);
        return new(OpenExclusiveCheckoutStream(Path.Combine(root, ".vault-checkout")));
    }

    private static FileStream OpenExclusiveCheckoutStream(string path)
    {
        try
        {
            // Keep the file in place: deleting it after release creates an inode race on Unix/SMB.
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ioException)
        {
            throw new IOException("This gallery is checked out by another instance, or its checkout file cannot be accessed. Close it in the other instance and try again.", ioException);
        }
    }

    public void Dispose() => fileStream.Dispose();
}
