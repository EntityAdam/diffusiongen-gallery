namespace Gallery.Services.DataManagers;

internal sealed class VaultCheckout : IDisposable
{
    private readonly FileStream stream;

    private VaultCheckout(FileStream stream) => this.stream = stream;

    public static VaultCheckout Acquire(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ".vault-checkout");
        try
        {
            // Keep the file in place: deleting it after release creates an inode race on Unix/SMB.
            return new(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException exception)
        {
            throw new IOException("This gallery is checked out by another instance, or its checkout file cannot be accessed. Close it in the other instance and try again.", exception);
        }
    }

    public void Dispose() => stream.Dispose();
}
